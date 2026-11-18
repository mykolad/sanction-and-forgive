//! Identifiers and dates of birth (#68; owner's decisions, 2026-11-18; the Rust port first). An identifier entered
//! that equals one a list gives for a party makes that party a Match, whether or not the name matches; one that
//! differs never lowers a result, it's only said. A date of birth is supporting detail: same or different, never a
//! Match on its own. Deterministic, and nothing entered is kept.

/// An identifier as compared: its letters and digits, upper case ("AB 123-456" and "ab123456" are the same).
pub fn identifier_key(value: &str) -> String {
    value.chars().filter(|c| c.is_alphanumeric()).flat_map(char::to_uppercase).collect()
}

/// Whether an entered identifier can be compared: at least five letters or digits, four of them digits. Shorter ones
/// would equal unrelated numbers.
pub fn comparable(key: &str) -> bool {
    key.chars().count() >= 5 && key.chars().filter(char::is_ascii_digit).count() >= 4
}

/// The keys a listed identifier is found by: the whole value's, and each alternative within it, without the words
/// that label it: the parts between commas, semicolons, slashes, brackets and colons, each without its leading and
/// trailing words that have no digit ("OGRN 1027700132195" is found by "1027700132195" too, "7704734000/770301001" by
/// each number). Spaces within a number are its formatting, never a separator: "4510 123456" is one passport number,
/// never "123456". Each key comparable.
pub fn listed_keys(value: &str) -> Vec<String> {
    let mut keys: Vec<String> = Vec::new();
    let mut add = |key: String| {
        if comparable(&key) && !keys.contains(&key) {
            keys.push(key);
        }
    };
    add(identifier_key(value));
    for part in value.split([',', ';', '/', '(', ')', ':']) {
        let words: Vec<&str> = part.split_whitespace().collect();
        let has_digit = |w: &&str| w.chars().any(|c| c.is_ascii_digit());
        let (Some(first), Some(last)) = (words.iter().position(has_digit), words.iter().rposition(has_digit)) else { continue };
        add(identifier_key(&words[first..=last].join(" ")));
    }
    keys
}

/// How an entered date of birth compares with a listed one: the same (the listed one, a day, month, year or range of
/// years, includes the entered day, or the entered year includes it), or different.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BirthDateComparison {
    Same,
    Different,
}

/// An entered date of birth: a day "YYYY-MM-DD", a month "YYYY-MM" or a year "YYYY". None when it isn't one.
pub fn entered_birth_date(value: &str) -> Option<String> {
    let value = value.trim();
    let ok = match value.len() {
        10 => chrono::NaiveDate::parse_from_str(value, "%Y-%m-%d").is_ok(),
        7 => chrono::NaiveDate::parse_from_str(&format!("{value}-01"), "%Y-%m-%d").is_ok(),
        4 => value.chars().all(|c| c.is_ascii_digit()),
        _ => false,
    };
    ok.then(|| value.to_string())
}

/// The years a date covers, as an inclusive range: "1963" and "1963-04-02" are 1963, "1955..1957" is 1955 to 1957.
fn years(date: &str) -> Option<(i32, i32)> {
    if let Some((from, to)) = date.split_once("..") {
        return Some((from.parse().ok()?, to.parse().ok()?));
    }
    let year: i32 = date.get(..4)?.parse().ok()?;
    Some((year, year))
}

/// Compares an entered date of birth with one listed date: the same when either contains the other (a year contains
/// its days, a month its days), different otherwise.
pub fn compare_birth_date(entered: &str, listed: &str) -> Option<BirthDateComparison> {
    let (Some(e), Some(l)) = (years(entered), years(listed)) else { return None };
    if e.1 < l.0 || l.1 < e.0 {
        return Some(BirthDateComparison::Different);
    }
    // Overlapping years: the same unless both are more precise than a year and disagree there.
    let precise = |d: &str| (!d.contains("..") && d.len() > 4).then(|| d.to_string());
    match (precise(entered), precise(listed)) {
        (Some(a), Some(b)) => {
            let n = a.len().min(b.len());
            Some(if a[..n] == b[..n] { BirthDateComparison::Same } else { BirthDateComparison::Different })
        }
        _ => Some(BirthDateComparison::Same),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn identifiers_compare_on_their_letters_and_digits() {
        assert_eq!(identifier_key("ab 123-456.7"), "AB1234567");
        assert!(comparable("AB12345") && comparable("7704734000") && !comparable("AB123") && !comparable("ABCDEF1"));
        assert_eq!(listed_keys("OGRN 1027700132195"), ["OGRN1027700132195", "1027700132195"]);
        assert_eq!(listed_keys("7704734000/770301001"), ["7704734000770301001", "7704734000", "770301001"]);
        assert_eq!(listed_keys("CHE-123.456.789"), ["CHE123456789"]);
        // A number's own spaces are formatting: its last group alone is no key.
        assert_eq!(listed_keys("4510 123456"), ["4510123456"]);
        assert_eq!(listed_keys("190950894 (Belarus)"), ["190950894BELARUS", "190950894"]);
        assert!(listed_keys("123").is_empty());
    }

    #[test]
    fn dates_of_birth_are_the_same_when_one_contains_the_other() {
        use BirthDateComparison::*;
        for (entered, listed, expected) in [
            ("1960-03-12", "1960-03-12", Same),
            ("1960-03-12", "1960", Same),
            ("1960-03-12", "1960-03", Same),
            ("1960-03-12", "1959..1961", Same),
            ("1960", "1960-03-12", Same),
            ("1960-03-12", "1960-04-12", Different),
            ("1960-03-12", "1961", Different),
            ("1960-03-12", "1955..1957", Different),
        ] {
            assert_eq!(compare_birth_date(entered, listed), Some(expected), "{entered} / {listed}");
        }
        assert_eq!(entered_birth_date(" 1960-03-12 ").as_deref(), Some("1960-03-12"));
        assert_eq!(entered_birth_date("1960-13").as_deref(), None);
        assert_eq!(entered_birth_date("12/03/1960"), None);
    }
}
