//! One screening: matching against the lists in memory (never the database), the response, the summary's wording,
//! and the audit line, which carries the reference and what each list answered, never what was entered.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use regex::Regex;
use serde::Serialize;
use serde_json::Value;

use super::catalog::{Catalog, LoadedList};
use crate::lists::catalogue::find_source;
use crate::lists::parse::imo_number;
use crate::log::Logger;
use crate::matching::identifiers::{comparable, entered_birth_date, identifier_key, imo_check_digit_ok};
use crate::matching::{
    AllowList, Cell, DestinationNotice, DestinationState, Destinations, Entry, Options, Outcome, OverallOutcome, Party, ScreenedList,
    Screener, ScreeningRequest, ScreeningResult, SubjectMatch, WordAlignment, ROLES,
};
use crate::store::queries::Time;

/// On every result. Of embargoes, only the destinations the table lists (#67).
pub const DISCLAIMER: &str =
    "This is a demo list check, not a legal determination. It doesn't cover ownership or control by listed parties. Of \
embargoes, it only flags an address in a destination its table lists as under broad restrictions, and never checks the \
rules for the goods. A person must review possible matches. The percentage is an estimate that this entry is the listed \
party, based on the data entered; it isn't the chance that a shipment is lawful.";

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

/// The request: the caller's reference and at least one field, or parties (#65).
#[derive(Debug, Clone, Default)]
pub struct ScreeningRequestBody {
    pub reference: Option<String>,
    pub receiver_name: Option<String>,
    pub company_name: Option<String>,
    pub address: Option<String>,
    pub parties: Vec<PartyBody>,
    /// Identifiers entered for the receiver and the company, and the receiver's date of birth (#68).
    pub receiver_identifiers: Vec<String>,
    pub company_identifiers: Vec<String>,
    pub receiver_date_of_birth: Option<String>,
    /// The vessel (#69): its name and its IMO number, either or both.
    pub vessel_name: Option<String>,
    pub vessel_imo_number: Option<String>,
}

/// A list of identifiers as sent: strings, or null for none. None when it's anything else.
fn identifier_list(value: &Value) -> Option<Vec<String>> {
    match value {
        Value::Null => Some(Vec::new()),
        Value::Array(items) => items.iter().map(|i| i.as_str().map(str::to_string)).collect(),
        _ => None,
    }
}

/// At most this many identifiers with one name.
pub const MAX_IDENTIFIERS: usize = 5;
const MAX_IDENTIFIER_LENGTH: usize = 64;

/// What's wrong with a name's identifiers and date of birth, by field (field names as the reference's validation
/// writes them), into errors. Never echoes a value.
fn validate_particulars(
    errors: &mut Errors,
    prefix: &str,
    name_given: bool,
    identifiers: &[String],
    birth_date: &Option<String>,
    person: bool,
) {
    let field = |name: &str| format!("{prefix}{name}");
    if identifiers.len() > MAX_IDENTIFIERS {
        errors.insert(field("Identifiers"), vec![format!("At most {MAX_IDENTIFIERS} identifiers.")]);
    }
    for (i, value) in identifiers.iter().enumerate().take(MAX_IDENTIFIERS) {
        let key = identifier_key(value);
        if utf16_len(value) > MAX_IDENTIFIER_LENGTH || !comparable(&key) {
            errors.insert(
                format!("{}[{i}]", field("Identifiers")),
                vec![
                    "Up to 64 characters, with at least 5 letters or digits, 4 of them digits: shorter ones would equal unrelated numbers."
                        .into(),
                ],
            );
        }
    }
    if !identifiers.is_empty() && !name_given {
        errors.insert(field("Identifiers"), vec!["Enter the name the identifiers belong to.".into()]);
    }
    if let Some(date) = birth_date.as_deref().filter(|d| !d.trim().is_empty()) {
        if !person {
            errors.insert(field("DateOfBirth"), vec!["Only a person has a date of birth.".into()]);
        } else if entered_birth_date(date).is_none() {
            errors.insert(field("DateOfBirth"), vec!["YYYY-MM-DD, YYYY-MM or YYYY.".into()]);
        } else if !name_given {
            errors.insert(field("DateOfBirth"), vec!["Enter the name the date of birth belongs to.".into()]);
        }
    }
}

