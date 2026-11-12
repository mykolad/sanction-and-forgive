//! The admin sign-in in the app (AdminSignIn.cs): OpenID Connect with Microsoft Entra ID, the ID token posted back to
//! the callback, and that token as the session cookie, checked on every admin request. No secret, nothing on the server.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::{Duration, Instant};

use axum::extract::{Query, State};
use axum::http::{header, HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, post};
use axum::{Form, Router};
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use jsonwebtoken::{Algorithm, DecodingKey, Validation};
use serde::Deserialize;
use serde_json::Value;
use tokio::sync::Mutex;

use super::server::{AdminUser, Shared, ADMIN_ROLE};
use crate::log::Logger;

pub const SESSION_COOKIE: &str = "saf-admin";
const PENDING_COOKIE: &str = "saf-sign-in";
const START_PATH: &str = "/.auth/login/aad";
const CALLBACK_PATH: &str = "/.auth/login/aad/callback";
const SIGN_OUT_PATH: &str = "/.auth/logout";
const DEFAULT_RETURN: &str = "/admin";
const SKEW_SECONDS: u64 = 300;
const MAX_TOKEN: usize = 4000;
const METADATA_TTL: Duration = Duration::from_secs(24 * 3600);
const KEYS_REFRESH_GAP: Duration = Duration::from_secs(300);
const FAILED_RETRY_GAP: Duration = Duration::from_secs(60);

/// Admin:ClientId and Admin:TenantId (or Admin:Authority, any OpenID Connect issuer); on only with both.
pub struct SignIn {
    client_id: String,
    authority: String,
    http: reqwest::Client,
    logger: Logger,
    cache: Mutex<Cache>,
}

#[derive(Default)]
struct Cache {
    meta: Option<Arc<Metadata>>,
    /// When a fetch last failed: the next is tried only after FAILED_RETRY_GAP.
    failed_at: Option<Instant>,
}

struct Metadata {
    issuer: String,
    authorization_endpoint: String,
    keys: HashMap<String, DecodingKey>,
    fetched: Instant,
}

/// A signed-in administrator from a valid ID token, and when it ends (seconds since 1970).
pub struct Session {
    pub user: AdminUser,
    pub expires: u64,
}

#[derive(Deserialize)]
struct Discovery {
    issuer: String,
    authorization_endpoint: String,
    jwks_uri: String,
}

#[derive(Deserialize)]
struct KeySet {
    keys: Vec<Jwk>,
}

#[derive(Deserialize)]
struct Jwk {
    kty: String,
    #[serde(default)]
    kid: String,
    #[serde(default, rename = "use")]
    usage: Option<String>,
    #[serde(default)]
    n: String,
    #[serde(default)]
    e: String,
}

impl SignIn {
    pub fn new(client_id: String, tenant_id: String, authority: String, logger: Logger) -> Self {
        let authority = if authority.is_empty() && !tenant_id.is_empty() {
            format!("https://login.microsoftonline.com/{tenant_id}/v2.0")
        } else {
            authority
        };
        let http = reqwest::Client::builder().timeout(Duration::from_secs(10)).build().expect("an HTTP client");
        SignIn { client_id, authority, http, logger, cache: Mutex::new(Cache::default()) }
    }

    pub fn enabled(&self) -> bool {
        !self.client_id.is_empty() && !self.authority.is_empty()
    }

    /// The issuer's endpoints and keys, cached for a day; `force` reads them again (a token named a key it doesn't
    /// know), at most every five minutes. The keys it had stay when the issuer can't be reached, and after a fetch that
    /// failed the next is tried only a minute later: in an outage admin requests get those keys (or none) at once,
    /// instead of each waiting for another fetch.
    async fn metadata(&self, force: bool) -> Option<Arc<Metadata>> {
        let mut cache = self.cache.lock().await;
        if let Some(meta) = cache.meta.as_ref() {
            let age = meta.fetched.elapsed();
            if age < METADATA_TTL && (!force || age < KEYS_REFRESH_GAP) {
                return Some(meta.clone());
            }
        }
        if cache.failed_at.is_some_and(|at| at.elapsed() < FAILED_RETRY_GAP) {
            return cache.meta.clone();
        }
        match self.fetch().await {
            Ok(meta) => {
                let meta = Arc::new(meta);
                *cache = Cache { meta: Some(meta.clone()), failed_at: None };
                Some(meta)
            }
            Err(e) => {
                self.logger.error("The sign-in issuer's metadata couldn't be read", &[("error", e.into())]);
                cache.failed_at = Some(Instant::now());
                cache.meta.clone()
            }
        }
    }

