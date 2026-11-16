//! Spreadsheets in the reference: rows of an OpenDocument (.ods) spreadsheet, read from its zip and XML. A row is its
//! cells' text, trailing empty cells dropped; empty rows are skipped. A file that isn't the format, or whose part
//! unpacks to more than MAX_PART_BYTES, is a format error. And the Dutch terrorism list, published as one.

use std::collections::HashSet;
use std::io::{BufRead, Cursor, Read};

use quick_xml::events::Event;
use quick_xml::name::{Namespace, ResolveResult};
use quick_xml::NsReader;

use super::parse::{clean, format_error, join, parse_date, Builder, ParseError, ParsedSubject, SubjectType};

const MAX_PART_BYTES: u64 = 256 << 20;
const MAX_COLUMNS: usize = 256;
const ODS_TABLE: &[u8] = b"urn:oasis:names:tc:opendocument:xmlns:table:1.0";
const ODS_TEXT: &[u8] = b"urn:oasis:names:tc:opendocument:xmlns:text:1.0";

/// One part of a zip, refusing one that unpacks past the limit (whatever the zip's directory claims).
fn read_part(data: Vec<u8>, name: &str) -> Result<Vec<u8>, ParseError> {
    let mut archive =
        zip::ZipArchive::new(Cursor::new(data)).map_err(|e| format_error(format!("The file isn't a spreadsheet (zip): {e}")))?;
    let file = archive.by_name(name).map_err(|_| format_error(format!("The spreadsheet has no {name}.")))?;
    if file.size() > MAX_PART_BYTES {
        return Err(format_error(format!("The spreadsheet's {name} unpacks to {} bytes, more than {MAX_PART_BYTES}.", file.size())));
    }
    let mut part = Vec::new();
    file.take(MAX_PART_BYTES + 1)
        .read_to_end(&mut part)
        .map_err(|e| format_error(format!("The spreadsheet's {name} can't be read: {e}")))?;
    if part.len() as u64 > MAX_PART_BYTES {
        return Err(format_error(format!("The spreadsheet's {name} unpacks to more than {MAX_PART_BYTES} bytes.")));
    }
    Ok(part)
}

fn is(resolved: &ResolveResult, namespace: &[u8]) -> bool {
    matches!(resolved, ResolveResult::Bound(Namespace(ns)) if *ns == namespace)
}

fn count(reader: &NsReader<&[u8]>, element: &quick_xml::events::BytesStart, namespace: &[u8], local: &[u8]) -> usize {
    element
        .attributes()
        .flatten()
        .find(|a| {
            let (resolved, name) = reader.resolve_attribute(a.key);
            is(&resolved, namespace) && name.as_ref() == local
        })
        .and_then(|a| std::str::from_utf8(&a.value).ok().and_then(|v| v.parse::<usize>().ok()))
        .filter(|n| *n > 0)
        .unwrap_or(1)
}

fn xml_error(e: impl std::fmt::Display) -> ParseError {
    format_error(format!("The spreadsheet's content.xml isn't valid XML: {e}"))
}

/// The rows of an OpenDocument spreadsheet's first table.
pub fn ods_rows(input: &mut dyn BufRead) -> Result<Vec<Vec<String>>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let content = read_part(data, "content.xml")?;
    let mut reader = NsReader::from_reader(content.as_slice());
    reader.config_mut().trim_text(false);
    let mut rows = Vec::new();
    let mut in_table = false;
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => break,
            Event::Start(e) | Event::Empty(e) if is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table" => {
                if in_table {
                    break;
                }
                in_table = true;
            }
            Event::End(e) if in_table && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table" => break,
            Event::Start(e) if in_table && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table-row" => {
                let repeat = count(&reader, &e, ODS_TABLE, b"number-rows-repeated");
                let row = ods_row(&mut reader)?;
                if !row.is_empty() {
                    // A filled row repeated is rare; an empty one repeated to the sheet's end is common, and skipped.
                    for _ in 0..repeat.min(1000) {
                        rows.push(row.clone());
                    }
                }
            }
            _ => {}
        }
    }
    if !in_table {
        return Err(format_error("The spreadsheet has no table."));
    }
    Ok(rows)
}