/// A party as sent: a role, a name with its kind (person or company), an address; each a string or null.
#[derive(Debug, Clone, Default)]
pub struct PartyBody {
    pub role: Option<String>,
    pub name: Option<String>,
    pub kind: Option<String>,
    pub address: Option<String>,
    /// The name's identifiers and, for a person, date of birth (#68).
    pub identifiers: Vec<String>,
    pub date_of_birth: Option<String>,
}

/// At most this many parties in one request.
pub const MAX_PARTIES: usize = 10;

impl PartyBody {
    fn parse(value: &Value) -> Option<PartyBody> {
        let mut party = PartyBody::default();
        for (key, value) in value.as_object()? {
            let target = match key.to_ascii_lowercase().as_str() {
                "role" => &mut party.role,
                "name" => &mut party.name,
                "kind" => &mut party.kind,
                "address" => &mut party.address,
                "dateofbirth" => &mut party.date_of_birth,
                "identifiers" => {
                    party.identifiers = identifier_list(value)?;
                    continue;
                }
                _ => continue,
            };
            *target = match value {
                Value::Null => None,
                Value::String(s) => Some(s.clone()),
                _ => return None,
            };
        }
        Some(party)
    }

    /// The role as one of ROLES (matched ignoring case), or None.
    pub fn role(&self) -> Option<&'static str> {
        let role = self.role.as_deref()?.trim();
        ROLES.iter().copied().find(|r| r.eq_ignore_ascii_case(role))
    }

    /// Whether the name is a person's: kind "person" (else a company's, "company" or none given).
    pub fn is_person(&self) -> bool {
        self.kind.as_deref().is_some_and(|k| k.trim().eq_ignore_ascii_case("person"))
    }
}

