//! Manual records (ManualCsvReader.cs in the reference) and downloading a feed (FeedDownloader.cs).

use std::collections::HashSet;
use std::io::Write;
use std::time::{Duration, Instant};

use super::parse::{clean, detect_script, format_error, parse_date, ParseError, ParsedAddress, ParsedName, ParsedSubject, SubjectType};
use crate::matching::text::{lower_invariant, upper_invariant};

const MANUAL_COLUMNS: [&str; 9] = ["external_id", "type", "name", "aliases", "address", "country", "programme", "listed_on", "remarks"];

/// Manual records: UTF-8 CSV with a header.
pub fn read_manual_csv(text: &str) -> Result<Vec<ParsedSubject>, ParseError> {
    let rows = csv_rows(text)?;
    let Some(header) = rows.first() else { return Err(format_error("The CSV is empty: a header is required.")) };
    let header: Vec<String> = header.iter().map(|c| c.trim_matches(char::is_whitespace).chars().map(lower_invariant).collect()).collect();
    let index = |column: &str| header.iter().position(|h| h == column);
    for required in ["external_id", "type", "name"] {
        if index(required).is_none() {
            return Err(format_error(format!("The header has no {required} column.")));
        }
    }
    let columns: Vec<(&str, Option<usize>)> = MANUAL_COLUMNS.iter().map(|c| (*c, index(c))).collect();
    let mut subjects = Vec::new();
    for (line, row) in rows.iter().enumerate().skip(1) {
        if row.iter().all(|f| f.trim_matches(char::is_whitespace).is_empty()) {
            continue;
        }
        let number = line + 1;
        if row.len() > header.len() {
            return Err(format_error(format!(
                "Line {number} has {} fields but the header has {}: quote any value containing a comma.",
                row.len(),
                header.len()
            )));
        }
        let field = |column: &str| -> Option<String> {
            let i = columns.iter().find(|(c, _)| *c == column)?.1?;
            clean(row.get(i).map(String::as_str))
        };
        let external_id = field("external_id").ok_or_else(|| format_error(format!("Line {number}: external_id is required.")))?;
        let name = field("name").ok_or_else(|| format_error(format!("Line {number}: name is required.")))?;
        let kind_text: String = field("type").unwrap_or_default().chars().map(lower_invariant).collect();
        let kind = match kind_text.as_str() {
            "individual" => SubjectType::Individual,
            "entity" => SubjectType::Entity,
            other => return Err(format_error(format!("Line {number}: type must be Individual or Entity, not '{other}'."))),
        };
        let country = field("country").map(|c| c.chars().map(upper_invariant).collect::<String>());
        if let Some(c) = &country {
            if c.len() != 2 || !c.bytes().all(|b| b.is_ascii_uppercase()) {
                return Err(format_error(format!("Line {number}: country must be a two-letter ISO code, not '{c}'.")));
            }
        }
        let listed_on_text = field("listed_on");
        let listed_on = parse_date(listed_on_text.as_deref(), "%Y-%m-%d");
        if let (Some(text), None) = (&listed_on_text, listed_on) {
            return Err(format_error(format!("Line {number}: listed_on must be yyyy-MM-dd, not '{text}'.")));
        }
        let mut names = vec![ParsedName { full_name: name.clone(), is_weak: false, script: detect_script(&name) }];
        let mut seen = HashSet::new();
        for alias in field("aliases").unwrap_or_default().split('|') {
            if let Some(a) = clean(Some(alias)) {
                if a != name && seen.insert(a.clone()) {
                    let script = detect_script(&a);
                    names.push(ParsedName { full_name: a, is_weak: false, script });
                }
            }
        }
        let address = field("address");
        let addresses = match (&address, &country) {
            (None, None) => Vec::new(),
            _ => vec![ParsedAddress { raw: address.clone().or(country.clone()).unwrap(), country_code: country.clone() }],
        };
        subjects.push(ParsedSubject {
            external_id,
            kind,
            primary_name: name,
            names,
            addresses,
            programmes: field("programme"),
            listed_on,
            remarks: field("remarks"),
            source_url: None,
        });
    }
    Ok(subjects)
}

fn csv_rows(text: &str) -> Result<Vec<Vec<String>>, ParseError> {
    let mut rows: Vec<Vec<String>> = Vec::new();
    let mut row: Vec<String> = Vec::new();
    let mut field = String::new();
    let (mut quoted, mut any) = (false, false);
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        any = true;
        if quoted {
            if c == '"' {
                if chars.peek() == Some(&'"') {
                    field.push('"');
                    chars.next();
                } else {
                    quoted = false;
                }
            } else {
                field.push(c);
            }
            continue;
        }
        match c {
            '"' if field.is_empty() => quoted = true,
            ',' => row.push(std::mem::take(&mut field)),
            '\r' => {}
            '\n' => {
                row.push(std::mem::take(&mut field));
                rows.push(std::mem::take(&mut row));
                any = false;
            }
            // A byte order mark: the reference's reader detects and drops it.
            '\u{FEFF}' if rows.is_empty() && row.is_empty() && field.is_empty() => {}
            _ => field.push(c),
        }
    }
    if quoted {
        return Err(format_error("The CSV ends inside a quoted field: a closing quote is missing."));
    }
    if any || !field.is_empty() || !row.is_empty() {
        row.push(field);
        rows.push(row);
    }
    Ok(rows)
}