fn ods_row(reader: &mut NsReader<&[u8]>) -> Result<Vec<String>, ParseError> {
    let mut cells: Vec<String> = Vec::new();
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => return Err(format_error("The spreadsheet's content.xml ends inside a row.")),
            Event::Start(e) if is(&resolved, ODS_TABLE) && matches!(e.local_name().as_ref(), b"table-cell" | b"covered-table-cell") => {
                let repeat = count(reader, &e, ODS_TABLE, b"number-columns-repeated");
                let text = ods_cell_text(reader)?;
                for _ in 0..repeat {
                    if cells.len() >= MAX_COLUMNS {
                        break;
                    }
                    cells.push(text.clone());
                }
            }
            Event::Empty(e) if is(&resolved, ODS_TABLE) && matches!(e.local_name().as_ref(), b"table-cell" | b"covered-table-cell") => {
                let repeat = count(reader, &e, ODS_TABLE, b"number-columns-repeated");
                for _ in 0..repeat {
                    if cells.len() >= MAX_COLUMNS {
                        break;
                    }
                    cells.push(String::new());
                }
            }
            Event::End(e) if is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table-row" => {
                let last = cells.iter().rposition(|c| !c.trim().is_empty());
                cells.truncate(last.map_or(0, |i| i + 1));
                return Ok(cells);
            }
            _ => {}
        }
    }
}

/// A cell's paragraphs, one a line; text:s is spaces, text:line-break and text:tab are what they say.
fn ods_cell_text(reader: &mut NsReader<&[u8]>) -> Result<String, ParseError> {
    let (mut paragraphs, mut current, mut in_paragraph, mut depth) = (Vec::new(), String::new(), false, 0usize);
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => return Err(format_error("The spreadsheet's content.xml ends inside a cell.")),
            Event::Start(e) => {
                depth += 1;
                if is(&resolved, ODS_TEXT) && matches!(e.local_name().as_ref(), b"p" | b"h") {
                    in_paragraph = true;
                    current.clear();
                }
            }
            Event::Empty(e) if is(&resolved, ODS_TEXT) => match e.local_name().as_ref() {
                b"p" | b"h" => paragraphs.push(String::new()),
                b"s" if in_paragraph => current.push_str(&" ".repeat(count(reader, &e, ODS_TEXT, b"c").min(100))),
                b"line-break" if in_paragraph => current.push('\n'),
                b"tab" if in_paragraph => current.push('\t'),
                _ => {}
            },
            Event::End(e) => {
                if depth == 0 {
                    return Ok(paragraphs.join("\n"));
                }
                depth -= 1;
                if is(&resolved, ODS_TEXT) && matches!(e.local_name().as_ref(), b"p" | b"h") {
                    paragraphs.push(std::mem::take(&mut current));
                    in_paragraph = false;
                }
            }
            Event::Text(t) if in_paragraph => current.push_str(&t.unescape().map_err(xml_error)?),
            Event::CData(t) if in_paragraph => current.push_str(&String::from_utf8_lossy(&t)),
            _ => {}
        }
    }
}

/// The Dutch list's legal basis.
pub const NETHERLANDS_PROGRAMME: &str = "Sanctieregeling terrorisme 2007-II";

/// The Netherlands' national terrorism list (ODS): NetherlandsParser in the reference.
pub fn parse_netherlands(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let rows = ods_rows(input)?;
    let is_cell = |cell: &str, start: &str| cell.trim().to_lowercase().starts_with(start);
    let header_at = rows
        .iter()
        .position(|row| row.len() > 1 && is_cell(&row[0], "surname") && is_cell(&row[1], "first name"))
        .ok_or_else(|| format_error("No header row (Surname, First name(s), …) in the Dutch terrorism list."))?;
    let header = &rows[header_at];
    let column = |start: &str| -> Result<usize, ParseError> {
        header
            .iter()
            .position(|cell| is_cell(cell, start))
            .ok_or_else(|| format_error(format!("The Dutch terrorism list has no \"{start}\" column.")))
    };
    let (surname, given, alias, born, place, decided, notice) = (
        column("surname")?,
        column("first name")?,
        column("alias")?,
        column("date of birth")?,
        column("place of birth")?,
        column("date of ministerial decision")?,
        column("link official notification")?,
    );

    let mut out = Vec::new();
    let mut ids: HashSet<String> = HashSet::new();
    for row in &rows[header_at + 1..] {
        let cell = |index: usize| row.get(index).and_then(|v| clean(Some(v)));
        let Some(family) = cell(surname) else { continue };
        let (first, place_of_birth) = (cell(given), cell(place));
        let kind = if first.is_some() {
            SubjectType::Individual
        } else if place_of_birth.as_deref().is_some_and(|p| p.to_lowercase().starts_with("n/a")) {
            SubjectType::Entity
        } else {
            SubjectType::Unknown
        };

        // One listing per notification; a repeated one (never seen) still gets an id of its own.
        let mut id =
            cell(notice).or_else(|| join(" ", &[Some(family.clone()), first.clone(), cell(born)])).unwrap_or_else(|| family.clone());
        let mut n = 2;
        while !ids.insert(id.clone()) {
            id = format!("{} #{n}", cell(notice).unwrap_or_else(|| family.clone()));
            n += 1;
        }

        let mut b = Builder::new(&id, kind);
        b.programmes = Some(NETHERLANDS_PROGRAMME.to_string());
        let decision = cell(decided);
        b.listed_on = parse_date(decision.as_deref(), "%d-%m-%Y").or_else(|| parse_date(decision.as_deref(), "%d/%m/%Y"));
        let born_text = cell(born).map(|date| format!("{}{date}", if kind == SubjectType::Entity { "established " } else { "born " }));
        b.remarks = join("; ", &[born_text, place_of_birth]);
        let primary = if kind == SubjectType::Individual { join(" ", &[first, Some(dutch_surname(&family))]) } else { Some(family) };
        b.add_name(primary.as_deref(), false, true);
        b.add_name(cell(alias).as_deref(), false, false);
        out.extend(b.build());
    }
    Ok(out)
}

