//! One list's in-memory index (spec section 5.5): aliases read both ways, document frequencies, postings by fold key,
//! fuzzy vocabularies, address postings. Immutable once built.

use std::any::Any;
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};

use super::addresses::{compare_address, compare_address_only, parse_address, AddressComparison, AddressForm, AddressOnly, AddressState};
use super::compare::{FuzzyVocabulary, TokenComparer};
use super::data::{data, MatcherData};
use super::names::{NameForm, NameParser};
use super::options::{Options, Outcome};
use super::scorer::{NameScorer, ScoreValue, WordAlignment};
use super::text::{fold_key, Token, TokenRef, Tokenizer};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubjectKind {
    Individual,
    Entity,
    Unknown,
}

#[derive(Debug, Clone)]
pub struct ListedName {
    pub full_name: String,
    pub is_weak: bool,
}

#[derive(Debug, Clone)]
pub struct ListedAddress {
    pub raw: String,
    pub country_code: String,
}

/// A listed party as the matcher sees it; `names` includes the primary name.
#[derive(Debug, Clone)]
pub struct ListedSubject {
    pub external_id: String,
    pub kind: SubjectKind,
    pub primary_name: String,
    pub names: Vec<ListedName>,
    pub addresses: Vec<ListedAddress>,
    pub programme: Option<String>,
    pub source_url: Option<String>,
}

/// One listed subject's match for one entry: the best of its names.
#[derive(Debug, Clone)]
pub struct SubjectMatch {
    pub subject: Arc<ListedSubject>,
    pub percent: i32,
    pub outcome: Outcome,
    pub alias: String,
    pub alignment: Vec<String>,
    pub address_state: AddressState,
    pub words: Vec<WordAlignment>,
    pub notes: Vec<String>,
}

struct AliasEntry {
    subject: u32,
    name: String,
    is_weak: bool,
    form: NameForm,
}

/// An entry parsed once per request, then searched in every list.
pub struct PreparedName<'a> {
    pub entity: &'a NameForm,
    pub person: &'a NameForm,
    pub address: Option<&'a AddressForm>,
}

/// One name search's working memory, kept between searches of the index: votes by alias and the best alias by
/// subject, in arrays the size of the index, reset after use through what was touched.
struct SearchState {
    votes: Vec<i32>,
    voted: Vec<u32>,
    slot: Vec<u32>,
    bests: Vec<BestAlias>,
}

struct BestAlias {
    subject: u32,
    alias: u32,
    score: ScoreValue,
}

pub struct ListIndex {
    data: &'static MatcherData,
    subjects: Vec<Arc<ListedSubject>>,
    aliases: Vec<AliasEntry>,
    addresses: Vec<Vec<AddressForm>>,
    /// By token key: ln(aliases / aliases with the key), the weight before its floor.
    idf: HashMap<String, f64>,
    aliases_by_fold: HashMap<String, Box<[u32]>>,
    name_vocabulary: FuzzyVocabulary,
    subjects_by_address_key: HashMap<String, Box<[u32]>>,
    address_vocabulary: FuzzyVocabulary,
    states: Mutex<Vec<SearchState>>,
    memo: Mutex<HashMap<String, Arc<dyn Any + Send + Sync>>>,
}

/// A map that remembers the order its keys were first added: the vocabularies are built in the reference's dictionary
/// order, so the fuzzy lookups enumerate candidates in the same order.
#[derive(Default)]
struct OrderedPostings {
    keys: Vec<String>,
    ids: HashMap<String, Vec<u32>>,
}

impl OrderedPostings {
    fn post(&mut self, key: &str, id: usize) {
        match self.ids.get_mut(key) {
            Some(list) => {
                if list.last() != Some(&(id as u32)) {
                    list.push(id as u32);
                }
            }
            None => {
                self.keys.push(key.to_string());
                self.ids.insert(key.to_string(), vec![id as u32]);
            }
        }
    }

    fn packed(self) -> (Vec<String>, HashMap<String, Box<[u32]>>) {
        let ids = self.ids.into_iter().map(|(k, v)| (k, v.into_boxed_slice())).collect();
        (self.keys, ids)
    }
}

fn distinct_tokens(tokens: impl IntoIterator<Item = TokenRef>) -> Vec<TokenRef> {
    let mut out: Vec<TokenRef> = Vec::new();
    for t in tokens {
        if !out.iter().any(|o| Arc::ptr_eq(o, &t)) {
            out.push(t);
        }
    }
    out
}

