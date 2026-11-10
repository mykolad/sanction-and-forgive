//! One screening: matching against the lists in memory (never the database), the response, the summary's wording,
//! and the audit line, which carries the reference and what each list answered, never what was entered.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use regex::Regex;
use serde::Serialize;
use serde_json::Value;

use super::catalog::{Catalog, LoadedList};
use crate::lists::catalogue::find_source;
use crate::log::Logger;
use crate::matching::{AllowList, Cell, Entry, Options, Outcome, OverallOutcome, ScreenedList, Screener, ScreeningRequest, ScreeningResult, SubjectMatch, WordAlignment};
use crate::store::queries::Time;

/// On every result.
pub const DISCLAIMER: &str = "This is a demo list check, not a legal determination. It doesn't cover ownership or control by listed parties, \
or embargoes. A person must review possible matches. The percentage is an estimate that this entry is the \
listed party, based on the data entered; it isn't the chance that a shipment is lawful.";

const MAX_FIELD_LENGTH: usize = 300;

/// A time as the API writes it: RFC 3339 in UTC, the fraction without trailing zeros (as the Go port's, which the
/// golden files normalise).
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub struct JsonTime(pub Time);

impl Serialize for JsonTime {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&format_time(self.0))
    }
}

pub fn format_time(t: Time) -> String {
    let mut s = t.format("%Y-%m-%dT%H:%M:%S").to_string();
    let nanos = t.timestamp_subsec_nanos();
    if nanos > 0 {
        let fraction = format!("{nanos:09}");
        s.push('.');
        s.push_str(fraction.trim_end_matches('0'));
    }
    s.push('Z');
    s
}

/// The request: the caller's reference and at least one field.
#[derive(Debug, Clone, Default)]
pub struct ScreeningRequestBody {
    pub reference: Option<String>,
    pub receiver_name: Option<String>,
    pub company_name: Option<String>,
    pub address: Option<String>,
}

impl ScreeningRequestBody {
    /// One JSON object (fields matched ignoring case, as the reference's web defaults do; the last of duplicates
    /// wins), each field a string or null. None when it isn't one.
    pub fn parse(text: &str) -> Option<ScreeningRequestBody> {
        let value: Value = serde_json::from_str(text).ok()?;
        let object = value.as_object()?;
        let mut body = ScreeningRequestBody::default();
        for (key, value) in object {
            let target = match key.to_ascii_lowercase().as_str() {
                "reference" => &mut body.reference,
                "receivername" => &mut body.receiver_name,
                "companyname" => &mut body.company_name,
                "address" => &mut body.address,
                _ => continue,
            };
            *target = match value {
                Value::Null => None,
                Value::String(s) => Some(s.clone()),
                _ => return None,
            };
        }
        Some(body)
    }

    pub fn reference(&self) -> &str {
        self.reference.as_deref().unwrap_or_default()
    }
}

/// As .NET's string.Length counts (UTF-16 code units).
pub fn utf16_len(s: &str) -> usize {
    s.encode_utf16().count()
}

fn is_blank(s: &Option<String>) -> bool {
    s.as_deref().is_none_or(|s| s.trim_matches(char::is_whitespace).is_empty())
}

pub type Errors = BTreeMap<String, Vec<String>>;

/// What's wrong with a request, by field (empty when valid). Never echoes a value.
pub fn validate(body: &ScreeningRequestBody) -> Errors {
    static REFERENCE: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    let pattern = REFERENCE.get_or_init(|| Regex::new(r"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,63}$").unwrap());
    let mut errors = Errors::new();
    match body.reference.as_deref() {
        None | Some("") => {
            errors.insert(
                "Reference".into(),
                vec!["A reference is required: your own id for this screening (a transaction id, say), to find it in the logs.".into()],
            );
        }
        Some(r) if !pattern.is_match(r) => {
            errors.insert(
                "Reference".into(),
                vec!["A reference is 1-64 characters: letters, digits and . _ : / -, starting with a letter or digit. Never put personal data in it.".into()],
            );
        }
        _ => {}
    }
    for (name, value) in [("ReceiverName", &body.receiver_name), ("CompanyName", &body.company_name), ("Address", &body.address)] {
        if value.as_deref().is_some_and(|v| utf16_len(v) > MAX_FIELD_LENGTH) {
            errors.insert(name.into(), vec![format!("At most {MAX_FIELD_LENGTH} characters.")]);
        }
    }
    if is_blank(&body.receiver_name) && is_blank(&body.company_name) && is_blank(&body.address) {
        errors.insert(String::new(), vec!["Enter a receiver, a company or an address.".into()]);
    }
    errors
}

