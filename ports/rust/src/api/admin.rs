//! /api/v1/admin (AdminController.cs): who is signed in, reviewing held updates, switching lists and the allow-list.
//! Everything but me needs the Lists.Admin role, and every change the site's own request header.

use std::collections::HashSet;

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use serde::Serialize;
use serde_json::{json, Value};

use super::screening::{utf16_len, JsonTime};
use super::server::{bad_request, from_this_site, json_response, not_found, one_error, read_principal, AdminUser, Shared, REQUEST_HEADER};
use crate::matching::{AllowList, AllowListEntry, Outcome, ScreenedList};
use crate::store::imports::{Imports, ReviewResult};
use crate::store::queries::{self, SwitchResult};

const SIGN_IN_PATH: &str = "/.auth/login/aad?post_login_redirect_uri=/admin";
const SIGN_OUT_PATH: &str = "/.auth/logout?post_logout_redirect_uri=/admin";
const MAX_POSSIBLE_MATCHES: usize = 100;

pub fn routes() -> Router<Shared> {
    Router::new()
        .route("/api/v1/admin/me", get(me))
        .route("/api/v1/admin/allow-list", get(allow_list).post(add_to_allow_list))
        .route("/api/v1/admin/imports/{id}/confirm", post(|s, h, p| review(s, h, p, true)))
        .route("/api/v1/admin/imports/{id}/reject", post(|s, h, p| review(s, h, p, false)))
        .route("/api/v1/admin/allow-list/{id}/remove", post(remove_from_allow_list))
        .route("/api/v1/admin/lists/{code}/switch-off", post(|s, h, p, b| switch_list(s, h, p, b, true)))
        .route("/api/v1/admin/lists/{code}/switch-on", post(|s, h, p, b| switch_list(s, h, p, b, false)))
}

async fn me(State(s): State<Shared>, headers: HeaderMap) -> Response {
    let enabled = s.config.sign_in_enabled;
    let user = read_principal(enabled, &headers);
    json_response(
        StatusCode::OK,
        &json!({
            "enabled": enabled,
            "signedIn": user.is_some(),
            "name": user.as_ref().map(|u| u.name.clone()),
            "isAdmin": user.as_ref().is_some_and(|u| u.is_admin),
            "signInUrl": enabled.then_some(SIGN_IN_PATH),
            "signOutUrl": user.as_ref().map(|_| SIGN_OUT_PATH),
        }),
    )
}

/// Why a request may not go on: sign-in off (404), not signed in (401), without the role (403), or a change without
/// the site's own header (400). Otherwise the user.
#[allow(clippy::result_large_err)] // a response, made at most once a request
fn refuse(s: &Shared, headers: &HeaderMap, change: bool) -> Result<AdminUser, Response> {
    let user = read_principal(s.config.sign_in_enabled, headers);
    if !s.config.sign_in_enabled {
        return Err(not_found());
    }
    let Some(user) = user else { return Err(StatusCode::UNAUTHORIZED.into_response()) };
    if !user.is_admin {
        return Err(StatusCode::FORBIDDEN.into_response());
    }
    if change && !from_this_site(headers) {
        return Err(bad_request(one_error(REQUEST_HEADER, "Send this request from the admin page.")));
    }
    Ok(user)
}

