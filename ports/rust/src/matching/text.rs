//! Text: normalised words, Latin readings and fold keys (spec sections 5.1 and 5.2), as the .NET reference makes them.
//! Strings are handled as chars; .NET counts UTF-16 units, the same for every character the lists and the dictionaries
//! use (the Basic Multilingual Plane).

use super::HashMap;
use std::sync::Arc;

use unicode_general_category::{get_general_category, GeneralCategory};
use unicode_normalization::UnicodeNormalization;

use super::data::MatcherData;

/// One normalised word with every Latin reading of it. Tokens are interned per index (compared by pointer).
#[derive(Debug)]
pub struct Token {
    pub text: String,
    pub forms: Vec<String>,
    pub folds: Vec<String>,
    pub is_cyrillic: bool,
    pub is_number: bool,
    length: u32,
    /// None: in no spelling or cognate group (most words).
    pub(crate) groups: Option<Box<TokenGroups>>,
}

/// The spelling-pair and cognate groups a token belongs to, looked up once when it's made.
#[derive(Debug, Default)]
pub(crate) struct TokenGroups {
    pub spelling: Vec<usize>,
    /// Through its fold keys, in the order the reference's set enumerates them (insertion order).
    pub cognates: Vec<usize>,
}

pub type TokenRef = Arc<Token>;

impl Token {
    /// The number of letters (or digits). A token is never empty, so no is_empty.
    #[allow(clippy::len_without_is_empty)]
    pub fn len(&self) -> usize {
        self.length as usize
    }

    /// The key for weights and postings: the first fold key, or the text itself.
    pub fn key(&self) -> &str {
        self.folds.first().map(String::as_str).unwrap_or(&self.text)
    }

    pub(crate) fn spelling_groups(&self) -> &[usize] {
        self.groups.as_ref().map(|g| g.spelling.as_slice()).unwrap_or(&[])
    }

    pub(crate) fn cognate_groups(&self) -> &[usize] {
        self.groups.as_ref().map(|g| g.cognates.as_slice()).unwrap_or(&[])
    }
}

/// A token as the tokenizer produced it; `initials` holds a run of single letters as separate tokens.
#[derive(Debug, Clone)]
pub struct RawToken {
    pub token: TokenRef,
    pub initials: Option<Vec<TokenRef>>,
}

const APOSTROPHES: &str = "'’ʼʻ`′‘";

/// A character's category as .NET's char checks see it: one beyond the Basic Multilingual Plane is two UTF-16 units,
/// each a surrogate (Cs), so never a letter, a digit or a mark, and a word ends there.
pub fn category(c: char) -> GeneralCategory {
    if c as u32 > 0xFFFF {
        GeneralCategory::Surrogate
    } else {
        get_general_category(c)
    }
}

pub fn is_letter(c: char) -> bool {
    matches!(
        category(c),
        GeneralCategory::UppercaseLetter
            | GeneralCategory::LowercaseLetter
            | GeneralCategory::TitlecaseLetter
            | GeneralCategory::ModifierLetter
            | GeneralCategory::OtherLetter
    )
}

fn is_category(c: char, category: GeneralCategory) -> bool {
    self::category(c) == category
}

/// .NET's char.IsLetterOrDigit: a letter (L) or a decimal digit (Nd).
pub fn is_letter_or_digit(c: char) -> bool {
    is_letter(c) || is_category(c, GeneralCategory::DecimalNumber)
}

fn is_mark(c: char) -> bool {
    matches!(category(c), GeneralCategory::NonspacingMark | GeneralCategory::SpacingMark)
}

/// The normalised words of value (NFKC, lower case, apostrophes inside words deleted, punctuation as separators,
/// & as "and").
pub fn words(value: &str) -> Vec<String> {
    let text: Vec<char> = to_lower_invariant(&value.nfkc().collect::<String>()).chars().collect();
    let mut words = Vec::new();
    let mut word = String::new();
    for (i, &c) in text.iter().enumerate() {
        if is_letter_or_digit(c) || is_mark(c) {
            word.push(c);
        } else if APOSTROPHES.contains(c) && !word.is_empty() && i + 1 < text.len() && is_letter(text[i + 1]) {
            // Inside a word: deleted, so the word stays whole.
        } else {
            if !word.is_empty() {
                words.push(std::mem::take(&mut word));
            }
            if c == '&' {
                words.push("and".to_string());
            }
        }
    }
    if !word.is_empty() {
        words.push(word);
    }
    words
}

