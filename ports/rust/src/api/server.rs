//! The API and the pages (docs/api.md): the same endpoints, responses and pages as the reference.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use axum::body::{Body, Bytes};
use axum::extract::{ConnectInfo, Path, Request, State};
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::Router;
use base64::Engine;
use futures_util::StreamExt;
use serde::Serialize;
use serde_json::{json, Value};
use tokio::sync::{mpsc, Semaphore};

use super::admin;
use super::catalog::{Catalog, Loader, HISTORY_SHOWN};
use super::handlers::{self, BrowseRequest, HeldUpdateResponse};
use super::screening::{
    list_response, stream_entry, utf16_len, validate, Errors, JsonTime, ListResponse, ScreeningRequestBody, ScreeningService,
    StreamEndLine, StreamErrorLine, StreamHeaderLine, StreamResultLine, DISCLAIMER,
};
use crate::lists::catalogue::{find_source, optional};
use crate::matching::data::PAGES;
use crate::store::db::Database;

/// The reference's OpenAPI document (the Go port keeps the one copy).
static OPEN_API: &[u8] = include_bytes!("../../../go/api/openapi.json");

/// The reference's configuration, by the same names.
#[derive(Debug, Clone)]
pub struct Config {
    pub version: String,
    pub revision: String,
    pub rate_limit_per_minute: u32,
    pub stream_max_records: usize,
    pub stream_max_seconds: u64,
    pub concurrent_streams: usize,
    pub sign_in_enabled: bool,
}

pub struct AppState {
    pub config: Config,
    pub catalog: Arc<Catalog>,
    pub screenings: Arc<ScreeningService>,
    pub loader: Arc<Loader>,
    pub db: Database,
    streams: Arc<Semaphore>,
    limiter: FixedWindow,
}

pub type Shared = Arc<AppState>;

impl AppState {
    pub fn new(config: Config, catalog: Arc<Catalog>, screenings: Arc<ScreeningService>, loader: Arc<Loader>, db: Database) -> Self {
        let streams = Arc::new(Semaphore::new(config.concurrent_streams.max(1)));
        let limiter = FixedWindow { limit: config.rate_limit_per_minute, window: Mutex::new((Instant::now(), HashMap::new())) };
        AppState { config, catalog, screenings, loader, db, streams, limiter }
    }
}

pub fn router(state: Shared) -> Router {
    Router::new()
        .route("/healthz", get(health))
        .route("/readyz", get(ready))
        .route("/openapi/v1.json", get(|| async { ([(header::CONTENT_TYPE, "application/json; charset=utf-8")], OPEN_API) }))
        .route("/api/v1/status", get(status))
        .route("/api/v1/sources", get(sources))
        .route("/api/v1/sources/{code}/history", get(history))
        .route("/api/v1/examples", get(examples))
        .route("/api/v1/lists/{code}/browse", post(browse))
        .route("/api/v1/screenings", post(screen))
        .route("/api/v1/screenings/stream", post(stream))
        .merge(admin::routes())
        .fallback(page)
        .with_state(state)
}

pub fn json_response<T: Serialize>(status: StatusCode, value: &T) -> Response {
    let mut body = serde_json::to_vec(value).unwrap_or_default();
    body.push(b'\n');
    (status, [(header::CONTENT_TYPE, "application/json; charset=utf-8")], body).into_response()
}

pub fn warming_up(state: &str) -> Response {
    json_response(StatusCode::SERVICE_UNAVAILABLE, &json!({ "state": state }))
}

