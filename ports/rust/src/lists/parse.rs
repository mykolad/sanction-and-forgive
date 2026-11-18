//! The shape every source is reduced to, the record builder, and the streaming XML reader (Parsers/Xml.cs in the
//! reference; ports/go/lists/parse.go).

use std::collections::{HashMap, HashSet};
use std::io::BufRead;

use chrono::NaiveDate;
use quick_xml::events::Event;
use quick_xml::Reader;

use crate::matching::text::{is_letter, upper_invariant};

/// A listed party's kind, as stored (Subjects.Type). Vessel (#69) is written by the Rust port only; the reference and
/// the Go port read it as Unknown.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubjectType {
    Individual = 0,
    Entity = 1,
    Unknown = 2,
    Vessel = 3,
}

/// The kind every parser gives a vessel's IMO number (#69), its value the seven digits alone.
pub const IMO_NUMBER: &str = "IMO number";

/// A ship's IMO number as the lists write it ("IMO 9187629", "IMO9187629", "IMO number: 9187629", "9187629"): its
/// seven digits, or None when the text isn't one. Only a value that is an IMO number and nothing else is read.
pub fn imo_number(text: &str) -> Option<String> {
    let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect::<String>().to_ascii_uppercase();
    let rest = compact.strip_prefix("IMO").map(|r| r.trim_start_matches("NUMBER").trim_start_matches("NO.").trim_start_matches(['.', ':']));
    let digits = rest.unwrap_or(&compact);
    (digits.len() == 7 && digits.bytes().all(|b| b.is_ascii_digit())).then(|| digits.to_string())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NameScript {
    Latin = 0,
    Cyrillic = 1,
    Other = 2,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ParsedName {
    pub full_name: String,
    pub is_weak: bool,
    pub script: NameScript,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ParsedAddress {
    pub raw: String,
    pub country_code: Option<String>,
}

/// An identifier a list gives for a party (#68; the Rust port first): its kind as the source names it ("Passport",
/// "Tax ID No."), its value as written, and the issuing country as the source gives it (a name or a code), if any.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedIdentifier {
    pub kind: String,
    pub value: String,
    pub country: Option<String>,
}

/// A listed party in the shape every source is reduced to.
#[derive(Debug, Clone)]
pub struct ParsedSubject {
    pub external_id: String,
    pub kind: SubjectType,
    pub primary_name: String,
    pub names: Vec<ParsedName>,
    pub addresses: Vec<ParsedAddress>,
    pub programmes: Option<String>,
    pub listed_on: Option<NaiveDate>,
    pub remarks: Option<String>,
    pub source_url: Option<String>,
    /// Identifiers (#68), in the order the source gives them, repeats dropped.
    pub identifiers: Vec<ParsedIdentifier>,
    /// Dates of birth (#68), each as `birth_date` reads one: "1948-12-10", "1960-03", "1963" or "1955..1957".
    pub birth_dates: Vec<String>,
}

/// A date of birth in one form for every source: a full date "YYYY-MM-DD", a month "YYYY-MM", a year "YYYY", or a
/// range of years "YYYY..YYYY"; from the first and last days it may fall on. None when they don't make a date.
pub fn birth_date(first: NaiveDate, last: NaiveDate) -> Option<String> {
    use chrono::Datelike;
    if last < first {
        return None;
    }
    if first == last {
        return Some(first.format("%Y-%m-%d").to_string());
    }
    let month_end = |d: NaiveDate| d.checked_add_months(chrono::Months::new(1)).and_then(|n| n.pred_opt());
    if first.year() == last.year() && first.month() == last.month() && first.day() == 1 && Some(last) == month_end(first) {
        return Some(first.format("%Y-%m").to_string());
    }
    if first.month() == 1 && first.day() == 1 && last.month() == 12 && last.day() == 31 {
        return Some(if first.year() == last.year() { first.year().to_string() } else { format!("{}..{}", first.year(), last.year()) });
    }
    Some(format!("{}..{}", first.year(), last.year()))
}

/// A date of birth as the lists write it: "1948-12-10", "1948", "12/03/1960" or "dd/mm/1963" (day and month unknown),
/// "mm/1960"... None when it isn't one.
pub fn birth_date_text(value: &str) -> Option<String> {
    let value = clean(Some(value))?;
    if let Ok(d) = NaiveDate::parse_from_str(&value, "%Y-%m-%d") {
        return birth_date(d, d);
    }
    if let Ok(d) = NaiveDate::parse_from_str(&value, "%d/%m/%Y") {
        return birth_date(d, d);
    }
    let year = |y: &str| y.parse::<i32>().ok().filter(|y| (1800..=2100).contains(y));
    let parts: Vec<&str> = value.split('/').collect();
    match parts.as_slice() {
        [y] => year(y).map(|y| y.to_string()),
        [m, y] | [_, m, y] => {
            let y = year(y)?;
            match m.parse::<u32>().ok().filter(|m| (1..=12).contains(m)) {
                Some(m) => Some(format!("{y}-{m:02}")),
                None => Some(y.to_string()),
            }
        }
        _ => None,
    }
}

/// The identifiers in a free-text field ("(УНН/ИНН): 190950894 (Belarus), 7704734000/770301001 (Russia)", "HRB 12345",
/// "CHE-123.456.789"): the parts between commas, semicolons, brackets and line breaks, each as written after any label
/// ending in a colon, with at least five digits (so a year alone isn't one). Two long numbers joined by a slash (the
/// Russian INN/KPP) are two. Matching (part 2) compares the numbers within a value too, so a label kept with its
/// number ("OGRN 1027700132195") still matches the number alone.
pub fn identifier_values(text: &str) -> Vec<String> {
    let digits = |s: &str| s.chars().filter(char::is_ascii_digit).count();
    let mut out: Vec<String> = Vec::new();
    let mut push = |value: &str| {
        if let Some(value) = clean(Some(value)) {
            if !out.contains(&value) {
                out.push(value);
            }
        }
    };
    for segment in text.split([',', ';', '(', ')', '[', ']', '\n', '\r']) {
        let segment = segment.rsplit_once(':').map(|(_, after)| after).unwrap_or(segment).trim();
        if digits(segment) < 5 {
            continue;
        }
        let pieces: Vec<&str> = segment.split('/').map(str::trim).collect();
        if pieces.len() > 1 && pieces.iter().all(|p| p.len() >= 6 && p.chars().all(|c| c.is_ascii_digit())) {
            pieces.iter().for_each(|p| push(p));
        } else {
            push(segment);
        }
    }
    out
}

/// The numbers in prose as some lists write their documents ("Yemen number 541939", "Afghan passport number SE 011252",
/// "série 1808 n° 233282", "アフガニスタン旅券 P04581926（…）"), read after NFKC (full-width letters and digits are plain
/// ones): in each part between commas, semicolons and brackets, each word of Latin or Cyrillic letters and digits with
/// at least five of them, four digits. One word is never cut into pieces (АВ1234567 stays whole), and words a list
/// writes as one number are joined: a series of one to three capitals before a number ("SE 011252"), a group of up to
/// four digits before the rest ("1808 233282", the words "n", "no", "nr" between them aside), and a suffix of one to
/// four capitals after it ("1234567 KZTV"). Dates are left out, never joined to a series ("DOB 1966-06-21").
pub fn free_text_identifiers(text: &str) -> Vec<String> {
    use unicode_normalization::UnicodeNormalization;
    static DATE: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let date = DATE.get_or_init(|| regex::Regex::new(r"^(\d{1,2}[./-]\d{1,2}[./-]\d{2,4}|\d{4}[./-]\d{1,2}[./-]\d{1,2})$").unwrap());
    // Letters of the scripts identifiers are written in (Latin, Cyrillic); any other script separates words.
    let kept = |c: char| c.is_ascii_alphanumeric() || (c.is_alphabetic() && matches!(c as u32, 0x00C0..=0x024F | 0x0400..=0x04FF));
    // A slash with a space on either side separates two identifiers (SE 011252 / SE 011697); one within a word
    // doesn't (1990/345751).
    static ALTERNATIVE: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let alternative = ALTERNATIVE.get_or_init(|| regex::Regex::new(r"\s+/\s*|\s*/\s+").unwrap());
    let text: String = text.nfkc().collect();
    let mut out: Vec<String> = Vec::new();
    let segments = text.split([',', ';', '(', ')', '[', ']', '\n', '\r']).flat_map(|segment| alternative.split(segment));
    for segment in segments {
        let cleaned: String = segment.chars().map(|c| if kept(c) || "-/.".contains(c) { c } else { ' ' }).collect();
        let words: Vec<&str> = cleaned
            .split_whitespace()
            .map(|w| w.trim_matches(['-', '/', '.']))
            .filter(|w| !w.is_empty() && !["n", "no", "nr"].contains(&w.to_lowercase().as_str()))
            .collect();
        let digits = |w: &str| w.chars().filter(char::is_ascii_digit).count();
        let all_digits = |w: &str| !w.is_empty() && w.chars().all(|c| c.is_ascii_digit());
        let capitals = |w: &str, most: usize| w.chars().count() <= most && w.chars().all(|c| c.is_alphabetic() && c.is_uppercase());
        let mut i = 0;
        while i < words.len() {
            let word = words[i];
            if date.is_match(word) {
                i += 1;
                continue;
            }
            let next = words.get(i + 1).copied().filter(|n| !date.is_match(n));
            // A series before its number, or a short group of digits before the rest of the number.
            let (mut value, mut used) = match next {
                Some(n) if capitals(word, 3) && n.starts_with(|c: char| c.is_ascii_digit()) => (format!("{word} {n}"), 2),
                Some(n) if all_digits(word) && word.len() <= 4 && all_digits(n) && n.len() >= 5 => (format!("{word} {n}"), 2),
                _ => (word.to_string(), 1),
            };
            // A suffix of capitals after a number stays with it, unless it's the series of a number that follows.
            let starts_number = |at: usize| words.get(at).is_some_and(|w| w.starts_with(|c: char| c.is_ascii_digit()));
            if value.ends_with(|c: char| c.is_ascii_digit()) && !starts_number(i + used + 1) {
                if let Some(suffix) = words.get(i + used).filter(|w| capitals(w, 4)) {
                    value = format!("{value} {suffix}");
                    used += 1;
                }
            }
            i += used;
            let alphanumeric = value.chars().filter(|c| c.is_alphanumeric()).count();
            if alphanumeric >= 5 && digits(&value) >= 4 && !date.is_match(&value) && !out.contains(&value) {
                out.push(value);
            }
        }
    }
    out
}

/// A date of birth from its parts as written (a year, and a month and a day when known): "1960", "1960-03" or
/// "1960-03-12". None without a year from 1800 to 2100, or when the parts don't make a date.
pub fn birth_date_parts(year: Option<&str>, month: Option<&str>, day: Option<&str>) -> Option<String> {
    let number = |v: Option<&str>| v.map(str::trim).filter(|v| !v.is_empty()).map(|v| v.parse::<u32>().ok());
    let year = number(year)??;
    if !(1800..=2100).contains(&year) {
        return None;
    }
    let (month, day) = (number(month).flatten(), number(day).flatten());
    // As precise as the parts allow: a day that isn't one leaves the month, a month that isn't one the year.
    if let (Some(m), Some(d)) = (month, day) {
        if let Some(date) = NaiveDate::from_ymd_opt(year as i32, m, d) {
            return Some(date.format("%Y-%m-%d").to_string());
        }
    }
    match month {
        Some(m) if (1..=12).contains(&m) => Some(format!("{year}-{m:02}")),
        _ => Some(year.to_string()),
    }
}

/// The reference's ListFormatException: the file isn't the list it should be.
#[derive(Debug)]
pub struct FormatError(pub String);

impl std::fmt::Display for FormatError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for FormatError {}

pub type ParseError = Box<dyn std::error::Error + Send + Sync>;

pub fn format_error(message: impl Into<String>) -> ParseError {
    Box::new(FormatError(message.into()))
}

/// Cyrillic if a name has any Cyrillic letter, Latin if all its letters are Latin, Other otherwise.
pub fn detect_script(value: &str) -> NameScript {
    let (mut latin, mut other) = (false, false);
    for c in value.chars().filter(|&c| is_letter(c)) {
        if ('Ѐ'..='ԯ').contains(&c) {
            return NameScript::Cyrillic;
        }
        if c <= 'ɏ' || ('Ḁ'..='ỿ').contains(&c) {
            latin = true;
        } else {
            other = true;
        }
    }
    if other || !latin {
        NameScript::Other
    } else {
        NameScript::Latin
    }
}

/// Trims and collapses whitespace (.NET's char.IsWhiteSpace); None when nothing is left.
pub fn clean(value: Option<&str>) -> Option<String> {
    let trimmed = value?.trim_matches(char::is_whitespace);
    if trimmed.is_empty() {
        return None;
    }
    let mut out = String::with_capacity(trimmed.len());
    let mut space = false;
    for c in trimmed.chars() {
        if c.is_whitespace() {
            space = true;
            continue;
        }
        if space {
            out.push(' ');
            space = false;
        }
        out.push(c);
    }
    Some(out)
}

/// The non-empty parts, cleaned and joined; None if there are none.
pub fn join(separator: &str, parts: &[Option<String>]) -> Option<String> {
    let cleaned: Vec<String> = parts.iter().filter_map(|p| clean(p.as_deref())).collect();
    (!cleaned.is_empty()).then(|| cleaned.join(separator))
}

/// A date in one exact format (chrono's), or None.
pub fn parse_date(value: Option<&str>, format: &str) -> Option<NaiveDate> {
    NaiveDate::parse_from_str(&clean(value)?, format).ok()
}

/// One subject's names and addresses, empty and repeated ones dropped. The first name added is the primary name
/// unless one is added as primary.
pub struct Builder {
    external_id: String,
    kind: SubjectType,
    names: Vec<ParsedName>,
    name_indexes: HashMap<String, usize>,
    addresses: Vec<ParsedAddress>,
    seen_addresses: HashSet<String>,
    primary_name: Option<String>,
    primary_is_flagged: bool,
    pub programmes: Option<String>,
    pub listed_on: Option<NaiveDate>,
    pub remarks: Option<String>,
    pub source_url: Option<String>,
    identifiers: Vec<ParsedIdentifier>,
    birth_dates: Vec<String>,
}

impl Builder {
    pub fn new(external_id: &str, kind: SubjectType) -> Self {
        Builder {
            external_id: external_id.to_string(),
            kind,
            names: Vec::new(),
            name_indexes: HashMap::new(),
            addresses: Vec::new(),
            seen_addresses: HashSet::new(),
            primary_name: None,
            primary_is_flagged: false,
            programmes: None,
            listed_on: None,
            remarks: None,
            source_url: None,
            identifiers: Vec::new(),
            birth_dates: Vec::new(),
        }
    }

    /// An identifier (#68): its kind as the source names it, the value as written, the issuing country if given.
    /// Empty values and repeats are dropped.
    pub fn add_identifier(&mut self, kind: Option<&str>, value: Option<&str>, country: Option<&str>) {
        let Some(value) = clean(value) else { return };
        let identifier = ParsedIdentifier { kind: clean(kind).unwrap_or_else(|| "Identifier".to_string()), value, country: clean(country) };
        if !self.identifiers.contains(&identifier) {
            self.identifiers.push(identifier);
        }
    }

    /// A vessel's IMO number (#69) as the list writes it: its seven digits under IMO_NUMBER when it is one, else as
    /// written under the list's own kind (so nothing a list gives is lost).
    pub fn add_imo_number(&mut self, value: Option<&str>, kind_otherwise: &str) {
        match value.and_then(imo_number) {
            Some(digits) => self.add_identifier(Some(IMO_NUMBER), Some(&digits), None),
            None => self.add_identifier(Some(kind_otherwise), value, None),
        }
    }

    /// A date of birth already in birth_date's form; repeats are dropped.
    pub fn add_birth_date(&mut self, date: Option<String>) {
        if let Some(date) = date {
            if !self.birth_dates.contains(&date) {
                self.birth_dates.push(date);
            }
        }
    }

    pub fn has_names(&self) -> bool {
        !self.names.is_empty()
    }

    pub fn add_name(&mut self, full_name: Option<&str>, is_weak: bool, is_primary: bool) {
        let Some(cleaned) = clean(full_name) else { return };
        if is_primary && !self.primary_is_flagged {
            self.primary_name = Some(cleaned.clone());
            self.primary_is_flagged = true;
        } else if self.primary_name.is_none() {
            self.primary_name = Some(cleaned.clone());
        }
        if let Some(&index) = self.name_indexes.get(&cleaned) {
            if self.names[index].is_weak && !is_weak {
                self.names[index].is_weak = false;
            }
            return;
        }
        self.name_indexes.insert(cleaned.clone(), self.names.len());
        let script = detect_script(&cleaned);
        self.names.push(ParsedName { full_name: cleaned, is_weak, script });
    }

    pub fn add_address(&mut self, raw: Option<&str>, country_code: Option<&str>) {
        let code = clean(country_code).map(|c| c.chars().map(upper_invariant).collect::<String>());
        let Some(cleaned) = clean(raw).or_else(|| code.clone()) else { return };
        let key = format!("{cleaned}|{}", code.as_deref().unwrap_or_default());
        if !self.seen_addresses.insert(key) {
            return;
        }
        let kept = code.filter(|c| c.len() == 2 && c.bytes().all(|b| b.is_ascii_uppercase()));
        self.addresses.push(ParsedAddress { raw: cleaned, country_code: kept });
    }

    /// The subject, or None if it has no usable name.
    pub fn build(self) -> Option<ParsedSubject> {
        Some(ParsedSubject {
            external_id: self.external_id,
            kind: self.kind,
            primary_name: self.primary_name?,
            names: self.names,
            addresses: self.addresses,
            programmes: self.programmes,
            listed_on: self.listed_on,
            remarks: self.remarks,
            source_url: self.source_url,
            identifiers: self.identifiers,
            birth_dates: self.birth_dates,
        })
    }
}

/// An XML element: its local name, attributes, children, and the text of all its descendants (.NET's XElement.Value),
/// whitespace-only text nodes left out as the reference's reader ignores them.
#[derive(Debug, Default)]
pub struct Element {
    pub name: String,
    attrs: Vec<(String, String)>,
    pub children: Vec<Element>,
    text: String,
}

impl Element {
    /// The concatenated text of the element and its descendants.
    pub fn value(&self) -> &str {
        &self.text
    }

    pub fn el(&self, name: &str) -> Option<&Element> {
        self.children.iter().find(|c| c.name == name)
    }

    pub fn els<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a Element> + 'a {
        self.children.iter().filter(move |c| c.name == name)
    }

    /// The cleaned value of the first child element named name.
    pub fn val(&self, name: &str) -> Option<String> {
        clean(Some(self.el(name)?.value()))
    }

    /// The cleaned value of an attribute (by local name).
    pub fn attr(&self, name: &str) -> Option<String> {
        clean(self.attrs.iter().find(|(k, _)| k == name).map(|(_, v)| v.as_str()))
    }
}

/// Helpers for optional elements: a missing element has no children, values or attributes.
pub trait OptionalElement<'a> {
    fn el(self, name: &str) -> Option<&'a Element>;
    fn els(self, name: &'a str) -> Vec<&'a Element>;
    fn val(self, name: &str) -> Option<String>;
    fn attr(self, name: &str) -> Option<String>;
}

impl<'a> OptionalElement<'a> for Option<&'a Element> {
    fn el(self, name: &str) -> Option<&'a Element> {
        self?.el(name)
    }
    fn els(self, name: &'a str) -> Vec<&'a Element> {
        self.map(|e| e.els(name).collect()).unwrap_or_default()
    }
    fn val(self, name: &str) -> Option<String> {
        self?.val(name)
    }
    fn attr(self, name: &str) -> Option<String> {
        self?.attr(name)
    }
}