fn server_error() -> Response {
    StatusCode::INTERNAL_SERVER_ERROR.into_response()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct AllowListEntryResponse {
    id: i32,
    name: String,
    reason: String,
    added_by: String,
    added_at: JsonTime,
    removed_by: Option<String>,
    removed_at: Option<JsonTime>,
}

async fn allow_list(State(s): State<Shared>, headers: HeaderMap) -> Response {
    if let Err(r) = refuse(&s, &headers, false) {
        return r;
    }
    let records = match s.db.connect().await {
        Ok(mut conn) => queries::allow_list_history(&mut conn).await,
        Err(e) => Err(e),
    };
    let Ok(records) = records else { return server_error() };
    let entries: Vec<AllowListEntryResponse> = records
        .into_iter()
        .map(|e| AllowListEntryResponse {
            id: e.id,
            name: e.name,
            reason: e.reason,
            added_by: e.added_by,
            added_at: JsonTime(e.added_at),
            removed_by: e.removed_by,
            removed_at: e.removed_at.map(JsonTime),
        })
        .collect();
    json_response(StatusCode::OK, &json!({ "entries": entries }))
}

/// The {id} route value as an int (the reference's {id:int} constraint): otherwise 404.
fn path_id(id: &str) -> Option<i32> {
    id.parse().ok()
}

async fn review(State(s): State<Shared>, headers: HeaderMap, Path(id): Path<String>, confirm: bool) -> Response {
    let user = match refuse(&s, &headers, true) {
        Ok(user) => user,
        Err(r) => return r,
    };
    let Some(id) = path_id(&id) else { return not_found() };
    let imports = Imports { db: s.db.clone() };
    let result = match imports.review(id, confirm, &user.name).await {
        Ok(result) => result,
        Err(e) => {
            s.loader.logger.error("Reviewing an import failed", &[("importId", id.into()), ("error", e.to_string().into())]);
            return server_error();
        }
    };
    s.loader.logger.info(&format!("Import {id} review: {} by {}", result.as_str(), user.name), &[]);
    match result {
        ReviewResult::Confirmed | ReviewResult::Rejected => {
            let reloaded = s.loader.refresh().await.is_ok();
            json_response(StatusCode::OK, &json!({ "importId": id, "result": result.as_str(), "reloaded": reloaded }))
        }
        ReviewResult::NotFound => not_found(),
        ReviewResult::NotPending => {
            json_response(StatusCode::CONFLICT, &json!({ "importId": id, "result": result.as_str(), "reloaded": false }))
        }
    }
}

/// A field of a JSON object, matched ignoring case (the last of duplicates wins).
fn field<'v>(object: &'v serde_json::Map<String, Value>, name: &str) -> Option<&'v Value> {
    object.iter().filter(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v).next_back()
}

