//! The dictionaries (`src/SanctionAndForgive.Matching/Data`, embedded by build.rs): loaded once, immutable. Every key is
//! normalised like the text it's compared with.

use super::{HashMap, HashSet};
use std::sync::OnceLock;

use serde_json::{Map, Value};
use sha2::{Digest, Sha256};

use super::compare::damerau_levenshtein;
use super::text::{create_token, is_cyrillic, strip_diacritics, words, Token, TransliterationTable};

mod resources {
    include!(concat!(env!("OUT_DIR"), "/resources.rs"));
}

pub use resources::PAGES;

#[derive(Debug, Clone)]
pub struct LegalForm {
    pub class: String,
    pub ambiguous: bool,
}

#[derive(Debug, Clone)]
pub struct LegalPhrase {
    pub words: Vec<String>,
    pub short: String,
}

#[derive(Debug, Clone)]
pub struct Place {
    pub id: usize,
    pub country: String,
    pub name: String,
}

#[derive(Debug)]
pub struct MatcherData {
    pub hash: String,
    pub tables: Vec<TransliterationTable>,
    pub legal_forms: HashMap<String, LegalForm>,
    /// Multi-word legal forms by first word, longest first.
    pub legal_phrases: HashMap<String, Vec<LegalPhrase>>,
    /// Spelling → canonical word; `generic_keys` in the reference's insertion order (near_generic takes the first).
    generic_keys: Vec<String>,
    pub generic: HashMap<String, String>,
    pub stop_words: HashSet<String>,
    pub honorifics: HashSet<String>,
    pub address_words: HashSet<String>,
    pub spelling_groups: HashMap<String, usize>,
    pub spelling_group_members: Vec<Vec<String>>,
    pub cognate_groups: HashMap<String, Vec<usize>>,
    pub cognate_folds: Vec<Vec<String>>,
    pub countries: HashMap<String, String>,
    pub country_codes: HashSet<String>,
    pub max_country_words: usize,
    pub places: HashMap<String, Place>,
    pub place_names: Vec<String>,
    pub max_place_words: usize,
}

static DATA: OnceLock<MatcherData> = OnceLock::new();

/// The dictionaries, loaded on first use.
pub fn data() -> &'static MatcherData {
    DATA.get_or_init(load)
}

impl MatcherData {
    /// A token with its groups.
    pub fn token(&self, text: &str) -> Token {
        let mut token = create_token(&self.tables, text);
        let mut spelling = Vec::new();
        for form in &token.forms {
            if let Some(&group) = self.spelling_groups.get(form) {
                if !spelling.contains(&group) {
                    spelling.push(group);
                }
            }
        }
        let mut cognates = Vec::new();
        for fold in &token.folds {
            for &group in self.cognate_groups.get(fold).map(Vec::as_slice).unwrap_or(&[]) {
                if !cognates.contains(&group) {
                    cognates.push(group);
                }
            }
        }
        if !spelling.is_empty() || !cognates.is_empty() {
            token.groups = Some(Box::new(super::text::TokenGroups { spelling, cognates }));
        }
        token
    }

    /// The canonical generic word one edit from form (same first letter, 7 letters and up), or None.
    pub fn near_generic(&self, form: &str) -> Option<&str> {
        let form_chars: Vec<char> = form.chars().collect();
        if form_chars.len() < 7 {
            return None;
        }
        for spelling in &self.generic_keys {
            let s: Vec<char> = spelling.chars().collect();
            if s.len() >= 7
                && s[0] == form_chars[0]
                && s.len().abs_diff(form_chars.len()) <= 1
                && damerau_levenshtein(&s, &form_chars, 2) == 1
            {
                return self.generic.get(spelling).map(String::as_str);
            }
        }
        None
    }

    /// The other spellings of spelling's group, or none.
    pub fn spelling_members(&self, spelling: &str) -> &[String] {
        match self.spelling_groups.get(spelling) {
            Some(&group) => &self.spelling_group_members[group],
            None => &[],
        }
    }
}

/// A single-word dictionary entry normalised like a token's text.
fn key(value: &str) -> String {
    words(value).concat()
}

/// A multi-word name's lookup keys: its normalised words joined by spaces, and for a name without Cyrillic also
/// without diacritics.
fn keys(name: &str) -> Vec<String> {
    let k = words(name).join(" ");
    let mut out = vec![k.clone()];
    if !k.chars().any(is_cyrillic) {
        out.push(strip_diacritics(&k));
    }
    out
}

fn strings(value: &Value) -> Vec<String> {
    value.as_array().map(|items| items.iter().map(|item| item.as_str().unwrap().to_string()).collect()).unwrap_or_default()
}

fn object(value: &Value) -> &Map<String, Value> {
    value.as_object().expect("a JSON object")
}

const RESOURCE_PREFIX: &str = "SanctionAndForgive.Matching.Data.";

