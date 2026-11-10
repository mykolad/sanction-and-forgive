//! Screening: each entry against each list separately (spec section 5.7), and the allow-list. Deterministic; stores
//! and logs nothing.

use super::HashMap;
use std::sync::Arc;

use super::addresses::parse_address;
use super::data::{data, MatcherData};
use super::index::{ListIndex, PreparedName, SubjectMatch};
use super::names::NameParser;
use super::options::{Options, Outcome};
use super::text::{RawToken, Tokenizer};

/// What the user entered; every field optional, at least one required.
#[derive(Debug, Clone, Default)]
pub struct ScreeningRequest {
    pub receiver_name: String,
    pub company_name: String,
    pub address: String,
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
}

impl EntryKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            EntryKind::Receiver => "Receiver",
            EntryKind::Company => "Company",
            EntryKind::Address => "Address",
        }
    }
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
}

#[derive(Debug, Clone)]
pub struct ScreeningResult {
    pub outcome: OverallOutcome,
    pub matcher_version: String,
    pub entries: Vec<Entry>,
    pub allow_list_version: String,
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
        f.write_str("enter a receiver, a company or an address")
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
        let (receiver, company, address) = (clean(&request.receiver_name), clean(&request.company_name), clean(&request.address));
        if receiver.is_empty() && company.is_empty() && address.is_empty() {
            return Err(NothingEntered);
        }
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
            let cells = lists
                .iter()
                .map(|list| {
                    let mut cell = self.to_cell(list, |index| index.search_name(&prepared, &self.options));
                    if allowed_by.is_some() && cell.outcome == Some(Outcome::Review) {
                        cell.allowed = true;
                    }
                    cell
                })
                .collect();
            entries.push(Entry { kind, input: name.to_string(), compared, cells, allowed_by });
        }
        if let Some(form) = &address_form {
            let cells = lists.iter().map(|list| self.to_cell(list, |index| index.search_address(form, &self.options))).collect();
            entries.push(Entry { kind: EntryKind::Address, input: address.to_string(), compared: "Address only", cells, allowed_by: None });
        }
        Ok(ScreeningResult {
            outcome: overall(&entries),
            matcher_version: self.matcher_version.clone(),
            entries,
            allow_list_version: allow_list.version.clone(),
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

fn overall(entries: &[Entry]) -> OverallOutcome {
    let cells: Vec<&Cell> = entries.iter().flat_map(|e| e.cells.iter()).collect();
    if cells.iter().any(|c| c.outcome == Some(Outcome::Match)) {
        OverallOutcome::Match
    } else if cells.iter().any(|c| c.outcome == Some(Outcome::Review) && !c.allowed) {
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