    async fn fetch(&self) -> Result<Metadata, String> {
        let address = format!("{}/.well-known/openid-configuration", self.authority.trim_end_matches('/'));
        let discovery: Discovery = self.get_json(&address).await?;
        let set: KeySet = self.get_json(&discovery.jwks_uri).await?;
        let keys = set
            .keys
            .into_iter()
            .filter(|k| k.kty == "RSA" && k.usage.as_deref().is_none_or(|u| u == "sig"))
            .filter_map(|k| DecodingKey::from_rsa_components(&k.n, &k.e).ok().map(|key| (k.kid, key)))
            .collect();
        Ok(Metadata { issuer: discovery.issuer, authorization_endpoint: discovery.authorization_endpoint, keys, fetched: Instant::now() })
    }

    async fn get_json<T: serde::de::DeserializeOwned>(&self, address: &str) -> Result<T, String> {
        let response = self.http.get(address).send().await.map_err(|e| e.to_string())?;
        if !response.status().is_success() {
            return Err(format!("{address}: {}", response.status().as_u16()));
        }
        let body = response.bytes().await.map_err(|e| e.to_string())?;
        serde_json::from_slice(&body).map_err(|e| e.to_string())
    }

    /// The administrator an ID token names, if this issuer signed it (RS256) for this app and it hasn't expired; with
    /// `nonce`, also only if it answers that sign-in.
    pub async fn validate(&self, token: &str, nonce: Option<&str>) -> Option<Session> {
        if !self.enabled() || token.is_empty() || token.len() > MAX_TOKEN {
            return None;
        }
        let kid = jsonwebtoken::decode_header(token).ok()?.kid.unwrap_or_default();
        let mut meta = self.metadata(false).await?;
        if !meta.keys.contains_key(&kid) {
            // Entra rotates its keys: look again once.
            meta = self.metadata(true).await?;
        }
        let key = meta.keys.get(&kid)?;
        let mut validation = Validation::new(Algorithm::RS256);
        validation.set_audience(&[&self.client_id]);
        validation.set_issuer(&[&meta.issuer]);
        validation.set_required_spec_claims(&["exp", "aud", "iss"]);
        validation.leeway = SKEW_SECONDS;
        validation.validate_nbf = true;
        let claims = jsonwebtoken::decode::<HashMap<String, Value>>(token, key, &validation).ok()?.claims;
        if let Some(nonce) = nonce {
            let said = claims.get("nonce").and_then(Value::as_str).unwrap_or_default();
            if !constant_time_eq(said.as_bytes(), nonce.as_bytes()) {
                return None;
            }
        }
        let name = ["preferred_username", "name"]
            .iter()
            .filter_map(|c| claims.get(*c).and_then(Value::as_str))
            .find(|v| !v.is_empty())
            .unwrap_or("signed-in user")
            .to_string();
        let is_admin =
            claims.get("roles").and_then(Value::as_array).is_some_and(|roles| roles.iter().any(|r| r.as_str() == Some(ADMIN_ROLE)));
        let expires = claims.get("exp").and_then(Value::as_u64).unwrap_or_default();
        Some(Session { user: AdminUser { name, is_admin }, expires })
    }