/// "Dusseldorp, van" is "van Dusseldorp": a particle written after a comma goes first.
fn dutch_surname(family: &str) -> String {
    if let Some(comma) = family.rfind(',') {
        let particle = family[comma + 1..].trim();
        if comma > 0 && comma < family.len() - 1 && !particle.contains(' ') {
            return format!("{particle} {}", family[..comma].trim());
        }
    }
    family.to_string()
}

const XLSX_MAIN: &[u8] = b"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
const XLSX_RELATIONSHIPS: &[u8] = b"http://schemas.openxmlformats.org/officeDocument/2006/relationships";
const PACKAGE_RELATIONSHIPS: &[u8] = b"http://schemas.openxmlformats.org/package/2006/relationships";

/// An attribute's value by namespace and local name (an empty namespace: no prefix).
fn attribute(reader: &NsReader<&[u8]>, element: &quick_xml::events::BytesStart, namespace: &[u8], local: &[u8]) -> Option<String> {
    element.attributes().flatten().find_map(|a| {
        let (resolved, name) = reader.resolve_attribute(a.key);
        let matches = if namespace.is_empty() { matches!(resolved, ResolveResult::Unbound) } else { is(&resolved, namespace) };
        (matches && name.as_ref() == local)
            .then(|| a.decode_and_unescape_value(reader.decoder()).map(|v| v.into_owned()).unwrap_or_default())
    })
}

