//! Korean names in Latin letters (data/korean.json, #93; Rust first): the South's, McCune-Reischauer's, the North's and
//! the popular spellings of one name read alike. A name of two to four words, each one or two romanised syllables,
//! with a Korean surname first or last, is read as Korean (`is_korean_name`); its words then carry readings
//! (`readings`): one per way the word splits into syllables, each syllable as the classes of its initial, vowel and
//! final, and one per surname group the word spells. Two words with a reading in common compare as one spelling.

use std::sync::LazyLock;

use super::HashMap;

struct KoreanTable {
    /// Each spelling with its class, longest first, so a parse tries "tch" before "t".
    initials: Vec<(String, String)>,
    vowels: Vec<(String, String)>,
    finals: Vec<(String, String)>,
    /// Each surname spelling with the groups it belongs to.
    surnames: HashMap<String, Vec<usize>>,
}

static KOREAN: LazyLock<KoreanTable> = LazyLock::new(|| {
    let bytes = super::data::resource("korean.json").expect("korean.json is embedded");
    let document: serde_json::Value = serde_json::from_slice(bytes).expect("korean.json is JSON");
    let classes = |key: &str| {
        let mut list: Vec<(String, String)> = document[key]
            .as_object()
            .unwrap_or_else(|| panic!("korean.json has {key}"))
            .iter()
            .map(|(spelling, class)| (spelling.clone(), class.as_str().expect("a class is text").to_string()))
            .collect();
        list.sort_by(|a, b| b.0.len().cmp(&a.0.len()).then_with(|| a.0.cmp(&b.0)));
        list
    };
    let mut surnames: HashMap<String, Vec<usize>> = HashMap::default();
    for (group, spellings) in document["surnames"].as_array().expect("korean.json has surnames").iter().enumerate() {
        for spelling in spellings.as_array().expect("a surname group is a list") {
            surnames.entry(spelling.as_str().expect("a surname is text").to_string()).or_default().push(group);
        }
    }
    KoreanTable { initials: classes("initials"), vowels: classes("vowels"), finals: classes("finals"), surnames }
});

/// At most this many ways to split one word, and readings of it: a name's words are short, so this only bounds the odd
/// long one.
const MAX_PARSES: usize = 16;
const MAX_READINGS: usize = 32;
/// A word of a Korean name is one syllable, or a given name's two written as one (Jongun): three makes Maria.
const MAX_SYLLABLES: usize = 2;

/// A syllable's classes: initial (empty when it starts with its vowel), vowel, final (empty when none).
#[derive(Clone)]
struct Syllable<'t> {
    initial: &'t str,
    vowel: &'t str,
    last: &'t str,
}

/// Every way word splits into one or two (MAX_SYLLABLES) romanised syllables (at most MAX_PARSES).
fn parses(word: &str) -> Vec<Vec<Syllable<'static>>> {
    let table = &*KOREAN;
    let mut out = Vec::new();
    let mut stack: Vec<Syllable<'static>> = Vec::new();
    fn walk(table: &'static KoreanTable, rest: &str, stack: &mut Vec<Syllable<'static>>, out: &mut Vec<Vec<Syllable<'static>>>) {
        if out.len() >= MAX_PARSES {
            return;
        }
        if rest.is_empty() {
            if !stack.is_empty() {
                out.push(stack.clone());
            }
            return;
        }
        if stack.len() == MAX_SYLLABLES {
            return;
        }
        let initials = table.initials.iter().filter(|(s, _)| rest.starts_with(s.as_str())).map(|(s, c)| (s.len(), c.as_str()));
        for (taken, initial) in initials.chain(std::iter::once((0, ""))) {
            let after_initial = &rest[taken..];
            for (vowel, vowel_class) in table.vowels.iter().filter(|(s, _)| after_initial.starts_with(s.as_str())) {
                let after_vowel = &after_initial[vowel.len()..];
                let finals = table.finals.iter().filter(|(s, _)| after_vowel.starts_with(s.as_str())).map(|(s, c)| (s.len(), c.as_str()));
                for (final_len, last) in finals.chain(std::iter::once((0, ""))) {
                    stack.push(Syllable { initial, vowel: vowel_class, last });
                    walk(table, &after_vowel[final_len..], stack, out);
                    stack.pop();
                }
            }
        }
    }
    walk(table, word, &mut stack, &mut out);
    out
}

/// The word as the romanisations write it: McCune-Reischauer's breves dropped (chŏng, ŭn), as a word's Latin form is.
fn latin(word: &str) -> String {
    super::text::strip_diacritics(word)
}