/// After a passing fault, wait each delay in turn, never starting an attempt past the budget.
#[derive(Debug, Clone)]
pub struct DownloadRetry {
    pub delays: Vec<Duration>,
    pub budget: Duration,
}

impl DownloadRetry {
    /// Five retries, 15 s doubling to 4 minutes, within 10 minutes of the first attempt.
    pub fn default_retry() -> Self {
        DownloadRetry { delays: [15, 30, 60, 120, 240].map(Duration::from_secs).to_vec(), budget: Duration::from_secs(600) }
    }
}

/// Downloads a feed to a temporary file, identifying itself honestly.
pub struct Downloader {
    pub client: reqwest::Client,
    pub timeout: Duration,
    pub retry: DownloadRetry,
    pub version: String,
}

enum Fault {
    Status(u16),
    Timeout,
    Other(String, bool),
}

impl Fault {
    fn passing(&self) -> bool {
        match self {
            Fault::Status(code) => *code >= 500 || *code == 408 || *code == 429,
            Fault::Timeout => true,
            Fault::Other(_, passing) => *passing,
        }
    }

    fn describe(&self) -> String {
        match self {
            Fault::Status(code) => {
                let reason = reqwest::StatusCode::from_u16(*code).ok().and_then(|s| s.canonical_reason()).unwrap_or_default().replace(' ', "");
                format!("{code} {reason}")
            }
            Fault::Timeout => "no complete answer in time".into(),
            Fault::Other(kind, _) => kind.clone(),
        }
    }

    fn message(&self) -> String {
        match self {
            Fault::Status(code) => format!("Response status code does not indicate success: {code}"),
            Fault::Timeout => "The download timed out.".into(),
            Fault::Other(message, _) => message.clone(),
        }
    }
}

impl Downloader {
    pub async fn download(&self, source: &str, url: &str) -> Result<tempfile_path::TempPath, ParseError> {
        let started = Instant::now();
        let mut attempt = 1;
        loop {
            let fault = match self.attempt(url).await {
                Ok(path) => return Ok(path),
                Err(fault) => fault,
            };
            if !fault.passing() || attempt > self.retry.delays.len() {
                return Err(fault.message().into());
            }
            let delay = self.retry.delays[attempt - 1];
            if started.elapsed() + delay > self.retry.budget {
                return Err(fault.message().into());
            }
            // Worded without "failed" or "rejected": the import alert looks for those.
            println!("{source}: download attempt {attempt} of {} got {}; trying again in {} s.", self.retry.delays.len() + 1, fault.describe(), delay.as_secs());
            let _ = std::io::stdout().flush();
            tokio::time::sleep(delay).await;
            if started.elapsed() > self.retry.budget {
                return Err(fault.message().into());
            }
            attempt += 1;
        }
    }

    async fn attempt(&self, url: &str) -> Result<tempfile_path::TempPath, Fault> {
        let work = async {
            let response = self
                .client
                .get(url)
                .header("User-Agent", format!("sanction-and-forgive/{} (+https://github.com/mykolad/sanction-and-forgive)", self.version))
                .send()
                .await
                .map_err(|e| Fault::Other(e.to_string(), e.is_connect() || e.is_timeout() || e.is_request() || e.is_body()))?;
            let status = response.status().as_u16();
            if !(200..=299).contains(&status) {
                return Err(Fault::Status(status));
            }
            let path = tempfile_path::TempPath::new();
            let mut file = tokio::fs::File::create(path.path()).await.map_err(|e| Fault::Other(e.to_string(), false))?;
            let mut stream = response.bytes_stream();
            use futures_util::StreamExt;
            use tokio::io::AsyncWriteExt;
            while let Some(chunk) = stream.next().await {
                let chunk = chunk.map_err(|e| Fault::Other(e.to_string(), true))?;
                file.write_all(&chunk).await.map_err(|e| Fault::Other(e.to_string(), false))?;
            }
            file.flush().await.map_err(|e| Fault::Other(e.to_string(), false))?;
            Ok(path)
        };
        tokio::time::timeout(self.timeout, work).await.map_err(|_| Fault::Timeout)?
    }
}

/// A temporary file, deleted when dropped.
pub mod tempfile_path {
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicU64, Ordering};

    pub struct TempPath(PathBuf);

    static NEXT: AtomicU64 = AtomicU64::new(0);

    impl TempPath {
        pub fn new() -> Self {
            let n = NEXT.fetch_add(1, Ordering::Relaxed);
            TempPath(std::env::temp_dir().join(format!("feed-{}-{n}", std::process::id())))
        }

        pub fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Default for TempPath {
        fn default() -> Self {
            Self::new()
        }
    }

    impl Drop for TempPath {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }
}
