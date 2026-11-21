//! Manual records (ManualCsvReader.cs in the reference) and downloading a feed (FeedDownloader.cs).

use std::collections::HashSet;
use std::io::Write;
use std::time::{Duration, Instant};

use super::catalogue::ListSource;
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
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        });
    }
    Ok(subjects)
}

pub(crate) fn csv_rows(text: &str) -> Result<Vec<Vec<String>>, ParseError> {
    let mut rows: Vec<Vec<String>> = Vec::new();
    delimited_rows(text, ',', |row| {
        rows.push(row);
        Ok(())
    })?;
    Ok(rows)
}

/// Each row of delimited text, in turn (quoted fields with "" for a quote, CR LF or LF, a leading byte order mark
/// dropped): a large file's rows needn't all be held at once.
pub(crate) fn delimited_rows(
    text: &str,
    delimiter: char,
    mut each: impl FnMut(Vec<String>) -> Result<(), ParseError>,
) -> Result<(), ParseError> {
    let mut row: Vec<String> = Vec::new();
    let mut field = String::new();
    let (mut quoted, mut any, mut first) = (false, false, true);
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
            c if c == delimiter => row.push(std::mem::take(&mut field)),
            '\r' => {}
            '\n' => {
                row.push(std::mem::take(&mut field));
                each(std::mem::take(&mut row))?;
                any = false;
                first = false;
            }
            // A byte order mark: the reference's reader detects and drops it.
            '\u{FEFF}' if first && row.is_empty() && field.is_empty() => {}
            _ => field.push(c),
        }
    }
    if quoted {
        return Err(format_error("The CSV ends inside a quoted field: a closing quote is missing."));
    }
    if any || !field.is_empty() || !row.is_empty() {
        row.push(field);
        each(row)?;
    }
    Ok(())
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
                let reason =
                    reqwest::StatusCode::from_u16(*code).ok().and_then(|s| s.canonical_reason()).unwrap_or_default().replace(' ', "");
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

/// The largest publisher's page read for a link (ListSource::link_pattern).
pub const MAX_PAGE_BYTES: u64 = 4 << 20;

