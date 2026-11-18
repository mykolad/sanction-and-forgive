//! Screening: each entry against each list separately (spec section 5.7), and the allow-list. Deterministic; stores
//! and logs nothing.

use super::HashMap;
use std::sync::Arc;

use super::addresses::parse_address;
use super::addresses::AddressState;
use super::data::{data, MatcherData};
use super::destinations::{DestinationNotice, Destinations};
use super::identifiers::{comparable, compare_birth_date, entered_keys, identifier_key, BirthDateComparison};
use super::index::{vessel_name, ListIndex, ListedIdentifier, ListedSubject, PreparedName, SubjectMatch};
use super::names::NameParser;
use super::options::{Options, Outcome};
use super::text::{RawToken, Tokenizer};
use crate::lists::parse::IMO_NUMBER;

/// What the user entered; every field optional, at least one required.
#[derive(Debug, Clone, Default)]
pub struct ScreeningRequest {
    pub receiver_name: String,
    pub company_name: String,
    pub address: String,
    /// Identifiers entered for the receiver and for the company (#68), as written; each compared by its key.
    pub receiver_identifiers: Vec<String>,
    pub company_identifiers: Vec<String>,
    /// The receiver's date of birth (#68), as identifiers::entered_birth_date reads it.
    pub receiver_birth_date: Option<String>,
    /// The vessel (#69): its name, and its IMO number's seven digits (lists::parse::imo_number); either or both.
    pub vessel_name: String,
    pub vessel_imo_number: Option<String>,
}

/// A list as the screener sees it; no index means Not checked.
#[derive(Clone)]
pub struct ScreenedList {
    pub code: String,
    pub name: String,
    pub index: Option<Arc<ListIndex>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EntryKind {
    Receiver,
    Company,
    Address,
    /// A vessel (#69): its name against the lists' vessels only, and its IMO number.
    Vessel,
}

impl EntryKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            EntryKind::Receiver => "Receiver",
            EntryKind::Company => "Company",
            EntryKind::Address => "Address",
            EntryKind::Vessel => "Vessel",
        }
    }
}

/// A shipment's party (#65, Rust first): its role, and a name (a person's or a company's), an address, or both.
#[derive(Debug, Clone, Default)]
pub struct Party {
    /// One of ROLES: a word from a fixed set, never entered text, so the audit line can name it.
    pub role: &'static str,
    pub name: String,
    pub person: bool,
    pub address: String,
    /// The name's identifiers and, for a person, date of birth (#68).
    pub identifiers: Vec<String>,
    pub birth_date: Option<String>,
}

/// The roles a party can have.
pub const ROLES: [&str; 7] = ["shipper", "consignee", "notifyParty", "forwarder", "carrier", "endUser", "other"];

/// Which party an entry belongs to: its number in the request (1-based) and its role.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PartyTag {
    pub number: u32,
    pub role: &'static str,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OverallOutcome {
    Match,
    Review,
    Incomplete,
    NoMatch,
}

impl OverallOutcome {
    pub fn as_str(&self) -> &'static str {
        match self {
            OverallOutcome::Match => "Match",
            OverallOutcome::Review => "Review",
            OverallOutcome::Incomplete => "Incomplete",
            OverallOutcome::NoMatch => "NoMatch",
        }
    }
}

/// One list's answer for one entry. Not checked: no percentage, no outcome.
#[derive(Debug, Clone)]
pub struct Cell {
    pub list: String,
    pub checked: bool,
    pub percent: Option<i32>,
    pub outcome: Option<Outcome>,
    pub best: Option<SubjectMatch>,
    pub others: Vec<SubjectMatch>,
    pub allowed: bool,
}

#[derive(Debug, Clone)]
pub struct Entry {
    pub kind: EntryKind,
    pub input: String,
    pub compared: &'static str,
    pub cells: Vec<Cell>,
    pub allowed_by: Option<AllowListEntry>,
    /// The party it belongs to; None for the request's own receiver, company and address.
    pub party: Option<PartyTag>,
    /// How many identifiers were entered with the name (#68): the audit line says so, never which.
    pub identifiers: usize,
    /// An address in a destination under broad restrictions (#67): it raises the outcome to at least Review.
    pub destination: Option<DestinationNotice>,
}

#[derive(Debug, Clone)]
pub struct ScreeningResult {
    pub outcome: OverallOutcome,
    pub matcher_version: String,
    pub entries: Vec<Entry>,
    pub allow_list_version: String,
    pub destinations_version: String,
}