/// .NET's char.ToLowerInvariant: the simple lower case, except that the Turkish capital İ (U+0130) stays as it is.
/// (A character whose full mapping is more than one character has no simple one here: it stays too.)
pub fn lower_invariant(c: char) -> char {
    if c == 'İ' {
        return c;
    }
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

/// .NET's char.ToUpperInvariant: the simple upper case, except that the Turkish dotless ı (U+0131) stays as it is.
pub fn upper_invariant(c: char) -> char {
    if c == 'ı' {
        return c;
    }
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

pub fn to_lower_invariant(s: &str) -> String {
    s.chars().map(lower_invariant).collect()
}

pub fn to_upper_invariant(s: &str) -> String {
    s.chars().map(upper_invariant).collect()
}

/// A Latin word without diacritics: Ştefan → stefan, straße → strasse.
pub fn strip_diacritics(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.nfd() {
        match c {
            'ß' => out.push_str("ss"),
            'ø' => out.push('o'),
            'đ' => out.push('d'),
            'ł' => out.push('l'),
            'æ' => out.push_str("ae"),
            'œ' => out.push_str("oe"),
            'ı' => out.push('i'),
            _ if is_category(c, GeneralCategory::NonspacingMark) => {}
            _ => out.push(c),
        }
    }
    out.nfc().collect()
}

pub fn is_cyrillic(c: char) -> bool {
    ('Ѐ'..='ԯ').contains(&c)
}

pub fn is_latin(c: char) -> bool {
    c.is_ascii_lowercase() || ('À'..='ɏ').contains(&c) || ('Ḁ'..='ỿ').contains(&c)
}

const FOLD_RULES: [(&str, &str); 19] = [
    ("shch", "s"),
    ("sch", "s"),
    ("kh", "h"),
    ("zh", "z"),
    ("ts", "c"),
    ("tz", "c"),
    ("ch", "c"),
    ("sh", "s"),
    ("ya", "a"),
    ("ia", "a"),
    ("yu", "u"),
    ("iu", "u"),
    ("ye", "e"),
    ("ie", "e"),
    ("yo", "o"),
    ("io", "o"),
    ("y", "i"),
    ("j", "i"),
    ("g", "h"),
];

/// A key on which romanisation schemes agree, for comparison only (spec section 5.2).
pub fn fold_key(latin: &str) -> String {
    let mut key = latin.to_string();
    for (from, to) in FOLD_RULES {
        if key.contains(from) {
            key = key.replace(from, to);
        }
    }
    if key.ends_with("ff") {
        key.truncate(key.len() - 2);
        key.push('v');
    }
    let mut out = String::with_capacity(key.len());
    let mut last = None;
    for c in key.chars() {
        if Some(c) != last {
            out.push(c);
            last = Some(c);
        }
    }
    out
}

/// One language's Cyrillic-to-Latin table; `initial` letters apply at the start of a word.
#[derive(Debug)]
pub struct TransliterationTable {
    pub language: String,
    pub letters: HashMap<char, String>,
    pub initial: HashMap<char, String>,
}

impl TransliterationTable {
    pub fn transliterate(&self, text: &str) -> String {
        let chars: Vec<char> = text.chars().collect();
        let mut out = String::with_capacity(text.len() * 2);
        for (i, &c) in chars.iter().enumerate() {
            let word_start = i == 0 || !is_letter(chars[i - 1]);
            if let (true, Some(initial)) = (word_start, self.initial.get(&c)) {
                out.push_str(initial);
            } else if let Some(latin) = self.letters.get(&c) {
                out.push_str(latin);
            } else {
                out.push(c);
            }
        }
        out
    }
}

const MAX_FORMS: usize = 8;

fn look_alike(c: char) -> Option<char> {
    Some(match c {
        'а' => 'a',
        'в' => 'b',
        'е' => 'e',
        'к' => 'k',
        'м' => 'm',
        'н' => 'h',
        'о' => 'o',
        'р' => 'p',
        'с' => 'c',
        'т' => 't',
        'у' => 'y',
        'х' => 'x',
        'і' => 'i',
        'ј' => 'j',
        'ѕ' => 's',
        _ => return None,
    })
}

/// Builds a token: every Latin reading and its fold key. Never strips marks before transliterating.
pub(crate) fn create_token(tables: &[TransliterationTable], text: &str) -> Token {
    let chars: Vec<char> = text.chars().collect();
    let has_cyrillic = chars.iter().any(|&c| is_cyrillic(c));
    let has_latin = chars.iter().any(|&c| is_latin(c));
    let is_number = chars.iter().all(|c| c.is_ascii_digit());
    let mut forms: Vec<String> = Vec::new();
    if has_cyrillic {
        for table in tables {
            forms.push(table.transliterate(text));
        }
        let all_look_alike = chars.len() <= 4 && chars.iter().all(|&c| look_alike(c).is_some() || c.is_ascii_alphabetic());
        if has_latin || all_look_alike {
            forms.push(chars.iter().map(|&c| look_alike(c).unwrap_or(c)).collect());
        }
        for form in forms.iter_mut() {
            *form = strip_diacritics(form);
        }
    } else if has_latin || is_number {
        forms.push(strip_diacritics(text));
        if text.contains(['ü', 'ö', 'ä']) {
            forms.push(strip_diacritics(&text.replace('ü', "ue").replace('ö', "oe").replace('ä', "ae")));
        }
    }

    let mut distinct: Vec<String> = Vec::new();
    for form in forms {
        if !form.is_empty() && !distinct.contains(&form) {
            distinct.push(form);
        }
    }
    distinct.truncate(MAX_FORMS);
    let folds = if is_number {
        distinct.clone()
    } else {
        let mut folds: Vec<String> = Vec::new();
        for form in &distinct {
            let fold = fold_key(form);
            if !folds.contains(&fold) {
                folds.push(fold);
            }
        }
        folds
    };
    Token { text: text.to_string(), forms: distinct, folds, is_cyrillic: has_cyrillic, is_number, length: chars.len() as u32, groups: None }
}

/// Section 5.1: normalise, replace multi-word legal forms, join runs of single letters (keeping the letters for
/// initials), then transliterate. With a cache, tokens are interned.
pub struct Tokenizer<'a> {
    pub data: &'a MatcherData,
    pub cache: Option<HashMap<String, TokenRef>>,
}

impl<'a> Tokenizer<'a> {
    pub fn new(data: &'a MatcherData) -> Self {
        Tokenizer { data, cache: None }
    }

    pub fn interning(data: &'a MatcherData) -> Self {
        Tokenizer { data, cache: Some(HashMap::default()) }
    }

    pub fn tokenize(&mut self, value: &str, join_initials: bool) -> Vec<RawToken> {
        let words = self.replace_legal_phrases(words(&remove_hyphenated_honorifics(value)));
        let mut tokens = Vec::with_capacity(words.len());
        let mut i = 0;
        while i < words.len() {
            if !join_initials || !is_single_letter(&words[i]) {
                tokens.push(RawToken { token: self.get(&words[i]), initials: None });
                i += 1;
                continue;
            }
            let start = i;
            while i + 1 < words.len() && is_single_letter(&words[i + 1]) {
                i += 1;
            }
            let run = &words[start..=i];
            let initials: Vec<TokenRef> = run.iter().map(|letter| self.get(letter)).collect();
            tokens.push(RawToken { token: self.get(&run.concat()), initials: Some(initials) });
            i += 1;
        }
        tokens
    }

    pub fn get(&mut self, text: &str) -> TokenRef {
        match &mut self.cache {
            None => Arc::new(self.data.token(text)),
            Some(cache) => {
                if let Some(token) = cache.get(text) {
                    return token.clone();
                }
                let token = Arc::new(self.data.token(text));
                cache.insert(text.to_string(), token.clone());
                token
            }
        }
    }

    fn replace_legal_phrases(&self, words: Vec<String>) -> Vec<String> {
        if words.len() < 2 {
            return words;
        }
        let mut result = Vec::with_capacity(words.len());
        let mut i = 0;
        while i < words.len() {
            let found = self.data.legal_phrases.get(&words[i]).and_then(|candidates| {
                candidates.iter().find(|candidate| {
                    i + candidate.words.len() <= words.len() && candidate.words.iter().enumerate().all(|(j, w)| &words[i + j] == w)
                })
            });
            match found {
                None => {
                    result.push(words[i].clone());
                    i += 1;
                }
                Some(phrase) => {
                    result.push(phrase.short.clone());
                    i += phrase.words.len();
                }
            }
        }
        result
    }
}

fn is_single_letter(word: &str) -> bool {
    let mut chars = word.chars();
    matches!((chars.next(), chars.next()), (Some(c), None) if is_letter(c))
}

/// Removes г-н and г-жа (any case), with an optional full stop, where no letter touches them: the reference's
/// `(?<!\p{L})г-(н|жа)(?!\p{L})\.?` replaced by a space.
pub fn remove_hyphenated_honorifics(value: &str) -> String {
    let chars: Vec<char> = value.chars().collect();
    let is = |i: usize, lower: char| i < chars.len() && simple_lower(chars[i]) == lower;
    let mut out = String::with_capacity(value.len());
    let mut i = 0;
    while i < chars.len() {
        if (i == 0 || !is_letter(chars[i - 1])) && is(i, 'г') && i + 1 < chars.len() && chars[i + 1] == '-' {
            let end = if is(i + 2, 'н') {
                Some(i + 3)
            } else if is(i + 2, 'ж') && is(i + 3, 'а') {
                Some(i + 4)
            } else {
                None
            };
            if let Some(mut end) = end {
                if end >= chars.len() || !is_letter(chars[end]) {
                    if end < chars.len() && chars[end] == '.' {
                        end += 1;
                    }
                    out.push(' ');
                    i = end;
                    continue;
                }
            }
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

/// Go's unicode.ToLower (the simple mapping, the Turkish İ included), which the honorific check uses.
fn simple_lower(c: char) -> char {
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The reference reads UTF-16 units: a character beyond the Basic Multilingual Plane is two surrogates, neither a
    /// letter, so it separates words (Copilot's point on #37), and a mark there isn't stripped.
    #[test]
    fn a_character_beyond_the_basic_plane_separates_words() {
        assert_eq!(words("Acme\u{10400}"), ["acme"]);
        assert_eq!(words("ab\u{10400}cd"), ["ab", "cd"]);
        assert_eq!(words("o'\u{10400}neil"), ["o", "neil"]);
        assert_eq!(strip_diacritics("e\u{1D167}"), "e\u{1D167}");
    }
}
