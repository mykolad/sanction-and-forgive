//! The CLI: the Rust port of SanctionAndForgive.Cli (imports, manual records, review, status, history, probe). The
//! same commands, options, output and exit codes: 0 success, 1 a source failed, was rejected or held, 2 bad arguments.

use std::collections::HashMap;
use std::io::BufReader;
use std::time::{Duration, Instant};

use regex::Regex;
use sanction_and_forgive::lists::catalogue::{find_source, FeedStatus, ListSource, EU_PUBLIC_TOKEN, SOURCES};
use sanction_and_forgive::lists::manual::{read_manual_csv, DownloadRetry, Downloader};
use sanction_and_forgive::lists::parse::{FormatError, NameScript, ParseError, ParsedSubject, SubjectType};
use sanction_and_forgive::lists::parsers::parser_for;
use sanction_and_forgive::store::db::{Database, DbError};
use sanction_and_forgive::store::imports::{ImportOutcome, ImportResult, Imports, ReviewResult, SourceRecord};
use sanction_and_forgive::store::queries::{Subject, SubjectAddress, SubjectName, Time};

const USAGE: &str = "Usage (tools/lists.ps1 wraps these):
  probe        [--source <code>|all] [--file <path>] [--keep <directory>]
  import       --source <code>|all [--file <path>] [--force]
  status
  history      [--source <code>]
  review                         (updates held for review: a drop of more than 20%)
  confirm      --import <id>     (make a held update active)
  reject       --import <id>     (discard a held update)
  source-add   --code <code> --name <name>
  add          --source <code> --file <records.csv>
  remove       --source <code> --external-id <id>

Feeds: eu, ofac-sdn, ofac-cons, un, uk, us-export, ch-seco, ca-sema, au-dfat, ua-nsdc, ua-sfms.
Configuration (environment variables): ConnectionStrings__Lists, and Lists__EuToken
(or LISTS_EU_TOKEN; default: the public token).";

const KNOWN: [&str; 9] = ["source", "file", "keep", "force", "code", "name", "external-id", "count", "import"];

fn parse(args: &[String]) -> Result<HashMap<String, Option<String>>, String> {
    let mut values = HashMap::new();
    let mut i = 0;
    while i < args.len() {
        let name = args[i].strip_prefix("--").filter(|n| KNOWN.contains(n)).ok_or_else(|| format!("Unknown option {}.", args[i]))?;
        if name == "force" {
            values.insert(name.to_string(), None);
            i += 1;
            continue;
        }
        if i + 1 >= args.len() || args[i + 1].starts_with("--") {
            return Err(format!("{} needs a value.", args[i]));
        }
        values.insert(name.to_string(), Some(args[i + 1].clone()));
        i += 2;
    }
    Ok(values)
}

#[tokio::main]
async fn main() {
    std::process::exit(run().await);
}

async fn run() -> i32 {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 {
        eprintln!("{USAGE}");
        return 2;
    }
    let options = match parse(&args[2..]) {
        Ok(options) => options,
        Err(e) => {
            eprintln!("{e}\n{USAGE}");
            return 2;
        }
    };
    let get = |name: &str| options.get(name).cloned().flatten().unwrap_or_default();
    let force = options.contains_key("force");
    let eu_token = ["Lists__EuToken", "LISTS_EU_TOKEN"]
        .iter()
        .find_map(|v| std::env::var(v).ok().filter(|t| !t.is_empty()))
        .unwrap_or_else(|| EU_PUBLIC_TOKEN.into());
    let downloader = Downloader {
        client: reqwest::Client::new(),
        timeout: Duration::from_secs(600),
        retry: DownloadRetry::default_retry(),
        version: version(),
    };

    if args[1] == "probe" {
        return probe(&downloader, &eu_token, &get("source"), &get("file")).await;
    }
    let connection = std::env::var("ConnectionStrings__Lists").unwrap_or_default();
    if connection.is_empty() {
        eprintln!("No connection string: set ConnectionStrings__Lists (tools/lists.ps1 does).");
        return 2;
    }
    let db = match Database::parse(&connection) {
        Ok(db) => db,
        Err(e) => {
            eprintln!("{e}");
            return 2;
        }
    };
    let c = Commands { imports: Imports { db }, downloader, eu_token };
    let command = args[1].as_str();
    match command {
        "import" if !get("source").is_empty() => return c.import_feeds(&get("source"), &get("file"), force).await,
        "status" => return c.status().await,
        "history" => return c.history(&get("source")).await,
        "review" => return c.review_list().await,
        "confirm" | "reject" => {
            if let Ok(id) = get("import").parse::<i32>() {
                return c.review(id, command == "confirm").await;
            }
        }
        "source-add" if !get("code").is_empty() && !get("name").is_empty() => return c.source_add(&get("code"), &get("name")).await,
        "add" if !get("source").is_empty() && !get("file").is_empty() => return c.add(&get("source"), &get("file")).await,
        "remove" if !get("source").is_empty() && !get("external-id").is_empty() => {
            return c.remove(&get("source"), &get("external-id")).await
        }
        _ => {}
    }
    eprintln!("Missing or unknown arguments for {command}.\n{USAGE}");
    2
}