/// The rows of an Office Open XML workbook's sheet named sheet_name: XlsxRows in the reference.
// A match guard would borrow the reader while the event still does: the attribute checks stay inside the arms.
#[allow(clippy::collapsible_match)]
pub fn xlsx_rows(input: &mut dyn BufRead, sheet_name: &str) -> Result<Vec<Vec<String>>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let part = |name: &str| read_part(data.clone(), name);

    let workbook = part("xl/workbook.xml")?;
    let mut reader = NsReader::from_reader(workbook.as_slice());
    let mut relationship = None;
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => break,
            Event::Start(e) | Event::Empty(e) if is(&resolved, XLSX_MAIN) && e.local_name().as_ref() == b"sheet" => {
                if attribute(&reader, &e, b"", b"name").as_deref() == Some(sheet_name) {
                    relationship = attribute(&reader, &e, XLSX_RELATIONSHIPS, b"id");
                    if relationship.is_some() {
                        break;
                    }
                }
            }
            _ => {}
        }
    }
    let relationship = relationship.ok_or_else(|| format_error(format!("The workbook has no sheet named \"{sheet_name}\".")))?;

    let relationships = part("xl/_rels/workbook.xml.rels")?;
    let mut reader = NsReader::from_reader(relationships.as_slice());
    let mut target = None;
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => break,
            Event::Start(e) | Event::Empty(e) if is(&resolved, PACKAGE_RELATIONSHIPS) && e.local_name().as_ref() == b"Relationship" => {
                if attribute(&reader, &e, b"", b"Id").as_deref() == Some(relationship.as_str()) {
                    target = attribute(&reader, &e, b"", b"Target").map(|t| match t.strip_prefix('/') {
                        Some(rooted) => rooted.to_string(),
                        None => format!("xl/{t}"),
                    });
                    break;
                }
            }
            _ => {}
        }
    }
    let target = target.ok_or_else(|| format_error(format!("The workbook's relationship {relationship} has no target.")))?;

    let has_shared = zip::ZipArchive::new(Cursor::new(&data)).ok().is_some_and(|mut a| a.by_name("xl/sharedStrings.xml").is_ok());
    let mut shared: Vec<String> = Vec::new();
    if has_shared {
        let strings = part("xl/sharedStrings.xml")?;
        let mut reader = NsReader::from_reader(strings.as_slice());
        let (mut current, mut phonetic, mut in_t) = (String::new(), 0usize, false);
        loop {
            let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
            let main = is(&resolved, XLSX_MAIN);
            match event {
                Event::Eof => break,
                Event::Start(e) if main => match e.local_name().as_ref() {
                    b"si" => current.clear(),
                    b"rPh" => phonetic += 1,
                    b"t" => in_t = true,
                    _ => {}
                },
                Event::Empty(e) if main && e.local_name().as_ref() == b"si" => shared.push(String::new()),
                Event::End(e) if main => match e.local_name().as_ref() {
                    b"si" => shared.push(std::mem::take(&mut current)),
                    b"rPh" => phonetic = phonetic.saturating_sub(1),
                    b"t" => in_t = false,
                    _ => {}
                },
                Event::Text(t) if in_t && phonetic == 0 => current.push_str(&t.unescape().map_err(xml_error)?),
                Event::CData(t) if in_t && phonetic == 0 => current.push_str(&String::from_utf8_lossy(&t)),
                _ => {}
            }
        }
    }

    let sheet = part(&target)?;
    let mut reader = NsReader::from_reader(sheet.as_slice());
    reader.config_mut().trim_text(false);
    let mut rows = Vec::new();
    let mut cells: Vec<String> = Vec::new();
    let (mut in_row, mut column, mut cell_type) = (false, 0usize, String::new());
    let (mut value, mut inline, mut element, mut phonetic) = (String::new(), String::new(), "", 0usize);
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        let main = is(&resolved, XLSX_MAIN);
        match event {
            Event::Eof => break,
            Event::Start(ref e) | Event::Empty(ref e) if main && e.local_name().as_ref() == b"row" => {
                in_row = true;
                cells.clear();
                if matches!(event, Event::Empty(_)) {
                    in_row = false;
                }
            }
            Event::Start(ref e) | Event::Empty(ref e) if main && in_row && e.local_name().as_ref() == b"c" => {
                column = attribute(&reader, e, b"", b"r").and_then(|r| cell_column(&r)).unwrap_or(cells.len());
                cell_type = attribute(&reader, e, b"", b"t").unwrap_or_default();
                value.clear();
                inline.clear();
                if matches!(event, Event::Empty(_)) {
                    place(&mut cells, column, String::new());
                }
            }
            Event::Start(e) if main => match e.local_name().as_ref() {
                b"rPh" => phonetic += 1,
                b"v" => element = "v",
                b"t" => element = "t",
                _ => {}
            },
            Event::End(e) if main => match e.local_name().as_ref() {
                b"row" => {
                    in_row = false;
                    let last = cells.iter().rposition(|c| !c.trim().is_empty());
                    cells.truncate(last.map_or(0, |i| i + 1));
                    if !cells.is_empty() {
                        rows.push(std::mem::take(&mut cells));
                    }
                }
                b"c" if in_row => {
                    let text = match cell_type.as_str() {
                        "s" => {
                            let index = value.trim().parse::<usize>().ok().filter(|i| *i < shared.len()).ok_or_else(|| {
                                format_error(format!("A cell refers to shared string {value}, which the workbook doesn't have."))
                            })?;
                            shared[index].clone()
                        }
                        "inlineStr" => inline.clone(),
                        _ => value.clone(),
                    };
                    place(&mut cells, column, text);
                }
                b"rPh" => phonetic = phonetic.saturating_sub(1),
                b"v" | b"t" => element = "",
                _ => {}
            },
            Event::Text(t) if element == "v" => value.push_str(&t.unescape().map_err(xml_error)?),
            Event::Text(t) if element == "t" && phonetic == 0 => inline.push_str(&t.unescape().map_err(xml_error)?),
            _ => {}
        }
    }
    Ok(rows)
}

fn place(cells: &mut Vec<String>, column: usize, text: String) {
    if column < MAX_COLUMNS {
        if cells.len() <= column {
            cells.resize(column + 1, String::new());
        }
        cells[column] = text;
    }
}

/// The zero-based column of a cell reference ("C12" is 2).
fn cell_column(reference: &str) -> Option<usize> {
    let (mut column, mut letters) = (0usize, 0usize);
    for c in reference.chars() {
        if !c.is_ascii_uppercase() {
            break;
        }
        letters += 1;
        if letters > 3 {
            return Some(MAX_COLUMNS);
        }
        column = column * 26 + (c as usize - 'A' as usize + 1);
    }
    (letters > 0).then(|| column - 1)
}

