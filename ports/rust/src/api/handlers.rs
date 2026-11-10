//! GET /api/v1/sources and its history, GET /api/v1/examples, POST /api/v1/lists/{code}/browse.

use std::sync::Arc;

use serde::Serialize;
use serde_json::Value;
use unicode_general_category::GeneralCategory;
use unicode_normalization::UnicodeNormalization;

use super::catalog::LoadedList;
use super::screening::{kind_name, list_response, utf16_len, Errors, JsonTime, ListResponse};
use crate::lists::catalogue::{find_source, optional, ListSource, SOURCES};
use crate::matching::text::{category, is_letter_or_digit, lower_invariant};
use crate::matching::{ListIndex, ListedSubject, SubjectKind};
use crate::store::queries::{HistoryEntry, Time};

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HeldUpdateResponse {
    pub import_id: i32,
    pub record_count: i32,
    pub downloaded_at: JsonTime,
    pub reason: Option<String>,
    pub added: Option<i32>,
    pub removed: Option<i32>,
    pub changed: Option<i32>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoryEntryResponse {
    pub import_id: i32,
    pub result: String,
    pub started_at: JsonTime,
    pub completed_at: Option<JsonTime>,
    pub record_count: i32,
    pub note: Option<String>,
    pub reviewed: bool,
    pub added: Option<i32>,
    pub removed: Option<i32>,
    pub changed: Option<i32>,
}

pub fn history_entry(e: &HistoryEntry) -> HistoryEntryResponse {
    let result = if e.active {
        "Active".to_string()
    } else if e.status == "Completed" {
        "Replaced".to_string()
    } else if e.status == "PendingReview" {
        "HeldForReview".to_string()
    } else {
        e.status.clone()
    };
    let note = if e.status == "Failed" { Some("The download or the parse failed.".to_string()) } else { e.message.clone() };
    HistoryEntryResponse {
        import_id: e.id,
        result,
        started_at: JsonTime(e.started_at),
        completed_at: e.completed_at.map(JsonTime),
        record_count: e.record_count,
        note,
        reviewed: e.reviewed_at.is_some(),
        added: e.added,
        removed: e.removed,
        changed: e.changed,
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceResponse {
    pub code: String,
    pub name: String,
    pub state: &'static str,
    pub status: &'static str,
    pub reason: Option<String>,
    pub record_count: Option<i32>,
    pub version: Option<i32>,
    pub last_checked_at: Option<JsonTime>,
    pub updated_at: Option<JsonTime>,
    pub max_age_hours: Option<i32>,
    pub homepage: Option<String>,
    pub file_url: Option<String>,
    pub terms_url: Option<String>,
    pub held: Option<HeldUpdateResponse>,
    pub history: Vec<HistoryEntryResponse>,
    pub history_count: usize,
}

pub fn as_of(t: Time) -> String {
    t.format("%-d %b %Y %H:%M UTC").to_string()
}

pub fn source_response(list: &LoadedList, source: Option<&ListSource>, now: Time) -> SourceResponse {
    let state = list.state(now);
    let reason = if state == "Current" {
        None
    } else if let Some(off) = &list.switched_off {
        Some(format!(
            "Switched off by an administrator on {}: {} It isn't searched until it's switched on again, and it's still updated daily.",
            as_of(off.at),
            off.reason
        ))
    } else if list.active_import_id.is_none() {
        Some("No completed import yet.".to_string())
    } else if list.index.is_none() {
        Some("Loading, or its last load failed. Its cells say Not checked until it loads.".to_string())
    } else {
        Some(format!(
            "Couldn't be updated within {} hours. It's still searched as last downloaded, and every result warns about it until a check succeeds.",
            list.max_age_hours.unwrap_or_default()
        ))
    };
    SourceResponse {
        code: list.code.clone(),
        name: list.name.clone(),
        state,
        status: source.map(|s| s.status.as_str()).unwrap_or("Manual"),
        reason,
        record_count: Some(list.record_count),
        version: if list.index.is_some() { list.active_import_id } else { None },
        last_checked_at: list.last_checked_at.map(JsonTime),
        updated_at: list.updated_at.map(JsonTime),
        max_age_hours: list.max_age_hours,
        homepage: source.and_then(|s| optional(s.homepage)),
        file_url: source.and_then(|s| s.public_url()),
        terms_url: source.and_then(|s| optional(s.terms_url)),
        held: None,
        history: Vec::new(),
        history_count: 0,
    }
}

pub fn not_imported(source: &ListSource) -> SourceResponse {
    SourceResponse {
        code: source.code.to_string(),
        name: source.name.to_string(),
        state: "NotImported",
        status: source.status.as_str(),
        reason: Some(if source.note.is_empty() { "Not imported yet.".to_string() } else { source.note.to_string() }),
        record_count: None,
        version: None,
        last_checked_at: None,
        updated_at: None,
        max_age_hours: Some(source.max_age_hours),
        homepage: optional(source.homepage),
        file_url: None,
        terms_url: optional(source.terms_url),
        held: None,
        history: Vec::new(),
        history_count: 0,
    }
}

pub fn catalogue_rows(lists: &[Arc<LoadedList>]) -> impl Iterator<Item = &'static ListSource> + '_ {
    SOURCES.iter().filter(move |s| !lists.iter().any(|l| l.code.eq_ignore_ascii_case(s.code)))
}

pub fn source_of(code: &str) -> Option<&'static ListSource> {
    find_source(code)
}

// --- Examples (ExamplesController.cs) ---

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExampleResponse {
    pub label: &'static str,
    pub receiver_name: Option<String>,
    pub company_name: Option<String>,
    pub address: Option<String>,
    pub list: String,
}

pub fn examples(lists: &[Arc<LoadedList>]) -> Vec<ExampleResponse> {
    let mut entities: Vec<(&str, &Arc<ListedSubject>)> = Vec::new();
    for list in lists.iter().filter(|l| l.is_searched()) {
        for subject in list.index.as_ref().unwrap().subjects() {
            if subject.kind == SubjectKind::Entity {
                entities.push((list.code.as_str(), subject));
            }
        }
    }
    entities.sort_by(|a, b| a.1.external_id.cmp(&b.1.external_id));
    let is_latin = |v: &str| v.chars().all(|c| (c as u32) < 0x250);
    let has_cyrillic = |v: &str| v.chars().any(|c| ('Ѐ'..='ӿ').contains(&c));
    let word_count = |v: &str| v.split(' ').filter(|w| !w.is_empty()).count();
    let good_address = |raw: &str| {
        let n = utf16_len(raw);
        n > 5 && n < 80
    };

    let mut examples = Vec::new();
    let mut latin: Option<&Arc<ListedSubject>> = None;
    let mut with_address: Option<&Arc<ListedSubject>> = None;
    for (list, subject) in &entities {
        let n = word_count(&subject.primary_name);
        if is_latin(&subject.primary_name) && (2..=5).contains(&n) {
            latin = Some(subject);
            examples.push(ExampleResponse {
                label: "A listed company",
                receiver_name: None,
                company_name: optional(&subject.primary_name),
                address: None,
                list: list.to_string(),
            });
            break;
        }
    }
    for (list, subject) in &entities {
        if let Some(name) = subject.names.iter().find(|n| has_cyrillic(&n.full_name) && word_count(&n.full_name) <= 6) {
            examples.push(ExampleResponse {
                label: "In Cyrillic",
                receiver_name: None,
                company_name: optional(&name.full_name),
                address: None,
                list: list.to_string(),
            });
            break;
        }
    }
    let same = |a: Option<&Arc<ListedSubject>>, b: &Arc<ListedSubject>| a.is_some_and(|a| Arc::ptr_eq(a, b));
    for (list, subject) in &entities {
        if !is_latin(&subject.primary_name) || same(latin, subject) {
            continue;
        }
        if let Some(address) = subject.addresses.iter().find(|a| good_address(&a.raw)) {
            with_address = Some(subject);
            examples.push(ExampleResponse {
                label: "With an address",
                receiver_name: None,
                company_name: optional(&subject.primary_name),
                address: optional(&address.raw),
                list: list.to_string(),
            });
            break;
        }
    }
    for (list, subject) in &entities {
        if !is_latin(&subject.primary_name) || same(latin, subject) || same(with_address, subject) {
            continue;
        }
        let words: Vec<String> = subject.primary_name.split(' ').map(str::to_string).collect();
        if words.iter().any(|w| utf16_len(w) >= 8) {
            examples.push(ExampleResponse {
                label: "With a typo",
                receiver_name: None,
                company_name: Some(with_typo(words)),
                address: None,
                list: list.to_string(),
            });
            break;
        }
    }
    examples
}

/// Drops one letter from the longest word (after its first letter): a near miss.
fn with_typo(mut words: Vec<String>) -> String {
    let mut longest = 0;
    for (i, word) in words.iter().enumerate() {
        if utf16_len(word) > utf16_len(&words[longest]) {
            longest = i;
        }
    }
    let first = words.iter().position(|w| w == &words[longest]).unwrap_or(longest);
    let chars: Vec<char> = words[first].chars().collect();
    let cut = chars.len() / 2;
    words[first] = chars[..cut].iter().chain(&chars[cut + 1..]).collect();
    words.join(" ")
}

// --- Browse (BrowseController.cs) ---

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowseName {
    pub name: String,
    pub is_weak: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowseAddress {
    pub raw: String,
    pub country_code: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BrowseEntry {
    pub external_id: String,
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub primary_name: String,
    pub names: Vec<BrowseName>,
    pub addresses: Vec<BrowseAddress>,
    pub programme: Option<String>,
    pub source_url: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct BrowseResponse {
    pub list: ListResponse,
    pub total: usize,
    pub offset: i64,
    pub limit: i64,
    pub entries: Vec<BrowseEntry>,
}

pub struct BrowseRequest {
    pub query: Option<String>,
    pub kind: Option<String>,
    pub offset: i64,
    pub limit: i64,
}

impl BrowseRequest {
    /// One JSON object, fields matched ignoring case; offset and limit integers. None when it isn't one.
    pub fn parse(text: &[u8]) -> Option<BrowseRequest> {
        let value: Value = serde_json::from_slice(text).ok()?;
        let object = value.as_object()?;
        let mut request = BrowseRequest { query: None, kind: None, offset: 0, limit: 0 };
        for (key, value) in object {
            match key.to_ascii_lowercase().as_str() {
                "query" | "type" => {
                    let text = match value {
                        Value::Null => None,
                        Value::String(s) => Some(s.clone()),
                        _ => return None,
                    };
                    if key.eq_ignore_ascii_case("query") {
                        request.query = text;
                    } else {
                        request.kind = text;
                    }
                }
                "offset" | "limit" => {
                    let number = match value {
                        Value::Null => 0,
                        Value::Number(n) => i32::try_from(n.as_i64()?).ok()?.into(),
                        _ => return None,
                    };
                    if key.eq_ignore_ascii_case("offset") {
                        request.offset = number;
                    } else {
                        request.limit = number;
                    }
                }
                _ => {}
            }
        }
        Some(request)
    }

    /// What's wrong with it, by field, and the kind asked for.
    pub fn validate(&self) -> (Errors, Option<SubjectKind>) {
        let mut errors = Errors::new();
        if self.query.as_deref().is_some_and(|q| utf16_len(q) > 100) {
            errors.insert("Query".into(), vec!["At most 100 characters.".into()]);
        }
        if self.offset < 0 {
            errors.insert("Offset".into(), vec!["Zero or more.".into()]);
        }
        if self.limit < 1 || self.limit > 100 {
            errors.insert("Limit".into(), vec!["From 1 to 100.".into()]);
        }
        let mut kind = None;
        if let Some(t) = self.kind.as_deref().filter(|t| !t.is_empty()) {
            kind = [SubjectKind::Individual, SubjectKind::Entity, SubjectKind::Unknown]
                .into_iter()
                .find(|k| kind_name(*k).eq_ignore_ascii_case(t));
            if kind.is_none() {
                errors.insert("Type".into(), vec!["Individual, Entity or Unknown.".into()]);
            }
        }
        (errors, kind)
    }
}

pub struct Browsable {
    subject: Arc<ListedSubject>,
    folded: Vec<String>,
}

pub fn browse(list: &LoadedList, index: &ListIndex, request: &BrowseRequest, kind: Option<SubjectKind>, now: Time) -> BrowseResponse {
    let entries = index.memo("browse", || build_name_order(index));
    let id = request.query.as_deref().unwrap_or_default().trim_matches(char::is_whitespace);
    let query = fold(id);
    let matched: Vec<&Arc<ListedSubject>> = entries
        .iter()
        .filter(|e| kind.is_none_or(|k| e.subject.kind == k))
        .filter(|e| {
            query.is_empty() || e.subject.external_id.to_lowercase() == id.to_lowercase() || e.folded.iter().any(|n| n.contains(&query))
        })
        .map(|e| &e.subject)
        .collect();
    let page = matched
        .iter()
        .skip(request.offset as usize)
        .take(request.limit as usize)
        .map(|subject| BrowseEntry {
            external_id: subject.external_id.clone(),
            kind: kind_name(subject.kind),
            primary_name: subject.primary_name.clone(),
            names: subject.names.iter().map(|n| BrowseName { name: n.full_name.clone(), is_weak: n.is_weak }).collect(),
            addresses: subject
                .addresses
                .iter()
                .map(|a| BrowseAddress { raw: a.raw.clone(), country_code: optional(&a.country_code) })
                .collect(),
            programme: subject.programme.clone(),
            source_url: subject.source_url.clone(),
        })
        .collect();
    BrowseResponse { list: list_response(list, now), total: matched.len(), offset: request.offset, limit: request.limit, entries: page }
}

fn build_name_order(index: &ListIndex) -> Vec<Browsable> {
    let mut entries: Vec<(String, Browsable)> = index
        .subjects()
        .iter()
        .map(|subject| {
            (
                sort_key(&subject.primary_name),
                Browsable { subject: subject.clone(), folded: subject.names.iter().map(|n| fold(&n.full_name)).collect() },
            )
        })
        .collect();
    entries.sort_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.subject.external_id.cmp(&b.1.subject.external_id)));
    entries.into_iter().map(|(_, e)| e).collect()
}

fn sort_key(name: &str) -> String {
    let folded = fold(name);
    folded.chars().skip_while(|&c| !is_letter_or_digit(c)).collect()
}

/// The browse filter's fold: lower case, no diacritics, spaces collapsed. Cyrillic stays Cyrillic.
pub fn fold(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut last_space = false;
    for c in text.nfd() {
        if category(c) == GeneralCategory::NonspacingMark {
            continue;
        }
        if is_go_space(c) {
            if !out.is_empty() && !last_space {
                out.push(' ');
                last_space = true;
            }
            continue;
        }
        out.push(lower_invariant(c));
        last_space = false;
    }
    out.trim_end_matches(is_go_space).to_string()
}

/// Go's unicode.IsSpace, which the Go port's fold uses: the Latin-1 spaces and the Z categories' White_Space.
fn is_go_space(c: char) -> bool {
    c.is_whitespace()
}
