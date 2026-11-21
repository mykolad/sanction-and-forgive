//! Lists an administrator uploads (FeedStatus::Upload; owner's decision, 2026-11-21; the Rust port first): the
//! publisher's file, downloaded in a browser, uploaded on the admin page, parsed by the list's parser and always held for
//! review; and what a held update changes against the list it would replace, entry by entry, before anyone confirms it.
//! Confirming and rejecting are the held updates' own (admin.rs). The file itself isn't kept: only its records, as with
//! every import.

use std::collections::{BTreeMap, BTreeSet, HashMap};

use axum::body::Bytes;
use axum::extract::{DefaultBodyLimit, FromRequest, Multipart, Path, Request, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::Response;
use axum::routing::{get, post};
use axum::Router;
use serde::Serialize;
use serde_json::json;

use super::admin::{path_id, refuse, server_error};
use super::server::{bad_request, json_response, not_found, one_error, Shared};
use crate::lists::catalogue::{find_source, max_upload_bytes, missing_upload_files, FeedStatus};
use crate::lists::parse::ParsedSubject;
use crate::lists::parsers::parser_for;
use crate::store::imports::{to_stored, Imports};
use crate::store::queries::{self, Subject};

/// The most any upload may send, all its files together (each list's own limit, catalogue::max_upload_bytes, is lower
/// or equal). Each file is parsed as it arrives and dropped, so only one is held at a time.
pub const MAX_UPLOAD_BYTES: usize = 256 << 20;

/// One upload at a time per replica: each holds a file and its parsed entries in memory beside the lists in use.
static UPLOADING: tokio::sync::Semaphore = tokio::sync::Semaphore::const_new(1);

/// At most this many entries of each kind (added, removed, changed) in a review; the counts are always whole.
pub const MAX_CHANGES_SHOWN: usize = 500;

pub fn routes() -> Router<Shared> {
    Router::new()
        .route("/api/v1/admin/lists/{code}/upload", post(upload).layer(DefaultBodyLimit::max(MAX_UPLOAD_BYTES)))
        .route("/api/v1/admin/imports/{id}/changes", get(changes))
}

/// POST the list's file as the request's body (any content type), or its files as multipart/form-data (each part a
/// file; Ukraine's register is three). The list must be one that's uploaded (its catalogue status Upload, with a
/// parser). A file the parser refuses is 400, its reason said, and so is an upload without one of the files its list is
/// published as; one equal to the list in use is "Unchanged" (and counts as a check: the list was current when
/// uploaded); otherwise the update is written in full and held for review, its import id returned.
async fn upload(State(s): State<Shared>, headers: HeaderMap, Path(code): Path<String>, request: Request) -> Response {
    let user = match refuse(&s, &headers, true).await {
        Ok(user) => user,
        Err(r) => return r,
    };
    let Some(source) = find_source(&code).filter(|source| source.status == FeedStatus::Upload) else { return not_found() };
    let Some(parser) = parser_for(source.code) else { return not_found() };
    // Its length said first (a browser's form and file uploads say it), and within the list's limit: nothing is read
    // before then.
    let length = headers.get(axum::http::header::CONTENT_LENGTH).and_then(|v| v.to_str().ok()).and_then(|v| v.parse::<u64>().ok());
    let Some(length) = length else {
        return json_response(StatusCode::LENGTH_REQUIRED, &json!({ "errors": one_error("File", "The upload must state its length.") }));
    };
    if length > max_upload_bytes(source.code) {
        let message = format!("{}'s upload is at most {} MB.", source.name, max_upload_bytes(source.code) >> 20);
        return json_response(StatusCode::PAYLOAD_TOO_LARGE, &json!({ "errors": one_error("File", &message) }));
    }
    let Ok(_uploading) = UPLOADING.try_acquire() else {
        let message = "Another upload is being read: try again when it's done.";
        return json_response(StatusCode::CONFLICT, &json!({ "errors": one_error("File", message) }));
    };
    let mut file_kinds: Vec<Bytes> = Vec::new();
    let parse = |file: Bytes| async move {
        match tokio::task::spawn_blocking(move || parser(&mut file.as_ref())).await {
            Ok(Ok(subjects)) => Ok(subjects),
            // The parser's reason names the file's shape (a missing column, a broken part), never an entry's text.
            Ok(Err(e)) => Err(bad_request(one_error("File", &format!("This isn't {}'s file as expected: {e}", source.name)))),
            Err(_) => Err(server_error()),
        }
    };
    let multipart = headers
        .get(axum::http::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .is_some_and(|v| v.to_ascii_lowercase().starts_with("multipart/form-data"));
    let mut subjects: Vec<ParsedSubject> = Vec::new();
    let mut files = 0;
    if multipart {
        let Ok(mut form) = Multipart::from_request(request, &()).await else {
            return bad_request(one_error("File", "The upload isn't a readable form."));
        };
        loop {
            let field = match form.next_field().await {
                Ok(Some(field)) => field,
                Ok(None) => break,
                Err(_) => return bad_request(one_error("File", "The upload was cut short or is too large.")),
            };
            let Ok(bytes) = field.bytes().await else {
                return bad_request(one_error("File", "The upload was cut short or is too large."));
            };
            if bytes.is_empty() {
                continue;
            }
            files += 1;
            // Its header row, kept to check every file of the list was sent.
            let end = bytes.iter().position(|b| *b == b'\n').map_or(bytes.len(), |i| i + 1).min(64 << 10);
            file_kinds.push(bytes.slice(..end));
            match parse(bytes).await {
                Ok(parsed) => subjects.extend(parsed),
                Err(r) => return r,
            }
        }
    } else {
        let Ok(bytes) = Bytes::from_request(request, &()).await else {
            return bad_request(one_error("File", "The upload was cut short or is too large."));
        };
        if !bytes.is_empty() {
            files = 1;
            let end = bytes.iter().position(|b| *b == b'\n').map_or(bytes.len(), |i| i + 1).min(64 << 10);
            file_kinds.push(bytes.slice(..end));
            match parse(bytes).await {
                Ok(parsed) => subjects = parsed,
                Err(r) => return r,
            }
        }
    }
    if files == 0 {
        return bad_request(one_error("File", "Choose the list's file."));
    }
    let headers_sent: Vec<&[u8]> = file_kinds.iter().map(|b| b.as_ref()).collect();
    let missing = missing_upload_files(source.code, &headers_sent);
    if !missing.is_empty() {
        let message = format!("Upload all of {}'s files together; missing: {}.", source.name, missing.join(", "));
        return bad_request(one_error("File", &message));
    }
    if subjects.is_empty() {
        return bad_request(one_error("File", &format!("The file has no entries: {} is never emptied by an upload.", source.name)));
    }
    let imports = Imports { db: s.db.clone() };
    let outcome = async {
        let mut record = imports.ensure_source(source.code, source.name, Some(source.homepage), Some(source.max_age_hours)).await?;
        imports.upload(&mut record, to_stored(subjects)).await
    }
    .await;
    let outcome = match outcome {
        Ok(outcome) => outcome,
        Err(e) => {
            s.loader.logger.error("Uploading a list failed", &[("code", source.code.into()), ("error", e.to_string().into())]);
            return server_error();
        }
    };
    // Who uploaded is logged, never shown publicly.
    s.loader.logger.info(
        &format!("List {} uploaded by {}: {} ({} records)", source.code, user.name, outcome.result.as_str(), outcome.record_count),
        &[],
    );
    let reloaded = s.loader.refresh().await.is_ok();
    json_response(
        StatusCode::OK,
        &json!({
            "code": source.code,
            "result": outcome.result.as_str(),
            "importId": outcome.import_id,
            "recordCount": outcome.record_count,
            "previousCount": outcome.previous_count,
            "message": outcome.message,
            "reloaded": reloaded,
        }),
    )
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct EntryChange {
    external_id: String,
    name: String,
    #[serde(rename = "type")]
    kind: &'static str,
    /// What changed, field by field: what the update adds and what it drops (empty for an entry added or removed).
    #[serde(skip_serializing_if = "Vec::is_empty")]
    fields: Vec<FieldChange>,
}

#[derive(Serialize, PartialEq, Debug)]
#[serde(rename_all = "camelCase")]
struct FieldChange {
    field: &'static str,
    added: Vec<String>,
    removed: Vec<String>,
}

#[derive(Serialize)]
struct ChangeGroup {
    count: usize,
    entries: Vec<EntryChange>,
}

fn kind_name(kind: u8) -> &'static str {
    match kind {
        0 => "Individual",
        1 => "Entity",
        3 => "Vessel",
        _ => "Unknown",
    }
}

/// An entry's fields as compared, each a set of values: what a person reviewing an update reads.
fn fields(s: &Subject) -> Vec<(&'static str, BTreeSet<String>)> {
    let one = |v: Option<&str>| v.filter(|v| !v.is_empty()).map(str::to_string).into_iter().collect::<BTreeSet<String>>();
    vec![
        ("Type", one(Some(kind_name(s.kind)))),
        ("Primary name", one(Some(&s.primary_name))),
        ("Names", s.names.iter().map(|n| if n.is_weak { format!("{} (weak)", n.full_name) } else { n.full_name.clone() }).collect()),
        ("Addresses", s.addresses.iter().map(|a| a.country_code.as_ref().map_or(a.raw.clone(), |c| format!("{} ({c})", a.raw))).collect()),
        (
            "Identifiers",
            s.identifiers
                .iter()
                .map(|i| i.country.as_ref().map_or(format!("{} {}", i.kind, i.value), |c| format!("{} {} ({c})", i.kind, i.value)))
                .collect(),
        ),
        ("Dates of birth", s.birth_dates.iter().cloned().collect()),
        ("Programmes", one(s.programmes.as_deref())),
        ("Listed on", one(s.listed_on.map(|d| d.format("%Y-%m-%d").to_string()).as_deref())),
        ("Remarks", one(s.remarks.as_deref())),
        ("Official record", one(s.source_url.as_deref())),
    ]
}

/// What changed between an entry before and after, field by field.
fn compare(before: &Subject, after: &Subject) -> Vec<FieldChange> {
    fields(before)
        .into_iter()
        .zip(fields(after))
        .filter(|((_, b), (_, a))| a != b)
        .map(|((field, b), (_, a))| FieldChange {
            field,
            added: a.difference(&b).cloned().collect(),
            removed: b.difference(&a).cloned().collect(),
        })
        .collect()
}

fn entry(s: &Subject, fields: Vec<FieldChange>) -> EntryChange {
    EntryChange { external_id: s.external_id.clone(), name: s.primary_name.clone(), kind: kind_name(s.kind), fields }
}

/// The changes a held update makes to the list it was compared with (its BasedOnImportId: the one in use when it was
/// written; none for a list's first upload, where everything is added), by external id, in id order, at most
/// MAX_CHANGES_SHOWN of each kind. Admin only; 404 for an import that isn't held or has no records.
async fn changes(State(s): State<Shared>, headers: HeaderMap, Path(id): Path<String>) -> Response {
    if let Err(r) = refuse(&s, &headers, false).await {
        return r;
    }
    let Some(id) = path_id(&id) else { return not_found() };
    let read = async {
        let mut conn = s.db.connect().await?;
        queries::held_changes(&mut conn, id).await
    }
    .await;
    let (held, before, after) = match read {
        Ok(Some(found)) => found,
        Ok(None) => return not_found(),
        Err(e) => {
            s.loader.logger.error("Reading a held update's changes failed", &[("importId", id.into()), ("error", e.to_string().into())]);
            return server_error();
        }
    };
    let response = diff(&before, &after);
    json_response(
        StatusCode::OK,
        &json!({
            "importId": id,
            "code": held.code,
            "comparedWith": held.based_on,
            "added": response.0,
            "removed": response.1,
            "changed": response.2,
            "unchanged": response.3,
            "shownAtMost": MAX_CHANGES_SHOWN,
        }),
    )
}

/// Added, removed and changed entries, and how many are unchanged.
fn diff(before: &[Subject], after: &[Subject]) -> (ChangeGroup, ChangeGroup, ChangeGroup, usize) {
    fn by_id(list: &[Subject]) -> BTreeMap<&str, &Subject> {
        list.iter().map(|s| (s.external_id.as_str(), s)).collect()
    }
    let (old, new) = (by_id(before), by_id(after));
    let group = |entries: Vec<EntryChange>| {
        let count = entries.len();
        ChangeGroup { count, entries: entries.into_iter().take(MAX_CHANGES_SHOWN).collect() }
    };
    let added: Vec<EntryChange> = new.iter().filter(|(k, _)| !old.contains_key(*k)).map(|(_, s)| entry(s, Vec::new())).collect();
    let removed: Vec<EntryChange> = old.iter().filter(|(k, _)| !new.contains_key(*k)).map(|(_, s)| entry(s, Vec::new())).collect();
    let mut changed = Vec::new();
    let mut unchanged = 0;
    let olds: HashMap<&str, &Subject> = old.into_iter().collect();
    for (k, s) in &new {
        if let Some(before) = olds.get(k) {
            let fields = compare(before, s);
            if fields.is_empty() {
                unchanged += 1;
            } else {
                changed.push(entry(s, fields));
            }
        }
    }
    (group(added), group(removed), group(changed), unchanged)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::store::queries::{SubjectAddress, SubjectName};

    fn subject(id: &str, name: &str, aliases: &[&str], address: Option<&str>) -> Subject {
        Subject {
            id: 0,
            external_id: id.to_string(),
            kind: 0,
            primary_name: name.to_string(),
            programmes: None,
            listed_on: None,
            remarks: None,
            source_url: None,
            names: std::iter::once(name)
                .chain(aliases.iter().copied())
                .map(|n| SubjectName { full_name: n.to_string(), is_weak: false, script: 0 })
                .collect(),
            addresses: address.map(|a| SubjectAddress { raw: a.to_string(), country_code: None }).into_iter().collect(),
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        }
    }

    #[test]
    fn a_held_update_is_compared_entry_by_entry_field_by_field() {
        let before = [
            subject("1", "Ivan Petrov", &["I. Petrov"], Some("Moscow")),
            subject("2", "Gone Away", &[], None),
            subject("3", "Same", &[], None),
        ];
        let after = [
            subject("1", "Ivan Petrov", &["Vanya Petrov"], Some("Moscow")),
            subject("3", "Same", &[], None),
            subject("4", "New One", &[], None),
        ];
        let (added, removed, changed, unchanged) = diff(&before, &after);
        assert_eq!((added.count, removed.count, changed.count, unchanged), (1, 1, 1, 1));
        assert_eq!((added.entries[0].external_id.as_str(), removed.entries[0].external_id.as_str()), ("4", "2"));
        assert_eq!(
            changed.entries[0].fields,
            [FieldChange { field: "Names", added: vec!["Vanya Petrov".into()], removed: vec!["I. Petrov".into()] }]
        );
    }
}
