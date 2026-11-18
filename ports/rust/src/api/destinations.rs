//! The table of destinations under broad restrictions (#67; owner's decisions, 2026-11-18; the Rust port only so far):
//! read by anyone at /api/v1/destinations, edited by administrators at /api/v1/admin/destinations. Rows are never
//! deleted: a change is a new row with the same code, which removes the old one. The starting rows the owner approved
//! (starting-destinations.json) are loaded once, from the admin page, into a table that has never had a row.

use axum::body::Bytes;
use axum::extract::{Path, State};
use axum::http::{HeaderMap, StatusCode};
use axum::response::Response;
use axum::routing::{get, post};
use axum::Router;
use chrono::{Months, NaiveDate};
use regex::Regex;
use serde::Serialize;
use serde_json::{json, Map, Value};

use super::admin::{field, path_id, refuse, server_error};
use super::screening::{utf16_len, Errors, JsonTime};
use super::server::{bad_request, json_response, not_found, one_error, Shared};
use crate::matching::data::data;
use crate::matching::{Destination, DestinationKind, DestinationStatus};
use crate::store::queries::{self, DestinationAddition, DestinationRecord};

const MAX_NAME: usize = 200;
const MAX_OTHER_NAMES: usize = 100;
const MAX_OTHER_NAMES_TEXT: usize = 4000;
const MAX_RESTRICTED_BY: usize = 200;
const MAX_LEGAL_SOURCE: usize = 1000;