    /// The administrator whose session cookie is valid; nobody with sign-in off.
    pub async fn current(&self, headers: &HeaderMap) -> Option<AdminUser> {
        let token = cookie(headers, SESSION_COOKIE)?;
        self.validate(&token, None).await.map(|s| s.user)
    }
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

/// A cookie's value from the request, if it has exactly that name.
fn cookie(headers: &HeaderMap, name: &str) -> Option<String> {
    headers
        .get_all(header::COOKIE)
        .iter()
        .filter_map(|v| v.to_str().ok())
        .flat_map(|v| v.split(';'))
        .filter_map(|pair| pair.trim().split_once('='))
        .find(|(n, _)| *n == name)
        .map(|(_, v)| v.to_string())
        .filter(|v| !v.is_empty())
}

/// A path on this site only: never another site's address (//host, /\host), nor one with a control character (a
/// browser drops a tab, so "/\t/host" would be "//host").
pub fn local_return(path: &str) -> &str {
    let bytes = path.as_bytes();
    if !path.is_empty()
        && path.len() <= 512
        && bytes[0] == b'/'
        && (bytes.len() == 1 || (bytes[1] != b'/' && bytes[1] != b'\\'))
        && !path.chars().any(char::is_control)
    {
        path
    } else {
        DEFAULT_RETURN
    }
}

fn random_token() -> String {
    let mut bytes = [0u8; 32];
    getrandom::fill(&mut bytes).expect("the system's random numbers");
    URL_SAFE_NO_PAD.encode(bytes)
}

fn plain_text(status: StatusCode, text: &str) -> Response {
    (status, [(header::CONTENT_TYPE, "text/plain; charset=utf-8")], format!("{text}\n")).into_response()
}

fn redirect(location: &str, cookies: &[String]) -> Response {
    let mut response = StatusCode::FOUND.into_response();
    if let Ok(value) = HeaderValue::from_str(location) {
        response.headers_mut().insert(header::LOCATION, value);
    }
    for c in cookies {
        if let Ok(value) = HeaderValue::from_str(c) {
            response.headers_mut().append(header::SET_COOKIE, value);
        }
    }
    response
}

fn pending_cookie(value: &str, max_age: i64) -> String {
    format!("{PENDING_COOKIE}={value}; Path={START_PATH}; Max-Age={max_age}; HttpOnly; Secure; SameSite=None")
}

fn session_cookie(value: &str, max_age: i64) -> String {
    format!("{SESSION_COOKIE}={value}; Path=/; Max-Age={max_age}; HttpOnly; Secure; SameSite=Lax")
}

pub fn routes() -> Router<Shared> {
    Router::new().route(START_PATH, get(start)).route(CALLBACK_PATH, post(callback)).route(SIGN_OUT_PATH, get(sign_out))
}

/// Sends the browser to sign in, keeping the state and nonce in a cookie only the callback gets.
async fn start(State(s): State<Shared>, headers: HeaderMap, Query(query): Query<HashMap<String, String>>) -> Response {
    let sign_in = &s.sign_in;
    if !sign_in.enabled() {
        return StatusCode::NOT_FOUND.into_response();
    }
    let Some(meta) = sign_in.metadata(false).await.filter(|m| !m.authorization_endpoint.is_empty()) else {
        return plain_text(StatusCode::SERVICE_UNAVAILABLE, "The sign-in service can't be reached right now. Try again in a minute.");
    };
    let scheme = headers
        .get_all("x-forwarded-proto")
        .iter()
        .next_back()
        .and_then(|v| v.to_str().ok())
        .map(|v| v.rsplit(',').next().unwrap_or_default().trim().to_string())
        .unwrap_or_else(|| "http".to_string());
    let host = headers.get(header::HOST).and_then(|v| v.to_str().ok()).unwrap_or("localhost");
    let (state, nonce) = (random_token(), random_token());
    let callback = format!("{scheme}://{host}{CALLBACK_PATH}");
    let params = [
        ("client_id", sign_in.client_id.as_str()),
        ("response_type", "id_token"),
        ("response_mode", "form_post"),
        ("redirect_uri", callback.as_str()),
        ("scope", "openid profile"),
        ("state", state.as_str()),
        ("nonce", nonce.as_str()),
    ];
    let Ok(url) = reqwest::Url::parse_with_params(&meta.authorization_endpoint, &params) else {
        return plain_text(StatusCode::SERVICE_UNAVAILABLE, "The sign-in service can't be reached right now. Try again in a minute.");
    };
    let return_to = local_return(query.get("post_login_redirect_uri").map(String::as_str).unwrap_or_default());
    let pending = format!("{state}.{nonce}.{}", URL_SAFE_NO_PAD.encode(return_to));
    redirect(url.as_str(), &[pending_cookie(&pending, 600)])
}

/// The issuer's answer: the ID token becomes the session cookie.
async fn callback(State(s): State<Shared>, headers: HeaderMap, Form(form): Form<HashMap<String, String>>) -> Response {
    let sign_in = &s.sign_in;
    if !sign_in.enabled() {
        return StatusCode::NOT_FOUND.into_response();
    }
    let clear = pending_cookie("", 0);
    let with_cleared = |mut response: Response| {
        if let Ok(value) = HeaderValue::from_str(&clear) {
            response.headers_mut().append(header::SET_COOKIE, value);
        }
        response
    };
    if form.contains_key("error") {
        return with_cleared(plain_text(StatusCode::FORBIDDEN, "The sign-in was refused. Only lists administrators can sign in here."));
    }
    let pending = cookie(&headers, PENDING_COOKIE).unwrap_or_default();
    let parts: Vec<&str> = pending.split('.').collect();
    let state = form.get("state").map(String::as_str).unwrap_or_default();
    let token = form.get("id_token").map(String::as_str).unwrap_or_default();
    let session = if parts.len() == 3 && !state.is_empty() && constant_time_eq(parts[0].as_bytes(), state.as_bytes()) {
        sign_in.validate(token, Some(parts[1])).await
    } else {
        None
    };
    let Some(session) = session else {
        sign_in.logger.warn("Admin sign-in refused: the state or the ID token isn't valid", &[]);
        return with_cleared(plain_text(StatusCode::BAD_REQUEST, "The sign-in couldn't be completed. Start again from the admin page."));
    };
    let decoded = URL_SAFE_NO_PAD.decode(parts[2]).ok().and_then(|b| String::from_utf8(b).ok()).unwrap_or_default();
    let return_to = local_return(&decoded).to_string();
    sign_in.logger.info(&format!("Admin sign-in: {}", session.user.name), &[]);
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or_default();
    let max_age = if session.expires > now { (session.expires - now) as i64 } else { 60 };
    redirect(&return_to, &[clear.clone(), session_cookie(token, max_age)])
}

/// Ends the session here.
async fn sign_out(Query(query): Query<HashMap<String, String>>) -> Response {
    let return_to = local_return(query.get("post_logout_redirect_uri").map(String::as_str).unwrap_or_default()).to_string();
    redirect(&return_to, &[session_cookie("", 0)])
}

#[cfg(test)]
mod tests {
    use super::*;