impl ScreeningRequestBody {
    /// One JSON object (fields matched ignoring case, as the reference's web defaults do; the last of duplicates
    /// wins), each field a string or null. None when it isn't one.
    pub fn parse(text: &str) -> Option<ScreeningRequestBody> {
        let value: Value = serde_json::from_str(text).ok()?;
        let object = value.as_object()?;
        let mut body = ScreeningRequestBody::default();
        for (key, value) in object {
            if key.eq_ignore_ascii_case("receiverIdentifiers") || key.eq_ignore_ascii_case("companyIdentifiers") {
                let list = identifier_list(value)?;
                if key.eq_ignore_ascii_case("receiverIdentifiers") {
                    body.receiver_identifiers = list;
                } else {
                    body.company_identifiers = list;
                }
                continue;
            }
            if key.eq_ignore_ascii_case("parties") {
                body.parties = match value {
                    Value::Null => Vec::new(),
                    Value::Array(items) => items.iter().map(PartyBody::parse).collect::<Option<Vec<_>>>()?,
                    _ => return None,
                };
                continue;
            }
            let target = match key.to_ascii_lowercase().as_str() {
                "reference" => &mut body.reference,
                "receivername" => &mut body.receiver_name,
                "companyname" => &mut body.company_name,
                "address" => &mut body.address,
                "receiverdateofbirth" => &mut body.receiver_date_of_birth,
                "vesselname" => &mut body.vessel_name,
                "vesselimonumber" => &mut body.vessel_imo_number,
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
    for (name, value) in [
        ("ReceiverName", &body.receiver_name),
        ("CompanyName", &body.company_name),
        ("Address", &body.address),
        ("VesselName", &body.vessel_name),
    ] {
        if value.as_deref().is_some_and(|v| utf16_len(v) > MAX_FIELD_LENGTH) {
            errors.insert(name.into(), vec![format!("At most {MAX_FIELD_LENGTH} characters.")]);
        }
    }
    if body.parties.len() > MAX_PARTIES {
        errors.insert("Parties".into(), vec![format!("At most {MAX_PARTIES} parties.")]);
    }
    for (i, party) in body.parties.iter().enumerate().take(MAX_PARTIES) {
        let field = |name: &str| format!("Parties[{i}].{name}");
        if party.role().is_none() {
            errors.insert(field("Role"), vec![format!("A role, one of: {}.", ROLES.join(", "))]);
        }
        if party
            .kind
            .as_deref()
            .is_some_and(|k| !k.trim().is_empty() && !["person", "company"].iter().any(|v| v.eq_ignore_ascii_case(k.trim())))
        {
            errors.insert(field("Kind"), vec!["person or company.".into()]);
        }
        for (name, value) in [("Name", &party.name), ("Address", &party.address)] {
            if value.as_deref().is_some_and(|v| utf16_len(v) > MAX_FIELD_LENGTH) {
                errors.insert(field(name), vec![format!("At most {MAX_FIELD_LENGTH} characters.")]);
            }
        }
        if is_blank(&party.name) && is_blank(&party.address) {
            errors.insert(field("Name"), vec!["Enter the party's name, its address, or both.".into()]);
        }
        validate_particulars(
            &mut errors,
            &format!("Parties[{i}]."),
            !is_blank(&party.name),
            &party.identifiers,
            &party.date_of_birth,
            party.is_person(),
        );
    }
    validate_particulars(
        &mut errors,
        "Receiver",
        !is_blank(&body.receiver_name),
        &body.receiver_identifiers,
        &body.receiver_date_of_birth,
        true,
    );
    validate_particulars(&mut errors, "Company", !is_blank(&body.company_name), &body.company_identifiers, &None, false);
    // An IMO number (#69): seven digits, "IMO" before them or not, the last the check digit (a slip of a digit fails).
    if !is_blank(&body.vessel_imo_number) {
        let value = body.vessel_imo_number.as_deref().unwrap_or_default();
        if utf16_len(value) > MAX_IDENTIFIER_LENGTH || !imo_number(value).is_some_and(|d| imo_check_digit_ok(&d)) {
            errors.insert(
                "VesselImoNumber".into(),
                vec!["An IMO number: seven digits, \"IMO\" before them or not, the last one its check digit.".into()],
            );
        }
    }
    if is_blank(&body.receiver_name)
        && is_blank(&body.company_name)
        && is_blank(&body.address)
        && is_blank(&body.vessel_name)
        && is_blank(&body.vessel_imo_number)
        && body.parties.is_empty()
    {
        errors.insert(String::new(), vec!["Enter a receiver, a company, an address or a vessel.".into()]);
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
    /// The listed party's identifiers and dates of birth (#68), when the list gives any.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub listed_identifiers: Vec<IdentifierResponse>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub listed_birth_dates: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct IdentifierResponse {
    pub kind: String,
    pub value: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub country: Option<String>,
}

fn identifier_responses(m: &SubjectMatch) -> Vec<IdentifierResponse> {
    m.subject
        .identifiers
        .iter()
        .map(|i| IdentifierResponse { kind: i.kind.clone(), value: i.value.clone(), country: i.country.clone() })
        .collect()
}

pub fn kind_name(kind: crate::matching::SubjectKind) -> &'static str {
    match kind {
        crate::matching::SubjectKind::Individual => "Individual",
        crate::matching::SubjectKind::Entity => "Entity",
        crate::matching::SubjectKind::Unknown => "Unknown",
        crate::matching::SubjectKind::Vessel => "Vessel",
    }
}

fn candidate_response(m: &SubjectMatch) -> CandidateResponse {
    let listed_addresses = m
        .subject
        .addresses
        .iter()
        .map(|a| {
            if !a.country_code.is_empty() && !a.raw.contains(&a.country_code) {
                format!("{} ({})", a.raw, a.country_code)
            } else {
                a.raw.clone()
            }
        })
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
        listed_identifiers: identifier_responses(m),
        listed_birth_dates: m.subject.birth_dates.clone(),
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

/// An address in a destination under broad restrictions (#67): the row that decided it, as the table has it.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DestinationResponse {
    pub state: &'static str,
    pub code: String,
    pub kind: &'static str,
    pub name: String,
    pub restricted_by: String,
    pub legal_source: String,
    pub as_of: String,
}

pub fn destination_response(notice: &DestinationNotice) -> DestinationResponse {
    let d = &notice.destination;
    DestinationResponse {
        state: notice.state.as_str(),
        code: d.code.clone(),
        kind: d.kind.as_str(),
        name: d.name.clone(),
        restricted_by: d.restricted_by.clone(),
        legal_source: d.legal_source.clone(),
        as_of: d.as_of.format("%Y-%m-%d").to_string(),
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EntryResponse {
    /// The party (#65): its number in the request and its role; absent for the request's own fields.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub party: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub role: Option<&'static str>,
    pub kind: &'static str,
    pub input: String,
    pub compared: &'static str,
    pub cells: Vec<CellResponse>,
    pub allowed_by: Option<AllowedByResponse>,
    /// An address's destination notice (#67); absent when there is none.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub destination: Option<DestinationResponse>,
}

fn entry_response(entry: &Entry, o: &Options) -> EntryResponse {
    EntryResponse {
        party: entry.party.map(|p| p.number),
        role: entry.party.map(|p| p.role),
        kind: entry.kind.as_str(),
        input: entry.input.clone(),
        compared: entry.compared,
        cells: entry.cells.iter().map(|c| cell_response(c, o)).collect(),
        allowed_by: entry.allowed_by.as_ref().map(|a| AllowedByResponse { id: a.id, name: a.name.clone(), reason: a.reason.clone() }),
        destination: entry.destination.as_ref().map(destination_response),
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
    pub destinations_version: String,
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
    pub destinations_version: String,
    /// The earliest review date of the destinations table already passed (#67): every result of the stream is
    /// screened against a table that is due for review. Absent when none is.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub destinations_review_overdue: Option<String>,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    pub party: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub role: Option<&'static str>,
    pub kind: &'static str,
    pub cells: Vec<StreamCell>,
    pub allow_list_entry_id: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub destination: Option<DestinationResponse>,
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
        party: entry.party.map(|p| p.number),
        role: entry.party.map(|p| p.role),
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
        destination: entry.destination.as_ref().map(destination_response),
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

    fn run(
        &self,
        body: &ScreeningRequestBody,
        lists: &[Arc<LoadedList>],
        allow_list: &AllowList,
        destinations: &Destinations,
        now: Time,
    ) -> (ScreeningResult, Vec<ListResponse>) {
        let screened: Vec<ScreenedList> = lists
            .iter()
            .filter(|l| l.switched_off.is_none())
            .map(|l| ScreenedList { code: l.code.clone(), name: l.name.clone(), index: l.index.clone() })
            .collect();
        let request = ScreeningRequest {
            receiver_name: body.receiver_name.clone().unwrap_or_default(),
            company_name: body.company_name.clone().unwrap_or_default(),
            address: body.address.clone().unwrap_or_default(),
            receiver_identifiers: body.receiver_identifiers.clone(),
            company_identifiers: body.company_identifiers.clone(),
            receiver_birth_date: body.receiver_date_of_birth.as_deref().and_then(entered_birth_date),
            vessel_name: body.vessel_name.clone().unwrap_or_default(),
            vessel_imo_number: body.vessel_imo_number.as_deref().and_then(imo_number),
        };
        let parties: Vec<Party> = body
            .parties
            .iter()
            .map(|p| Party {
                role: p.role().expect("validated: a known role"),
                name: p.name.clone().unwrap_or_default(),
                person: p.is_person(),
                address: p.address.clone().unwrap_or_default(),
                identifiers: p.identifiers.clone(),
                birth_date: p.date_of_birth.as_deref().and_then(entered_birth_date),
            })
            .collect();
        let result =
            self.screener.screen_parties(&request, &parties, &screened, allow_list, destinations).expect("validated: at least one field");

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
        let (lists, allow_list, destinations, now) =
            (self.catalog.lists(), self.catalog.allow_list(), self.catalog.destinations(), self.now());
        let (result, rows) = self.run(body, &lists, &allow_list, &destinations, now);
        ScreeningResponse {
            reference: body.reference().to_string(),
            outcome: result.outcome.as_str(),
            summary: self.summary(&result, &lists, &destinations, now),
            disclaimer: DISCLAIMER,
            matcher_version: result.matcher_version.clone(),
            lists: rows,
            entries: result.entries.iter().map(|e| entry_response(e, self.options())).collect(),
            allow_list_version: result.allow_list_version.clone(),
            destinations_version: result.destinations_version.clone(),
        }
    }

    /// One stream record against the stream's snapshot.
    pub fn screen_for_stream(
        &self,
        body: &ScreeningRequestBody,
        lists: &[Arc<LoadedList>],
        allow_list: &AllowList,
        destinations: &Destinations,
    ) -> ScreeningResult {
        self.run(body, lists, allow_list, destinations, self.now()).0
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
            // A party's number and role (a word from a fixed set): never what was entered.
            if let Some(p) = entry.party {
                results.push_str(&format!("party {} {} ", p.number, p.role));
            }
            results.push_str(entry.kind.as_str());
            if entry.identifiers > 0 {
                results.push_str(&format!(" (with {} identifiers)", entry.identifiers));
            }
            // The table's own code for the row: never what was entered.
            if let Some(d) = &entry.destination {
                results.push_str(&format!(" (destination {} {})", d.state.as_str(), d.destination.code));
            }
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
                    format!(
                        "{} {}{allowed} {}%{best}",
                        cell.list,
                        cell.outcome.map(|o| o.as_str()).unwrap_or_default(),
                        cell.percent.unwrap_or_default()
                    )
                })
                .collect();
            results.push_str(&cells.join("; "));
            results.push_str(" | ");
        }
        let message = format!(
            "Screening {reference}: {}; matcher {}; lists {}; allow-list {}; destinations {}; {}",
            result.outcome.as_str(),
            result.matcher_version,
            versions.join(", "),
            result.allow_list_version,
            result.destinations_version,
            results.trim_end_matches([' ', '|'])
        );
        self.logger.info(&message, &[("Reference", reference.into())]);
    }

    /// The summary line, worded precisely: never "not sanctioned" or "cleared", always which lists, as of when.
    pub fn summary(&self, result: &ScreeningResult, lists: &[Arc<LoadedList>], destinations: &Destinations, now: Time) -> String {
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
            gap +=
                &format!(" Warning: couldn't be updated within {age} hours, so searched as last downloaded: {}.", overdue[age].join("; "));
        }
        if let Some(due) = destinations.review_overdue(now.date_naive()) {
            gap +=
                &format!(" Warning: the table of destinations under broad restrictions was due for review on {}.", due.format("%-d %b %Y"));
        }
        let destination = destination_sentences(result);

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
                .filter_map(|e| {
                    e.allowed_by.as_ref().filter(|_| e.cells.iter().any(|c| c.allowed)).map(|a| format!("{}: {}", a.name, a.reason))
                })
                .collect();
            allowed = format!(
                " A possible match on {} is allowed by the allow-list ({}); a person can still review it.",
                allowed_on.join(", "),
                reasons.join("; ")
            );
        }

        match result.outcome {
            OverallOutcome::Match => format!(
                "Possible match on {}. A person must review it before anything else happens.{destination}{gap}{allowed}",
                hits.join(", ")
            ),
            // Raised by a destination alone (#67): what the lists answered follows.
            OverallOutcome::Review if hits.is_empty() => {
                let searched = match (checked.is_empty(), allowed.is_empty()) {
                    (true, _) => String::new(),
                    (false, true) => format!(" No match found in: {}.", checked.join("; ")),
                    (false, false) => format!(" No match found in: {}, apart from what the allow-list allows.", checked.join("; ")),
                };
                format!("Review needed for the destination.{destination}{searched}{gap}{allowed}")
            }
            OverallOutcome::Review => {
                format!("Review needed: a similar listed party on {}.{destination}{gap}{allowed}", hits.join(", "))
            }
            OverallOutcome::Incomplete if checked.is_empty() => format!("Incomplete: no list could be checked.{gap}"),
            OverallOutcome::Incomplete => format!("Incomplete. No match found in: {}.{gap}{allowed}", checked.join("; ")),
            OverallOutcome::NoMatch if !allowed.is_empty() => {
                format!("No match found in: {}, apart from what the allow-list allows.{gap}{allowed}", checked.join("; "))
            }
            OverallOutcome::NoMatch => format!("No match found in: {}.{gap}", checked.join("; ")),
        }
    }
}

/// One sentence per destination notice (#67), each place once, in the order the addresses were entered.
fn destination_sentences(result: &ScreeningResult) -> String {
    let mut seen: Vec<(&str, DestinationState)> = Vec::new();
    let mut out = String::new();
    for notice in result.entries.iter().filter_map(|e| e.destination.as_ref()) {
        let d = &notice.destination;
        if seen.contains(&(d.code.as_str(), notice.state)) {
            continue;
        }
        seen.push((d.code.as_str(), notice.state));
        let as_of = d.as_of.format("%-d %b %Y");
        out += &match notice.state {
            DestinationState::Restricted => format!(
                " Destination under broad restrictions by {}: {} (as of {as_of}). Check the rules for the goods.",
                d.restricted_by, d.name
            ),
            DestinationState::PossiblyRestricted => format!(
                " The destination may be in an area under broad restrictions by {}: {}, and the address doesn't tell which side of the line (as of {as_of}).",
                d.restricted_by, d.name
            ),
        };
    }
    out
}