/// The file a page links, for a feed published under a dated name: of the page's links (href, entities decoded,
/// resolved against the page) on the page's own host and scheme, those whose address matches the pattern; of those, the
/// one whose matched part (the dated name) is last in byte order, so the newest date wherever it's filed, the whole
/// address breaking a tie. A format error when none does: the import fails rather than guess.
pub fn find_link(page: &str, document: &str, link_pattern: &str) -> Result<String, ParseError> {
    let base = reqwest::Url::parse(page).map_err(|e| format_error(format!("The page's address isn't valid: {e}")))?;
    let pattern = regex::Regex::new(link_pattern).map_err(|e| format_error(format!("The link pattern isn't valid: {e}")))?;
    let href = regex::Regex::new(r#"(?i)href\s*=\s*["']([^"']*)["']"#).expect("a valid pattern");
    let mut found: Option<(String, String)> = None;
    for link in href.captures_iter(document) {
        let Ok(target) = base.join(decode_entities(&link[1]).trim()) else { continue };
        let same_host = target.host_str().zip(base.host_str()).is_some_and(|(a, b)| a.eq_ignore_ascii_case(b));
        let Some(key) = pattern.find(target.as_str()).map(|m| m.as_str().to_string()) else { continue };
        if target.scheme() != base.scheme() || !same_host {
            continue;
        }
        let candidate = (key, target.to_string());
        if found.as_ref().is_none_or(|f| candidate > *f) {
            found = Some(candidate);
        }
    }
    found.map(|(_, address)| address).ok_or_else(|| format_error(format!("The publisher's page has no link matching {link_pattern}.")))
}

/// HTML's character references in an attribute: the five named ones and numeric ones (all a link needs).
pub(crate) fn decode_entities(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(at) = rest.find('&') {
        out.push_str(&rest[..at]);
        rest = &rest[at..];
        let decoded = rest.find(';').filter(|end| *end <= 10).and_then(|end| {
            let entity = &rest[1..end];
            let c = match entity {
                "amp" => Some('&'),
                "lt" => Some('<'),
                "gt" => Some('>'),
                "quot" => Some('"'),
                "apos" => Some('\''),
                "nbsp" => Some('\u{a0}'),
                "ndash" => Some('\u{2013}'),
                "mdash" => Some('\u{2014}'),
                "lsquo" => Some('\u{2018}'),
                "rsquo" => Some('\u{2019}'),
                "ldquo" => Some('\u{201c}'),
                "rdquo" => Some('\u{201d}'),
                "hellip" => Some('\u{2026}'),
                _ => entity
                    .strip_prefix("#x")
                    .or_else(|| entity.strip_prefix("#X"))
                    .map(|hex| u32::from_str_radix(hex, 16))
                    .or_else(|| entity.strip_prefix('#').map(|dec| dec.parse::<u32>()))
                    .and_then(|n| n.ok())
                    .and_then(char::from_u32),
            };
            c.map(|c| (c, end))
        });
        match decoded {
            Some((c, end)) => {
                out.push(c);
                rest = &rest[end + 1..];
            }
            None => {
                out.push('&');
                rest = &rest[1..];
            }
        }
    }
    out.push_str(rest);
    out
}

impl Downloader {
    /// A catalogued feed's file: its URL with the EU token in place of {token}, or, for a feed with a link pattern,
    /// the file its page links today. The page and the file are each tried again as download says.
    pub async fn download_feed(&self, source: &ListSource, eu_token: &str) -> Result<tempfile_path::TempPath, ParseError> {
        let mut address = source.url.replace("{token}", eu_token);
        if !source.link_pattern.is_empty() {
            let page = self.download_limited(source.code, &address, Some(MAX_PAGE_BYTES)).await?;
            let document = String::from_utf8_lossy(&std::fs::read(page.path())?).into_owned();
            address = find_link(&address, &document, source.link_pattern)?;
        }
        self.download(source.code, &address).await
    }

    pub async fn download(&self, source: &str, url: &str) -> Result<tempfile_path::TempPath, ParseError> {
        self.download_limited(source, url, None).await
    }

    /// download, stopping as soon as the body passes max_bytes (when given): an error not tried again.
    async fn download_limited(&self, source: &str, url: &str, max_bytes: Option<u64>) -> Result<tempfile_path::TempPath, ParseError> {
        let started = Instant::now();
        let mut attempt = 1;
        loop {
            let fault = match self.attempt(url, max_bytes).await {
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
            println!(
                "{source}: download attempt {attempt} of {} got {}; trying again in {} s.",
                self.retry.delays.len() + 1,
                fault.describe(),
                delay.as_secs()
            );
            let _ = std::io::stdout().flush();
            tokio::time::sleep(delay).await;
            if started.elapsed() > self.retry.budget {
                return Err(fault.message().into());
            }
            attempt += 1;
        }
    }

    async fn attempt(&self, url: &str, max_bytes: Option<u64>) -> Result<tempfile_path::TempPath, Fault> {
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
            let (path, file) = tempfile_path::TempPath::create().map_err(|e| Fault::Other(e.to_string(), false))?;
            let mut file = tokio::fs::File::from_std(file);
            let mut stream = response.bytes_stream();
            use futures_util::StreamExt;
            use tokio::io::AsyncWriteExt;
            let mut written = 0u64;
            while let Some(chunk) = stream.next().await {
                let chunk = chunk.map_err(|e| Fault::Other(e.to_string(), true))?;
                written += chunk.len() as u64;
                if let Some(limit) = max_bytes.filter(|limit| written > *limit) {
                    return Err(Fault::Other(format!("The publisher's page is larger than {limit} bytes."), false));
                }
                file.write_all(&chunk).await.map_err(|e| Fault::Other(e.to_string(), false))?;
            }
            file.flush().await.map_err(|e| Fault::Other(e.to_string(), false))?;
            Ok(path)
        };
        tokio::time::timeout(self.timeout, work).await.map_err(|_| Fault::Timeout)?
    }
}

/// A temporary file this process created, deleted when dropped.
pub mod tempfile_path {
    use std::fs::{File, OpenOptions};
    use std::io::ErrorKind;
    use std::path::{Path, PathBuf};

    pub struct TempPath(PathBuf);

    impl TempPath {
        /// Creates a new file under an unpredictable name, exclusively (never an existing file or a link someone
        /// planted), and returns it open.
        pub fn create() -> std::io::Result<(TempPath, File)> {
            for _ in 0..16 {
                let mut random = [0u8; 16];
                getrandom::fill(&mut random).map_err(|e| std::io::Error::other(e.to_string()))?;
                let path = std::env::temp_dir().join(format!("feed-{}", hex::encode(random)));
                match OpenOptions::new().write(true).create_new(true).open(&path) {
                    Ok(file) => return Ok((TempPath(path), file)),
                    Err(e) if e.kind() == ErrorKind::AlreadyExists => continue,
                    Err(e) => return Err(e),
                }
            }
            Err(std::io::Error::other("no free name for a temporary file"))
        }

        pub fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempPath {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }
}

#[cfg(test)]
mod link_tests {
    use super::find_link;

    #[test]
    fn a_linked_file_is_the_newest_matching_link_on_the_pages_own_host() {
        let document = r#"<a href="./list20261001.csv">October</a> <a HREF='list20261105.csv'>November</a>
            <a href="https://elsewhere.example/sanctions/list20991231.csv">a copy</a> <a href="./list20261201.xlsx">Excel</a>
            <a href="/sanctions/list20261231.csv?download=1&amp;x=2">with a query</a> <a href = "./list20261104.csv">again</a>
            <a href="zarchive/list20261001.csv">filed elsewhere, older</a>"#;
        let page = "https://www.example.go.jp/sanctions/list.html";
        assert_eq!(find_link(page, document, r"/list[0-9]{8}\.csv$").unwrap(), "https://www.example.go.jp/sanctions/list20261105.csv");
        assert!(find_link(page, r#"<a href="list.pdf">PDF</a>"#, r"/list[0-9]{8}\.csv$").is_err());
    }
}