async fn switch_list(State(s): State<Shared>, headers: HeaderMap, Path(code): Path<String>, body: Bytes, off: bool) -> Response {
    let user = match refuse(&s, &headers, true) {
        Ok(user) => user,
        Err(r) => return r,
    };
    let mut reason = None;
    if off {
        // A missing or unreadable reason is refused below.
        let value: Value = serde_json::from_slice(&body[..body.len().min(1 << 16)]).unwrap_or(Value::Null);
        let given = value.as_object().and_then(|o| field(o, "reason")).and_then(Value::as_str).unwrap_or_default();
        let trimmed = given.trim_matches(char::is_whitespace).to_string();
        if trimmed.is_empty() || utf16_len(&trimmed) > 500 {
            return bad_request(one_error("Reason", "1 to 500 characters: why it's switched off. Every result shows it."));
        }
        reason = Some(trimmed);
    }
    if code.len() > 32 {
        return not_found();
    }
    let now = chrono::Utc::now();
    let result = match s.db.connect().await {
        Ok(mut conn) => queries::switch(&mut conn, &code, off, reason.as_deref(), &user.name, now).await,
        Err(e) => Err(e),
    };
    let Ok(result) = result else { return server_error() };
    let (direction, answer, already) = if off { ("off", "SwitchedOff", "AlreadyOff") } else { ("on", "On", "AlreadyOn") };
    match result {
        SwitchResult::Switched => {
            s.loader.logger.info(&format!("List {code} switched {direction} by {}", user.name), &[]);
            let reloaded = s.loader.refresh().await.is_ok();
            json_response(StatusCode::OK, &json!({ "code": code, "result": answer, "reloaded": reloaded }))
        }
        SwitchResult::NotFound => not_found(),
        SwitchResult::Unchanged => json_response(StatusCode::CONFLICT, &json!({ "code": code, "result": already, "reloaded": false })),
    }
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct PossibleMatch {
    key: String,
    list: String,
    list_name: String,
    listed_name: String,
    alias: String,
    percent: i32,
    programme: Option<String>,
}

fn refusal(code: &str, message: String, possible: Vec<PossibleMatch>) -> Value {
    json!({ "code": code, "message": message, "possibleMatches": possible })
}

fn exists_refusal(id: i32) -> Value {
    refusal(
        "exists",
        format!("This name is already on the allow-list (entry {id}). To change its reason, remove it and add it again."),
        Vec::new(),
    )
}

async fn add_to_allow_list(State(s): State<Shared>, headers: HeaderMap, body: Bytes) -> Response {
    let user = match refuse(&s, &headers, true) {
        Ok(user) => user,
        Err(r) => return r,
    };
    let invalid = || bad_request(one_error("$", "The request body isn't a valid allow-list entry."));
    let Ok(Value::Object(object)) = serde_json::from_slice::<Value>(&body[..body.len().min(1 << 20)]) else { return invalid() };
    let text_field = |name: &str| -> Result<String, ()> {
        match field(&object, name) {
            None | Some(Value::Null) => Ok(String::new()),
            Some(Value::String(s)) => Ok(s.trim_matches(char::is_whitespace).to_string()),
            _ => Err(()),
        }
    };
    let (Ok(name), Ok(reason)) = (text_field("name"), text_field("reason")) else { return invalid() };
    let mut errors = super::screening::Errors::new();
    if name.is_empty() || utf16_len(&name) > 300 {
        errors.insert("Name".into(), vec!["1 to 300 characters: the name exactly as it will be entered.".into()]);
    }
    if reason.is_empty() || utf16_len(&reason) > 500 {
        errors.insert("Reason".into(), vec!["1 to 500 characters: why this party isn't the listed one.".into()]);
    }
    let mut confirmed: HashSet<String> = HashSet::new();
    match field(&object, "confirmed") {
        None | Some(Value::Null) => {}
        Some(Value::Array(keys)) => {
            let mut valid = keys.len() <= MAX_POSSIBLE_MATCHES;
            for key in keys {
                match key {
                    Value::String(k) if !k.is_empty() && utf16_len(k) <= 256 => {
                        confirmed.insert(k.clone());
                    }
                    Value::String(_) | Value::Null => valid = false,
                    _ => return invalid(),
                }
            }
            if !valid {
                errors
                    .insert("Confirmed".into(), vec!["At most 100 keys of 1 to 256 characters, as the possible matches gave them.".into()]);
            }
        }
        Some(_) => return invalid(),
    }
    if !errors.is_empty() {
        return bad_request(errors);
    }

    let gate = s.loader.gate().await;
    let (status, response, added) = check_and_store(&s, &name, &reason, &user.name, &confirmed).await;
    let response = match added {
        // Stored: reloaded at once, under the same gate.
        Some((id, _)) => {
            let reloaded = s.loader.load_allow_list_now().await;
            json!({ "id": id, "reloaded": reloaded })
        }
        None => response,
    };
    drop(gate);
    if let Some((id, keys)) = added {
        let confirmed_text = if keys.is_empty() { "none".to_string() } else { keys.join(", ") };
        s.loader.logger.info(&format!("Allow-list entry {id} added by {}; possible matches confirmed: {confirmed_text}", user.name), &[]);
    }
    json_response(status, &response)
}

/// Runs under the refresh gate, so the lists the name is checked against are the ones in use when it's stored
/// (AllowListAdmin.CheckAndStoreAsync).
async fn check_and_store(
    s: &Shared,
    name: &str,
    reason: &str,
    by: &str,
    confirmed: &HashSet<String>,
) -> (StatusCode, Value, Option<(i32, Vec<String>)>) {
    let catalog = &s.catalog;
    let lists = catalog.lists();
    let unavailable = !catalog.is_ready() || lists.is_empty() || lists.iter().any(|l| l.index.is_none());
    if unavailable {
        return (
            StatusCode::SERVICE_UNAVAILABLE,
            refusal(
                "unavailable",
                "A list can't be checked right now (loading, or not current), so the name can't be checked against it. Try again later."
                    .into(),
                Vec::new(),
            ),
            None,
        );
    }
    if let Some(existing) = catalog.allow_list().find(name) {
        return (StatusCode::CONFLICT, exists_refusal(existing.id), None);
    }
    let screened: Vec<ScreenedList> =
        lists.iter().map(|l| ScreenedList { code: l.code.clone(), name: l.name.clone(), index: l.index.clone() }).collect();
    let list_name = |code: &str| lists.iter().find(|l| l.code == code).map(|l| l.name.clone()).unwrap_or_default();
    let (service, owned_name) = (s.screenings.clone(), name.to_string());
    let hits = tokio::task::spawn_blocking(move || service.screener.name_hits(&owned_name, &screened).unwrap_or_default())
        .await
        .unwrap_or_default();
    let mut best_match: Option<&crate::matching::ListedHit> = None;
    for hit in &hits {
        if hit.matched.outcome == Outcome::Match && best_match.is_none_or(|m| hit.matched.percent > m.matched.percent) {
            best_match = Some(hit);
        }
    }
    if let Some(m) = best_match {
        return (
            StatusCode::CONFLICT,
            refusal(
                "listed",
                format!(
                    "This name is a match on {}: {}: a listed party can't be allow-listed.",
                    list_name(&m.list),
                    m.matched.subject.primary_name
                ),
                Vec::new(),
            ),
            None,
        );
    }
    let mut seen = HashSet::new();
    let mut possible: Vec<PossibleMatch> = Vec::new();
    for hit in &hits {
        let key = format!("{}/{}", hit.list, hit.matched.subject.external_id);
        if !seen.insert(key.clone()) {
            continue;
        }
        possible.push(PossibleMatch {
            key,
            list: hit.list.clone(),
            list_name: list_name(&hit.list),
            listed_name: hit.matched.subject.primary_name.clone(),
            alias: hit.matched.alias.clone(),
            percent: hit.matched.percent,
            programme: hit.matched.subject.programme.clone(),
        });
    }
    possible.sort_by(|p, q| q.percent.cmp(&p.percent).then_with(|| p.list.cmp(&q.list)).then_with(|| p.listed_name.cmp(&q.listed_name)));
    if possible.len() > MAX_POSSIBLE_MATCHES {
        let message = format!(
            "This name is a possible match for {} listed parties, more than the {MAX_POSSIBLE_MATCHES} an entry can be checked against: it's too generic to allow-list. Enter the full name, legal form included.",
            possible.len()
        );
        possible.truncate(MAX_POSSIBLE_MATCHES);
        return (StatusCode::CONFLICT, refusal("tooBroad", message, possible), None);
    }
    if possible.iter().any(|p| !confirmed.contains(&p.key)) {
        let message = if possible.len() == 1 {
            "This name is a possible match for a listed party. Once it's on the allow-list, that match is shown as allowed and no longer raises the result. Add it only if this name is not that party.".to_string()
        } else {
            format!(
                "This name is a possible match for {} listed parties. Once it's on the allow-list, these matches are shown as allowed and no longer raise the result. Add it only if this name is none of them.",
                possible.len()
            )
        };
        return (StatusCode::CONFLICT, refusal("confirm", message, possible), None);
    }
    let stored = match s.db.connect().await {
        Ok(mut conn) => {
            queries::allow_list_add(&mut conn, name, reason, by, chrono::Utc::now(), |in_force| {
                let entries: Vec<AllowListEntry> =
                    in_force.iter().map(|e| AllowListEntry { id: e.id, name: e.name.clone(), reason: e.reason.clone() }).collect();
                AllowList::build(&entries, "stored").find(name).map(|e| e.id)
            })
            .await
        }
        Err(e) => Err(e),
    };
    match stored {
        Err(e) => {
            s.loader.logger.error("Adding to the allow-list failed", &[("error", e.to_string().into())]);
            (StatusCode::INTERNAL_SERVER_ERROR, json!({}), None)
        }
        Ok((None, Some(existing))) => (StatusCode::CONFLICT, exists_refusal(existing), None),
        Ok((Some(id), _)) => (StatusCode::OK, Value::Null, Some((id, possible.into_iter().map(|p| p.key).collect()))),
        Ok((None, None)) => (StatusCode::INTERNAL_SERVER_ERROR, json!({}), None),
    }
}

async fn remove_from_allow_list(State(s): State<Shared>, headers: HeaderMap, Path(id): Path<String>) -> Response {
    let user = match refuse(&s, &headers, true) {
        Ok(user) => user,
        Err(r) => return r,
    };
    let Some(id) = path_id(&id) else { return not_found() };
    let removed = match s.db.connect().await {
        Ok(mut conn) => queries::allow_list_remove(&mut conn, id, &user.name, chrono::Utc::now()).await,
        Err(e) => Err(e),
    };
    match removed {
        Err(_) => server_error(),
        Ok(false) => not_found(),
        Ok(true) => {
            s.loader.logger.info(&format!("Allow-list entry {id} removed by {}", user.name), &[]);
            let reloaded = s.loader.reload_allow_list(Some(id)).await;
            json_response(StatusCode::OK, &json!({ "id": id, "reloaded": reloaded }))
        }
    }
}