fn lookup_keys(token: &Token) -> Vec<&str> {
    if token.folds.is_empty() {
        vec![token.text.as_str()]
    } else {
        token.folds.iter().map(String::as_str).collect()
    }
}

fn address_keys(address: &AddressForm) -> Vec<String> {
    let mut out: Vec<String> =
        address.words.iter().flat_map(|w| lookup_keys(w).into_iter().map(str::to_string).collect::<Vec<_>>()).collect();
    out.extend(address.places.iter().map(|p| format!("#place:{p}")));
    out
}

impl ListIndex {
    pub fn build(subjects: Vec<ListedSubject>) -> ListIndex {
        let data = data();
        let mut tokenizer = Tokenizer::interning(data);
        let parser = NameParser { data };
        let mut aliases = Vec::new();
        let mut addresses = Vec::with_capacity(subjects.len());
        for (s, subject) in subjects.iter().enumerate() {
            let mut seen = HashSet::new();
            let primary = ListedName { full_name: subject.primary_name.clone(), is_weak: false };
            for name in subject.names.iter().chain(std::iter::once(&primary)) {
                if !seen.insert(name.full_name.as_str()) {
                    continue;
                }
                let tokens = tokenizer.tokenize(&name.full_name, true);
                if subject.kind != SubjectKind::Individual {
                    let form = parser.entity(&tokens);
                    if !form.is_empty() {
                        aliases.push(AliasEntry { subject: s as u32, name: name.full_name.clone(), is_weak: name.is_weak, form });
                    }
                }
                if subject.kind != SubjectKind::Entity {
                    let form = parser.person(&tokens);
                    if !form.is_empty() {
                        aliases.push(AliasEntry { subject: s as u32, name: name.full_name.clone(), is_weak: name.is_weak, form });
                    }
                }
            }
            addresses
                .push(subject.addresses.iter().map(|a| parse_address(data, &mut tokenizer, &a.raw, &a.country_code)).collect::<Vec<_>>());
        }

        let mut document_frequency: HashMap<String, usize> = HashMap::new();
        let mut by_fold = OrderedPostings::default();
        for (a, alias) in aliases.iter().enumerate() {
            for token in distinct_tokens(alias.form.core.iter().cloned()) {
                *document_frequency.entry(token.key().to_string()).or_default() += 1;
                for k in lookup_keys(&token) {
                    by_fold.post(k, a);
                }
            }
        }
        let mut by_address = OrderedPostings::default();
        for (s, list) in addresses.iter().enumerate() {
            for address in list {
                for k in address_keys(address) {
                    by_address.post(&k, s);
                }
            }
        }
        // The logarithm once per key here, not once per search. f64::ln is the C library's log on Linux (glibc), as
        // .NET's Math.Log is: the same bits for every argument.
        let alias_count = aliases.len().max(1) as f64;
        let idf = document_frequency.into_iter().map(|(k, frequency)| (k, (alias_count / frequency.max(1) as f64).ln())).collect();
        let (fold_keys, aliases_by_fold) = by_fold.packed();
        let (address_keys, subjects_by_address_key) = by_address.packed();
        let address_vocabulary: Vec<String> = address_keys.into_iter().filter(|k| !k.starts_with('#')).collect();
        ListIndex {
            data,
            subjects: subjects.into_iter().map(Arc::new).collect(),
            aliases,
            addresses,
            idf,
            aliases_by_fold,
            name_vocabulary: FuzzyVocabulary::new(&fold_keys),
            subjects_by_address_key,
            address_vocabulary: FuzzyVocabulary::new(&address_vocabulary),
            states: Mutex::new(Vec::new()),
            memo: Mutex::new(HashMap::new()),
        }
    }

    pub fn subjects(&self) -> &[Arc<ListedSubject>] {
        &self.subjects
    }

    pub fn alias_count(&self) -> usize {
        self.aliases.len()
    }

    /// What build makes for key, built once per index: a cache that goes when the index goes (the API's browse order,
    /// as the reference's ConditionalWeakTable keyed by the index).
    pub fn memo<T: Any + Send + Sync>(&self, key: &str, build: impl FnOnce() -> T) -> Arc<T> {
        if let Some(value) = self.memo.lock().unwrap().get(key) {
            if let Ok(value) = value.clone().downcast::<T>() {
                return value;
            }
        }
        let value: Arc<T> = Arc::new(build());
        let mut memo = self.memo.lock().unwrap();
        let stored = memo.entry(key.to_string()).or_insert_with(|| value.clone() as Arc<dyn Any + Send + Sync>);
        stored.clone().downcast::<T>().unwrap_or(value)
    }