/// Whether word can be written by the romanisations at all (lower case, ASCII letters, once diacritics are dropped),
/// or is a surname's spelling (Yi, Park, Choi: spellings the classes don't make).
fn parses_as_korean(word: &str) -> bool {
    let word = latin(word);
    KOREAN.surnames.contains_key(&word) || (!word.is_empty() && word.bytes().all(|b| b.is_ascii_lowercase()) && !parses(&word).is_empty())
}

/// Whether a name of these words (already normalised) reads as Korean: two to four words, each one or two romanised
/// syllables (or a surname's spelling), and a Korean surname first or last.
pub fn is_korean_name(words: &[String]) -> bool {
    if !(2..=4).contains(&words.len()) || !words.iter().all(|w| parses_as_korean(w)) {
        return false;
    }
    let table = &*KOREAN;
    table.surnames.contains_key(&latin(&words[0])) || table.surnames.contains_key(&latin(&words[words.len() - 1]))
}

/// A syllable's readings: its classes, and where the South drops or changes an initial r or n (before i or y: Ri, Yi;
/// otherwise r as n: Ro, No), the reading without it too.
fn syllable_readings(s: &Syllable) -> Vec<String> {
    let reading = |initial: &str| format!("{initial}.{}.{}", s.vowel, s.last);
    let mut out = vec![reading(s.initial)];
    let before_i_or_y = s.vowel.starts_with('I') || s.vowel.starts_with('Y');
    match s.initial {
        "R" | "N" if before_i_or_y => out.push(reading("")),
        "R" => out.push(reading("N")),
        _ => {}
    }
    out
}

/// A word's Korean readings (in a name is_korean_name accepts): one per split into syllables and per initial-sound
/// variant, and one per surname group it spells. Prefixed so they never meet a word's other readings or fold keys.
pub fn readings(word: &str) -> Vec<String> {
    let word = latin(word);
    let mut out: Vec<String> = Vec::new();
    for parse in parses(&word) {
        let mut readings = vec![String::new()];
        for syllable in &parse {
            let variants = syllable_readings(syllable);
            readings = readings
                .iter()
                .flat_map(|r| variants.iter().map(move |v| if r.is_empty() { v.clone() } else { format!("{r}|{v}") }))
                .take(MAX_READINGS)
                .collect();
        }
        for r in readings {
            let key = format!("\u{1}ko:{r}");
            if !out.contains(&key) && out.len() < MAX_READINGS {
                out.push(key);
            }
        }
    }
    if let Some(groups) = KOREAN.surnames.get(&word) {
        out.extend(groups.iter().map(|g| format!("\u{1}ko-surname:{g}")));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn shares(a: &str, b: &str) -> bool {
        let (x, y) = (readings(a), readings(b));
        x.iter().any(|r| y.contains(r))
    }

    #[test]
    fn spellings_of_one_syllable_read_alike() {
        for (a, b) in [
            ("jong", "jeong"),
            ("jong", "chong"),
            ("jong", "chung"),
            ("un", "eun"),
            ("sung", "song"),
            ("sung", "seong"),
            ("chol", "cheol"),
            ("chol", "chul"),
            ("ryong", "yong"),
            ("taek", "thaek"),
            ("hyon", "hyun"),
            ("lee", "ri"),
            ("park", "pak"),
            ("choi", "choe"),
            ("gim", "kim"),
            ("jongun", "jeongeun"),
        ] {
            assert!(shares(a, b), "{a} / {b}");
        }
    }

    #[test]
    fn different_syllables_dont() {
        for (a, b) in [("un", "il"), ("jong", "kim"), ("chol", "chil"), ("ho", "hae"), ("nam", "min")] {
            assert!(!shares(a, b), "{a} / {b}");
        }
    }

    #[test]
    fn only_korean_names_are_read_as_korean() {
        let words = |s: &str| s.split(' ').map(str::to_string).collect::<Vec<_>>();
        for name in
            ["kim jong un", "ri yong ho", "yi yong ho", "park pong ju", "choi ryong hae", "jang song thaek", "kim jong", "yong ho lee"]
        {
            assert!(is_korean_name(&words(name)), "{name}");
        }
        // A word no romanisation writes (john, smith), no Korean surname at either end, or one word.
        for name in ["john kim", "kim smith", "carl jung", "maria jung", "hong", "anna maria", "abu bakr"] {
            assert!(!is_korean_name(&words(name)), "{name}");
        }
    }
}