pub fn bad_request(errors: Errors) -> Response {
    json_response(
        StatusCode::BAD_REQUEST,
        &json!({ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1", "title": "One or more validation errors occurred.", "status": 400, "errors": errors }),
    )
}

pub fn one_error(field: &str, message: &str) -> Errors {
    Errors::from([(field.to_string(), vec![message.to_string()])])
}

pub fn not_found() -> Response {
    StatusCode::NOT_FOUND.into_response()
}

#[derive(Serialize)]
struct HealthResponse<'a> {
    status: &'a str,
    version: &'a str,
    revision: Option<String>,
}

async fn health(State(s): State<Shared>) -> Response {
    json_response(StatusCode::OK, &HealthResponse { status: "ok", version: &s.config.version, revision: optional(&s.config.revision) })
}

async fn ready(State(s): State<Shared>) -> Response {
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    json_response(StatusCode::OK, &HealthResponse { status: "ready", version: &s.config.version, revision: optional(&s.config.revision) })
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StatusResponse<'a> {
    state: &'a str,
    version: &'a str,
    matcher_version: &'a str,
    lists: Vec<ListResponse>,
}

async fn status(State(s): State<Shared>) -> Response {
    let now = s.screenings.now();
    let lists = s.catalog.lists().iter().map(|l| list_response(l, now)).collect();
    let state = if s.catalog.is_ready() { "ready" } else { "warmingUp" };
    json_response(
        StatusCode::OK,
        &StatusResponse { state, version: &s.config.version, matcher_version: &s.screenings.screener.matcher_version, lists },
    )
}

async fn sources(State(s): State<Shared>) -> Response {
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    let now = s.screenings.now();
    let (history, held, lists) = (s.catalog.history(), s.catalog.held(), s.catalog.lists());
    let mut rows = Vec::new();
    for list in &lists {
        let mut response = handlers::source_response(list, find_source(&list.code), now);
        if let Some(h) = held.get(&list.code) {
            response.held = Some(HeldUpdateResponse {
                import_id: h.id,
                record_count: h.record_count,
                downloaded_at: JsonTime(h.started_at),
                reason: h.message.clone(),
                added: h.added,
                removed: h.removed,
                changed: h.changed,
            });
        }
        let entries = history.get(&list.code).map(Vec::as_slice).unwrap_or_default();
        response.history = entries.iter().take(HISTORY_SHOWN).map(handlers::history_entry).collect();
        response.history_count = entries.len();
        rows.push(response);
    }
    rows.extend(handlers::catalogue_rows(&lists).map(handlers::not_imported));
    json_response(StatusCode::OK, &json!({ "now": JsonTime(now), "lists": rows }))
}

async fn history(State(s): State<Shared>, Path(code): Path<String>) -> Response {
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    let Some(list) = s.catalog.find(&code) else { return not_found() };
    let entries: Vec<_> = s.catalog.history().get(&list.code).map(|e| e.iter().map(handlers::history_entry).collect()).unwrap_or_default();
    json_response(StatusCode::OK, &json!({ "code": list.code, "name": list.name, "history": entries }))
}

async fn examples(State(s): State<Shared>) -> Response {
    json_response(StatusCode::OK, &handlers::examples(&s.catalog.lists()))
}

async fn browse(
    State(s): State<Shared>,
    ConnectInfo(peer): ConnectInfo<SocketAddr>,
    Path(code): Path<String>,
    headers: HeaderMap,
    body: Bytes,
) -> Response {
    if let Some(r) = s.limited(&headers, peer) {
        return r;
    }
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    let Some(request) = BrowseRequest::parse(&body[..body.len().min(1 << 16)]) else {
        return bad_request(one_error("$", "The request body isn't a valid browse request."));
    };
    let (errors, kind) = request.validate();
    if !errors.is_empty() {
        return bad_request(errors);
    }
    let Some(list) = s.catalog.find(&code) else { return not_found() };
    let Some(index) = list.index.clone() else { return warming_up("listNotLoaded") };
    let now = s.screenings.now();
    let response = tokio::task::spawn_blocking(move || handlers::browse(&list, &index, &request, kind, now)).await.unwrap();
    json_response(StatusCode::OK, &response)
}

// --- Rate limit ---

/// A per-client fixed one-minute window (the reference's FixedWindowRateLimiter, no queue).
struct FixedWindow {
    limit: u32,
    window: Mutex<(Instant, HashMap<String, u32>)>,
}

impl FixedWindow {
    /// None if allowed, else the seconds until the window resets.
    fn allow(&self, key: &str) -> Option<u64> {
        if self.limit == 0 {
            return None;
        }
        let mut window = self.window.lock().unwrap();
        let now = Instant::now();
        if now.duration_since(window.0) >= Duration::from_secs(60) {
            *window = (now, HashMap::new());
        }
        let count = window.1.entry(key.to_string()).or_default();
        if *count >= self.limit {
            let left = Duration::from_secs(60).saturating_sub(now.duration_since(window.0));
            return Some(left.as_secs_f64().ceil().max(1.0) as u64);
        }
        *count += 1;
        None
    }
}

/// The X-Forwarded-For entry the ingress appended (forward limit 1: the last one), else the peer.
fn client_ip(headers: &HeaderMap, peer: SocketAddr) -> String {
    if let Some(last) = headers.get_all("x-forwarded-for").iter().last().and_then(|v| v.to_str().ok()) {
        let ip = last.rsplit(',').next().unwrap_or_default().trim();
        if !ip.is_empty() {
            return ip.to_string();
        }
    }
    peer.ip().to_string()
}

impl AppState {
    /// 429 with Retry-After when the client is over its per-minute quota.
    fn limited(&self, headers: &HeaderMap, peer: SocketAddr) -> Option<Response> {
        self.limiter
            .allow(&client_ip(headers, peer))
            .map(|wait| (StatusCode::TOO_MANY_REQUESTS, [(header::RETRY_AFTER, wait.to_string())]).into_response())
    }
}

// --- Screening ---

async fn screen(State(s): State<Shared>, ConnectInfo(peer): ConnectInfo<SocketAddr>, headers: HeaderMap, body: Bytes) -> Response {
    if let Some(r) = s.limited(&headers, peer) {
        return r;
    }
    let text = std::str::from_utf8(&body[..body.len().min(1 << 20)]).ok();
    // The parser's message can quote the input: say only that the body isn't valid.
    let Some(request) = text.and_then(ScreeningRequestBody::parse) else {
        return bad_request(one_error("$", "The request body isn't a valid screening request."));
    };
    let errors = validate(&request);
    if !errors.is_empty() {
        return bad_request(errors);
    }
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    let service = s.screenings.clone();
    let response = tokio::task::spawn_blocking(move || service.screen(&request)).await.unwrap();
    json_response(StatusCode::OK, &response)
}

const MAX_LINE_BYTES: usize = 4096;

enum ReadLine {
    Line(String),
    TooLong,
    /// The upload broke off.
    Failed,
}

/// Reads the body as lines (\n, a \r before it dropped), at most MAX_LINE_BYTES UTF-8 bytes each: a longer one comes
/// back marked too long, its rest skipped. A UTF-8 byte order mark at the start is skipped.
async fn read_lines(body: Body, out: mpsc::Sender<ReadLine>) {
    let mut stream = body.into_data_stream();
    let mut line: Vec<u8> = Vec::new();
    let mut too_long = false;
    let mut first = true;
    let send = |line: &mut Vec<u8>, too_long: &mut bool| {
        let item = if *too_long {
            ReadLine::TooLong
        } else {
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            if line.len() > MAX_LINE_BYTES {
                ReadLine::TooLong
            } else {
                ReadLine::Line(String::from_utf8_lossy(line).into_owned())
            }
        };
        line.clear();
        *too_long = false;
        item
    };
    loop {
        let chunk = match stream.next().await {
            Some(Ok(chunk)) => chunk,
            Some(Err(_)) => {
                let _ = out.send(ReadLine::Failed).await;
                return;
            }
            None => break,
        };
        let mut data: &[u8] = &chunk;
        if first {
            first = false;
            if data.starts_with(&[0xEF, 0xBB, 0xBF]) {
                data = &data[3..];
            }
        }
        while let Some(end) = data.iter().position(|&b| b == b'\n') {
            if !too_long {
                line.extend_from_slice(&data[..end]);
            }
            let item = send(&mut line, &mut too_long);
            if out.send(item).await.is_err() {
                return;
            }
            data = &data[end + 1..];
        }
        if !too_long {
            line.extend_from_slice(data);
            if line.len() > MAX_LINE_BYTES + 1 {
                too_long = true;
                line.clear();
            }
        }
    }
    if !line.is_empty() || too_long {
        let item = send(&mut line, &mut too_long);
        let _ = out.send(item).await;
    }
}

fn parse_line(read: &str) -> (Option<ScreeningRequestBody>, Option<Errors>) {
    let trimmed = read.trim();
    if trimmed == "null" {
        return (None, Some(one_error("", "The line isn't a JSON object.")));
    }
    let Some(body) = ScreeningRequestBody::parse(read).filter(|_| trimmed.starts_with('{')) else {
        return (None, Some(one_error("", "The line isn't a valid JSON object.")));
    };
    let errors = validate(&body);
    if errors.is_empty() {
        (Some(body), None)
    } else {
        (Some(body), Some(errors))
    }
}

fn line_of<T: Serialize>(value: &T) -> Bytes {
    let mut bytes = serde_json::to_vec(value).unwrap_or_default();
    bytes.push(b'\n');
    Bytes::from(bytes)
}

async fn stream(State(s): State<Shared>, request: Request) -> Response {
    let Ok(permit) = s.streams.clone().try_acquire_owned() else {
        return (StatusCode::TOO_MANY_REQUESTS, [(header::RETRY_AFTER, "1")]).into_response();
    };
    if !s.catalog.is_ready() {
        return warming_up("warmingUp");
    }
    let (max_records, max_seconds) = (s.config.stream_max_records, s.config.stream_max_seconds);
    let deadline = tokio::time::Instant::now() + Duration::from_secs(max_seconds);
    let (lists, allow_list, now) = (s.catalog.lists(), s.catalog.allow_list(), s.screenings.now());
    let (tx, rx) = mpsc::channel::<Bytes>(64);
    let header_line = StreamHeaderLine {
        kind: "header",
        matcher_version: s.screenings.screener.matcher_version.clone(),
        disclaimer: DISCLAIMER,
        lists: lists.iter().map(|l| list_response(l, now)).collect(),
        max_records,
        max_seconds,
        allow_list_version: allow_list.version.clone(),
    };
    let body = request.into_body();
    let state = s.clone();
    tokio::spawn(async move {
        let _permit = permit;
        let _ = tx.send(line_of(&header_line)).await;
        let (lines_tx, mut lines) = mpsc::channel::<ReadLine>(16);
        let reader = tokio::spawn(read_lines(body, lines_tx));
        let (mut line, mut screened, mut errors) = (0usize, 0usize, 0usize);
        let mut next_line: Option<usize> = None;
        loop {
            if screened + errors >= max_records {
                next_line = Some(line + 1);
                break;
            }
            let read = tokio::select! {
                read = lines.recv() => read,
                _ = tokio::time::sleep_until(deadline) => { next_line = Some(line + 1); break; }
            };
            let Some(read) = read else { break };
            let text = match read {
                // The upload broke off (or ran past the deadline) before it ended: resume from the next line.
                ReadLine::Failed => {
                    next_line = Some(line + 1);
                    break;
                }
                ReadLine::TooLong => None,
                ReadLine::Line(text) => Some(text),
            };
            line += 1;
            if tokio::time::Instant::now() >= deadline {
                next_line = Some(line);
                break;
            }
            let problem = match &text {
                None => (None, Some(one_error("", "The line is longer than 4096 bytes."))),
                Some(t) if t.is_empty() => continue,
                Some(t) => parse_line(t),
            };
            let out = match problem {
                (body, Some(problem)) => {
                    errors += 1;
                    let reference = body.and_then(|b| b.reference).filter(|r| utf16_len(r) <= 64);
                    line_of(&StreamErrorLine { kind: "error", line, reference, errors: problem })
                }
                (Some(body), None) => {
                    let (service, lists, allow_list) = (state.screenings.clone(), lists.clone(), allow_list.clone());
                    let (result, body) =
                        tokio::task::spawn_blocking(move || (service.screen_for_stream(&body, &lists, &allow_list), body)).await.unwrap();
                    screened += 1;
                    let options = state.screenings.options();
                    let entries = result.entries.iter().map(|e| stream_entry(e, options)).collect();
                    line_of(&StreamResultLine {
                        kind: "result",
                        line,
                        reference: body.reference().to_string(),
                        outcome: result.outcome.as_str(),
                        entries,
                    })
                }
                (None, None) => continue,
            };
            if tx.send(out).await.is_err() {
                break;
            }
        }
        // Whatever ends the stream (its record limit included) stops reading the upload.
        reader.abort();
        let _ = tx.send(line_of(&StreamEndLine { kind: "end", screened, errors, truncated: next_line.is_some(), next_line })).await;
    });
    let body = Body::from_stream(tokio_stream_of(rx));
    let mut response = Response::new(body);
    response.headers_mut().insert(header::CONTENT_TYPE, HeaderValue::from_static("application/x-ndjson; charset=utf-8"));
    response
}

fn tokio_stream_of(mut rx: mpsc::Receiver<Bytes>) -> impl futures_util::Stream<Item = Result<Bytes, std::convert::Infallible>> {
    futures_util::stream::poll_fn(move |cx| rx.poll_recv(cx).map(|item| item.map(Ok)))
}

// --- Pages ---

fn content_type(path: &str) -> &'static str {
    match path.rsplit('.').next().unwrap_or_default() {
        "html" => "text/html; charset=utf-8",
        "js" => "text/javascript; charset=utf-8",
        "css" => "text/css; charset=utf-8",
        "json" => "application/json",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "ico" => "image/x-icon",
        "txt" => "text/plain; charset=utf-8",
        "webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    }
}

fn find_page(path: &str) -> Option<&'static [u8]> {
    PAGES.iter().find(|(name, _)| *name == path).map(|(_, bytes)| *bytes)
}