    fn weight(&self, token: &Token, o: &Options) -> f64 {
        let idf = match self.idf.get(token.key()) {
            Some(&idf) => idf,
            None => (self.aliases.len().max(1) as f64).ln(), // in no alias: as in one
        };
        o.min_token_weight.max(idf)
    }

    fn borrow_state(&self) -> SearchState {
        self.states.lock().unwrap().pop().unwrap_or_else(|| SearchState {
            votes: vec![0; self.aliases.len()],
            voted: Vec::new(),
            slot: vec![0; self.subjects.len()],
            bests: Vec::new(),
        })
    }

    /// Returns a state whose votes and slots are all back to zero.
    fn release_state(&self, mut state: SearchState) {
        for b in &state.bests {
            state.slot[b.subject as usize] = 0;
        }
        state.bests.clear();
        self.states.lock().unwrap().push(state);
    }

    /// The aliases with the most votes first (a stable sort: ties in the order of their first vote, the reference's
    /// dictionary order), at most CandidateLimit.
    fn candidates(&self, query: &PreparedName, state: &mut SearchState, o: &Options) -> Vec<u32> {
        let votes = &mut state.votes;
        let voted = &mut state.voted;
        let mut vote = |k: &str, weight: i32| {
            if let Some(list) = self.aliases_by_fold.get(k) {
                for &alias in list.iter() {
                    if votes[alias as usize] == 0 {
                        voted.push(alias);
                    }
                    votes[alias as usize] += weight;
                }
            }
        };
        for token in distinct_tokens(query.entity.core.iter().chain(query.person.core.iter()).cloned()) {
            for k in lookup_keys(&token) {
                vote(k, 3);
            }
            for &group in token.cognate_groups() {
                for fold in &self.data.cognate_folds[group] {
                    vote(fold, 2);
                }
            }
            for form in &token.forms {
                for spelling in self.data.spelling_members(form) {
                    vote(&fold_key(spelling), 2);
                }
            }
            let mut near: Vec<&str> = Vec::new();
            for fold in &token.folds {
                for other in self.name_vocabulary.near(fold) {
                    if !near.contains(&other) {
                        near.push(other);
                    }
                }
            }
            for other in near {
                vote(other, 1);
            }
        }
        let mut ids = voted.clone();
        ids.sort_by(|a, b| votes[*b as usize].cmp(&votes[*a as usize]));
        for &alias in voted.iter() {
            votes[alias as usize] = 0;
        }
        voted.clear();
        ids.truncate(o.candidate_limit);
        ids
    }