/// An Excel date (days since 1899-12-30, the 1900 date system); None when the value isn't one.
pub fn excel_date(value: Option<&str>) -> Option<chrono::NaiveDate> {
    let serial: f64 = value?.trim().parse().ok()?;
    if !(1.0..2958466.0).contains(&serial) {
        return None;
    }
    chrono::NaiveDate::from_ymd_opt(1899, 12, 30)?.checked_add_days(chrono::Days::new(serial.floor() as u64))
}

pub const NEW_ZEALAND_SHEET: &str = "Russia Sanctions Register";
pub const NEW_ZEALAND_PROGRAMME: &str = "Russia Sanctions Act 2022";

/// New Zealand's Russia Sanctions Register (XLSX): NewZealandParser in the reference.
pub fn parse_new_zealand(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let rows = xlsx_rows(input, NEW_ZEALAND_SHEET)?;
    let is_cell = |cell: &str, name: &str| cell.trim().eq_ignore_ascii_case(name);
    let header_at = rows
        .iter()
        .position(|row| row.len() > 1 && is_cell(&row[0], "Type") && is_cell(&row[1], "Unique Identifier"))
        .ok_or_else(|| format_error("No header row (Type, Unique Identifier, …) in New Zealand's register."))?;
    let header = &rows[header_at];
    let find = |name: &str| header.iter().position(|cell| is_cell(cell, name));
    let required = |name: &str| find(name).ok_or_else(|| format_error(format!("New Zealand's register has no \"{name}\" column.")));
    let (kind_column, identifier, first, last, alias, address, status, sanctioned) = (
        required("Type")?,
        required("Unique Identifier")?,
        required("First name")?,
        required("Last name")?,
        required("Alias/Alternate Spellings")?,
        required("Address")?,
        required("Sanction Status")?,
        required("Date of Sanction")?,
    );
    let (middle, title, rationale) = (find("Middle name(s)"), find("Title"), find("General Rationale for Sanction"));

    let mut out = Vec::new();
    for row in &rows[header_at + 1..] {
        let cell = |index: Option<usize>| index.and_then(|i| row.get(i)).and_then(|v| clean(Some(v)));
        let kind_text = cell(Some(kind_column));
        let kind = match kind_text.as_deref().map(str::to_uppercase).as_deref() {
            Some("INDIVIDUAL") => Some(SubjectType::Individual),
            Some("ENTITY" | "BANK") => Some(SubjectType::Entity),
            Some("ASSET" | "TOTAL" | "SHIP" | "AIRCRAFT") => None,
            None => cell(Some(identifier)).map(|_| SubjectType::Unknown),
            Some(_) => Some(SubjectType::Unknown),
        };
        let gone = cell(Some(status)).is_some_and(|s| {
            let lower = s.to_lowercase();
            ["revoked", "delisted", "removed"].iter().any(|word| lower.contains(word))
        });
        let Some(kind) = kind.filter(|_| !gone) else { continue };
        let id = cell(Some(identifier)).ok_or_else(|| {
            format_error(format!("A {} row in New Zealand's register has no Unique Identifier.", kind_text.clone().unwrap_or_default()))
        })?;
        let mut b = Builder::new(&id, kind);
        b.programmes = Some(NEW_ZEALAND_PROGRAMME.to_string());
        let date = cell(Some(sanctioned));
        b.listed_on = excel_date(date.as_deref()).or_else(|| parse_date(date.as_deref(), "%d/%m/%Y"));
        b.remarks = join("; ", &[cell(title), cell(rationale)]);
        let primary = if kind == SubjectType::Individual {
            join(" ", &[cell(Some(first)), cell(middle), cell(Some(last))])
        } else {
            cell(Some(first)).or_else(|| cell(Some(last)))
        };
        b.add_name(primary.as_deref(), false, true);
        if let Some(aliases) = cell(Some(alias)) {
            for name in aliases.split(';') {
                b.add_name(Some(name), false, false);
            }
        }
        b.add_address(cell(Some(address)).as_deref(), None);
        out.extend(b.build());
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::{dutch_surname, parse_date};

    #[test]
    fn a_dutch_surname_with_its_particle_after_a_comma_puts_it_first() {
        assert_eq!(dutch_surname("Dusseldorp, van"), "van Dusseldorp");
        assert_eq!(dutch_surname("Jansen"), "Jansen");
    }

    #[test]
    fn a_decision_date_without_leading_zeros_reads() {
        assert_eq!(parse_date(Some("7-12-2016"), "%d-%m-%Y").map(|d| d.to_string()), Some("2016-12-07".to_string()));
    }
}