/// The pages, as a file server serves them: a directory's index.html, and a directory without its slash redirected.
async fn page(request: Request) -> Response {
    let path = request.uri().path().trim_start_matches('/').to_string();
    if request.method() != axum::http::Method::GET && request.method() != axum::http::Method::HEAD {
        return StatusCode::METHOD_NOT_ALLOWED.into_response();
    }
    let file = if path.is_empty() || path.ends_with('/') { format!("{path}index.html") } else { path.clone() };
    if let Some(bytes) = find_page(&file) {
        return ([(header::CONTENT_TYPE, content_type(&file))], bytes).into_response();
    }
    if !path.is_empty() && find_page(&format!("{path}/index.html")).is_some() {
        return (StatusCode::MOVED_PERMANENTLY, [(header::LOCATION, format!("/{path}/"))]).into_response();
    }
    not_found()
}

// --- Admin access (AdminAccess.cs) ---

pub const ADMIN_ROLE: &str = "Lists.Admin";
pub const REQUEST_HEADER: &str = "X-Requested-By";
pub const REQUEST_HEADER_VALUE: &str = "sanction-and-forgive-admin";

/// The signed-in user.
pub struct AdminUser {
    pub name: String,
    pub is_admin: bool,
}

/// Reads X-MS-CLIENT-PRINCIPAL (base64 JSON claims; the platform doesn't let clients set it), only when sign-in is on.
pub fn read_principal(enabled: bool, headers: &HeaderMap) -> Option<AdminUser> {
    let values: Vec<&HeaderValue> = headers.get_all("x-ms-client-principal").iter().collect();
    if !enabled || values.len() != 1 || values[0].is_empty() || values[0].len() >= 16384 {
        return None;
    }
    let decoded = base64::engine::general_purpose::STANDARD.decode(values[0].as_bytes()).ok()?;
    let principal: Value = serde_json::from_slice(&decoded).ok()?;
    let claims = principal.get("claims")?.as_array()?;
    let role_type = principal.get("role_typ").and_then(Value::as_str);
    let mut name_types: Vec<&str> = vec!["preferred_username", "name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"];
    if let Some(t) = principal.get("name_typ").and_then(Value::as_str) {
        name_types.insert(0, t);
    }
    let role_types = ["roles", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
    let claim = |c: &Value, key: &str| c.get(key).and_then(Value::as_str).unwrap_or_default().to_string();
    let mut user = AdminUser { name: "signed-in user".to_string(), is_admin: false };
    for c in claims {
        let (t, v) = (claim(c, "typ"), claim(c, "val"));
        if (role_type == Some(t.as_str()) || role_types.contains(&t.as_str())) && v == ADMIN_ROLE {
            user.is_admin = true;
        }
    }
    for name_type in name_types {
        if let Some(c) = claims.iter().find(|c| claim(c, "typ") == name_type) {
            let v = claim(c, "val");
            if !v.is_empty() {
                user.name = v;
                return Some(user);
            }
        }
    }
    Some(user)
}

pub fn from_this_site(headers: &HeaderMap) -> bool {
    headers.get(REQUEST_HEADER).and_then(|v| v.to_str().ok()) == Some(REQUEST_HEADER_VALUE)
}