    /// RFC 7515, appendix A.2.1: a token signed RS256 and its key's public half (n, e). A published example: no key of
    /// this app's, nothing secret.
    const RFC_N: &str = "ofgWCuLjybRlzo0tZWJjNiuSfb4p4fAkd_wWJcyQoTbji9k0l8W26mPddxHmfHQp-Vaw-4qPCJrcS2mJPMEzP1Pt0Bm4d4QlL-yRT-SFd2lZS-pCgNMsD1W_YpRPEwOWvG6b32690r2jZ47soMZo9wGzjb_7OMg0LOL-bSf63kpaSHSXndS5z5rexMdbBYUsLA9e-KXBdQOS-UTo7WTBEMa2R2CapHg665xsmtdVMTBQY4uDZlxvb3qCo5ZwKh9kG4LT6_I5IhlJH7aGhyxXFvUK-DWNmoudF8NAco9_h9iaGNj8q2ethFkMLs91kzk2PAcDTW9gb54h4FRWyuXpoQ";
    const RFC_E: &str = "AQAB";
    const RFC_TOKEN: &str = "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ.cC4hiUPoj9Eetdgtv3hF80EGrhuB__dzERat0XF9g2VtQgr9PJbu3XOiZj5RZmh7AAuHIm4Bh-0Qc_lF5YKt_O8W2Fp5jujGbds9uJdbF9CUAr7t1dnZcAcQjbKBYNX4BAynRFdiuB--f_nZLgrnbyTyWzO75vRK5h6xBArLIARNPvkSjtQBMHlb1L07Qe7K0GarZRmB_eSN9383LcOLn6_dO--xi12jzDwusC-eOkHWEsqtFZESc6BfI7noOPqvhJ1phCnvWh6IeYI2w9QOYEUipUTI8np6LbgGY9Fs98rqVt5AXLIhWkWywlVmtVrBp0igcN_IoypGlUPQGe77Rw";

    /// The signature check itself, as the sign-in uses it (RS256 with a key from its n and e): a library or build
    /// change that leaves it without a crypto backend panics here, not at the first sign-in (Dependabot's #50).
    #[test]
    fn an_rs256_signature_is_checked() {
        let key = jsonwebtoken::DecodingKey::from_rsa_components(RFC_N, RFC_E).expect("the RFC's key");
        let mut validation = jsonwebtoken::Validation::new(jsonwebtoken::Algorithm::RS256);
        // Only the signature: the example expired in 2011 and names no audience.
        validation.validate_exp = false;
        validation.validate_aud = false;
        validation.required_spec_claims.clear();

        let claims = jsonwebtoken::decode::<serde_json::Value>(RFC_TOKEN, &key, &validation).expect("the RFC's signature").claims;
        assert_eq!(claims["iss"], "joe");

        // One character in the middle of the signature changed.
        let middle = RFC_TOKEN.len() - 150;
        let other = if &RFC_TOKEN[middle..=middle] == "A" { "B" } else { "A" };
        let tampered = format!("{}{other}{}", &RFC_TOKEN[..middle], &RFC_TOKEN[middle + 1..]);
        let refused = jsonwebtoken::decode::<serde_json::Value>(&tampered, &key, &validation).expect_err("a changed signature");
        assert_eq!(refused.kind(), &jsonwebtoken::errors::ErrorKind::InvalidSignature);
    }

    #[test]
    fn returns_only_to_this_site() {
        for (path, want) in [
            ("/allow-list", "/allow-list"),
            ("//elsewhere.example", "/admin"),
            ("/\\elsewhere.example", "/admin"),
            ("/\t/elsewhere.example", "/admin"),
            ("https://elsewhere.example/", "/admin"),
            ("", "/admin"),
        ] {
            assert_eq!(local_return(path), want, "{path}");
        }
    }

    #[test]
    fn reads_only_the_named_cookie() {
        let mut headers = HeaderMap::new();
        headers.insert(header::COOKIE, HeaderValue::from_static("other=1; saf-admin=token.value.sig; saf-admin-x=2"));
        assert_eq!(cookie(&headers, SESSION_COOKIE).as_deref(), Some("token.value.sig"));
        assert_eq!(cookie(&headers, PENDING_COOKIE), None);
    }
}