// --- Responses (ApiResponses.cs) ---

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListResponse {
    pub code: String,
    pub name: String,
    pub as_of: Option<JsonTime>,
    pub state: &'static str,
    pub version: Option<i32>,
    pub record_count: i32,
    pub url: Option<String>,
}

pub fn list_response(list: &LoadedList, now: Time) -> ListResponse {
    let version = if list.index.is_some() { list.active_import_id } else { None };
    let url = match find_source(&list.code) {
        Some(source) => source.public_url(),
        None => list.url.clone(),
    };
    ListResponse {
        code: list.code.clone(),
        name: list.name.clone(),
        as_of: list.last_checked_at.map(JsonTime),
        state: list.state(now),
        version,
        record_count: list.record_count,
        url,
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CandidateResponse {
    pub listed_name: String,
    pub alias: String,
    pub programme: Option<String>,
    pub source_url: Option<String>,
    pub external_id: String,
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub percent: i32,
    pub outcome: &'static str,
    pub alignment: Vec<String>,
    pub address_state: &'static str,
    pub listed_addresses: Vec<String>,
}

pub fn kind_name(kind: crate::matching::SubjectKind) -> &'static str {
    match kind {
        crate::matching::SubjectKind::Individual => "Individual",
        crate::matching::SubjectKind::Entity => "Entity",
        crate::matching::SubjectKind::Unknown => "Unknown",
    }
}

fn candidate_response(m: &SubjectMatch) -> CandidateResponse {
    let listed_addresses = m
        .subject
        .addresses
        .iter()
        .map(|a| if !a.country_code.is_empty() && !a.raw.contains(&a.country_code) { format!("{} ({})", a.raw, a.country_code) } else { a.raw.clone() })
        .collect();
    CandidateResponse {
        listed_name: m.subject.primary_name.clone(),
        alias: m.alias.clone(),
        programme: m.subject.programme.clone(),
        source_url: m.subject.source_url.clone(),
        external_id: m.subject.external_id.clone(),
        kind: kind_name(m.subject.kind),
        percent: m.percent,
        outcome: m.outcome.as_str(),
        alignment: m.alignment.clone(),
        address_state: m.address_state.as_str(),
        listed_addresses,
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CellResponse {
    pub list: String,
    pub state: &'static str,
    pub percent: Option<i32>,
    pub outcome: Option<&'static str>,
    pub best_candidate: Option<CandidateResponse>,
    pub other_hits: Vec<CandidateResponse>,
    pub allowed: bool,
}

fn state_of(cell: &Cell) -> &'static str {
    if cell.checked {
        "Checked"
    } else {
        "NotChecked"
    }
}

fn cell_response(cell: &Cell, o: &Options) -> CellResponse {
    CellResponse {
        list: cell.list.clone(),
        state: state_of(cell),
        percent: cell.percent,
        outcome: cell.outcome.map(|o| o.as_str()),
        best_candidate: cell.best.as_ref().filter(|b| b.percent >= o.best_candidate_min_percent).map(candidate_response),
        other_hits: cell.others.iter().map(candidate_response).collect(),
        allowed: cell.allowed,
    }
}

#[derive(Debug, Clone, Serialize)]
pub struct AllowedByResponse {
    pub id: i32,
    pub name: String,
    pub reason: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EntryResponse {
    pub kind: &'static str,
    pub input: String,
    pub compared: &'static str,
    pub cells: Vec<CellResponse>,
    pub allowed_by: Option<AllowedByResponse>,
}

fn entry_response(entry: &Entry, o: &Options) -> EntryResponse {
    EntryResponse {
        kind: entry.kind.as_str(),
        input: entry.input.clone(),
        compared: entry.compared,
        cells: entry.cells.iter().map(|c| cell_response(c, o)).collect(),
        allowed_by: entry.allowed_by.as_ref().map(|a| AllowedByResponse { id: a.id, name: a.name.clone(), reason: a.reason.clone() }),
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScreeningResponse {
    pub reference: String,
    pub outcome: &'static str,
    pub summary: String,
    pub disclaimer: &'static str,
    pub matcher_version: String,
    pub lists: Vec<ListResponse>,
    pub entries: Vec<EntryResponse>,
    pub allow_list_version: String,
}

// --- Stream lines (StreamResponses.cs) ---

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamHeaderLine {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub matcher_version: String,
    pub disclaimer: &'static str,
    pub lists: Vec<ListResponse>,
    pub max_records: usize,
    pub max_seconds: u64,
    pub allow_list_version: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamCandidate {
    pub listed_name: String,
    pub alias: String,
    pub external_id: String,
    pub source_url: Option<String>,
    pub percent: i32,
    pub words: Vec<WordAlignment>,
    pub notes: Vec<String>,
    pub address_state: &'static str,
}

#[derive(Debug, Clone, Serialize)]
pub struct StreamCell {
    pub list: String,
    pub state: &'static str,
    pub percent: Option<i32>,
    pub outcome: Option<&'static str>,
    pub best: Option<StreamCandidate>,
    pub allowed: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamEntry {
    pub kind: &'static str,
    pub cells: Vec<StreamCell>,
    pub allow_list_entry_id: Option<i32>,
}

#[derive(Debug, Clone, Serialize)]
pub struct StreamResultLine {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub line: usize,
    pub reference: String,
    pub outcome: &'static str,
    pub entries: Vec<StreamEntry>,
}

#[derive(Debug, Clone, Serialize)]
pub struct StreamErrorLine {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub line: usize,
    pub reference: Option<String>,
    pub errors: Errors,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StreamEndLine {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub screened: usize,
    pub errors: usize,
    pub truncated: bool,
    pub next_line: Option<usize>,
}

pub fn stream_entry(entry: &Entry, o: &Options) -> StreamEntry {
    StreamEntry {
        kind: entry.kind.as_str(),
        cells: entry
            .cells
            .iter()
            .map(|cell| StreamCell {
                list: cell.list.clone(),
                state: state_of(cell),
                percent: cell.percent,
                outcome: cell.outcome.map(|o| o.as_str()),
                best: cell.best.as_ref().filter(|b| b.percent >= o.best_candidate_min_percent).map(|b| StreamCandidate {
                    listed_name: b.subject.primary_name.clone(),
                    alias: b.alias.clone(),
                    external_id: b.subject.external_id.clone(),
                    source_url: b.subject.source_url.clone(),
                    percent: b.percent,
                    words: b.words.clone(),
                    notes: b.notes.clone(),
                    address_state: b.address_state.as_str(),
                }),
                allowed: cell.allowed,
            })
            .collect(),
        allow_list_entry_id: entry.allowed_by.as_ref().map(|a| a.id),
    }
}

/// Runs screenings and writes their audit lines.
pub struct ScreeningService {
    pub catalog: Arc<Catalog>,
    pub screener: Screener,
    pub logger: Logger,
}

impl ScreeningService {
    pub fn options(&self) -> &Options {
        self.screener.options()
    }

    pub fn now(&self) -> Time {
        chrono::Utc::now()
    }

    fn run(&self, body: &ScreeningRequestBody, lists: &[Arc<LoadedList>], allow_list: &AllowList, now: Time) -> (ScreeningResult, Vec<ListResponse>) {
        let screened: Vec<ScreenedList> =
            lists.iter().filter(|l| l.switched_off.is_none()).map(|l| ScreenedList { code: l.code.clone(), name: l.name.clone(), index: l.index.clone() }).collect();
        let request = ScreeningRequest {
            receiver_name: body.receiver_name.clone().unwrap_or_default(),
            company_name: body.company_name.clone().unwrap_or_default(),
            address: body.address.clone().unwrap_or_default(),
        };
        let result = self.screener.screen(&request, &screened, allow_list).expect("validated: at least one field");

        let mut best: HashMap<&str, i32> = HashMap::new();
        for entry in &result.entries {
            for cell in &entry.cells {
                let percent = cell.percent.unwrap_or(-1);
                let current = best.entry(cell.list.as_str()).or_insert(percent);
                *current = (*current).max(percent);
            }
        }
        let mut rows: Vec<ListResponse> = lists.iter().map(|l| list_response(l, now)).collect();
        let review_min = self.options().review_min_percent;
        let key = |row: &ListResponse| best.get(row.code.as_str()).copied().filter(|&b| b >= review_min).unwrap_or(-1);
        rows.sort_by_key(|row| std::cmp::Reverse(key(row)));
        self.audit(body.reference(), &result, &rows);
        (result, rows)
    }

    /// The single endpoint's answer.
    pub fn screen(&self, body: &ScreeningRequestBody) -> ScreeningResponse {
        let (lists, allow_list, now) = (self.catalog.lists(), self.catalog.allow_list(), self.now());
        let (result, rows) = self.run(body, &lists, &allow_list, now);
        ScreeningResponse {
            reference: body.reference().to_string(),
            outcome: result.outcome.as_str(),
            summary: self.summary(&result, &lists, now),
            disclaimer: DISCLAIMER,
            matcher_version: result.matcher_version.clone(),
            lists: rows,
            entries: result.entries.iter().map(|e| entry_response(e, self.options())).collect(),
            allow_list_version: result.allow_list_version.clone(),
        }
    }

    /// One stream record against the stream's snapshot.
    pub fn screen_for_stream(&self, body: &ScreeningRequestBody, lists: &[Arc<LoadedList>], allow_list: &AllowList) -> ScreeningResult {
        self.run(body, lists, allow_list, self.now()).0
    }

    fn audit(&self, reference: &str, result: &ScreeningResult, rows: &[ListResponse]) {
        let versions: Vec<String> = rows
            .iter()
            .map(|row| {
                let v = row.version.map(|v| v.to_string()).unwrap_or_else(|| "-".into());
                let suffix = match row.state {
                    "Overdue" => " overdue",
                    "SwitchedOff" => " off",
                    _ => "",
                };
                format!("{}@{v}{suffix}", row.code)
            })
            .collect();
        let mut results = String::new();
        for entry in &result.entries {
            results.push_str(entry.kind.as_str());
            if let Some(a) = &entry.allowed_by {
                results.push_str(&format!(" (allow-list entry {})", a.id));
            }
            results.push_str(": ");
            let cells: Vec<String> = entry
                .cells
                .iter()
                .map(|cell| {
                    if !cell.checked {
                        return format!("{} NotChecked", cell.list);
                    }
                    let allowed = if cell.allowed { " allowed" } else { "" };
                    let best = match &cell.best {
                        Some(b) if b.percent >= self.options().best_candidate_min_percent => format!(" [{}]", b.subject.external_id),
                        _ => String::new(),
                    };
                    format!("{} {}{allowed} {}%{best}", cell.list, cell.outcome.map(|o| o.as_str()).unwrap_or_default(), cell.percent.unwrap_or_default())
                })
                .collect();
            results.push_str(&cells.join("; "));
            results.push_str(" | ");
        }
        let message = format!(
            "Screening {reference}: {}; matcher {}; lists {}; allow-list {}; {}",
            result.outcome.as_str(),
            result.matcher_version,
            versions.join(", "),
            result.allow_list_version,
            results.trim_end_matches([' ', '|'])
        );
        self.logger.info(&message, &[("Reference", reference.into())]);
    }

    /// The summary line, worded precisely: never "not sanctioned" or "cleared", always which lists, as of when.
    pub fn summary(&self, result: &ScreeningResult, lists: &[Arc<LoadedList>], now: Time) -> String {
        let as_of = |t: Option<Time>| t.map(|t| t.format("%-d %b %Y %H:%M UTC").to_string()).unwrap_or_else(|| "unknown".into());
        let mut checked = Vec::new();
        let mut not_checked = Vec::new();
        let mut switched_off = Vec::new();
        let by_code: HashMap<&str, &LoadedList> = lists.iter().map(|l| (l.code.as_str(), l.as_ref())).collect();
        for list in lists {
            if list.is_searched() {
                checked.push(format!("{}, as of {}", list.name, as_of(list.last_checked_at)));
            }
            if list.switched_off.is_none() && !list.is_searched() {
                not_checked.push(list.name.clone());
            }
            if let Some(off) = &list.switched_off {
                switched_off.push(format!("{} (switched off by an administrator: {})", list.name, off.reason.trim_end_matches('.')));
            }
        }
        // One sentence per allowed age, naming each list that couldn't be updated in it, in order of first appearance.
        let mut ages: Vec<i32> = Vec::new();
        let mut overdue: HashMap<i32, Vec<String>> = HashMap::new();
        for list in lists {
            if list.is_overdue(now) {
                let age = list.max_age_hours.unwrap_or_default();
                if !ages.contains(&age) {
                    ages.push(age);
                }
                overdue.entry(age).or_default().push(format!("{}, as of {}", list.name, as_of(list.last_checked_at)));
            }
        }
        let mut gap = String::new();
        if !not_checked.is_empty() {
            gap += &format!(" Not checked: {}.", not_checked.join(", "));
        }
        if !switched_off.is_empty() {
            gap += &format!(" Not searched: {}.", switched_off.join("; "));
        }
        for age in &ages {
            gap += &format!(" Warning: couldn't be updated within {age} hours, so searched as last downloaded: {}.", overdue[age].join("; "));
        }

        let mut hits: Vec<String> = Vec::new();
        let mut allowed_on: Vec<String> = Vec::new();
        for entry in &result.entries {
            for cell in &entry.cells {
                let name = by_code.get(cell.list.as_str()).map(|l| l.name.clone()).unwrap_or_default();
                if matches!(cell.outcome, Some(Outcome::Match) | Some(Outcome::Review)) && !cell.allowed && !hits.contains(&name) {
                    hits.push(name.clone());
                }
                if cell.allowed && !allowed_on.contains(&name) {
                    allowed_on.push(name);
                }
            }
        }
        let mut allowed = String::new();
        if !allowed_on.is_empty() {
            let reasons: Vec<String> = result
                .entries
                .iter()
                .filter_map(|e| e.allowed_by.as_ref().filter(|_| e.cells.iter().any(|c| c.allowed)).map(|a| format!("{}: {}", a.name, a.reason)))
                .collect();
            allowed = format!(" A possible match on {} is allowed by the allow-list ({}); a person can still review it.", allowed_on.join(", "), reasons.join("; "));
        }

        match result.outcome {
            OverallOutcome::Match => format!("Possible match on {}. A person must review it before anything else happens.{gap}{allowed}", hits.join(", ")),
            OverallOutcome::Review => format!("Review needed: a similar listed party on {}.{gap}{allowed}", hits.join(", ")),
            OverallOutcome::Incomplete if checked.is_empty() => format!("Incomplete: no list could be checked.{gap}"),
            OverallOutcome::Incomplete => format!("Incomplete. No match found in: {}.{gap}{allowed}", checked.join("; ")),
            OverallOutcome::NoMatch if !allowed.is_empty() => {
                format!("No match found in: {}, apart from what the allow-list allows.{gap}{allowed}", checked.join("; "))
            }
            OverallOutcome::NoMatch => format!("No match found in: {}.{gap}", checked.join("; ")),
        }
    }
}
