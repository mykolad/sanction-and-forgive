//! One list's in-memory index (spec section 5.5): aliases read both ways, document frequencies, postings by fold key,
//! fuzzy vocabularies, address postings. Immutable once built.

use super::identifiers::listed_keys;
use super::{HashMap, HashSet};
use std::any::Any;
use std::sync::{Arc, Mutex};

use super::addresses::{compare_address, compare_address_only, parse_address, AddressComparison, AddressForm, AddressOnly, AddressState};
use super::compare::{FuzzyVocabulary, TokenComparer};
use super::data::{data, MatcherData};
use super::names::{NameForm, NameParser};
use super::options::{Options, Outcome};
use super::scorer::{NameScorer, ScoreValue, WordAlignment};
use super::text::{fold_key, is_latin_word, latin_reads_as, latin_spellings, Token, TokenRef, Tokenizer};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubjectKind {
    Individual,
    Entity,
    Unknown,
    /// A ship (#69): in the list's vessel index, searched only by a vessel entry.
    Vessel,
}

/// A vessel's name without the prefix some write before it ("M/V Maria", "MT Ocean Faye"): what is compared.
pub fn vessel_name(name: &str) -> &str {
    let trimmed = name.trim_start();
    for prefix in ["M/V ", "M/T ", "M.V. ", "M.T. ", "MV ", "MT "] {
        if trimmed.get(..prefix.len()).is_some_and(|head| head.eq_ignore_ascii_case(prefix)) {
            let rest = trimmed[prefix.len()..].trim_start();
            if !rest.is_empty() {
                return rest;
            }
        }
    }
    trimmed
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
    /// Identifiers the list gives (#68): an entered one equal to one of them makes this subject a Match.
    pub identifiers: Vec<ListedIdentifier>,
    /// Dates of birth the list gives (#68), in the parsers' form ("1960-03-12", "1963", "1955..1957").
    pub birth_dates: Vec<String>,
}

/// An identifier as a list gives it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ListedIdentifier {
    pub kind: String,
    pub value: String,
    pub country: Option<String>,
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
    /// By the fold key of two adjacent words read as one (NameForm::joined).
    aliases_by_joined: HashMap<String, Box<[u32]>>,
    /// Chinese words by their pinyin readings (#93), for a word or two in Latin script; kept apart from the vocabulary.
    aliases_by_pinyin: HashMap<String, Box<[u32]>>,
    name_vocabulary: FuzzyVocabulary,
    subjects_by_address_key: HashMap<String, Box<[u32]>>,
    address_vocabulary: FuzzyVocabulary,
    /// By identifier key (identifiers::listed_keys, #68): the subjects that list it, each once.
    subjects_by_identifier: HashMap<String, Box<[u32]>>,
    /// The list's vessels (#69), in an index of their own: their names never weigh on how a person's or a company's
    /// words are weighted, and only a vessel entry searches them. None when the list has none.
    vessels: Option<Box<ListIndex>>,
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

/// At most this many aliases that read exactly as pinyin are kept past the candidate limit (#93).
const EXACT_PINYIN_KEPT: usize = 100;

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

/// A Korean name's word's readings (korean.rs): posted with its lookup keys, so its other spellings find it.
fn korean_keys(token: &Token) -> impl Iterator<Item = &str> {
    token.korean.iter().flat_map(|readings| readings.iter().map(String::as_str))
}

/// The aliases posted under k, or none.
fn postings<'m>(map: &'m HashMap<String, Box<[u32]>>, k: &str) -> &'m [u32] {
    map.get(k).map_or(&[][..], |list| &list[..])
}

fn address_keys(address: &AddressForm) -> Vec<String> {
    let mut out: Vec<String> =
        address.words.iter().flat_map(|w| lookup_keys(w).into_iter().map(str::to_string).collect::<Vec<_>>()).collect();
    out.extend(address.places.iter().map(|p| format!("#place:{p}")));
    out
}

impl ListIndex {
    /// One list's index: its people, companies and parties of unknown kind, and its vessels in an index of their own.
    pub fn build(subjects: Vec<ListedSubject>) -> ListIndex {
        let (vessels, others): (Vec<ListedSubject>, Vec<ListedSubject>) = subjects.into_iter().partition(|s| s.kind == SubjectKind::Vessel);
        let mut index = ListIndex::build_part(others);
        if !vessels.is_empty() {
            index.vessels = Some(Box::new(ListIndex::build_part(vessels)));
        }
        index
    }