pub fn routes() -> Router<Shared> {
    Router::new()
        .route("/api/v1/destinations", get(public_table))
        .route("/api/v1/admin/destinations", get(admin_table).post(add))
        .route("/api/v1/admin/destinations/start", post(start))
        .route("/api/v1/admin/destinations/{id}/remove", post(remove))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct DestinationRow {
    id: i32,
    code: String,
    kind: &'static str,
    name: String,
    other_names: Vec<String>,
    country: Option<String>,
    parent: Option<String>,
    needs_region: bool,
    status: &'static str,
    restricted_by: String,
    legal_source: String,
    as_of: String,
    review_by: String,
}

fn date(d: NaiveDate) -> String {
    d.format("%Y-%m-%d").to_string()
}

fn row_response(d: &Destination) -> DestinationRow {
    DestinationRow {
        id: d.id,
        code: d.code.clone(),
        kind: d.kind.as_str(),
        name: d.name.clone(),
        other_names: d.other_names.clone(),
        country: (!d.country.is_empty()).then(|| d.country.clone()),
        parent: d.parent.clone(),
        needs_region: d.needs_region,
        status: d.status.as_str(),
        restricted_by: d.restricted_by.clone(),
        legal_source: d.legal_source.clone(),
        as_of: date(d.as_of),
        review_by: date(d.review_by),
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct HistoryRow {
    #[serde(flatten)]
    row: DestinationRow,
    added_by: String,
    added_at: JsonTime,
    removed_by: Option<String>,
    removed_at: Option<JsonTime>,
}

/// The table in force, as the screenings use it: never who edited it.
async fn public_table(State(s): State<Shared>) -> Response {
    let table = s.catalog.destinations();
    let today = s.screenings.now().date_naive();
    json_response(
        StatusCode::OK,
        &json!({
            "version": table.version,
            "reviewOverdue": table.review_overdue(today).map(date),
            "destinations": table.rows().iter().map(row_response).collect::<Vec<_>>(),
        }),
    )
}

/// Every row ever added, newest first, with who added and removed it; and whether the starting rows can be loaded.
async fn admin_table(State(s): State<Shared>, headers: HeaderMap) -> Response {
    if let Err(r) = refuse(&s, &headers, false).await {
        return r;
    }
    let history = match s.db.connect().await {
        Ok(mut conn) => queries::destinations_history(&mut conn).await,
        Err(e) => Err(e),
    };
    let Ok(history) = history else { return server_error() };
    let rows: Vec<HistoryRow> = history.iter().map(history_row).collect();
    json_response(
        StatusCode::OK,
        &json!({
            "version": s.catalog.destinations().version,
            "canStart": history.is_empty(),
            "startingCount": starting_rows().len(),
            "rows": rows,
        }),
    )
}

fn history_row(r: &DestinationRecord) -> HistoryRow {
    HistoryRow {
        row: row_response(&r.destination),
        added_by: r.added_by.clone(),
        added_at: JsonTime(r.added_at),
        removed_by: r.removed_by.clone(),
        removed_at: r.removed_at.map(JsonTime),
    }
}

/// A row from a request body, or what's wrong with it by field (never echoing a value).
fn parse_row(object: &Map<String, Value>) -> Result<(Destination, bool), Errors> {
    static CODE: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    let code_pattern = CODE.get_or_init(|| Regex::new(r"^[a-z0-9][a-z0-9-]{0,63}$").unwrap());
    let mut errors = Errors::new();
    // By field as the reference's validation names them ("Code", "OtherNames"); a later message for a field replaces
    // an earlier one (a value that isn't a string, then what it should be).
    let mut fail = |name: &str, message: &str| {
        let mut chars = name.chars();
        let field = chars.next().map(|c| c.to_ascii_uppercase().to_string() + chars.as_str()).unwrap_or_default();
        errors.insert(field, vec![message.to_string()]);
    };
    let text = |name: &str| -> Option<String> {
        match field(object, name) {
            None | Some(Value::Null) => Some(String::new()),
            Some(Value::String(s)) => Some(s.trim_matches(char::is_whitespace).to_string()),
            _ => None,
        }
    };
    let text_or = |name: &str, fail: &mut dyn FnMut(&str, &str)| -> String {
        text(name).unwrap_or_else(|| {
            fail(name, "A string.");
            String::new()
        })
    };

    let code = text_or("code", &mut fail);
    if !code_pattern.is_match(&code) {
        fail("Code", "1 to 64 characters: lower-case letters, digits and -, starting with a letter or digit (\"ua-crimea\").");
    }
    let kind = DestinationKind::parse(&text_or("kind", &mut fail));
    if kind.is_none() {
        fail("Kind", "Country, Region or Settlement.");
    }
    let name = text_or("name", &mut fail);
    if name.is_empty() || utf16_len(&name) > MAX_NAME {
        fail("Name", "1 to 200 characters.");
    }
    let mut other_names: Vec<String> = Vec::new();
    match field(object, "otherNames") {
        None | Some(Value::Null) => {}
        Some(Value::Array(items)) => {
            let mut valid = items.len() <= MAX_OTHER_NAMES;
            for item in items {
                match item {
                    Value::String(s) => {
                        let s = s.trim_matches(char::is_whitespace);
                        if s.is_empty() || utf16_len(s) > MAX_NAME || s.contains(['\n', '\r']) {
                            valid = false;
                        } else if !other_names.iter().any(|n| n == s) {
                            other_names.push(s.to_string());
                        }
                    }
                    _ => valid = false,
                }
            }
            if !valid || utf16_len(&other_names.join("\n")) > MAX_OTHER_NAMES_TEXT {
                fail("OtherNames", "At most 100 names, each 1 to 200 characters on one line, 4,000 in all.");
            }
        }
        Some(_) => fail("OtherNames", "A list of names."),
    }
    if std::iter::once(&name).chain(other_names.iter()).any(|n| !n.is_empty() && crate::matching::text::words(n).is_empty()) {
        fail("Name", "Every name needs a letter or a digit: it is what an address is read for.");
    }
    let country = text_or("country", &mut fail).to_ascii_uppercase();
    match kind {
        Some(DestinationKind::Country) if !data().country_codes.contains(&country) => {
            fail("Country", "The country's ISO 3166-1 alpha-2 code (\"IR\").");
        }
        _ if !country.is_empty() && !data().country_codes.contains(&country) => {
            fail("Country", "An ISO 3166-1 alpha-2 code (\"UA\"), or nothing.");
        }
        _ => {}
    }
    let parent = text_or("parent", &mut fail);
    if !parent.is_empty() && (kind != Some(DestinationKind::Settlement) || !code_pattern.is_match(&parent)) {
        fail("Parent", "Only a settlement has a region: the region's code.");
    }
    let needs_region = match field(object, "needsRegion") {
        None | Some(Value::Null) => false,
        Some(Value::Bool(b)) => *b,
        Some(_) => {
            fail("NeedsRegion", "true or false.");
            false
        }
    };
    if needs_region && parent.is_empty() {
        fail("NeedsRegion", "A settlement recognised only with its region needs its region (Parent).");
    }
    let status = DestinationStatus::parse(&text_or("status", &mut fail));
    match status {
        None => fail("Status", "Restricted, Partly or Outside."),
        Some(DestinationStatus::Outside) if kind == Some(DestinationKind::Country) => {
            fail("Status", "A country is Restricted or Partly: one outside every restricted area needs no row.");
        }
        _ => {}
    }
    let restricted_by = text_or("restrictedBy", &mut fail);
    let legal_source = text_or("legalSource", &mut fail);
    let outside = status == Some(DestinationStatus::Outside);
    if utf16_len(&restricted_by) > MAX_RESTRICTED_BY || (restricted_by.is_empty() && !outside) {
        fail("RestrictedBy", "Who restricts it (\"EU, UK, US\"), at most 200 characters.");
    }
    if utf16_len(&legal_source) > MAX_LEGAL_SOURCE || (legal_source.is_empty() && !outside) {
        fail("LegalSource", "The legal acts it comes from, at most 1,000 characters.");
    }
    let parse_date = |value: &str| NaiveDate::parse_from_str(value, "%Y-%m-%d").ok().filter(|_| value.len() == 10);
    let as_of = parse_date(&text_or("asOf", &mut fail));
    if as_of.is_none() {
        fail("AsOf", "The date the sources were checked: YYYY-MM-DD.");
    }
    let review_text = text_or("reviewBy", &mut fail);
    let review_by =
        if review_text.is_empty() { as_of.and_then(|d| d.checked_add_months(Months::new(12))) } else { parse_date(&review_text) };
    if review_by.is_none() || matches!((as_of, review_by), (Some(a), Some(r)) if r < a) {
        fail("ReviewBy", "When to check it again, YYYY-MM-DD, not before AsOf (12 months after it when left out).");
    }
    let confirmed = matches!(field(object, "confirmed"), Some(Value::Bool(true)));
    if !errors.is_empty() {
        return Err(errors);
    }
    Ok((
        Destination {
            id: 0,
            code,
            kind: kind.unwrap(),
            name,
            other_names,
            country,
            parent: (!parent.is_empty()).then_some(parent),
            needs_region,
            status: status.unwrap(),
            restricted_by,
            legal_source,
            as_of: as_of.unwrap(),
            review_by: review_by.unwrap(),
        },
        confirmed,
    ))
}

/// What a new row can't be among the rows in force: a settlement's region must be a region in force.
fn refused_by_table(row: &Destination, in_force: &[Destination]) -> Option<String> {
    let parent = row.parent.as_deref()?;
    let found = in_force.iter().any(|d| d.code == parent && d.kind == DestinationKind::Region);
    (!found).then(|| "Its region (Parent) isn't a region in the table: add the region first.".to_string())
}

/// Adds a row, or changes the one in force with the same code. A name also read as another place needs confirming.
async fn add(State(s): State<Shared>, headers: HeaderMap, body: Bytes) -> Response {
    let user = match refuse(&s, &headers, true).await {
        Ok(user) => user,
        Err(r) => return r,
    };
    let invalid = || bad_request(one_error("$", "The request body isn't a valid row of the table."));
    let Ok(Value::Object(object)) = serde_json::from_slice::<Value>(&body[..body.len().min(1 << 20)]) else { return invalid() };
    let (row, confirmed) = match parse_row(&object) {
        Ok(parsed) => parsed,
        Err(errors) => return bad_request(errors),
    };
    let conflicts = s.catalog.destinations().name_conflicts(data(), &row);
    if !conflicts.is_empty() && !confirmed {
        return json_response(
            StatusCode::CONFLICT,
            &json!({
                "code": "confirm",
                "message": "A name of this row is also read as another place. Once it's in the table, an address with that name is read as this one. Confirm only if that's right.",
                "conflicts": conflicts,
            }),
        );
    }
    let added = match s.db.connect().await {
        Ok(mut conn) => {
            queries::destination_add(&mut conn, &row, &user.name, chrono::Utc::now(), |in_force| refused_by_table(&row, in_force)).await
        }
        Err(e) => Err(e),
    };
    match added {
        Err(e) => {
            s.loader.logger.error("Adding to the destinations under broad restrictions failed", &[("error", e.to_string().into())]);
            server_error()
        }
        Ok(DestinationAddition::Refused(reason)) => bad_request(one_error("Parent", &reason)),
        Ok(DestinationAddition::Added { id, replaced }) => {
            let replaced_text = replaced.map(|r| format!(", replacing row {r}")).unwrap_or_default();
            s.loader.logger.info(&format!("Destination row {id} ({}) added by {}{replaced_text}", row.code, user.name), &[]);
            let reloaded = s.loader.reload_destinations(None).await;
            json_response(StatusCode::OK, &json!({ "id": id, "replaced": replaced, "reloaded": reloaded }))
        }
    }
}

async fn remove(State(s): State<Shared>, headers: HeaderMap, Path(id): Path<String>) -> Response {
    let user = match refuse(&s, &headers, true).await {
        Ok(user) => user,
        Err(r) => return r,
    };
    let Some(id) = path_id(&id) else { return not_found() };
    let removed = match s.db.connect().await {
        Ok(mut conn) => queries::destination_remove(&mut conn, id, &user.name, chrono::Utc::now()).await,
        Err(e) => Err(e),
    };
    match removed {
        Err(_) => server_error(),
        Ok(None) => not_found(),
        Ok(Some(code)) => {
            s.loader.logger.info(&format!("Destination row {id} ({code}) removed by {}", user.name), &[]);
            let reloaded = s.loader.reload_destinations(Some(&code)).await;
            json_response(StatusCode::OK, &json!({ "id": id, "reloaded": reloaded }))
        }
    }
}

/// Loads the starting rows into a table that has never had a row; 409 otherwise.
async fn start(State(s): State<Shared>, headers: HeaderMap) -> Response {
    let user = match refuse(&s, &headers, true).await {
        Ok(user) => user,
        Err(r) => return r,
    };
    let rows = starting_rows();
    let started = match s.db.connect().await {
        Ok(mut conn) => queries::destinations_start(&mut conn, &rows, &user.name, chrono::Utc::now()).await,
        Err(e) => Err(e),
    };
    match started {
        Err(e) => {
            s.loader.logger.error("Loading the starting destinations failed", &[("error", e.to_string().into())]);
            server_error()
        }
        Ok(false) => {
            json_response(StatusCode::CONFLICT, &json!({ "code": "started", "message": "The table already has rows: edit them instead." }))
        }
        Ok(true) => {
            s.loader.logger.info(&format!("{} starting destination rows loaded by {}", rows.len(), user.name), &[]);
            let reloaded = s.loader.reload_destinations(None).await;
            json_response(StatusCode::OK, &json!({ "added": rows.len(), "reloaded": reloaded }))
        }
    }
}

/// The starting rows (starting-destinations.json), each settlement with its region's restrictedBy and legalSource when
/// it has none of its own.
pub fn starting_rows() -> Vec<Destination> {
    let document: Value = serde_json::from_str(include_str!("starting-destinations.json")).expect("starting-destinations.json");
    let day = |key: &str| NaiveDate::parse_from_str(document[key].as_str().unwrap(), "%Y-%m-%d").unwrap();
    let (as_of, review_by) = (day("asOf"), day("reviewBy"));
    let items = document["rows"].as_array().unwrap();
    let text = |item: &Value, key: &str| item[key].as_str().unwrap_or_default().to_string();
    let mut rows: Vec<Destination> = Vec::new();
    for item in items {
        let parent = item["parent"].as_str().map(str::to_string);
        let inherited = |key: &str| -> String {
            let own = text(item, key);
            if !own.is_empty() {
                return own;
            }
            let region = items.iter().find(|r| r["code"].as_str() == parent.as_deref()).expect("a settlement's region");
            text(region, key)
        };
        rows.push(Destination {
            id: 0,
            code: text(item, "code"),
            kind: DestinationKind::parse(&text(item, "kind")).unwrap(),
            name: text(item, "name"),
            other_names: item["otherNames"].as_array().unwrap().iter().map(|n| n.as_str().unwrap().to_string()).collect(),
            country: text(item, "country"),
            parent: parent.clone(),
            needs_region: item["needsRegion"].as_bool().unwrap_or(false),
            status: DestinationStatus::parse(&text(item, "status")).unwrap(),
            restricted_by: if parent.is_some() { inherited("restrictedBy") } else { text(item, "restrictedBy") },
            legal_source: if parent.is_some() { inherited("legalSource") } else { text(item, "legalSource") },
            as_of,
            review_by,
        });
    }
    rows
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::matching::addresses::parse_address;
    use crate::matching::text::Tokenizer;
    use crate::matching::{DestinationState, Destinations};

    fn table() -> Destinations {
        let rows = starting_rows().into_iter().enumerate().map(|(i, mut r)| {
            r.id = i as i32 + 1;
            r
        });
        Destinations::build(rows.collect(), "1")
    }

    /// Every starting row is one an administrator could add: valid, its region in the table, its codes distinct, and its
    /// names read as nothing else unless that's known.
    #[test]
    fn the_starting_rows_are_valid_rows() {
        let rows = starting_rows();
        assert!(rows.len() > 60, "{}", rows.len());
        let mut in_force: Vec<Destination> = Vec::new();
        for row in &rows {
            let body = json!({
                "code": row.code, "kind": row.kind.as_str(), "name": row.name, "otherNames": row.other_names, "country": row.country,
                "parent": row.parent, "needsRegion": row.needs_region, "status": row.status.as_str(), "restrictedBy": row.restricted_by,
                "legalSource": row.legal_source, "asOf": date(row.as_of), "reviewBy": date(row.review_by),
            });
            let (parsed, _) = parse_row(body.as_object().unwrap()).unwrap_or_else(|e| panic!("{}: {e:?}", row.code));
            assert_eq!(&parsed, row);
            assert_eq!(refused_by_table(row, &in_force), None, "{}", row.code);
            assert!(!in_force.iter().any(|d| d.code == row.code), "{} twice", row.code);
            in_force.push(row.clone());
        }
        let table = table();
        let known = ["Donetsk: also Donetsk (UA)"];
        for row in table.rows() {
            let conflicts: Vec<String> = table.name_conflicts(data(), row).into_iter().filter(|c| !known.contains(&c.as_str())).collect();
            assert!(conflicts.is_empty(), "{}: {conflicts:?}", row.code);
        }
    }

    fn find(address: &str) -> Option<(DestinationState, String)> {
        let data = data();
        let mut tokenizer = Tokenizer::new(data);
        let form = parse_address(data, &mut tokenizer, address, "");
        table().find(data, &mut tokenizer, address, &form).map(|n| (n.state, n.destination.code))
    }

    #[test]
    fn the_starting_rows_read_addresses_as_the_owner_decided() {
        use DestinationState::*;
        let cases: [(&str, Option<(DestinationState, &str)>); 16] = [
            ("ul. Kirova 10, Simferopol, Republic of Crimea, Russia", Some((Restricted, "ua-simferopol"))),
            ("вул. Леніна 1, м. Севастополь", Some((Restricted, "ua-sevastopol"))),
            ("пр. Мира 5, Донецк, ДНР", Some((Restricted, "ua-donetsk"))),
            ("Mariupol, Donetsk Oblast, Ukraine", Some((Restricted, "ua-mariupol"))),
            ("Kramatorsk, Donetsk Oblast, Ukraine", Some((PossiblyRestricted, "ua-donetsk-oblast"))),
            ("vul. Ushakova 5, Kherson, Khersonska oblast, 73000", None),
            ("Nova Kakhovka, Kherson region", Some((Restricted, "ua-nova-kakhovka"))),
            ("Zaporizhzhia, Zaporizhzhia Oblast, Ukraine", None),
            ("Melitopol, Zaporizhzhia Oblast", Some((Restricted, "ua-melitopol"))),
            ("Vasylivka, Vinnytsia Oblast", None),
            ("Vasylivka, Zaporizhzhia Oblast", Some((Restricted, "ua-vasylivka"))),
            ("12 Azadi Ave, Tehran, Iran", Some((Restricted, "ir"))),
            ("Pyongyang, DPRK", Some((Restricted, "kp"))),
            ("Calle 23, La Habana, Cuba", Some((Restricted, "cu"))),
            ("Kyiv, Ukraine", None),
            ("Moscow, Russia", None),
        ];
        for (address, expected) in cases {
            assert_eq!(find(address), expected.map(|(s, c)| (s, c.to_string())), "{address}");
        }
    }
}