/// One listed subject found for a name on one list.
#[derive(Debug, Clone)]
pub struct ListedHit {
    pub list: String,
    pub matched: SubjectMatch,
}

#[derive(Debug, PartialEq, Eq)]
pub struct NothingEntered;

impl std::fmt::Display for NothingEntered {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("enter a receiver, a company, an address or a vessel")
    }
}

pub struct Screener {
    options: Options,
    data: &'static MatcherData,
    pub matcher_version: String,
}

/// The reference's IsNullOrWhiteSpace check and Trim (char.IsWhiteSpace: Unicode White_Space).
fn clean(value: &str) -> &str {
    value.trim_matches(char::is_whitespace)
}

impl Screener {
    pub fn new(options: Options) -> Self {
        let data = data();
        let matcher_version = options.version(data);
        Screener { options, data, matcher_version }
    }

    pub fn options(&self) -> &Options {
        &self.options
    }

    pub fn screen(
        &self,
        request: &ScreeningRequest,
        lists: &[ScreenedList],
        allow_list: &AllowList,
    ) -> Result<ScreeningResult, NothingEntered> {
        self.screen_parties(request, &[], lists, allow_list, &Destinations::empty())
    }

    /// A shipment: the request's own receiver, company and address, then each party's name and address, every
    /// entry against every list, and every address against the destinations under broad restrictions (#67); the outcome
    /// is the highest of them all (#65).
    pub fn screen_parties(
        &self,
        request: &ScreeningRequest,
        parties: &[Party],
        lists: &[ScreenedList],
        allow_list: &AllowList,
        destinations: &Destinations,
    ) -> Result<ScreeningResult, NothingEntered> {
        let checks = Checks { lists, allow_list, destinations };
        let own = Particulars {
            receiver_identifiers: &request.receiver_identifiers,
            company_identifiers: &request.company_identifiers,
            birth_date: request.receiver_birth_date.as_deref(),
        };
        let mut entries = self.entries(&request.receiver_name, &request.company_name, &request.address, None, &own, &checks);
        entries.extend(self.vessel_entry(&request.vessel_name, request.vessel_imo_number.as_deref(), lists));
        for (i, party) in parties.iter().enumerate() {
            let tag = PartyTag { number: i as u32 + 1, role: party.role };
            let (receiver, company) = if party.person { (party.name.as_str(), "") } else { ("", party.name.as_str()) };
            let particulars = Particulars {
                receiver_identifiers: if party.person { &party.identifiers } else { &[] },
                company_identifiers: if party.person { &[] } else { &party.identifiers },
                birth_date: if party.person { party.birth_date.as_deref() } else { None },
            };
            entries.extend(self.entries(receiver, company, &party.address, Some(tag), &particulars, &checks));
        }
        if entries.is_empty() {
            return Err(NothingEntered);
        }
        Ok(ScreeningResult {
            outcome: overall(&entries),
            matcher_version: self.matcher_version.clone(),
            entries,
            allow_list_version: allow_list.version.clone(),
            destinations_version: destinations.version.clone(),
        })
    }

