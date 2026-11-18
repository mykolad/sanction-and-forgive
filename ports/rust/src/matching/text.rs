//! Text: normalised words, Latin readings and fold keys (spec sections 5.1 and 5.2), as the .NET reference makes them.
//! Strings are handled as chars; .NET counts UTF-16 units, the same for every character the lists and the dictionaries
//! use (the Basic Multilingual Plane).

use super::{HashMap, HashSet};
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
    /// A word of a name read as Korean, its readings (korean.rs: the spellings of one name read alike); None for any
    /// other word, so a Jung elsewhere never meets a Chung.
    pub korean: Option<Box<[String]>>,
    /// A Chinese word's readings in pinyin (pinyin_readings: "zhangwei"), compared exactly with a word in Latin script
    /// only, never with another Chinese word or a transliteration; None for any other word.
    pub pinyin: Option<Box<[String]>>,
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

    /// Whether this word is too short to name a company on its own (the short-core cap): under four letters, or for a
    /// Han or Hangul word, where each character is a syllable, a single character.
    pub fn is_short_core(&self) -> bool {
        self.length < if cjk_script(&self.text) != 0 { 2 } else { 4 }
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
/// each a surrogate (Cs), so never a letter, a digit or a mark, and a word ends there. Except a Han character there
/// (Rust first, #93): a letter, as in the Basic Multilingual Plane, so a name written with one keeps it.
pub fn category(c: char) -> GeneralCategory {
    if c as u32 > 0xFFFF && is_han(c) {
        GeneralCategory::OtherLetter
    } else if c as u32 > 0xFFFF {
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
    // Eastern Arabic digits are read as ASCII ones (arabic.json), so a number compares as a number in any script.
    let text: Vec<char> = to_lower_invariant(&value.nfkc().collect::<String>()).chars().map(ascii_digit).collect();
    let mut words = Vec::new();
    let mut word = String::new();
    for (i, &c) in text.iter().enumerate() {
        // 〇 (U+3007), the ideographic zero, is a number letter, not a letter: kept, so 七〇八 isn't 七八.
        if is_letter_or_digit(c) || is_mark(c) || c == '\u{3007}' {
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
    // A "word" of Arabic marks or tatweel alone (a stray ـ) carries no letter: it isn't a word.
    words.retain(|word| !word.chars().all(is_arabic) || !arabic_form(word).is_empty());
    words
}

/// Chinese and Korean names compared in their own script (data/cjk.json, #93): the split-off forms, longest first,
/// and each traditional Han character's simplified reading. Rust only for now; .NET and Go hash the file.
struct CjkTable {
    split_off: Vec<String>,
    traditional: HashMap<char, char>,
}

static CJK: std::sync::LazyLock<CjkTable> = std::sync::LazyLock::new(|| {
    let bytes = super::data::resource("cjk.json").expect("cjk.json is embedded");
    let document: serde_json::Value = serde_json::from_slice(bytes).expect("cjk.json is JSON");
    let mut split_off: Vec<String> = document["splitOff"]
        .as_array()
        .expect("cjk.json has splitOff")
        .iter()
        .map(|form| form.as_str().expect("a form is text").to_string())
        .collect();
    split_off.sort_by_key(|form| std::cmp::Reverse(form.chars().count()));
    let one = |text: &str| {
        let mut chars = text.chars();
        match (chars.next(), chars.next()) {
            (Some(c), None) => c,
            _ => panic!("cjk.json: '{text}' isn't one character."),
        }
    };
    let traditional = document["traditional"]
        .as_object()
        .expect("cjk.json has traditional")
        .iter()
        .map(|(from, to)| (one(from), one(to.as_str().expect("a reading is text"))))
        .collect();
    CjkTable { split_off, traditional }
});

/// Chinese characters read in pinyin (data/pinyin.json, #93; Rust only for now, .NET and Go hash the file): each
/// character's most common reading, a surname's at the start of a name, and words read otherwise than their
/// characters one by one, longest first.
struct PinyinTable {
    readings: HashMap<char, String>,
    surnames: HashMap<char, String>,
    common_surnames: HashSet<char>,
    words: Vec<(Vec<char>, String)>,
}

static PINYIN: std::sync::LazyLock<PinyinTable> = std::sync::LazyLock::new(|| {
    let bytes = super::data::resource("pinyin.json").expect("pinyin.json is embedded");
    let document: serde_json::Value = serde_json::from_slice(bytes).expect("pinyin.json is JSON");
    let object = |key: &str| document[key].as_object().unwrap_or_else(|| panic!("pinyin.json has {key}"));
    let text = |value: &serde_json::Value| value.as_str().expect("a reading is text").to_string();
    let mut readings = HashMap::default();
    for (reading, characters) in object("readings") {
        for c in text(characters).chars() {
            readings.insert(c, reading.clone());
        }
    }
    let one = |key: &str| {
        let mut chars = key.chars();
        match (chars.next(), chars.next()) {
            (Some(c), None) => c,
            _ => panic!("pinyin.json: surname '{key}' isn't one character."),
        }
    };
    let surnames = object("surnames").iter().map(|(c, reading)| (one(c), text(reading))).collect();
    let mut words: Vec<(Vec<char>, String)> = object("words").iter().map(|(w, reading)| (w.chars().collect(), text(reading))).collect();
    words.sort_by_key(|(w, _)| std::cmp::Reverse(w.len()));
    let common_surnames = document["commonSurnames"].as_str().expect("pinyin.json has commonSurnames").chars().collect();
    PinyinTable { readings, surnames, common_surnames, words }
});

/// A Chinese word's readings in pinyin, lower case without tones ("zhangwei"): its characters' readings run together
/// (each character's own reading, else its simplified one's; a word of pinyin.json's read as a whole); when its first
/// character reads otherwise as a surname, that reading too (曾国藩: cengguofan and zengguofan); and for a name of two
/// or three characters, the given name before the surname, as some lists write it (张伟: weizhang, OFAC's Wei ZHANG).
/// Empty when a character has no reading.
pub fn pinyin_readings(word: &str) -> Vec<String> {
    let original: Vec<char> = word.chars().collect();
    let chars: Vec<char> = simplified(word).chars().collect();
    let table = &*PINYIN;
    // One part per character, or per word read as a whole.
    let mut parts: Vec<String> = Vec::new();
    let (mut i, mut first_alone) = (0, true);
    while i < chars.len() {
        if let Some((w, r)) = table.words.iter().find(|(w, _)| chars[i..].starts_with(w)) {
            first_alone &= i != 0;
            parts.push(r.clone());
            i += w.len();
            continue;
        }
        // 々 repeats the character before it, and reads as it does.
        let repeated = (chars[i] == '々').then(|| parts.last()).flatten();
        let Some(r) = repeated.or_else(|| original.get(i).and_then(|c| table.readings.get(c))).or_else(|| table.readings.get(&chars[i]))
        else {
            return Vec::new();
        };
        parts.push(r.clone());
        i += 1;
    }
    let mut out: Vec<String> = Vec::new();
    let mut add = |reading: String| {
        if !out.contains(&reading) {
            out.push(reading);
        }
    };
    // The surname's own reading only where the first part is that one character (not a word read as a whole).
    // A surname written as a word of its own (曾 Guofan) is one too.
    let surname = table.surnames.get(&chars[0]).filter(|_| first_alone);
    let firsts: Vec<&String> = std::iter::once(&parts[0]).chain(surname).collect();
    for first in &firsts {
        add(format!("{first}{}", parts[1..].concat()));
    }
    // Given name first only for a person's name: two or three characters, the first a common surname.
    if first_alone && (2..=3).contains(&chars.len()) && table.common_surnames.contains(&chars[0]) {
        for first in &firsts {
            add(format!("{}{first}", parts[1..].concat()));
        }
    }
    out
}

/// A word in Latin script (not a transliteration of another script's word, which reads in Latin letters too).
pub fn is_latin_word(token: &Token) -> bool {
    // A word joined from particles (An Na: an-na) is in Latin script too.
    !token.is_number && token.text.chars().any(is_latin) && token.text.chars().all(|c| is_latin(c) || c == '-')
}

/// A word in Latin script's letters as a pinyin reading is compared with them: its forms, and a word joined from
/// particles as written, without its hyphens (an-na: anna), never an Arabic reading that drops a part.
pub fn latin_reads_as(token: &Token, reading: &str) -> bool {
    is_latin_word(token) && latin_spellings(token).iter().any(|s| s == reading)
}

/// A word in Latin script's full spellings: its forms, or for a word joined from particles its text without hyphens
/// alone (Al Lina: allina, never the lina of a reading that drops a part).
pub fn latin_spellings(token: &Token) -> Vec<String> {
    if token.text.contains('-') {
        vec![token.text.replace('-', "")]
    } else {
        token.forms.clone()
    }
}

/// A Han character: the unified and compatibility ideographs, in the Basic Multilingual Plane and beyond it.
pub fn is_han(c: char) -> bool {
    // The supplementary ideographic planes too (extensions B to H, compatibility ideographs): 256 of cjk.json's
    // simplified readings are there.
    matches!(
        c as u32,
        0x3400..=0x4DBF | 0x4E00..=0x9FFF | 0xF900..=0xFAFF | 0x3005 | 0x3007 | 0x20000..=0x2FA1F | 0x30000..=0x323AF
    )
}

/// A Hangul letter: syllables and jamo.
pub fn is_hangul(c: char) -> bool {
    matches!(c as u32, 0xAC00..=0xD7AF | 0x1100..=0x11FF | 0x3130..=0x318F | 0xA960..=0xA97F | 0xD7B0..=0xD7FF)
}

/// Han characters read as simplified ones (cjk.json traditional); a character for a character.
pub fn simplified(text: &str) -> String {
    text.chars().map(|c| CJK.traditional.get(&c).copied().unwrap_or(c)).collect()
}

/// Whether a word is wholly Han (1) or wholly Hangul (2), or neither (0).
fn cjk_script(word: &str) -> u8 {
    if !word.is_empty() && word.chars().all(is_han) {
        1
    } else if !word.is_empty() && word.chars().all(is_hangul) {
        2
    } else {
        0
    }
}

/// Adjacent words wholly in Han or wholly in Hangul as one word (neither script spaces a name as Latin does), then the
/// company forms (cjk.json splitOff) split off either end as words of their own (names only: an address keeps its parts,
/// so its country and its numbers stay words of their own): the longest that fits, compared in
/// simplified characters, again and again while at least two characters are left. 中兴 通讯股份有限公司 is 中兴通讯 and
/// 股份有限公司; 주식회사 삼성전자 is 주식회사 and 삼성전자.
fn join_and_split_cjk(words: Vec<String>) -> Vec<String> {
    if !words.iter().any(|word| cjk_script(word) != 0) {
        return words;
    }
    let mut joined: Vec<String> = Vec::with_capacity(words.len());
    for word in words {
        let script = cjk_script(&word);
        match joined.last_mut() {
            Some(last) if script != 0 && cjk_script(last) == script => last.push_str(&word),
            _ => joined.push(word),
        }
    }
    let mut out = Vec::with_capacity(joined.len() + 2);
    for word in joined {
        if cjk_script(&word) == 0 {
            out.push(word);
            continue;
        }
        let (mut before, mut after, mut core) = (Vec::new(), Vec::new(), word);
        loop {
            let chars: Vec<char> = core.chars().collect();
            let folded = simplified(&core);
            let longest = |at_end: bool| {
                CJK.split_off
                    .iter()
                    .find(|form| if at_end { folded.ends_with(form.as_str()) } else { folded.starts_with(form.as_str()) })
                    .map(|form| form.chars().count())
            };
            if let Some(k) = longest(true).filter(|&k| chars.len() >= k + 2) {
                after.push(chars[chars.len() - k..].iter().collect::<String>());
                core = chars[..chars.len() - k].iter().collect();
            } else if let Some(k) = longest(false).filter(|&k| chars.len() >= k + 2) {
                before.push(chars[..k].iter().collect::<String>());
                core = chars[k..].iter().collect();
            } else {
                break;
            }
        }
        out.extend(before);
        out.push(core);
        out.extend(after.into_iter().rev());
    }
    out
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

/// The letters a word's marks don't come off by decomposing (data/letters.json, hashed into the matcher version with
/// the other dictionaries): what each folds to, and the second spellings.
struct LetterTables {
    folds: HashMap<char, String>,
    second_forms: Vec<HashMap<char, String>>,
}

static LETTERS: std::sync::LazyLock<LetterTables> = std::sync::LazyLock::new(|| {
    let bytes = super::data::resource("letters.json").expect("letters.json is embedded");
    let document: serde_json::Value = serde_json::from_slice(bytes).expect("letters.json is JSON");
    let table = |entries: &serde_json::Value| -> HashMap<char, String> {
        entries
            .as_object()
            .expect("letters.json's tables are objects")
            .iter()
            .map(|(letter, value)| {
                let mut chars = letter.chars();
                let (Some(c), None) = (chars.next(), chars.next()) else { panic!("letters.json: '{letter}' isn't one letter.") };
                (c, value.as_str().expect("a letter's reading is text").to_string())
            })
            .collect()
    };
    let groups = document["secondForms"].as_array().expect("letters.json has secondForms").iter().map(table).collect();
    LetterTables { folds: table(&document["folds"]), second_forms: groups }
});

/// A Latin word without diacritics: Ştefan → stefan, straße → strasse, þór → thor. The folds (letters.json) apply
/// first, then again to what decomposition leaves (ǿ is ø with an acute), and the marks are dropped.
pub fn strip_diacritics(value: &str) -> String {
    let mut folded = String::with_capacity(value.len() + 4);
    for c in value.chars() {
        match LETTERS.folds.get(&c) {
            Some(fold) => folded.push_str(fold),
            None => folded.push(c),
        }
    }
    let mut out = String::with_capacity(folded.len());
    for c in folded.nfd() {
        if is_category(c, GeneralCategory::NonspacingMark) {
            continue;
        }
        match LETTERS.folds.get(&c) {
            Some(fold) => out.push_str(fold),
            None => out.push(c),
        }
    }
    out.nfc().collect()
}

/// A word's second spellings (letters.json): one per language group whose letters it has, and one with every such
/// group together: müller → mueller; åström → åstroem, aaström, aastroem. None if it has none of those letters.
pub fn second_forms(value: &str) -> Vec<String> {
    let groups: Vec<&HashMap<char, String>> =
        LETTERS.second_forms.iter().filter(|group| value.chars().any(|c| group.contains_key(&c))).collect();
    let replace = |groups: &[&HashMap<char, String>]| -> String {
        value.chars().map(|c| groups.iter().find_map(|group| group.get(&c).cloned()).unwrap_or_else(|| c.to_string())).collect()
    };
    let mut forms: Vec<String> = groups.iter().map(|group| replace(&[group])).collect();
    if groups.len() > 1 {
        forms.push(replace(&groups));
    }
    forms
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

/// Arabic-script words compared in their own script (data/arabic.json, #92): the marks dropped and the letters read
/// as one another. Rust only for now; .NET and Go hash the file and will read it when they follow.
struct ArabicTable {
    drop: Vec<(u32, u32)>,
    letters: HashMap<char, String>,
    abd_names: HashSet<String>,
}

static ARABIC: std::sync::LazyLock<ArabicTable> = std::sync::LazyLock::new(|| {
    let bytes = super::data::resource("arabic.json").expect("arabic.json is embedded");
    let document: serde_json::Value = serde_json::from_slice(bytes).expect("arabic.json is JSON");
    let hex = |text: &str| u32::from_str_radix(text, 16).unwrap_or_else(|_| panic!("arabic.json: '{text}' isn't hexadecimal"));
    let drop = document["drop"]
        .as_array()
        .expect("arabic.json has drop")
        .iter()
        .map(|range| {
            let range = range.as_str().expect("a range is text");
            match range.split_once('-') {
                Some((first, last)) => (hex(first), hex(last)),
                None => (hex(range), hex(range)),
            }
        })
        .collect();
    let letters = document["letters"]
        .as_object()
        .expect("arabic.json has letters")
        .iter()
        .map(|(letter, value)| {
            let mut chars = letter.chars();
            let (Some(c), None) = (chars.next(), chars.next()) else { panic!("arabic.json: '{letter}' isn't one letter.") };
            (c, value.as_str().expect("a letter's reading is text").to_string())
        })
        .collect();
    let abd_names = document["abdNames"]
        .as_array()
        .expect("arabic.json has abdNames")
        .iter()
        .map(|name| name.as_str().expect("a name is text").to_string())
        .collect();
    ArabicTable { drop, letters, abd_names }
});

/// An Eastern Arabic digit as its ASCII digit (arabic.json reads ٢ and ۲ as 2); any other character as it is.
fn ascii_digit(c: char) -> char {
    if !('\u{0660}'..='\u{06F9}').contains(&c) {
        return c;
    }
    match ARABIC.letters.get(&c).map(String::as_bytes) {
        Some([digit]) if digit.is_ascii_digit() => *digit as char,
        _ => c,
    }
}

/// A letter of the Arabic script (Arabic, Persian, Urdu and the rest), including the presentation forms.
pub fn is_arabic(c: char) -> bool {
    matches!(c as u32, 0x0600..=0x06FF | 0x0750..=0x077F | 0x0870..=0x08FF | 0xFB50..=0xFDFF | 0xFE70..=0xFEFF)
}

/// An Arabic-script word as it's compared: the marks dropped, the letters read as one another (arabic.json).
pub fn arabic_form(text: &str) -> String {
    let table = &*ARABIC;
    let mut out = String::with_capacity(text.len());
    for c in text.chars() {
        let code = c as u32;
        if table.drop.iter().any(|&(first, last)| code >= first && code <= last) {
            continue;
        }
        match table.letters.get(&c) {
            Some(reading) => out.push_str(reading),
            None => out.push(c),
        }
    }
    out
}

/// The article of Arabic names as romanised: al, el, ul, and the forms it takes before a "sun letter" (ar-Rashid,
/// as-Sayyid, ash-Shami, at-Tahir, ad-Din, an-Nur, az-Zahra), which count only before a word starting with that
/// consonant, so English words (As, At, An) aren't taken for it.
fn is_article(word: &str, next: &str) -> bool {
    match word {
        "al" | "el" | "ul" => true,
        "ash" => next.starts_with("sh"),
        "ar" | "as" | "at" | "ad" | "an" | "az" => word[1..].chars().next() == next.chars().next(),
        _ => false,
    }
}

/// A word's Latin reading for the particle rules: as written when it's plain Latin, without its diacritics, or, for a
/// Cyrillic word, the first table's transliteration; None for another script or a number. The rules decide on it, so
/// "Abu Bákr" and "Абу Бакр" are merged where "Abu Bakr" is.
fn particle_reading(tables: &[TransliterationTable], word: &str) -> Option<String> {
    let reading =
        if word.chars().any(is_cyrillic) { strip_diacritics(&tables.first()?.transliterate(word)) } else { strip_diacritics(word) };
    (reading.len() >= 2 && reading.chars().all(|c| c.is_ascii_lowercase() || c == '-')).then_some(reading)
}

/// Arabic name particles as one word with the word they belong to, joined by a hyphen (a token never has one
/// otherwise), the words' own text kept: first Abd with its name ("abd al rahman" → abd-al-rahman, "abdel rahman" →
/// abdel-rahman), the article with its word ("al sayyid" → al-sayyid) and Allah with the name before it ("nasr allah" →
/// nasr-allah); then Abu with the word after it, merged or not ("abu abd allah" → abu-abd-allah). Each rule decides on
/// the words' Latin readings (particle_reading). Rust only for now (#92); create_token reads them (arabic_readings).
pub fn merge_arabic_particles(tables: &[TransliterationTable], words: Vec<String>) -> Vec<String> {
    const ABD: [&str; 9] = ["abd", "abdel", "abdul", "abdoul", "abdal", "abdil", "abdur", "abdus", "abdu"];
    const ABU: [&str; 3] = ["abu", "abou", "abo"];
    const ALLAH: [&str; 5] = ["allah", "ullah", "ollah", "illah", "ellah"];
    let readings: Vec<Option<String>> = words.iter().map(|word| particle_reading(tables, word)).collect();
    let reading = |i: usize| readings.get(i).and_then(|r| r.as_deref());
    let mut merged: Vec<(String, Option<String>)> = Vec::with_capacity(words.len());
    let mut i = 0;
    while i < words.len() {
        match (reading(i), reading(i + 1), reading(i + 2)) {
            (Some(word), Some(next), Some(after)) if ABD.contains(&word) && is_article(next, after) && is_abd_name(after) => {
                merged.push((format!("{}-{}-{}", words[i], words[i + 1], words[i + 2]), Some(format!("{word}-{next}-{after}"))));
                i += 3;
            }
            (Some(word), Some(next), _) if ABD.contains(&word) && !is_article(next, "") && is_abd_name(next) => {
                merged.push((format!("{}-{}", words[i], words[i + 1]), Some(format!("{word}-{next}"))));
                i += 2;
            }
            (Some(word), Some(next), _) if is_article(word, next) => {
                merged.push((format!("{}-{}", words[i], words[i + 1]), Some(format!("{word}-{next}"))));
                i += 2;
            }
            (Some(word), _, _) if ALLAH.contains(&word) && merged.last().is_some_and(|(_, last)| last.is_some()) => {
                let (text, last) = merged.pop().unwrap();
                merged.push((format!("{text}-{}", words[i]), Some(format!("{}-{word}", last.unwrap()))));
                i += 1;
            }
            _ => {
                merged.push((words[i].clone(), readings[i].clone()));
                i += 1;
            }
        }
    }
    let mut out = Vec::with_capacity(merged.len());
    let mut j = 0;
    while j < merged.len() {
        let is_abu = merged[j].1.as_deref().is_some_and(|r| ABU.contains(&r));
        match merged.get(j + 1) {
            Some((next, Some(_))) if is_abu => {
                out.push(format!("{}-{next}", merged[j].0));
                j += 2;
            }
            _ => {
                out.push(merged[j].0.clone());
                j += 1;
            }
        }
    }
    out
}

/// Whether a word is one of the names that follow Abd (arabic.json abdNames), however its article is written.
/// Compared by fold key too, so a transliteration's spelling counts: "рахман" reads rakhman, which folds as rahman does.
fn is_abd_name(word: &str) -> bool {
    static FOLDED: std::sync::LazyLock<HashSet<String>> =
        std::sync::LazyLock::new(|| ARABIC.abd_names.iter().map(|name| fold_key(name)).collect());
    [word.to_string(), abd_rest(word)].iter().any(|candidate| ARABIC.abd_names.contains(candidate) || FOLDED.contains(&fold_key(candidate)))
}

/// The canonical reading of a word after "abd": the article (al, el, ul, il, or an assimilated "ur", "us"...) and a
/// vowel that only links it dropped, "allah" in any spelling as allah: alrahman, elrahman, urrahman, urahman → rahman;
/// ullah, allah, ellah → allah.
fn abd_rest(rest: &str) -> String {
    let b = rest.as_bytes();
    let vowel = |c: u8| matches!(c, b'a' | b'e' | b'i' | b'o' | b'u');
    // The article (al, el, ul, il) or an assimilated one (ur-r, us-s), then a linking vowel alone.
    let stripped = if b.len() > 5 && rest.starts_with("oul") {
        // The French spelling of the article: Abdoul Rahman, Abdoulrahman.
        &rest[3..]
    } else if b.len() > 4 && vowel(b[0]) && (b[1] == b'l' || (!vowel(b[1]) && b[1] == b[2])) {
        &rest[2..]
    } else if b.len() > 4 && vowel(b[0]) && !vowel(b[1]) {
        &rest[1..]
    } else {
        rest
    };
    let allah = |text: &str| matches!(text, "lah" | "llah" | "allah" | "ullah" | "ellah" | "illah" | "ollah");
    if allah(stripped) || allah(rest) {
        "allah".to_string()
    } else {
        stripped.to_string()
    }
}

/// "allah" at the end of a name in any spelling (nasrullah, nasrollah, hizballah) as allah.
fn allah_ending(word: &str) -> Option<String> {
    ["ullah", "ollah", "ellah", "illah"]
        .iter()
        .find_map(|ending| word.strip_suffix(ending).filter(|stem| stem.len() >= 3).map(|stem| format!("{stem}allah")))
}

/// The extra readings of a romanised Arabic name word (#92), from its Latin form (diacritics stripped or
/// transliterated): a word merged from particles (merge_arabic_particles) as written together, and canonical forms a
/// merged and a typed-together word share: Abu, Abou and Abo as abu before the next word's readings; abd + its name
/// without the article (abdulrahman, abd-al-rahman → abdrahman); an article as al (elsayed, ulsayed, arrashid →
/// alsayed, alrashid) and, written apart, the word without it (al-sayyid → sayyid); Allah in any spelling.
pub(crate) fn arabic_readings(text: &str) -> Vec<String> {
    let mut readings = Vec::new();
    let joined: String = text.chars().filter(|&c| c != '-').collect();
    if let Some((first, rest)) = text.split_once('-') {
        if matches!(first, "abu" | "abou" | "abo") {
            let rest_joined: String = rest.chars().filter(|&c| c != '-').collect();
            readings.push(joined.clone());
            readings.push(format!("abu{rest_joined}"));
            readings.extend(arabic_readings(rest).into_iter().map(|reading| format!("abu{reading}")));
            return readings;
        }
    }
    if text.contains('-') {
        readings.push(joined.clone());
        if let [first, word] = text.split('-').collect::<Vec<_>>()[..] {
            if is_article(first, word) {
                readings.push(format!("al{word}"));
                readings.push(word.to_string());
            }
        }
    }
    if let Some(rest) = joined.strip_prefix("abou").filter(|rest| rest.len() >= 3) {
        readings.push(format!("abu{rest}"));
    }
    if let Some(rest) = joined.strip_prefix("abd").map(abd_rest).filter(|rest| is_abd_name(rest)) {
        readings.push(format!("abd{rest}"));
    }
    if let Some(rest) = joined_article_rest(&joined) {
        readings.push(format!("al{rest}"));
    }
    if let Some(reading) = allah_ending(&joined) {
        readings.push(reading);
    }
    readings
}

/// The word after an article written together with it, guarded against names that only start like one: el and ul
/// before a consonant (elsayed, ulhaq), and a sun-letter article, whose consonant is written twice (arrashid,
/// assayed, ashshami, annur); what's left has at least four letters (Attar and Assad stay names).
fn joined_article_rest(joined: &str) -> Option<&str> {
    let vowel = |c: u8| matches!(c, b'a' | b'e' | b'i' | b'o' | b'u');
    let b = joined.as_bytes();
    let rest = if (joined.starts_with("el") || joined.starts_with("ul")) && b.len() > 2 && !vowel(b[2]) {
        &joined[2..]
    } else if joined.starts_with("ashsh") {
        &joined[3..]
    } else if b.len() > 3 && b[0] == b'a' && b[1] == b[2] && matches!(b[1], b't' | b'd' | b'r' | b'z' | b's' | b'n') {
        &joined[2..]
    } else {
        return None;
    };
    (rest.len() >= 4).then_some(rest)
}

/// Builds a token: every Latin reading and its fold key. Never strips marks before transliterating. An Arabic-script
/// word gets its own script's form (arabic_form), so spellings that differ by marks or letter variants align.
pub(crate) fn create_token(tables: &[TransliterationTable], text: &str) -> Token {
    let chars: Vec<char> = text.chars().collect();
    let has_cyrillic = chars.iter().any(|&c| is_cyrillic(c));
    let has_latin = chars.iter().any(|&c| is_latin(c));
    let is_number = chars.iter().all(|c| c.is_ascii_digit());
    let mut forms: Vec<String> = Vec::new();
    let mut length = chars.len() as u32;
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
        if text.contains('-') {
            // Words merged from Arabic name particles: each reading's readings (arabic_readings).
            forms = forms.iter().flat_map(|form| arabic_readings(form)).collect();
        }
    } else if !has_latin && !is_number && chars.iter().all(|&c| is_han(c)) {
        // A Han word compares in simplified characters (cjk.json), a character for a character.
        forms.push(simplified(text));
    } else if !has_latin && !is_number && chars.iter().all(|&c| is_hangul(c)) {
        forms.push(text.to_string());
    } else if !has_latin && !is_number && chars.iter().any(|&c| is_arabic(c)) {
        let form = arabic_form(text);
        // Its length is the form's: a dropped mark or tatweel mustn't make a short name long enough to pass its cap.
        length = form.chars().count() as u32;
        forms.push(form);
    } else if text.contains('-') {
        // A word merged from Arabic name particles: its readings, not the hyphenated text.
        forms.extend(arabic_readings(&strip_diacritics(text)));
    } else if has_latin || is_number {
        forms.push(strip_diacritics(text));
        if !is_number {
            forms.extend(arabic_readings(&strip_diacritics(text)));
        }
        for second in second_forms(text) {
            forms.push(strip_diacritics(&second));
        }
    }

    let mut distinct: Vec<String> = Vec::new();
    for form in forms {
        if !form.is_empty() && !distinct.contains(&form) {
            distinct.push(form);
        }
    }
    distinct.truncate(MAX_FORMS);
    // A number, or a Han or Hangul word, is its own fold key: the Latin fold rules (repeated letters as one) would make
    // 李娜 and 李娜娜 one name.
    let folds = if is_number || cjk_script(text) != 0 {
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
    // A Chinese word's pinyin readings, for a Latin word (#93).
    let pinyin = (!has_latin && !is_number && chars.iter().all(|&c| is_han(c)))
        .then(|| pinyin_readings(text))
        .filter(|readings| !readings.is_empty())
        .map(Vec::into_boxed_slice);
    Token {
        text: text.to_string(),
        forms: distinct,
        folds,
        is_cyrillic: has_cyrillic,
        is_number,
        length,
        groups: None,
        korean: None,
        pinyin,
    }
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
        let words = merge_arabic_particles(
            &self.data.tables,
            self.replace_legal_phrases(join_and_split_cjk(words(&remove_hyphenated_honorifics(value)))),
        );
        let mut tokens = Vec::with_capacity(words.len());
        // A Korean name's words carry its readings (korean.rs); the same word in any other name doesn't. Titles and stop
        // words don't count against it (General Ri Yong Ho), as the person reading drops them. A single letter stays an
        // initial even where it could spell a surname (I, O, U): read as a surname, Ali O. lost its review against Ali.
        let named: Vec<String> = words
            .iter()
            .filter(|w| !self.data.honorifics.contains(w.as_str()) && !self.data.stop_words.contains(w.as_str()))
            .cloned()
            .collect();
        let korean = super::korean::is_korean_name(&named);
        // Each word's role in the Korean name (korean::roles over the named words), by its position among all words.
        let mut roles: Vec<Option<super::korean::Role>> = vec![None; words.len()];
        if korean {
            let mut named_roles = super::korean::roles(&named).into_iter();
            for (word, role) in words.iter().zip(roles.iter_mut()) {
                if !self.data.honorifics.contains(word.as_str()) && !self.data.stop_words.contains(word.as_str()) {
                    *role = named_roles.next();
                }
            }
        }
        let mut i = 0;
        while i < words.len() {
            if !join_initials || !is_single_letter(&words[i]) {
                let token = match roles[i] {
                    Some(role) => self.get_korean(&words[i], role),
                    None => self.get(&words[i]),
                };
                tokens.push(RawToken { token, initials: None });
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

    /// The word as part of a Korean name: its token with the Korean readings, interned apart from the plain word.
    fn get_korean(&mut self, text: &str, role: super::korean::Role) -> TokenRef {
        let key = format!("\u{1}ko:{role:?}:{text}");
        if let Some(token) = self.cache.as_ref().and_then(|cache| cache.get(&key)) {
            return token.clone();
        }
        let mut token = self.data.token(text);
        token.korean = Some(super::korean::readings(text, role).into_boxed_slice());
        let token = Arc::new(token);
        if let Some(cache) = &mut self.cache {
            cache.insert(key, token.clone());
        }
        token
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
    let one_letter = |text: &str| {
        let mut chars = text.chars();
        matches!((chars.next(), chars.next()), (Some(c), None) if is_letter(c))
    };
    // An Arabic letter written with a mark or a tatweel is still one letter: its reading decides, its text stays.
    // A Chinese or Korean character is a syllable of a name, never an initial (张 Guofan is Zhang Guofan).
    if word.chars().any(|c| is_han(c) || is_hangul(c)) {
        return false;
    }
    one_letter(word) || (word.chars().all(is_arabic) && one_letter(&arabic_form(word)))
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