fn load() -> MatcherData {
    // The reference hashes its embedded resources by manifest name (SanctionAndForgive.Matching.Data.translit.ru.json),
    // in ordinal order: the same bytes in the same order give the same hash.
    let mut resources: Vec<(String, &[u8])> =
        resources::DICTIONARIES.iter().map(|(path, bytes)| (format!("{RESOURCE_PREFIX}{}", path.replace('/', ".")), *bytes)).collect();
    resources.sort_by(|a, b| a.0.cmp(&b.0));
    let mut hash = Sha256::new();
    let mut documents: HashMap<String, Value> = HashMap::default();
    for (name, bytes) in &resources {
        hash.update(name.as_bytes());
        hash.update(bytes);
        let document: Value = serde_json::from_slice(bytes).unwrap_or_else(|e| panic!("{name}: {e}"));
        documents.insert(name[RESOURCE_PREFIX.len()..].to_string(), document);
    }
    let document = |name: &str| documents.get(name).unwrap_or_else(|| panic!("missing dictionary {name}"));

    let tables: Vec<TransliterationTable> =
        ["translit.ru.json", "translit.uk.json"].iter().map(|file| read_table(document(file))).collect();

    let legal = object(document("legal-forms.json"));
    let mut legal_forms = HashMap::default();
    for (class, form) in object(&legal["forms"]) {
        for (kind, ambiguous) in [("unambiguous", false), ("ambiguous", true)] {
            if let Some(spellings) = form.get(kind) {
                for spelling in strings(spellings) {
                    legal_forms.insert(key(&spelling), LegalForm { class: class.clone(), ambiguous });
                }
            }
        }
    }
    let mut phrase_list: Vec<LegalPhrase> = object(&legal["phrases"])
        .iter()
        .map(|(name, short)| LegalPhrase { words: words(name), short: key(short.as_str().unwrap()) })
        .collect();
    // OrderByDescending(words) is stable, then grouped by first word in order of appearance.
    phrase_list.sort_by_key(|phrase| std::cmp::Reverse(phrase.words.len()));
    let mut legal_phrases: HashMap<String, Vec<LegalPhrase>> = HashMap::default();
    for phrase in phrase_list {
        legal_phrases.entry(phrase.words[0].clone()).or_default().push(phrase);
    }

    let mut generic_keys = Vec::new();
    let mut generic = HashMap::default();
    for (word, spellings) in object(&object(document("generic.json"))["words"]) {
        for spelling in strings(spellings) {
            let k = key(&spelling);
            if !generic.contains_key(&k) {
                generic_keys.push(k.clone());
            }
            generic.insert(k, word.clone());
        }
    }

    let word_lists = object(document("words.json"));
    let set = |property: &str| -> HashSet<String> { strings(&word_lists[property]).iter().flat_map(|word| words(word)).collect() };
    let (stop_words, honorifics, address_words) = (set("stop"), set("honorifics"), set("addressWords"));

    let mut spelling_group_members: Vec<Vec<String>> = Vec::new();
    for group in object(document("spelling-pairs.json"))["groups"].as_array().unwrap() {
        let mut members: Vec<String> = Vec::new();
        for spelling in strings(group) {
            let s = strip_diacritics(&key(&spelling));
            if !members.contains(&s) {
                members.push(s);
            }
        }
        spelling_group_members.push(members);
    }
    let mut spelling_groups = HashMap::default();
    for (group, members) in spelling_group_members.iter().enumerate() {
        for spelling in members {
            spelling_groups.insert(spelling.clone(), group);
        }
    }

    let mut cognate_folds: Vec<Vec<String>> = Vec::new();
    for group in object(document("cognates.json"))["groups"].as_array().unwrap() {
        let mut folds: Vec<String> = Vec::new();
        for name in strings(group) {
            for fold in create_token(&tables, &key(&name)).folds {
                if !folds.contains(&fold) {
                    folds.push(fold);
                }
            }
        }
        cognate_folds.push(folds);
    }
    let mut cognate_groups: HashMap<String, Vec<usize>> = HashMap::default();
    for (group, folds) in cognate_folds.iter().enumerate() {
        for fold in folds {
            let groups = cognate_groups.entry(fold.clone()).or_default();
            if !groups.contains(&group) {
                groups.push(group);
            }
        }
    }

    let mut countries = HashMap::default();
    let mut country_codes = HashSet::default();
    for country in document("countries.json").as_array().unwrap() {
        let code = country["code"].as_str().unwrap().to_string();
        country_codes.insert(code.clone());
        for name in strings(&country["names"]) {
            for k in keys(&name) {
                countries.entry(k).or_insert_with(|| code.clone());
            }
        }
    }

    let mut places = HashMap::default();
    let mut place_names = Vec::new();
    for (id, place) in document("places.json").as_array().unwrap().iter().enumerate() {
        let country = place["country"].as_str().unwrap().to_string();
        let names = strings(&place["names"]);
        place_names.push(names[0].clone());
        for name in &names {
            for k in keys(name) {
                places.entry(k).or_insert_with(|| Place { id, country: country.clone(), name: names[0].clone() });
            }
        }
    }
    let max_country_words = countries.keys().map(|k| k.matches(' ').count() + 1).max().unwrap_or(0);
    let max_place_words = places.keys().map(|k| k.matches(' ').count() + 1).max().unwrap_or(0);

    MatcherData {
        hash: hex::encode(hash.finalize())[..16].to_string(),
        tables,
        legal_forms,
        legal_phrases,
        generic_keys,
        generic,
        stop_words,
        honorifics,
        address_words,
        spelling_groups,
        spelling_group_members,
        cognate_groups,
        cognate_folds,
        countries,
        country_codes,
        max_country_words,
        places,
        place_names,
        max_place_words,
    }
}

fn read_table(table: &Value) -> TransliterationTable {
    let letters = |value: &Value| -> HashMap<char, String> {
        object(value).iter().map(|(k, v)| (k.chars().next().unwrap(), v.as_str().unwrap().to_string())).collect()
    };
    TransliterationTable {
        language: table["language"].as_str().unwrap().to_string(),
        letters: letters(&table["letters"]),
        initial: letters(&table["initial"]),
    }
}