fn version() -> String {
    std::env::var("App__Version").ok().filter(|v| !v.is_empty()).unwrap_or_else(|| "dev".into())
}

struct Commands {
    imports: Imports,
    downloader: Downloader,
    eu_token: String,
}

fn to_stored(parsed: Vec<ParsedSubject>) -> Vec<Subject> {
    parsed
        .into_iter()
        .map(|p| Subject {
            id: 0,
            external_id: p.external_id,
            kind: p.kind as u8,
            primary_name: p.primary_name,
            programmes: p.programmes,
            listed_on: p.listed_on,
            remarks: p.remarks,
            source_url: p.source_url,
            names: p
                .names
                .into_iter()
                .map(|n| SubjectName { full_name: n.full_name, is_weak: n.is_weak, script: n.script as u8 })
                .collect(),
            addresses: p.addresses.into_iter().map(|a| SubjectAddress { raw: a.raw, country_code: a.country_code }).collect(),
        })
        .collect()
}

/// An error's kind, as the reference prints an exception's type.
fn kind(e: &(dyn std::error::Error + 'static)) -> &'static str {
    if e.is::<FormatError>() {
        "ListFormatException"
    } else if e.is::<quick_xml::Error>() || e.is::<quick_xml::events::attributes::AttrError>() {
        "XmlException"
    } else if e.is::<tiberius::error::Error>() {
        "SqlException"
    } else if e.is::<std::io::Error>() {
        "IOException"
    } else {
        "Exception"
    }
}

fn kind_of(e: &DbError) -> &'static str {
    kind(e.as_ref())
}

/// A number with thousands separators (.NET's N0 in the invariant culture).
fn n0(v: i64) -> String {
    let digits = v.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    if v < 0 {
        format!("-{out}")
    } else {
        out
    }
}

fn when(t: Option<Time>) -> String {
    t.map(|t| t.format("%Y-%m-%d %H:%M").to_string()).unwrap_or_default()
}

fn count(v: Option<i32>) -> String {
    v.map(|v| n0(v.into())).unwrap_or_else(|| "-".into())
}

fn parse_file(parser: sanction_and_forgive::lists::parsers::Parser, path: &std::path::Path) -> Result<Vec<ParsedSubject>, ParseError> {
    let file = std::fs::File::open(path)?;
    let mut reader = BufReader::with_capacity(1 << 16, file);
    parser(&mut reader)
}

impl Commands {
    async fn import_feeds(&self, code: &str, file: &str, force: bool) -> i32 {
        let sources: Vec<&ListSource> = if code == "all" {
            if !file.is_empty() {
                println!("--file needs a single --source: one file is one feed.");
                return 2;
            }
            SOURCES.iter().filter(|s| s.status == FeedStatus::Active).collect()
        } else if let Some(source) = find_source(code) {
            vec![source]
        } else {
            println!("{code} is not a feed. Manual sources change with add and remove.");
            return 2;
        };
        let mut failed = false;
        for source in sources {
            failed = !self.import_one(source, file, force).await || failed;
        }
        i32::from(failed)
    }

    async fn fail(&self, source: &ListSource, record: Option<&SourceRecord>, e: &(dyn std::error::Error + Send + Sync + 'static)) -> bool {
        let k = kind(e);
        println!("{}: FAILED: {k}: {e}", source.code);
        if record.is_some() {
            if let Ok(Some(stored)) = self.imports.find_source(source.code).await {
                if let Err(recording) = self.imports.record_failure(&stored, &format!("{k}: {e}")).await {
                    println!("{}: the failure couldn't be recorded either ({}).", source.code, kind_of(&recording));
                }
            }
        }
        false
    }

    async fn import_one(&self, source: &ListSource, file: &str, force: bool) -> bool {
        let Some(parser) = parser_for(source.code) else {
            println!("{}: no parser yet ({}); see docs/sources.md.", source.code, source.status.as_str());
            return false;
        };
        let masked = (!source.url.is_empty()).then(|| source.url.replace("{token}", "…"));
        let mut record = match self.imports.ensure_source(source.code, source.name, masked.as_deref(), Some(source.max_age_hours)).await {
            Ok(record) => record,
            Err(e) => return self.fail(source, None, e.as_ref()).await,
        };
        let downloaded;
        let path = if file.is_empty() {
            match self.downloader.download(source.code, &source.url.replace("{token}", &self.eu_token)).await {
                Ok(p) => {
                    downloaded = p;
                    downloaded.path().to_path_buf()
                }
                Err(e) => return self.fail(source, Some(&record), e.as_ref()).await,
            }
        } else {
            std::path::PathBuf::from(file)
        };
        let parsed = match tokio::task::spawn_blocking(move || parse_file(parser, &path)).await.unwrap() {
            Ok(parsed) => parsed,
            Err(e) => return self.fail(source, Some(&record), e.as_ref()).await,
        };
        let outcome = match self.imports.import(&mut record, to_stored(parsed), force).await {
            Ok(outcome) => outcome,
            Err(e) => return self.fail(source, Some(&record), e.as_ref()).await,
        };
        let was = outcome.previous_count.map(|c| c.to_string()).unwrap_or_else(|| "none".into());
        // "Rejected" and "HeldForReview" in this line are what the import alert looks for (infra/platform.bicep).
        println!("{}: {}, {} records (was {was}). {}", source.code, outcome.result.as_str(), outcome.record_count, outcome.message);
        if outcome.result == ImportResult::HeldForReview {
            let id = outcome.import_id.unwrap_or_default();
            println!("{}: check the publisher's site, then confirm -Import {id} or reject -Import {id}.", source.code);
        }
        outcome.result != ImportResult::Rejected && outcome.result != ImportResult::HeldForReview
    }

    async fn status(&self) -> i32 {
        let statuses = match self.imports.status().await {
            Ok(s) => s,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        let now = chrono::Utc::now();
        println!("source       records  imported (UTC)     last checked (UTC)  age     max   state         last import");
        for s in &statuses {
            let (age, current) = match s.last_checked_at {
                Some(checked) => {
                    let hours = (now - checked).num_milliseconds() as f64 / 3_600_000.0;
                    (
                        format!("{:5.1}h", (hours * 10.0).round() / 10.0),
                        s.record_count.is_some() && s.max_age_hours.is_none_or(|max| hours <= max as f64),
                    )
                }
                None => ("     -".to_string(), s.record_count.is_some() && s.max_age_hours.is_none()),
            };
            let max_age = s.max_age_hours.map(|m| format!("{m}h")).unwrap_or_else(|| "-".into());
            let state = if current {
                "Current"
            } else if s.record_count.is_none() {
                "Not imported"
            } else {
                "Not checked"
            };
            let last = match &s.last_import_status {
                Some(status) => match &s.last_import_message {
                    Some(message) => format!("{status}: {message}"),
                    None => status.clone(),
                },
                None => "-".into(),
            };
            let records = s.record_count.map(|r| n0(r.into())).unwrap_or_default();
            println!(
                "{:<10} {:>9}  {:<18}  {:<18}  {age}  {:>4}  {:<12}  {last}",
                s.code,
                records,
                when(s.imported_at),
                when(s.last_checked_at),
                max_age,
                state
            );
        }
        for feed in SOURCES.iter().filter(|f| !statuses.iter().any(|s| s.code == f.code)) {
            println!("{:<10}         -  never imported ({})", feed.code, feed.status.as_str());
        }
        0
    }

    async fn history(&self, code: &str) -> i32 {
        let entries = match self.imports.history_per_source(code, 20).await {
            Ok(e) => e,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        println!("source     import  started (UTC)     status         records    added  removed  changed  note");
        for e in entries {
            let status = if e.active { "Active".to_string() } else { e.status.clone() };
            let reviewed = e.reviewed_by.as_ref().map(|by| format!(" (reviewed by {by}, {})", when(e.reviewed_at))).unwrap_or_default();
            println!(
                "{:<10} {:>6}  {}  {:<13} {:>8} {:>8} {:>8} {:>8}  {}{reviewed}",
                e.source_code,
                e.id,
                when(Some(e.started_at)),
                status,
                n0(e.record_count.into()),
                count(e.added),
                count(e.removed),
                count(e.changed),
                e.message.unwrap_or_default()
            );
        }
        0
    }

    async fn review_list(&self) -> i32 {
        let held = match self.imports.held().await {
            Ok(h) => h,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        if held.is_empty() {
            println!("No updates are held for review.");
            return 0;
        }
        let statuses = match self.imports.status().await {
            Ok(s) => s,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        for h in held {
            let active = statuses
                .iter()
                .filter(|s| s.code == h.source_code)
                .filter_map(|s| s.record_count)
                .map(|c| n0(c.into()))
                .next_back()
                .unwrap_or_default();
            println!(
                "{}: import {}, downloaded {} UTC, {} records; active list {active} records. {}",
                h.source_code,
                h.id,
                when(Some(h.started_at)),
                n0(h.record_count.into()),
                h.message.unwrap_or_default()
            );
        }
        println!("Check the publisher's site, then: confirm -Import <id>, or reject -Import <id>.");
        0
    }

    async fn review(&self, id: i32, confirm: bool) -> i32 {
        let reviewer = self.imports.current_login().await;
        let result = match self.imports.review(id, confirm, &reviewer).await {
            Ok(r) => r,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        match result {
            ReviewResult::Confirmed => {
                println!("Import {id} confirmed: it's the active list now. The API picks it up within its refresh interval.")
            }
            ReviewResult::Rejected => println!("Import {id} rejected: its records are deleted; the active list is unchanged."),
            ReviewResult::NotFound => println!("There's no import {id}."),
            ReviewResult::NotPending => println!("Import {id} isn't held for review (see history)."),
        }
        i32::from(!matches!(result, ReviewResult::Confirmed | ReviewResult::Rejected))
    }

    async fn source_add(&self, code: &str, name: &str) -> i32 {
        let code = code.trim();
        if !Regex::new("^[a-z0-9][a-z0-9-]{1,31}$").unwrap().is_match(code) {
            println!("'{code}' isn't a valid code: use 2-32 lower-case letters, digits and hyphens.");
            return 2;
        }
        let existing = match self.imports.find_source(code).await {
            Ok(e) => e,
            Err(e) => {
                eprintln!("{e}");
                return 1;
            }
        };
        if find_source(code).is_some() || existing.is_some_and(|e| e.url.is_some()) {
            println!("{code} is a feed's code; pick another for a manual source.");
            return 2;
        }
        if let Err(e) = self.imports.ensure_source(code, name, None, None).await {
            eprintln!("{e}");
            return 1;
        }
        println!("Manual source {code} ({name}) is ready. Add records with: add -Source {code} -File records.csv");
        0
    }

    async fn manual_source(&self, code: &str) -> Option<SourceRecord> {
        match self.imports.find_source(code).await {
            Ok(Some(source)) if source.url.is_none() && find_source(code).is_none() => Some(source),
            _ => {
                println!("{code} is not a manual source. Create one with: source-add -Code {code} -Name \"…\"");
                None
            }
        }
    }

    async fn add(&self, code: &str, file: &str) -> i32 {
        let Some(mut source) = self.manual_source(code).await else { return 2 };
        let text = match std::fs::read(file) {
            Ok(bytes) => String::from_utf8_lossy(&bytes).into_owned(),
            Err(e) => {
                println!("{code}: FAILED: {e}");
                return 1;
            }
        };
        let parsed = match read_manual_csv(&text) {
            Ok(p) => p,
            Err(e) => {
                println!("{code}: FAILED: {}: {e}", kind(e.as_ref()));
                return 1;
            }
        };
        let records = to_stored(parsed);
        let count = records.len();
        let outcome = self
            .imports
            .change(&mut source, move |mut current| {
                // Upsert by external id: a replaced record keeps its place, a new one goes at the end.
                let mut position: HashMap<String, usize> = current.iter().enumerate().map(|(i, s)| (s.external_id.clone(), i)).collect();
                for r in records {
                    match position.get(&r.external_id) {
                        Some(&i) => current[i] = r,
                        None => {
                            position.insert(r.external_id.clone(), current.len());
                            current.push(r);
                        }
                    }
                }
                current
            })
            .await;
        match outcome {
            Ok(outcome) => report(code, &format!("{count} records upserted"), &outcome),
            Err(e) => {
                println!("{code}: FAILED: {e}");
                1
            }
        }
    }

    async fn remove(&self, code: &str, external_id: &str) -> i32 {
        let Some(mut source) = self.manual_source(code).await else { return 2 };
        let found = std::sync::atomic::AtomicBool::new(false);
        let outcome = self
            .imports
            .change(&mut source, |current| {
                let before = current.len();
                let kept: Vec<Subject> = current.into_iter().filter(|s| s.external_id != external_id).collect();
                found.store(kept.len() != before, std::sync::atomic::Ordering::Relaxed);
                kept
            })
            .await;
        match outcome {
            Err(e) => {
                println!("{code}: FAILED: {e}");
                1
            }
            Ok(_) if !found.load(std::sync::atomic::Ordering::Relaxed) => {
                println!("{code} has no record {external_id}; nothing changed.");
                1
            }
            Ok(outcome) => report(code, &format!("removed {external_id}"), &outcome),
        }
    }
}

fn report(code: &str, what: &str, outcome: &ImportOutcome) -> i32 {
    if outcome.result == ImportResult::Rejected {
        println!("{code}: NOT changed: {}. A list can't be emptied: it keeps at least one record.", outcome.message);
        return 1;
    }
    println!("{code}: {what}, {} in the list ({}).", outcome.record_count, outcome.result.as_str());
    0
}

/// Downloads (or reads), parses and counts each feed; it writes nothing else. Exit 1 if an Active feed failed.
async fn probe(downloader: &Downloader, eu_token: &str, code: &str, file: &str) -> i32 {
    let sources: Vec<&ListSource> = if code.is_empty() || code == "all" {
        if !file.is_empty() {
            eprintln!("--file needs a single --source.");
            return 2;
        }
        SOURCES.iter().collect()
    } else if let Some(source) = find_source(code) {
        vec![source]
    } else {
        eprintln!("Unknown source {code}.\n{USAGE}");
        return 2;
    };
    println!("source     status        subjects  indiv  entity  unknown   names   weak  cyrillic  other  addresses  seconds  result");
    let mut failed = false;
    for source in sources {
        let prefix = format!("{:<10} {:<12}", source.code, source.status.as_str());
        let Some(parser) = parser_for(source.code) else {
            println!("{prefix} {:>8}  no parser yet: needs access (docs/sources.md)", "-");
            continue;
        };
        let started = Instant::now();
        let downloaded;
        let path = if file.is_empty() {
            if source.url.is_empty() {
                println!("{prefix} {:>8}  no URL: pass --file", "-");
                continue;
            }
            match downloader.download(source.code, &source.url.replace("{token}", eu_token)).await {
                Ok(p) => {
                    downloaded = p;
                    downloaded.path().to_path_buf()
                }
                Err(e) => {
                    println!("{prefix} FAILED: {e}");
                    failed = failed || source.status == FeedStatus::Active;
                    continue;
                }
            }
        } else {
            std::path::PathBuf::from(file)
        };
        let parsed = match tokio::task::spawn_blocking(move || parse_file(parser, &path)).await.unwrap() {
            Ok(p) => p,
            Err(e) => {
                println!("{prefix} FAILED: {e}");
                failed = failed || source.status == FeedStatus::Active;
                continue;
            }
        };
        let (mut individuals, mut entities, mut unknown, mut names, mut weak, mut cyrillic, mut other, mut addresses) =
            (0i64, 0i64, 0i64, 0i64, 0i64, 0i64, 0i64, 0i64);
        for p in &parsed {
            match p.kind {
                SubjectType::Individual => individuals += 1,
                SubjectType::Entity => entities += 1,
                SubjectType::Unknown => unknown += 1,
            }
            for n in &p.names {
                names += 1;
                weak += i64::from(n.is_weak);
                match n.script {
                    NameScript::Cyrillic => cyrillic += 1,
                    NameScript::Other => other += 1,
                    NameScript::Latin => {}
                }
            }
            addresses += p.addresses.len() as i64;
        }
        let result = if parsed.is_empty() {
            failed = failed || source.status == FeedStatus::Active;
            "parsed, but no subjects"
        } else {
            "parsed"
        };
        println!(
            "{prefix} {:>9} {:>6} {:>7} {:>8} {:>7} {:>6} {:>9} {:>6} {:>10} {:>8.1}  {result}",
            n0(parsed.len() as i64),
            n0(individuals),
            n0(entities),
            n0(unknown),
            n0(names),
            n0(weak),
            n0(cyrillic),
            n0(other),
            n0(addresses),
            started.elapsed().as_secs_f64()
        );
    }
    i32::from(failed)
}
