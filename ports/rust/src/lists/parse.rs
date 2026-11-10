//! The shape every source is reduced to, the record builder, and the streaming XML reader (Parsers/Xml.cs in the
//! reference; ports/go/lists/parse.go).

use std::collections::{HashMap, HashSet};
use std::io::BufRead;

use chrono::NaiveDate;
use quick_xml::events::Event;
use quick_xml::Reader;

use crate::matching::text::{is_letter, upper_invariant};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubjectType {
    Individual = 0,
    Entity = 1,
    Unknown = 2,
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