    /// One party's entries (or the request's own): a receiver, a company and an address, each when entered.
    fn entries(
        &self,
        receiver: &str,
        company: &str,
        address: &str,
        party: Option<PartyTag>,
        particulars: &Particulars,
        checks: &Checks,
    ) -> Vec<Entry> {
        let (lists, allow_list) = (checks.lists, checks.allow_list);
        let (receiver, company, address) = (clean(receiver), clean(company), clean(address));
        let mut tokenizer = Tokenizer::new(self.data);
        let parser = NameParser { data: self.data };
        let address_form = (!address.is_empty()).then(|| parse_address(self.data, &mut tokenizer, address, ""));

        let mut entries = Vec::new();
        for (kind, name) in [(EntryKind::Receiver, receiver), (EntryKind::Company, company)] {
            if name.is_empty() {
                continue;
            }
            let tokens = tokenizer.tokenize(name, true);
            let (entity, person) = (parser.entity(&tokens), parser.person(&tokens));
            let prepared = PreparedName { entity: &entity, person: &person, address: address_form.as_ref() };
            let compared =
                if address_form.is_some() { "Name, with the address where the list has one" } else { "Name only: no address entered" };
            let allowed_by = allow_list.find_tokens(&tokens).cloned();
            let entered = if kind == EntryKind::Receiver { particulars.receiver_identifiers } else { particulars.company_identifiers };
            let keys: Vec<String> = entered.iter().flat_map(|v| entered_keys(v)).filter(|k| comparable(k)).collect();
            let birth_date = if kind == EntryKind::Receiver { particulars.birth_date } else { None };
            let cells = lists
                .iter()
                .map(|list| {
                    let mut cell = self.to_cell(list, |index| {
                        let mut matches = index.search_name(&prepared, &self.options);
                        apply_particulars(index, &mut matches, &keys, None, birth_date, &self.options);
                        matches
                    });
                    if allowed_by.is_some() && cell.outcome == Some(Outcome::Review) {
                        cell.allowed = true;
                    }
                    cell
                })
                .collect();
            entries.push(Entry {
                kind,
                input: name.to_string(),
                compared,
                cells,
                allowed_by,
                party,
                destination: None,
                identifiers: entered.iter().filter(|v| comparable(&identifier_key(v))).count(),
            });
        }
        if let Some(form) = &address_form {
            let cells = lists.iter().map(|list| self.to_cell(list, |index| index.search_address(form, &self.options))).collect();
            let destination = checks.destinations.find(self.data, &mut tokenizer, address);
            entries.push(Entry {
                kind: EntryKind::Address,
                input: address.to_string(),
                compared: "Address only",
                cells,
                allowed_by: None,
                party,
                destination,
                identifiers: 0,
            });
        }
        entries
    }

    /// A vessel (#69): its name against each list's vessels (read as a company's name, without a prefix like "M/V"),
    /// and its IMO number as an identifier: an equal one is a Match, whatever the name. None when neither is entered.
    fn vessel_entry(&self, name: &str, imo_number: Option<&str>, lists: &[ScreenedList]) -> Option<Entry> {
        let name = clean(name);
        if name.is_empty() && imo_number.is_none() {
            return None;
        }
        let tokens = Tokenizer::new(self.data).tokenize(vessel_name(name), true);
        let entity = NameParser { data: self.data }.entity(&tokens);
        let prepared = PreparedName { entity: &entity, person: &entity, address: None };
        let keys: Vec<String> = imo_number.map(str::to_string).into_iter().collect();
        let cells = lists
            .iter()
            .map(|list| {
                // A list that lists ships but whose import has none was imported before vessels were kept: Not checked
                // until it's imported again, never a NoMatch it can't vouch for.
                let unready = list.index.as_ref().is_some_and(|index| index.vessels().is_none())
                    && crate::lists::catalogue::LISTS_WITH_VESSELS.contains(&list.code.as_str());
                let unchecked = ScreenedList { code: list.code.clone(), name: list.name.clone(), index: None };
                self.to_cell(if unready { &unchecked } else { list }, |index| {
                    // A list without vessels has none to find: searched, and nothing found.
                    let Some(vessels) = index.vessels() else { return Vec::new() };
                    let mut matches = if name.is_empty() { Vec::new() } else { vessels.search_name(&prepared, &self.options) };
                    apply_particulars(vessels, &mut matches, &keys, Some(IMO_NUMBER), None, &self.options);
                    matches
                })
            })
            .collect();
        let (input, compared) = match (name.is_empty(), imo_number) {
            (true, Some(imo)) => (format!("IMO {imo}"), "IMO number only: no name entered"),
            (false, Some(_)) => (name.to_string(), "Vessel name, and the IMO number"),
            (_, None) => (name.to_string(), "Vessel name only: no IMO number entered"),
        };
        Some(Entry {
            kind: EntryKind::Vessel,
            input,
            compared,
            cells,
            allowed_by: None,
            party: None,
            identifiers: keys.len(),
            destination: None,
        })
    }