pub fn eq(value: &Option<String>, expected: &str) -> bool {
    value.as_deref() == Some(expected)
}

fn is_xml_space(c: char) -> bool {
    matches!(c, ' ' | '\t' | '\n' | '\r')
}

/// Streams the elements at depth (the root is depth 0), refusing a document whose root isn't expected_root or that
/// declares a DTD.
pub fn records(
    input: impl BufRead,
    expected_root: &str,
    depth: usize,
    mut each: impl FnMut(Element) -> Result<(), ParseError>,
) -> Result<(), ParseError> {
    let mut reader = Reader::from_reader(input);
    reader.config_mut().trim_text(false);
    reader.config_mut().expand_empty_elements = true;
    let mut buffer = Vec::new();
    let mut level: isize = -1;
    let mut stack: Vec<Element> = Vec::new();
    let mut root_seen = false;
    loop {
        let event = reader.read_event_into(&mut buffer)?;
        match event {
            Event::Eof => {
                if !root_seen {
                    return Err(format_error(format!("Expected root element <{expected_root}>, found <>.")));
                }
                // A file cut off before its root element closes is broken, never a shorter list.
                if level != -1 {
                    return Err(format_error("The list ends before its root element closes: the file is incomplete."));
                }
                return Ok(());
            }
            Event::DocType(_) => return Err(format_error("A DTD isn't allowed in the list.")),
            Event::Start(start) => {
                level += 1;
                let name = String::from_utf8_lossy(start.local_name().as_ref()).into_owned();
                if level == 0 {
                    if root_seen {
                        return Err(format_error("The list has more than one root element."));
                    }
                    root_seen = true;
                    if name != expected_root {
                        return Err(format_error(format!("Expected root element <{expected_root}>, found <{name}>.")));
                    }
                    continue;
                }
                if level as usize >= depth {
                    let mut element = Element { name, ..Default::default() };
                    for attribute in start.attributes() {
                        let attribute = attribute?;
                        let key = attribute.key;
                        if key.as_ref() == b"xmlns" || key.prefix().is_some_and(|p| p.as_ref() == b"xmlns") {
                            continue;
                        }
                        let value = attribute.decode_and_unescape_value(reader.decoder())?.into_owned();
                        element.attrs.push((String::from_utf8_lossy(key.local_name().as_ref()).into_owned(), value));
                    }
                    stack.push(element);
                }
            }
            Event::End(_) => {
                if level as usize >= depth && level >= 1 {
                    if let Some(element) = stack.pop() {
                        match stack.last_mut() {
                            Some(parent) => parent.children.push(element),
                            None => each(element)?,
                        }
                    }
                }
                level -= 1;
            }
            Event::Text(text) if !stack.is_empty() => {
                let value = text.unescape()?;
                if !value.trim_matches(is_xml_space).is_empty() {
                    for e in stack.iter_mut() {
                        e.text.push_str(&value);
                    }
                }
            }
            Event::CData(data) if !stack.is_empty() => {
                let value = reader.decoder().decode(&data)?.into_owned();
                if !value.trim_matches(is_xml_space).is_empty() {
                    for e in stack.iter_mut() {
                        e.text.push_str(&value);
                    }
                }
            }
            _ => {}
        }
        buffer.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::{birth_date, birth_date_parts, birth_date_text, free_text_identifiers, identifier_values};

    /// Numbers in prose (#68): whole words, a series or a short group joined to its number, dates left out.
    #[test]
    fn numbers_in_prose_are_read_whole() {
        assert_eq!(free_text_identifiers("Yemen number 541939"), ["541939"]);
        assert_eq!(free_text_identifiers("Afghan passport number SE 011252"), ["SE 011252"]);
        assert_eq!(free_text_identifiers(" série 1808 n° 233282"), ["1808 233282"]);
        assert_eq!(free_text_identifiers("Tunisia number G737411, issued on 24 Oct. 1990 (expired on 20.09.1997)"), ["G737411"]);
        assert_eq!(
            free_text_identifiers("アフガニスタン旅券 P04581926（2024年8月7日にアフガニスタンのカンダハールで発行）"),
            ["P04581926"]
        );
        assert_eq!(free_text_identifiers("ＳＥー０１１６９７"), ["SE 011697"]);
        assert_eq!(
            free_text_identifiers("Italian Fiscal Code: SSDSBN68B10Z352F; 1-78-09-44621-80"),
            ["SSDSBN68B10Z352F", "1-78-09-44621-80"]
        );
        assert!(free_text_identifiers("born 1966-06-21 in Kabul; 1234").is_empty());
        // Copilot's review of #127: a Cyrillic series and a suffix stay with the number; a date is never joined.
        assert_eq!(free_text_identifiers("passport АВ1234567"), ["АВ1234567"]);
        assert_eq!(free_text_identifiers("1234567 KZTV"), ["1234567 KZTV"]);
        assert_eq!(free_text_identifiers("SE 011252 / SE 011697"), ["SE 011252", "SE 011697"]);
        assert_eq!(free_text_identifiers("SE 011252 SE 011697"), ["SE 011252", "SE 011697"]);
        assert_eq!(free_text_identifiers("1990/345751"), ["1990/345751"]);
        assert_eq!(free_text_identifiers("Passport SE 011252; DOB 1966-06-21"), ["SE 011252"]);
    }

    #[test]
    fn a_date_of_birth_is_as_precise_as_its_parts() {
        assert_eq!(birth_date_parts(Some("1980"), Some("3"), Some("23")).as_deref(), Some("1980-03-23"));
        assert_eq!(birth_date_parts(Some("1964"), Some("10"), None).as_deref(), Some("1964-10"));
        assert_eq!(birth_date_parts(Some("1964"), Some("2"), Some("31")).as_deref(), Some("1964-02"));
        assert_eq!(birth_date_parts(Some("1964"), Some(""), Some("")).as_deref(), Some("1964"));
        assert_eq!(birth_date_parts(None, Some("3"), Some("23")), None);
        assert_eq!(birth_date_parts(Some("64"), None, None), None);
    }

    #[test]
    fn dates_of_birth_read_in_one_form() {
        let d = |s: &str| chrono::NaiveDate::parse_from_str(s, "%Y-%m-%d").unwrap();
        assert_eq!(birth_date(d("1948-12-10"), d("1948-12-10")).as_deref(), Some("1948-12-10"));
        assert_eq!(birth_date(d("1960-02-01"), d("1960-02-29")).as_deref(), Some("1960-02"));
        assert_eq!(birth_date(d("1963-01-01"), d("1963-12-31")).as_deref(), Some("1963"));
        assert_eq!(birth_date(d("1955-01-01"), d("1957-12-31")).as_deref(), Some("1955..1957"));
        assert_eq!(birth_date(d("1957-01-01"), d("1955-12-31")), None);
        for (text, expected) in [
            ("1937-04-28", Some("1937-04-28")),
            ("12/03/1960", Some("1960-03-12")),
            ("dd/mm/1963", Some("1963")),
            ("05/1961", Some("1961-05")),
            ("mm/1961", Some("1961")),
            ("1960", Some("1960")),
            ("unknown", None),
            ("", None),
        ] {
            assert_eq!(birth_date_text(text).as_deref(), expected, "{text}");
        }
    }

    #[test]
    fn identifiers_are_found_in_a_free_text_field_whole() {
        assert_eq!(
            identifier_values("(УНН/ИНН): 190950894 (Belarus), 7704734000/770301001 (Russia)"),
            ["190950894", "7704734000", "770301001"]
        );
        assert_eq!(identifier_values("OGRN 1027700132195; reg. no.: HRB-12345"), ["OGRN 1027700132195", "HRB-12345"]);
        assert_eq!(identifier_values("HRB 12345"), ["HRB 12345"]);
        assert_eq!(identifier_values("CHE-123.456.789"), ["CHE-123.456.789"]);
        assert_eq!(identifier_values("AB 12345678"), ["AB 12345678"]);
        assert!(identifier_values("Company no. 123 (2010)").is_empty());
    }
    use super::*;

    /// A feed cut off mid-file is broken, never a shorter list (Copilot's point on #37).
    #[test]
    fn a_truncated_document_is_an_error() {
        let mut seen = 0;
        let whole = records("<export><a>1</a><a>2</a></export>".as_bytes(), "export", 1, |_| {
            seen += 1;
            Ok(())
        });
        assert!(whole.is_ok() && seen == 2);
        assert!(records("<export><a>1</a><a>2</a>".as_bytes(), "export", 1, |_| Ok(())).is_err());
        assert!(records("<export><a>1</a><a>2".as_bytes(), "export", 1, |_| Ok(())).is_err());
    }
}
