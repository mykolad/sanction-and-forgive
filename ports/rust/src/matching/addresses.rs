//! Addresses read as spec section 5.8 says: a country, the places named, the other words and the numbers; compared
//! with a listed party's, or alone.

use std::collections::HashMap;

use unicode_general_category::GeneralCategory;

use super::compare::TokenComparer;
use super::data::MatcherData;
use super::scorer::{how, WordAlignment};
use super::text::{category, is_letter, to_upper_invariant, words, TokenRef, Tokenizer};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AddressState {
    NotCompared,
    Corroborated,
    Conflicting,
}

impl AddressState {
    pub fn as_str(&self) -> &'static str {
        match self {
            AddressState::NotCompared => "NotCompared",
            AddressState::Corroborated => "Corroborated",
            AddressState::Conflicting => "Conflicting",
        }
    }
}

#[derive(Debug)]
pub struct AddressForm {
    pub raw: String,
    pub words: Vec<TokenRef>,
    pub numbers: Vec<TokenRef>,
    pub places: Vec<usize>,
    pub country: String,
    pub implied_country: String,
}

impl AddressForm {
    pub fn effective_country(&self) -> &str {
        if self.country.is_empty() {
            &self.implied_country
        } else {
            &self.country
        }
    }

    pub fn is_empty(&self) -> bool {
        self.words.is_empty() && self.numbers.is_empty() && self.places.is_empty() && self.effective_country().is_empty()
    }
}

pub fn parse_address(data: &MatcherData, tokenizer: &mut Tokenizer, raw: &str, source_country_code: &str) -> AddressForm {
    let mut country = String::new();
    let code = to_upper_invariant(source_country_code);
    if source_country_code.chars().count() == 2 && data.country_codes.contains(&code) {
        country = code;
    }
    let mut ws = words(raw);
    if let Some(code) = trailing_code(raw) {
        if data.country_codes.contains(&code) && !ws.is_empty() {
            if country.is_empty() {
                country = code;
            }
            ws.pop();
        }
    }

    let tokens: Vec<TokenRef> = ws.iter().map(|w| tokenizer.get(w)).collect();
    let mut places = Vec::new();
    let (mut named_country, mut implied_country) = (String::new(), String::new());
    let (mut remaining, mut numbers) = (Vec::new(), Vec::new());
    let mut i = 0;
    while i < tokens.len() {
        if let Some((code, length)) = lookup(&tokens, i, data.max_country_words, &data.countries) {
            named_country = code.clone();
            i += length;
            continue;
        }
        if let Some((place, length)) = lookup(&tokens, i, data.max_place_words, &data.places) {
            places.push(place.id);
            if implied_country.is_empty() {
                implied_country = place.country.clone();
            }
            i += length;
            continue;
        }
        let token = &tokens[i];
        if token.is_number {
            numbers.push(token.clone());
        } else if !data.address_words.contains(&token.text)
            && !token.forms.iter().any(|f| data.address_words.contains(f))
            && token.len() > 1
        {
            remaining.push(token.clone());
        }
        i += 1;
    }
    let mut distinct_places: Vec<usize> = Vec::new();
    for id in places {
        if !distinct_places.contains(&id) {
            distinct_places.push(id);
        }
    }
    if country.is_empty() {
        country = named_country;
    }
    AddressForm { raw: raw.to_string(), words: remaining, numbers, places: distinct_places, country, implied_country }
}

/// The longest name starting at start, on the words' own text or one of their Latin readings.
fn lookup<'m, T>(tokens: &[TokenRef], start: usize, max_words: usize, names: &'m HashMap<String, T>) -> Option<(&'m T, usize)> {
    for length in (1..=max_words.min(tokens.len() - start)).rev() {
        let span = &tokens[start..start + length];
        let max_forms = span.iter().map(|t| t.forms.len()).max().unwrap_or(0);
        for variant in -1..max_forms as isize {
            let parts: Vec<&str> = span
                .iter()
                .map(
                    |t| if variant < 0 || variant as usize >= t.forms.len() { t.text.as_str() } else { t.forms[variant as usize].as_str() },
                )
                .collect();
            if let Some(value) = names.get(&parts.join(" ")) {
                return Some((value, length));
            }
        }
    }
    None
}

/// The reference's `\b([A-Z]{2})\W*$`: two capital ASCII letters, a word boundary before them, and nothing after them
/// but non-word characters (\w in .NET: letters, marks, decimal digits, connector punctuation).
fn trailing_code(raw: &str) -> Option<String> {
    let chars: Vec<char> = raw.chars().collect();
    let mut end = chars.len();
    while end > 0 && !is_word_char(chars[end - 1]) {
        end -= 1;
    }
    if end < 2 {
        return None;
    }
    let (a, b) = (chars[end - 2], chars[end - 1]);
    if !a.is_ascii_uppercase() || !b.is_ascii_uppercase() {
        return None;
    }
    if end >= 3 && is_word_char(chars[end - 3]) {
        return None;
    }
    Some([a, b].iter().collect())
}

fn is_word_char(c: char) -> bool {
    is_letter(c)
        || matches!(category(c), GeneralCategory::NonspacingMark | GeneralCategory::DecimalNumber | GeneralCategory::ConnectorPunctuation)
}

#[derive(Debug, Clone)]
pub struct AddressComparison {
    pub state: AddressState,
    pub explanation: String,
}