    /// Every listed subject at Review or above for name, on each list that can be searched.
    pub fn name_hits(&self, name: &str, lists: &[ScreenedList]) -> Result<Vec<ListedHit>, &'static str> {
        let cleaned = clean(name);
        if cleaned.is_empty() {
            return Err("enter a name");
        }
        let tokens = Tokenizer::new(self.data).tokenize(cleaned, true);
        let parser = NameParser { data: self.data };
        let (entity, person) = (parser.entity(&tokens), parser.person(&tokens));
        let prepared = PreparedName { entity: &entity, person: &person, address: None };
        let mut hits = Vec::new();
        for list in lists {
            if let Some(index) = &list.index {
                for matched in index.search_name(&prepared, &self.options) {
                    if matched.outcome != Outcome::NoMatch {
                        hits.push(ListedHit { list: list.code.clone(), matched });
                    }
                }
            }
        }
        Ok(hits)
    }

    fn to_cell(&self, list: &ScreenedList, search: impl FnOnce(&ListIndex) -> Vec<SubjectMatch>) -> Cell {
        let Some(index) = &list.index else {
            return Cell {
                list: list.code.clone(),
                checked: false,
                percent: None,
                outcome: None,
                best: None,
                others: Vec::new(),
                allowed: false,
            };
        };
        let mut matches = search(index).into_iter();
        let best = matches.next();
        let others: Vec<SubjectMatch> =
            matches.filter(|m| m.outcome != Outcome::NoMatch).take(self.options.hits_per_cell.saturating_sub(1)).collect();
        let percent = best.as_ref().map(|b| b.percent).unwrap_or(0);
        let outcome = self.options.to_outcome(percent);
        Cell { list: list.code.clone(), checked: true, percent: Some(percent), outcome: Some(outcome), best, others, allowed: false }
    }
}

/// What was entered with a party's name besides it (#68): identifiers for the receiver or the company, and the
/// receiver's date of birth.
struct Particulars<'a> {
    receiver_identifiers: &'a [String],
    company_identifiers: &'a [String],
    birth_date: Option<&'a str>,
}

/// The owner's rules for identifiers and dates of birth (#68), on one list's matches for a name:
/// - a subject that lists an identifier equal to an entered one is a Match (100%), whether or not its name matched,
///   the identifier its own explained line;
/// - a subject the name found whose listed identifiers are none of the entered ones keeps its percentage: a line says
///   they differ (never lower: a list may have an old or a second passport);
/// - a date of birth is said to be the same as or different from the listed ones, and changes nothing.
///
/// `only`: the one kind of listed identifier compared (a vessel's IMO number with listed IMO numbers alone), or every kind.
fn apply_particulars(
    index: &ListIndex,
    matches: &mut Vec<SubjectMatch>,
    keys: &[String],
    only: Option<&str>,
    birth_date: Option<&str>,
    o: &Options,
) {
    let compared = |i: &ListedIdentifier| only.is_none_or(|kind| i.kind == kind);
    let say = |m: &mut SubjectMatch, line: String, first: bool| {
        if first {
            m.alignment.insert(0, line.clone());
            m.notes.insert(0, line);
        } else {
            m.alignment.push(line.clone());
            m.notes.push(line);
        }
    };
    let mut identified: Vec<Arc<ListedSubject>> = Vec::new();
    if !keys.is_empty() {
        for (subject, identifier) in index.identified(keys, compared) {
            let country = identifier.country.as_deref().map(|c| format!(" ({c})")).unwrap_or_default();
            let line = format!("Identifier: equals the listed {} {}{country}", identifier.kind, identifier.value);
            match matches.iter_mut().find(|m| Arc::ptr_eq(&m.subject, &subject)) {
                Some(m) => {
                    // The name's best candidate even when it's no match: then say the identifier is what matched.
                    if m.outcome == Outcome::NoMatch {
                        say(m, "Name: not what matched: the identifier did".to_string(), true);
                    }
                    m.percent = 100;
                    m.outcome = o.to_outcome(100);
                    say(m, line, true);
                }
                None => matches.push(SubjectMatch {
                    alias: subject.primary_name.clone(),
                    subject: subject.clone(),
                    percent: 100,
                    outcome: o.to_outcome(100),
                    alignment: vec![line.clone(), "Name: not what matched: the identifier did".to_string()],
                    address_state: AddressState::NotCompared,
                    words: Vec::new(),
                    notes: vec![line, "Name: not what matched: the identifier did".to_string()],
                }),
            }
            identified.push(subject);
        }
        for m in matches.iter_mut() {
            let listed: Vec<&ListedIdentifier> = m.subject.identifiers.iter().filter(|i| compared(i)).collect();
            if m.outcome == Outcome::NoMatch || listed.is_empty() || identified.iter().any(|s| Arc::ptr_eq(s, &m.subject)) {
                continue;
            }
            let more = if listed.len() > 3 { ", …" } else { "" };
            let listed: Vec<String> = listed.iter().take(3).map(|i| format!("{} {}", i.kind, i.value)).collect();
            say(m, format!("Identifiers: the entered ones aren't among the listed ({}{more}); a person decides", listed.join(", ")), false);
        }
    }
    if let Some(entered) = birth_date {
        for m in matches.iter_mut().filter(|m| m.outcome != Outcome::NoMatch && !m.subject.birth_dates.is_empty()) {
            let listed = m.subject.birth_dates.join(", ");
            let same = m.subject.birth_dates.iter().any(|l| compare_birth_date(entered, l) == Some(BirthDateComparison::Same));
            let line = if same {
                format!("Date of birth: the entered one is among the listed ({listed})")
            } else {
                format!("Date of birth: differs from the listed ({listed})")
            };
            say(m, line, false);
        }
    }
    // Best first again, then by name, as search_name orders them.
    matches.sort_by(|a, b| b.percent.cmp(&a.percent).then_with(|| a.subject.primary_name.cmp(&b.subject.primary_name)));
}