    fn build_part(subjects: Vec<ListedSubject>) -> ListIndex {
        let data = data();
        let mut tokenizer = Tokenizer::interning(data);
        let parser = NameParser { data };
        let mut aliases = Vec::new();
        let mut addresses = Vec::with_capacity(subjects.len());
        for (s, subject) in subjects.iter().enumerate() {
            let mut seen = HashSet::default();
            let primary = ListedName { full_name: subject.primary_name.clone(), is_weak: false };
            for name in subject.names.iter().chain(std::iter::once(&primary)) {
                if !seen.insert(name.full_name.as_str()) {
                    continue;
                }
                let compared = if subject.kind == SubjectKind::Vessel { vessel_name(&name.full_name) } else { &name.full_name };
                let tokens = tokenizer.tokenize(compared, true);
                if subject.kind != SubjectKind::Individual {
                    let form = parser.entity(&tokens);
                    if !form.is_empty() {
                        aliases.push(AliasEntry { subject: s as u32, name: name.full_name.clone(), is_weak: name.is_weak, form });
                    }
                }
                // A person's reading for a person, or a party whose kind the list doesn't give; never a company's or a ship's.
                if matches!(subject.kind, SubjectKind::Individual | SubjectKind::Unknown) {
                    let form = parser.person(&tokens);
                    if !form.is_empty() {
                        aliases.push(AliasEntry { subject: s as u32, name: name.full_name.clone(), is_weak: name.is_weak, form });
                    }
                }
            }
            addresses
                .push(subject.addresses.iter().map(|a| parse_address(data, &mut tokenizer, &a.raw, &a.country_code)).collect::<Vec<_>>());
        }

        let mut document_frequency: HashMap<String, usize> = HashMap::default();
        let mut by_fold = OrderedPostings::default();
        let mut by_joined = OrderedPostings::default();
        let mut by_pinyin = OrderedPostings::default();
        for (a, alias) in aliases.iter().enumerate() {
            for token in distinct_tokens(alias.form.core.iter().cloned()) {
                *document_frequency.entry(token.key().to_string()).or_default() += 1;
                for k in lookup_keys(&token).into_iter().chain(korean_keys(&token)) {
                    by_fold.post(k, a);
                }
                // A Chinese word under its pinyin (#93), apart from the vocabulary: a word in Latin script finds it.
                for reading in token.pinyin.iter().flatten() {
                    by_pinyin.post(reading, a);
                }
            }
            // Two adjacent words read as one, by fold key (finding is loose, scoring exact), kept apart from the
            // vocabulary (the fuzzy lookups don't see them).
            for k in alias.form.joined.iter().filter(|k| !k.is_empty()) {
                by_joined.post(&fold_key(k), a);
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
        let (_, aliases_by_joined) = by_joined.packed();
        let (_, aliases_by_pinyin) = by_pinyin.packed();
        let (address_keys, subjects_by_address_key) = by_address.packed();
        let address_vocabulary: Vec<String> = address_keys.into_iter().filter(|k| !k.starts_with('#')).collect();
        let mut by_identifier: HashMap<String, Vec<u32>> = HashMap::default();
        for (s, subject) in subjects.iter().enumerate() {
            for identifier in &subject.identifiers {
                for key in listed_keys(&identifier.value) {
                    let ids = by_identifier.entry(key).or_default();
                    if ids.last() != Some(&(s as u32)) {
                        ids.push(s as u32);
                    }
                }
            }
        }
        let subjects_by_identifier = by_identifier.into_iter().map(|(k, v)| (k, v.into_boxed_slice())).collect();
        ListIndex {
            data,
            subjects: subjects.into_iter().map(Arc::new).collect(),
            subjects_by_identifier,
            aliases,
            addresses,
            idf,
            aliases_by_fold,
            aliases_by_joined,
            aliases_by_pinyin,
            name_vocabulary: FuzzyVocabulary::new(&fold_keys),
            subjects_by_address_key,
            address_vocabulary: FuzzyVocabulary::new(&address_vocabulary),
            vessels: None,
            states: Mutex::new(Vec::new()),
            memo: Mutex::new(HashMap::default()),
        }
    }

    /// The people, companies and parties of unknown kind: what a name or an address entry searches.
    pub fn subjects(&self) -> &[Arc<ListedSubject>] {
        &self.subjects
    }

    /// The list's vessels' index (#69), if it lists any.
    pub fn vessels(&self) -> Option<&ListIndex> {
        self.vessels.as_deref()
    }

    /// Every subject the list gives, vessels last: what browsing shows.
    pub fn all_subjects(&self) -> impl Iterator<Item = &Arc<ListedSubject>> {
        self.subjects.iter().chain(self.vessels.iter().flat_map(|v| v.subjects.iter()))
    }

    /// The subjects that list an identifier equal to an entered one (its key, identifiers::identifier_key), each with
    /// the listed identifier that matched, in list order, each subject once; only identifiers `compared` keeps count.
    pub fn identified(
        &self,
        entered_keys: &[String],
        compared: impl Fn(&ListedIdentifier) -> bool,
    ) -> Vec<(Arc<ListedSubject>, ListedIdentifier)> {
        let mut found: Vec<(Arc<ListedSubject>, ListedIdentifier)> = Vec::new();
        for key in entered_keys {
            for &s in postings(&self.subjects_by_identifier, key) {
                let subject = &self.subjects[s as usize];
                if found.iter().any(|(f, _)| Arc::ptr_eq(f, subject)) {
                    continue;
                }
                if let Some(identifier) = subject.identifiers.iter().find(|i| compared(i) && listed_keys(&i.value).contains(key)) {
                    found.push((subject.clone(), identifier.clone()));
                }
            }
        }
        found.sort_by_key(|(subject, _)| self.subjects.iter().position(|s| Arc::ptr_eq(s, subject)));
        found
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
    /// Also returns, sorted, the aliases found through two words read as one (NameForm::joined): the only ones where a
    /// join can apply, since a word that reads exactly as two folds as they do.
    fn candidates(&self, query: &PreparedName, state: &mut SearchState, o: &Options) -> (Vec<u32>, Vec<u32>) {
        let votes = &mut state.votes;
        let voted = &mut state.voted;
        let mut joinable: Vec<u32> = Vec::new();
        let mut vote = |list: &[u32], weight: i32, joined: bool| {
            if joined {
                joinable.extend(list.iter());
            }
            // A vote through words read as one counts three quarters of one through the words themselves, so an alias
            // the entered words find as written ranks ahead of one found partly joined (Hua Wei: the Hua Wei alias's 6
            // votes before Hua Huawei's 3 and 3). Every vote is scaled alike otherwise: the order of aliases found
            // without a join is the same as before.
            let weight = weight * if joined { 3 } else { 4 };
            for &alias in list {
                if votes[alias as usize] == 0 {
                    voted.push(alias);
                }
                votes[alias as usize] += weight;
            }
        };
        let (by_fold, by_joined) = (&self.aliases_by_fold, &self.aliases_by_joined);
        // The aliases a Chinese word's pinyin found by fold key: those that read exactly so are put first (below).
        let mut exact: Vec<u32> = Vec::new();
        let tokens = distinct_tokens(query.entity.core.iter().chain(query.person.core.iter()).cloned());
        for token in &tokens {
            for k in lookup_keys(token) {
                vote(postings(by_fold, k), 3, false);
            }
            // A Korean name's word through its readings: each alias once, and none its own keys found already, however
            // many readings the word has (Lee Cheol mustn't outvote Ri Chol with surname-only aliases).
            if token.korean.is_some() {
                let mut found: Vec<u32> = lookup_keys(token).iter().flat_map(|k| postings(by_fold, k).iter().copied()).collect();
                found.sort_unstable();
                found.dedup();
                // A set for membership (thousands of aliases can share a surname's reading), the list for the order.
                let (mut extra, mut seen): (Vec<u32>, HashSet<u32>) = (Vec::new(), HashSet::default());
                for k in korean_keys(token) {
                    for &alias in postings(by_fold, k) {
                        if found.binary_search(&alias).is_err() && seen.insert(alias) {
                            extra.push(alias);
                        }
                    }
                }
                vote(&extra, 3, false);
            }
            // A Chinese word's pinyin finds the aliases in Latin script that read exactly so, as one word or as two (found
            // by fold key, voted for only when exact: Zang Wei gets no vote from 张伟); a word in Latin script finds the
            // Chinese words that read as it (#93).
            for reading in token.pinyin.iter().flatten() {
                for (list, joined) in [(postings(by_fold, &fold_key(reading)), false), (postings(by_joined, &fold_key(reading)), true)] {
                    let found: Vec<u32> = list.iter().copied().filter(|&alias| self.reads_exactly(alias, reading)).collect();
                    vote(&found, 3, joined);
                    exact.extend_from_slice(&found);
                }
            }
            if is_latin_word(token) {
                for spelling in latin_spellings(token) {
                    let list = postings(&self.aliases_by_pinyin, &spelling);
                    vote(list, 3, false);
                    exact.extend_from_slice(list);
                }
            }
            for &group in token.cognate_groups() {
                for fold in &self.data.cognate_folds[group] {
                    vote(postings(by_fold, fold), 2, false);
                }
            }
            for form in &token.forms {
                for spelling in self.data.spelling_members(form) {
                    vote(postings(by_fold, &fold_key(spelling)), 2, false);
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
                vote(postings(by_fold, other), 1, false);
            }
        }
        // Then words one side writes as two (NameForm::joined), each key once though the company and person readings
        // both have it, and after the words themselves, so an alias found as written wins a tie (votes are counted in
        // the order of the first vote).
        let mut keys: Vec<String> = Vec::new();
        for token in &tokens {
            for k in lookup_keys(token) {
                if !keys.iter().any(|seen| seen == k) {
                    keys.push(k.to_string());
                    vote(postings(by_joined, k), 3, true);
                }
            }
        }
        let mut joined: Vec<String> = Vec::new();
        for k in query.entity.joined.iter().chain(query.person.joined.iter()).filter(|k| !k.is_empty()) {
            let key = fold_key(k);
            if !joined.contains(&key) {
                vote(postings(by_fold, &key), 3, true);
                joined.push(key);
            }
        }
        // Two words in Latin script read as one find a Chinese word that reads so (#93): Zhang Wei, 张伟.
        let mut read: Vec<&str> = Vec::new();
        for form in [query.entity, query.person] {
            for (pair, k) in form.core.windows(2).zip(&form.joined) {
                if !k.is_empty() && pair.iter().all(|t| is_latin_word(t)) && !read.contains(&k.as_str()) {
                    let list = postings(&self.aliases_by_pinyin, k);
                    vote(list, 3, true);
                    exact.extend_from_slice(list);
                    read.push(k);
                }
            }
        }
        // Aliases that read exactly as pinyin (#93), the only ones pinyin voted for, are kept past the limit when it cuts
        // them: up to EXACT_PINYIN_KEPT, those with the most votes (the evidence for the whole name) first.
        let (votes, voted) = (&mut state.votes, &mut state.voted);
        let mut ids = voted.clone();
        ids.sort_by(|a, b| votes[*b as usize].cmp(&votes[*a as usize]));
        let mut rescued: Vec<u32> = Vec::new();
        if !exact.is_empty() && ids.len() > o.candidate_limit {
            let mut present: HashSet<u32> = ids[..o.candidate_limit].iter().copied().collect();
            rescued = exact.iter().copied().filter(|a| present.insert(*a)).collect();
            rescued.sort_by(|a, b| votes[*b as usize].cmp(&votes[*a as usize]));
            rescued.truncate(EXACT_PINYIN_KEPT);
        }
        for &alias in voted.iter() {
            votes[alias as usize] = 0;
        }
        voted.clear();
        ids.truncate(o.candidate_limit);
        ids.extend(rescued);
        joinable.sort_unstable();
        joinable.dedup();
        (ids, joinable)
    }

    /// Whether an alias reads exactly as a Chinese word's pinyin reading (#93): a word in Latin script spelled so, or
    /// two adjacent ones read as one.
    fn reads_exactly(&self, alias: u32, reading: &str) -> bool {
        let form = &self.aliases[alias as usize].form;
        form.core.iter().any(|t| latin_reads_as(t, reading))
            || form.core.windows(2).zip(&form.joined).any(|(pair, k)| k == reading && pair.iter().all(|t| is_latin_word(t)))
    }

    /// The subjects a name entry finds, best first (then by name): the best one, whatever its percentage, and every
    /// other at Review or above.
    pub fn search_name(&self, query: &PreparedName, o: &Options) -> Vec<SubjectMatch> {
        TokenComparer::with(|comparer| {
            let mut state = self.borrow_state();
            let (candidates, joinable) = self.candidates(query, &mut state, o);
            let mut weights: HashMap<*const Token, f64> = HashMap::default();
            let mut weight_of = |token: &Token| *weights.entry(token as *const Token).or_insert_with(|| self.weight(token, o));
            let mut scorer = NameScorer::new(comparer, o, self.data);
            // The best alias of each subject (the first of equals), subjects in the order they were first found.
            for alias_id in candidates {
                let alias = &self.aliases[alias_id as usize];
                let form = if alias.form.is_person { query.person } else { query.entity };
                scorer.try_join = joinable.binary_search(&alias_id).is_ok();
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
                    scorer.try_join = joinable.binary_search(&(r.alias as u32)).is_ok();
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
            let mut count: HashMap<u32, i32> = HashMap::default();
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