    /// The subjects a name entry finds, best first (then by name): the best one, whatever its percentage, and every
    /// other at Review or above.
    pub fn search_name(&self, query: &PreparedName, o: &Options) -> Vec<SubjectMatch> {
        TokenComparer::with(|comparer| {
            let mut state = self.borrow_state();
            let candidates = self.candidates(query, &mut state, o);
            let mut weights: HashMap<*const Token, f64> = HashMap::new();
            let mut weight_of = |token: &Token| *weights.entry(token as *const Token).or_insert_with(|| self.weight(token, o));
            let mut scorer = NameScorer::new(comparer, o, self.data);
            // The best alias of each subject (the first of equals), subjects in the order they were first found.
            for alias_id in candidates {
                let alias = &self.aliases[alias_id as usize];
                let form = if alias.form.is_person { query.person } else { query.entity };
                let score = scorer.score(form, &alias.form, alias.is_weak, &mut weight_of);
                let slot = state.slot[alias.subject as usize];
                if slot == 0 {
                    state.bests.push(BestAlias { subject: alias.subject, alias: alias_id, score });
                    state.slot[alias.subject as usize] = state.bests.len() as u32;
                } else {
                    let b = &mut state.bests[slot as usize - 1];
                    if score.percent > b.score.percent {
                        b.alias = alias_id;
                        b.score = score;
                    }
                }
            }

            struct Ranked {
                subject: usize,
                percent: i32,
                alias: usize,
                address: AddressComparison,
            }
            let mut list = Vec::with_capacity(state.bests.len());
            for b in &state.bests {
                let subject = b.subject as usize;
                let address = compare_address(scorer.comparer, self.data, query.address, &self.addresses[subject]);
                let mut percent = b.score.percent;
                if address.state == AddressState::Conflicting {
                    percent = percent.min(o.conflicting_address_cap);
                } else if address.state == AddressState::Corroborated {
                    percent = (percent + o.corroborated_bonus).min(o.band_max(percent).min(b.score.ceiling));
                }
                list.push(Ranked { subject, percent, alias: b.alias as usize, address });
            }
            self.release_state(state);

            // Best first, then by name (a stable sort). What's reported is every subject at Review or above, or when
            // there's none, the best one: so only those are sorted, and the best is the first of equals.
            let order = |a: &Ranked, b: &Ranked| {
                b.percent.cmp(&a.percent).then_with(|| self.subjects[a.subject].primary_name.cmp(&self.subjects[b.subject].primary_name))
            };
            let mut reported: Vec<Ranked> = Vec::new();
            let mut best: Option<Ranked> = None;
            for r in list {
                if o.to_outcome(r.percent) != Outcome::NoMatch {
                    reported.push(r);
                } else if reported.is_empty() && best.as_ref().is_none_or(|b| order(&r, b).is_lt()) {
                    best = Some(r);
                }
            }
            if reported.is_empty() {
                reported.extend(best);
            }
            reported.sort_by(order);

            reported
                .into_iter()
                .map(|r| {
                    let alias = &self.aliases[r.alias];
                    let form = if alias.form.is_person { query.person } else { query.entity };
                    let details = scorer.explain(form, &alias.form, alias.is_weak, &mut weight_of);
                    let mut alignment = details.explanation;
                    alignment.push(r.address.explanation);
                    SubjectMatch {
                        subject: self.subjects[r.subject].clone(),
                        percent: r.percent,
                        outcome: o.to_outcome(r.percent),
                        alias: alias.name.clone(),
                        alignment,
                        address_state: r.address.state,
                        words: details.words,
                        notes: details.notes,
                    }
                })
                .collect()
        })
    }

    /// Address-only screening: subjects whose address shares at least 3 parts (a number and a word).
    pub fn search_address(&self, entered: &AddressForm, o: &Options) -> Vec<SubjectMatch> {
        TokenComparer::with(|comparer| {
            let mut key_list = address_keys(entered);
            for word in &entered.words {
                for fold in &word.folds {
                    key_list.extend(self.address_vocabulary.near(fold).into_iter().map(str::to_string));
                }
            }
            let mut distinct: Vec<String> = Vec::new();
            for k in key_list {
                if !distinct.contains(&k) {
                    distinct.push(k);
                }
            }
            let mut order: Vec<u32> = Vec::new();
            let mut count: HashMap<u32, i32> = HashMap::new();
            for k in &distinct {
                if let Some(subjects) = self.subjects_by_address_key.get(k) {
                    for &subject in subjects.iter() {
                        let c = count.entry(subject).or_insert_with(|| {
                            order.push(subject);
                            0
                        });
                        *c += 1;
                    }
                }
            }
            order.sort_by(|a, b| count[b].cmp(&count[a]));
            order.truncate(o.candidate_limit);

            let mut matches: Vec<SubjectMatch> = Vec::new();
            for subject in order {
                let mut best: Option<(&AddressForm, AddressOnly)> = None;
                for address in &self.addresses[subject as usize] {
                    let result = compare_address_only(comparer, self.data, entered, address);
                    if best.as_ref().is_none_or(|(_, b)| better(&result, b)) {
                        best = Some((address, result));
                    }
                }
                let Some((address, result)) = best else { continue };
                let percent = if result.matches { o.address_only_percent } else { o.no_match_max_percent.min(result.aligned as i32 * 15) };
                matches.push(SubjectMatch {
                    subject: self.subjects[subject as usize].clone(),
                    percent,
                    outcome: o.to_outcome(percent),
                    alias: address.raw.clone(),
                    alignment: vec![result.explanation.clone()],
                    address_state: AddressState::NotCompared,
                    words: result.words,
                    notes: vec![result.explanation],
                });
            }
            matches.sort_by(|a, b| b.percent.cmp(&a.percent).then_with(|| a.subject.primary_name.cmp(&b.subject.primary_name)));
            matches
        })
    }
}

/// Orders (Matches, Aligned) as the reference's tuple comparison does; MaxBy keeps the first of equals.
fn better(a: &AddressOnly, b: &AddressOnly) -> bool {
    if a.matches != b.matches {
        return a.matches;
    }
    a.aligned > b.aligned
}