/// What one screening is checked against.
struct Checks<'a> {
    lists: &'a [ScreenedList],
    allow_list: &'a AllowList,
    destinations: &'a Destinations,
}

/// Match > Review > Incomplete > NoMatch. A destination notice is at least Review, and the allow-list never clears it.
fn overall(entries: &[Entry]) -> OverallOutcome {
    let cells: Vec<&Cell> = entries.iter().flat_map(|e| e.cells.iter()).collect();
    if cells.iter().any(|c| c.outcome == Some(Outcome::Match)) {
        OverallOutcome::Match
    } else if cells.iter().any(|c| c.outcome == Some(Outcome::Review) && !c.allowed) || entries.iter().any(|e| e.destination.is_some()) {
        OverallOutcome::Review
    } else if cells.is_empty() || cells.iter().any(|c| !c.checked) {
        OverallOutcome::Incomplete
    } else {
        OverallOutcome::NoMatch
    }
}

/// An allow-list entry: a name known to be a legitimate party, and why.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AllowListEntry {
    pub id: i32,
    pub name: String,
    pub reason: String,
}

/// Names whose possible matches (Review) are shown as allowed. Immutable, with a version.
#[derive(Debug, Clone)]
pub struct AllowList {
    by_key: HashMap<String, AllowListEntry>,
    pub version: String,
}

impl AllowList {
    pub fn empty() -> Self {
        AllowList { by_key: HashMap::default(), version: "none".to_string() }
    }

    /// Of two entries for the same name, the older (lower id) wins.
    pub fn build(entries: &[AllowListEntry], version: &str) -> Self {
        let mut sorted = entries.to_vec();
        sorted.sort_by_key(|e| e.id);
        let mut tokenizer = Tokenizer::new(data());
        let mut by_key = HashMap::default();
        for entry in sorted {
            let k = allow_list_key(&tokenizer.tokenize(&entry.name, true));
            if !k.is_empty() {
                by_key.entry(k).or_insert(entry);
            }
        }
        AllowList { by_key, version: version.to_string() }
    }

    pub fn count(&self) -> usize {
        self.by_key.len()
    }

    /// This list without entry id ("12-5": version 12 without entry 5).
    pub fn without(&self, id: i32) -> Self {
        let by_key = self.by_key.iter().filter(|(_, e)| e.id != id).map(|(k, e)| (k.clone(), e.clone())).collect();
        AllowList { by_key, version: format!("{}-{id}", self.version) }
    }

    /// The entry for exactly this name, if there is one.
    pub fn find(&self, name: &str) -> Option<&AllowListEntry> {
        if self.by_key.is_empty() {
            return None;
        }
        self.by_key.get(&allow_list_key_of(name))
    }

    fn find_tokens(&self, tokens: &[RawToken]) -> Option<&AllowListEntry> {
        if self.by_key.is_empty() {
            return None;
        }
        self.by_key.get(&allow_list_key(tokens))
    }
}

/// What the matcher reads from a name: its tokens' text, initials runs marked "a.b.".
pub fn allow_list_key_of(name: &str) -> String {
    allow_list_key(&Tokenizer::new(data()).tokenize(name, true))
}

fn allow_list_key(tokens: &[RawToken]) -> String {
    tokens
        .iter()
        .map(|t| match &t.initials {
            Some(initials) => initials.iter().map(|l| l.text.as_str()).collect::<Vec<_>>().join(".") + ".",
            None => t.token.text.clone(),
        })
        .collect::<Vec<_>>()
        .join(" ")
}