#[derive(Debug, Clone)]
pub struct AddressOnly {
    pub aligned: usize,
    pub matches: bool,
    pub explanation: String,
    pub words: Vec<WordAlignment>,
}

pub fn compare_address(
    comparer: &mut TokenComparer,
    data: &MatcherData,
    entered: Option<&AddressForm>,
    listed: &[AddressForm],
) -> AddressComparison {
    let Some(entered) = entered.filter(|e| !e.is_empty()) else {
        return AddressComparison { state: AddressState::NotCompared, explanation: "Name only: no address entered".to_string() };
    };
    let results: Vec<AddressComparison> =
        listed.iter().filter(|a| !a.is_empty()).map(|a| compare_one(comparer, data, entered, a)).collect();
    if results.is_empty() {
        return AddressComparison { state: AddressState::NotCompared, explanation: "Address not compared: none listed".to_string() };
    }
    if let Some(r) = results.iter().find(|r| r.state == AddressState::Corroborated) {
        return r.clone();
    }
    if let Some(r) = results.iter().find(|r| r.state == AddressState::Conflicting) {
        return r.clone();
    }
    AddressComparison { state: AddressState::NotCompared, explanation: "Address compared, nothing in common".to_string() }
}

pub fn compare_address_only(comparer: &mut TokenComparer, data: &MatcherData, entered: &AddressForm, listed: &AddressForm) -> AddressOnly {
    let (a, b) = (entered.effective_country(), listed.effective_country());
    if !a.is_empty() && !b.is_empty() && a != b {
        return AddressOnly {
            aligned: 0,
            matches: false,
            explanation: "Address only: the countries differ".to_string(),
            words: Vec::new(),
        };
    }
    let mut places: Vec<usize> = Vec::new();
    for &p in &entered.places {
        if listed.places.contains(&p) && !places.contains(&p) {
            places.push(p);
        }
    }
    let words = aligned_words(comparer, &entered.words, &listed.words);
    let listed_numbers: Vec<&str> = listed.numbers.iter().map(|n| n.text.as_str()).collect();
    let mut numbers: Vec<&str> = Vec::new();
    for n in &entered.numbers {
        if listed_numbers.contains(&n.text.as_str()) && !numbers.contains(&n.text.as_str()) {
            numbers.push(&n.text);
        }
    }
    let aligned = places.len() + words.len() + numbers.len();
    let matches = aligned >= 3 && !numbers.is_empty() && places.len() + words.len() >= 1;
    let mut parts = Vec::new();
    for &p in &places {
        parts.push(WordAlignment { entered: None, listed: Some(data.place_names[p].clone()), how: "SamePlace" });
    }
    for (entered_word, listed_word) in &words {
        let position = entered.words.iter().position(|t| std::ptr::eq(t.as_ref(), entered_word.as_ref())).map(|p| p as i32).unwrap_or(-1);
        let method = comparer.compare(entered_word, listed_word).method;
        parts.push(WordAlignment { entered: Some(position + 1), listed: Some(listed_word.text.clone()), how: how(method) });
    }
    for n in &numbers {
        parts.push(WordAlignment { entered: None, listed: Some(n.to_string()), how: "SameNumber" });
    }
    AddressOnly {
        aligned,
        matches,
        explanation: format!(
            "Address only: {aligned} parts align ({} words or places, {} numbers)",
            places.len() + words.len(),
            numbers.len()
        ),
        words: parts,
    }
}

fn compare_one(comparer: &mut TokenComparer, data: &MatcherData, entered: &AddressForm, listed: &AddressForm) -> AddressComparison {
    let (entered_country, listed_country) = (entered.effective_country(), listed.effective_country());
    if !entered_country.is_empty() && !listed_country.is_empty() && entered_country != listed_country {
        return AddressComparison {
            state: AddressState::Conflicting,
            explanation: format!("Address: countries differ ({entered_country}, listed {listed_country})"),
        };
    }
    if let Some(&p) = entered.places.iter().find(|p| listed.places.contains(p)) {
        return AddressComparison {
            state: AddressState::Corroborated,
            explanation: format!("Address: same place ({})", data.place_names[p]),
        };
    }
    let words = aligned_words(comparer, &entered.words, &listed.words);
    if !words.is_empty() {
        let parts: Vec<String> = words.iter().map(|(e, l)| format!("{} ~ {}", e.text, l.text)).collect();
        return AddressComparison { state: AddressState::Corroborated, explanation: format!("Address: {}", parts.join(", ")) };
    }
    if !entered_country.is_empty() && entered_country == listed_country {
        return AddressComparison { state: AddressState::Corroborated, explanation: format!("Address: country only ({entered_country})") };
    }
    AddressComparison { state: AddressState::NotCompared, explanation: "Address compared, nothing in common".to_string() }
}

fn aligned_words(comparer: &mut TokenComparer, entered: &[TokenRef], listed: &[TokenRef]) -> Vec<(TokenRef, TokenRef)> {
    let mut used: Vec<*const super::text::Token> = Vec::new();
    let mut pairs = Vec::new();
    for word in entered {
        for candidate in listed {
            let pointer = candidate.as_ref() as *const _;
            if !used.contains(&pointer) && comparer.compare(word, candidate).aligned() {
                used.push(pointer);
                pairs.push((word.clone(), candidate.clone()));
                break;
            }
        }
    }
    pairs
}
