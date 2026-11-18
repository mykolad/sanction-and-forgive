//! Spreadsheets in the reference: rows of an OpenDocument (.ods) spreadsheet, read from its zip and XML. A row is its
//! cells' text, trailing empty cells dropped; empty rows are skipped. A file that isn't the format, whose part unpacks
//! to more than MAX_PART_BYTES, whose rows expand (repeated cells and rows) to more text than that, or whose XML is cut
//! short or broken anywhere, is a format error. And the Dutch terrorism list, published as one.

use std::collections::HashSet;
use std::io::{BufRead, Cursor, Read};

use quick_xml::events::Event;
use quick_xml::name::{Namespace, ResolveResult};
use quick_xml::NsReader;

use super::parse::{clean, format_error, join, parse_date, Builder, ParseError, ParsedSubject, SubjectType};

const MAX_PART_BYTES: u64 = 256 << 20;
const MAX_COLUMNS: usize = 256;
/// The longest text one cell may have, paragraphs and their line breaks included, checked as it's read (a few bytes of
/// text:s stand for a hundred spaces): far above any list's cell; spreadsheet programs stop at 32,767 characters.
const MAX_CELL_TEXT: usize = 1 << 20;
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

/// What a cell costs besides its text: about a string slot and its share of the row, so a sheet of empty cells
/// repeated is counted too.
const CELL_BYTES: usize = 32;

/// What a sheet expands to, repeats included: a few bytes of XML can repeat a long cell across a row and the row a
/// thousand times, so every cell slot and its text are held to the same limit as a part.
struct Expansion(u64);

impl Expansion {
    fn add(&mut self, bytes: usize) -> Result<(), ParseError> {
        self.0 += bytes as u64;
        if self.0 > MAX_PART_BYTES {
            return Err(format_error(format!("The spreadsheet's rows expand to more than {MAX_PART_BYTES} bytes.")));
        }
        Ok(())
    }
}

/// The rows of an OpenDocument spreadsheet's first table. The whole document is read, so one cut short or broken
/// after that table is refused too.
pub fn ods_rows(input: &mut dyn BufRead) -> Result<Vec<Vec<String>>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let content = read_part(data, "content.xml")?;
    let mut reader = NsReader::from_reader(content.as_slice());
    reader.config_mut().trim_text(false);
    let mut rows = Vec::new();
    let mut expanded = Expansion(0);
    let (mut in_table, mut done, mut depth) = (false, false, 0usize);
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof if depth == 0 => break,
            Event::Eof => return Err(format_error("The spreadsheet's content.xml ends before its elements close.")),
            Event::Start(e) if !done && in_table && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table-row" => {
                // The row is read to its end here, so the depth doesn't change.
                let repeat = count(&reader, &e, ODS_TABLE, b"number-rows-repeated");
                let row = ods_row(&mut reader, &mut expanded)?;
                if !row.is_empty() {
                    // A filled row repeated is rare; an empty one repeated to the sheet's end is common, and skipped.
                    let size = row.iter().map(|cell| cell.len() + CELL_BYTES).sum();
                    for i in 0..repeat.min(1000) {
                        if i > 0 {
                            expanded.add(size)?;
                        }
                        rows.push(row.clone());
                    }
                }
            }
            Event::Start(e) => {
                depth += 1;
                if !done && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table" {
                    done = in_table;
                    in_table = true;
                }
            }
            Event::Empty(e) if !done && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table" => {
                done = in_table;
                in_table = true;
            }
            Event::End(e) => {
                depth =
                    depth.checked_sub(1).ok_or_else(|| format_error("The spreadsheet's content.xml closes an element it never opened."))?;
                if !done && in_table && is(&resolved, ODS_TABLE) && e.local_name().as_ref() == b"table" {
                    done = true;
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

fn ods_row(reader: &mut NsReader<&[u8]>, expanded: &mut Expansion) -> Result<Vec<String>, ParseError> {
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
                    expanded.add(text.len() + CELL_BYTES)?;
                    cells.push(text.clone());
                }
            }
            Event::Empty(e) if is(&resolved, ODS_TABLE) && matches!(e.local_name().as_ref(), b"table-cell" | b"covered-table-cell") => {
                let repeat = count(reader, &e, ODS_TABLE, b"number-columns-repeated");
                for _ in 0..repeat {
                    if cells.len() >= MAX_COLUMNS {
                        break;
                    }
                    expanded.add(CELL_BYTES)?;
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

/// A cell's paragraphs, one a line; text:s is spaces, text:line-break and text:tab are what they say, whether written
/// self-closed or not.
fn ods_cell_text(reader: &mut NsReader<&[u8]>) -> Result<String, ParseError> {
    let (mut paragraphs, mut current, mut in_paragraph, mut depth) = (Vec::new(), String::new(), false, 0usize);
    let mut done = 0usize;
    loop {
        // Each finished paragraph, and its line break; then the one being read.
        if done + current.len() > MAX_CELL_TEXT {
            return Err(format_error(format!("A cell of the spreadsheet is longer than {MAX_CELL_TEXT} characters.")));
        }
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        match event {
            Event::Eof => return Err(format_error("The spreadsheet's content.xml ends inside a cell.")),
            Event::Start(e) => {
                depth += 1;
                if is(&resolved, ODS_TEXT) {
                    match e.local_name().as_ref() {
                        b"p" | b"h" => {
                            in_paragraph = true;
                            current.clear();
                        }
                        b"s" if in_paragraph => current.push_str(&" ".repeat(count(reader, &e, ODS_TEXT, b"c").min(100))),
                        b"line-break" if in_paragraph => current.push('\n'),
                        b"tab" if in_paragraph => current.push('\t'),
                        _ => {}
                    }
                }
            }
            Event::Empty(e) if is(&resolved, ODS_TEXT) => match e.local_name().as_ref() {
                b"p" | b"h" => {
                    paragraphs.push(String::new());
                    done += 1;
                }
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
                    done += current.len() + 1;
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

/// The rows of an Office Open XML workbook's sheet named sheet_name: XlsxRows in the reference. The sheet and the
/// shared strings are read to their ends, so one cut short or broken is refused; a shared string's text counts against
/// the expansion limit each time a cell uses it.
// A match guard would borrow the reader while the event still does: the attribute checks stay inside the arms.
#[allow(clippy::collapsible_match)]
pub fn xlsx_rows(input: &mut dyn BufRead, sheet_name: &str) -> Result<Vec<Vec<String>>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let part = |name: &str| read_part(data.clone(), name);

    // The workbook and its relationships are read to their ends, so one cut short after what's looked for is refused.
    let workbook = part("xl/workbook.xml")?;
    let mut reader = NsReader::from_reader(workbook.as_slice());
    let (mut relationship, mut depth) = (None, 0usize);
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        depth = open_elements(&event, depth, "xl/workbook.xml")?;
        match event {
            Event::Eof => break,
            Event::Start(e) | Event::Empty(e) if is(&resolved, XLSX_MAIN) && e.local_name().as_ref() == b"sheet" => {
                // An empty name takes the first sheet (a workbook whose sheet keeps its program's name).
                if relationship.is_none() && (sheet_name.is_empty() || attribute(&reader, &e, b"", b"name").as_deref() == Some(sheet_name))
                {
                    relationship = attribute(&reader, &e, XLSX_RELATIONSHIPS, b"id");
                }
            }
            _ => {}
        }
    }
    let relationship = relationship.ok_or_else(|| {
        format_error(if sheet_name.is_empty() {
            "The workbook has no sheet.".to_string()
        } else {
            format!("The workbook has no sheet named \"{sheet_name}\".")
        })
    })?;

    let relationships = part("xl/_rels/workbook.xml.rels")?;
    let mut reader = NsReader::from_reader(relationships.as_slice());
    let (mut target, mut depth) = (None, 0usize);
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        depth = open_elements(&event, depth, "xl/_rels/workbook.xml.rels")?;
        match event {
            Event::Eof => break,
            Event::Start(e) | Event::Empty(e) if is(&resolved, PACKAGE_RELATIONSHIPS) && e.local_name().as_ref() == b"Relationship" => {
                if target.is_none() && attribute(&reader, &e, b"", b"Id").as_deref() == Some(relationship.as_str()) {
                    target = attribute(&reader, &e, b"", b"Target").map(|t| match t.strip_prefix('/') {
                        Some(rooted) => rooted.to_string(),
                        None => format!("xl/{t}"),
                    });
                }
            }
            _ => {}
        }
    }
    let target = target.ok_or_else(|| format_error(format!("The workbook's relationship {relationship} has no target.")))?;

    let has_shared = zip::ZipArchive::new(Cursor::new(&data)).ok().is_some_and(|mut a| a.by_name("xl/sharedStrings.xml").is_ok());
    let mut shared: Vec<String> = Vec::new();
    let mut expanded = Expansion(0);
    if has_shared {
        let strings = part("xl/sharedStrings.xml")?;
        let mut reader = NsReader::from_reader(strings.as_slice());
        let (mut current, mut phonetic, mut in_t, mut depth) = (String::new(), 0usize, false, 0usize);
        loop {
            let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
            let main = is(&resolved, XLSX_MAIN);
            depth = open_elements(&event, depth, "xl/sharedStrings.xml")?;
            match event {
                Event::Eof => break,
                Event::Start(e) if main => match e.local_name().as_ref() {
                    b"si" => current.clear(),
                    b"rPh" => phonetic += 1,
                    b"t" => in_t = true,
                    _ => {}
                },
                Event::Empty(e) if main && e.local_name().as_ref() == b"si" => {
                    expanded.add(CELL_BYTES)?;
                    shared.push(String::new());
                }
                Event::End(e) if main => match e.local_name().as_ref() {
                    b"si" => {
                        expanded.add(current.len() + CELL_BYTES)?;
                        shared.push(std::mem::take(&mut current));
                    }
                    b"rPh" => phonetic = phonetic.saturating_sub(1),
                    b"t" => in_t = false,
                    _ => {}
                },
                Event::Text(t) if in_t && phonetic == 0 => push_cell_text(&mut current, &t.unescape().map_err(xml_error)?)?,
                Event::CData(t) if in_t && phonetic == 0 => push_cell_text(&mut current, &String::from_utf8_lossy(&t))?,
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
    let mut depth = 0usize;
    loop {
        let (resolved, event) = reader.read_resolved_event().map_err(xml_error)?;
        let main = is(&resolved, XLSX_MAIN);
        depth = open_elements(&event, depth, &target)?;
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
                    if column < MAX_COLUMNS {
                        expanded.add(CELL_BYTES * (column + 1).saturating_sub(cells.len()))?;
                    }
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
                    if column < MAX_COLUMNS {
                        expanded.add(text.len() + CELL_BYTES * (column + 1).saturating_sub(cells.len()))?;
                    }
                    place(&mut cells, column, text);
                }
                b"rPh" => phonetic = phonetic.saturating_sub(1),
                b"v" | b"t" => element = "",
                _ => {}
            },
            Event::Text(t) if element == "v" => push_cell_text(&mut value, &t.unescape().map_err(xml_error)?)?,
            Event::CData(t) if element == "v" => push_cell_text(&mut value, &String::from_utf8_lossy(&t))?,
            Event::Text(t) if element == "t" && phonetic == 0 => push_cell_text(&mut inline, &t.unescape().map_err(xml_error)?)?,
            Event::CData(t) if element == "t" && phonetic == 0 => push_cell_text(&mut inline, &String::from_utf8_lossy(&t))?,
            _ => {}
        }
    }
    Ok(rows)
}

/// Text added to a cell's, refused once the cell is longer than MAX_CELL_TEXT.
fn push_cell_text(cell: &mut String, text: &str) -> Result<(), ParseError> {
    cell.push_str(text);
    if cell.len() > MAX_CELL_TEXT {
        return Err(format_error(format!("A cell of the spreadsheet is longer than {MAX_CELL_TEXT} characters.")));
    }
    Ok(())
}

/// The elements open after this event; a part that ends with elements still open is cut short, a format error.
fn open_elements(event: &Event, depth: usize, part: &str) -> Result<usize, ParseError> {
    match event {
        Event::Start(_) => Ok(depth + 1),
        Event::End(_) => Ok(depth.saturating_sub(1)),
        Event::Eof if depth > 0 => Err(format_error(format!("The workbook's {part} ends before its elements close."))),
        _ => Ok(depth),
    }
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

pub const NEW_ZEALAND_SHIPS_SHEET: &str = "Ships";

/// New Zealand's Russia Sanctions Register (XLSX): NewZealandParser in the reference, and in the Rust port its ships
/// (#69), from the sheet of their own.
pub fn parse_new_zealand(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let mut out = parse_new_zealand_register(xlsx_rows(&mut data.as_slice(), NEW_ZEALAND_SHEET)?)?;
    out.extend(parse_new_zealand_ships(xlsx_rows(&mut data.as_slice(), NEW_ZEALAND_SHIPS_SHEET)?)?);
    Ok(out)
}

/// The register's Ships sheet (#69): a row per ship, with its IMO number, its name when sanctioned and other names.
/// A row whose status says it's no longer sanctioned, or that is marked deleted, is left out.
fn parse_new_zealand_ships(rows: Vec<Vec<String>>) -> Result<Vec<ParsedSubject>, ParseError> {
    let is_cell = |cell: &str, name: &str| cell.trim().eq_ignore_ascii_case(name);
    let header_at = rows
        .iter()
        .position(|row| {
            row.len() > 2 && is_cell(&row[0], "Type") && is_cell(&row[1], "Unique Identifier") && is_cell(&row[2], "IMO Number")
        })
        .ok_or_else(|| format_error("No header row (Type, Unique Identifier, IMO Number, …) in New Zealand's Ships sheet."))?;
    let header = &rows[header_at];
    let find = |name: &str| header.iter().position(|cell| is_cell(cell, name));
    let required = |name: &str| find(name).ok_or_else(|| format_error(format!("New Zealand's Ships sheet has no \"{name}\" column.")));
    let (identifier, imo, name, sanctioned, alias, status) = (
        required("Unique Identifier")?,
        required("IMO Number")?,
        required("Name of Ship as of Date of Sanction")?,
        required("Date of Sanction")?,
        required("Alias/Alternate Names")?,
        required("Sanction Status")?,
    );
    let deleted = find("Record Deleted Flag");
    let mut out = Vec::new();
    for row in &rows[header_at + 1..] {
        let cell = |index: Option<usize>| index.and_then(|i| row.get(i)).and_then(|v| clean(Some(v)));
        let gone = cell(Some(status)).is_some_and(|s| {
            let lower = s.to_lowercase();
            ["revoked", "delisted", "removed"].iter().any(|word| lower.contains(word))
        }) || cell(deleted).is_some_and(|d| ["yes", "true", "y"].contains(&d.to_lowercase().as_str()));
        if gone {
            continue;
        }
        // A ship listed without its identifier is a broken file, never a ship quietly lost; an empty row is skipped.
        let Some(id) = cell(Some(identifier)) else {
            if cell(Some(name)).is_some() || cell(Some(imo)).is_some() {
                return Err(format_error("A ship in New Zealand's Ships sheet has no Unique Identifier."));
            }
            continue;
        };
        let mut b = Builder::new(&id, SubjectType::Vessel);
        b.programmes = Some(NEW_ZEALAND_PROGRAMME.to_string());
        let date = cell(Some(sanctioned));
        b.listed_on = excel_date(date.as_deref()).or_else(|| parse_date(date.as_deref(), "%d/%m/%Y"));
        b.add_name(cell(Some(name)).as_deref(), false, true);
        if let Some(aliases) = cell(Some(alias)) {
            for other in aliases.split(';') {
                b.add_name(Some(other), false, false);
            }
        }
        b.add_imo_number(cell(Some(imo)).as_deref(), "IMO number as listed");
        out.extend(b.build());
    }
    Ok(out)
}

fn parse_new_zealand_register(rows: Vec<Vec<String>>) -> Result<Vec<ParsedSubject>, ParseError> {
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

pub const BELGIUM_PROGRAMME: &str = "Belgian national list (royal decree of 28 December 2006, financing of terrorism)";

/// Belgium's national terrorism list (XLSX, its first sheet): BelgiumParser in the reference. A row per number (first
/// column); a number without a name is a place kept for someone no longer listed. A row with a given name is a person,
/// one without is read both ways; the national register number isn't kept, the date of birth goes in the remarks, the
/// publication link is the official record.
pub fn parse_belgium(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let rows = xlsx_rows(input, "")?;
    let is_cell = |cell: &str, name: &str| cell.trim().eq_ignore_ascii_case(name);
    let header_at = rows
        .iter()
        .position(|row| row.iter().any(|c| is_cell(c, "Naam")) && row.iter().any(|c| is_cell(c, "Voornaam(voornamen)")))
        .ok_or_else(|| format_error("No header row (Naam, Voornaam(voornamen), …) in Belgium's national list."))?;
    let header = &rows[header_at];
    let find = |name: &str| header.iter().position(|cell| is_cell(cell, name));
    let (surname, given) = (find("Naam").unwrap_or(0), find("Voornaam(voornamen)").unwrap_or(0));
    let (born, decree, publication) = (find("Geboortedatum"), find("Datum KB"), find("Link naar de publicatie"));

    let mut out = Vec::new();
    for row in &rows[header_at + 1..] {
        let cell = |index: Option<usize>| index.and_then(|i| row.get(i)).and_then(|v| clean(Some(v)));
        let (last, first) = (cell(Some(surname)), cell(Some(given)));
        if last.is_none() && first.is_none() {
            continue;
        }
        let number = cell(Some(0)).ok_or_else(|| {
            format_error(format!(
                "A row of Belgium's national list ({}) has no number.",
                last.clone().or_else(|| first.clone()).unwrap_or_default()
            ))
        })?;
        let Some(last) = last else {
            return Err(format_error(format!("Row {number} of Belgium's national list has no surname.")));
        };
        let kind = if first.is_some() { SubjectType::Individual } else { SubjectType::Unknown };
        let mut b = Builder::new(&number, kind);
        b.programmes = Some(BELGIUM_PROGRAMME.to_string());
        b.listed_on = excel_date(cell(decree).as_deref());
        b.remarks = excel_date(cell(born).as_deref()).map(|date| format!("born {}", date.format("%Y-%m-%d")));
        b.source_url = cell(publication)
            .filter(|link| (link.starts_with("https://") || link.starts_with("http://")) && !link.chars().any(char::is_whitespace));
        b.add_name(join(" ", &[first, Some(last)]).as_deref(), false, true);
        out.extend(b.build());
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use std::io::Write;

    use super::{dutch_surname, ods_rows, parse_date, xlsx_rows};

    const NAMESPACES: &str = r#"xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0""#;

    fn ods(content: &str) -> Result<Vec<Vec<String>>, super::ParseError> {
        let mut buffer = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buffer);
            zip.start_file("content.xml", zip::write::SimpleFileOptions::default()).unwrap();
            zip.write_all(content.as_bytes()).unwrap();
            zip.finish().unwrap();
        }
        ods_rows(&mut std::io::Cursor::new(buffer.into_inner()))
    }

    #[test]
    fn the_first_table_is_read_with_its_separators_self_closed_or_not() {
        let content = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table><table:table-row>\
             <table:table-cell><text:p>Pieter<text:s/>Willem<text:s></text:s>Jansen</text:p></table:table-cell>\
             <table:table-cell table:number-columns-repeated=\"2\"/><table:table-cell><text:p>c</text:p></table:table-cell></table:table-row>\
             </table:table><table:table><table:table-row><table:table-cell><text:p>second</text:p></table:table-cell></table:table-row></table:table>\
             </office:spreadsheet></office:body></office:document-content>"
        );
        assert_eq!(ods(&content).unwrap(), vec![vec!["Pieter Willem Jansen", "", "", "c"]]);
    }

    #[test]
    fn a_document_cut_short_or_broken_after_its_first_table_is_a_format_error() {
        let start = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table><table:table-row>\
             <table:table-cell><text:p>a</text:p></table:table-cell></table:table-row>"
        );
        for content in [start.clone(), format!("{start}</table:table>"), format!("{start}</table:table><table:table></office:spreadsheet>")]
        {
            assert!(ods(&content).is_err(), "{content}");
        }
    }

    #[test]
    fn repeats_that_expand_past_the_limit_are_a_format_error() {
        // A cell of 16 KiB repeated across 256 columns, the row a thousand times: about 4 GiB from a few kilobytes.
        let content = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table>\
             <table:table-row table:number-rows-repeated=\"1000\"><table:table-cell table:number-columns-repeated=\"256\"><text:p>{}</text:p>\
             </table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>",
            "x".repeat(16 * 1024)
        );
        assert!(ods(&content).is_err());
    }

    /// An empty sheet name reads the workbook's first sheet, whatever it's called (Belgium's Blad1).
    #[test]
    fn an_empty_sheet_name_reads_the_first_sheet() {
        let workbook = r#"<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Blad1" sheetId="1" r:id="rId1"/><sheet name="Other" sheetId="2" r:id="rId2"/></sheets></workbook>"#;
        let relationships = r#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="worksheet" Target="worksheets/sheet2.xml"/></Relationships>"#;
        let sheet = |text: &str| {
            format!(
                r#"<worksheet xmlns="{MAIN}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c></row></sheetData></worksheet>"#
            )
        };
        let mut buffer = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buffer);
            for (name, content) in [
                ("xl/workbook.xml", workbook.to_string()),
                ("xl/_rels/workbook.xml.rels", relationships.to_string()),
                ("xl/worksheets/sheet1.xml", sheet("first")),
                ("xl/worksheets/sheet2.xml", sheet("second")),
            ] {
                zip.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(content.as_bytes()).unwrap();
            }
            zip.finish().unwrap();
        }
        let bytes = buffer.into_inner();
        assert_eq!(xlsx_rows(&mut std::io::Cursor::new(bytes.clone()), "").unwrap(), vec![vec!["first"]]);
        assert_eq!(xlsx_rows(&mut std::io::Cursor::new(bytes), "Other").unwrap(), vec![vec!["second"]]);
    }

    const WORKBOOK: &str = r#"<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Register" sheetId="1" r:id="rId1"/></sheets></workbook>"#;
    const RELATIONSHIPS: &str = r#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>"#;
    const MAIN: &str = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// A workbook of one sheet, "Register", written as given, with these shared strings.
    fn workbook(sheet: &str, shared_strings: &str) -> Result<Vec<Vec<String>>, super::ParseError> {
        workbook_parts(WORKBOOK, RELATIONSHIPS, sheet, shared_strings)
    }

    fn workbook_parts(
        workbook: &str,
        relationships: &str,
        sheet: &str,
        shared_strings: &str,
    ) -> Result<Vec<Vec<String>>, super::ParseError> {
        let mut buffer = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buffer);
            for (name, content) in [
                ("xl/workbook.xml", workbook),
                ("xl/_rels/workbook.xml.rels", relationships),
                ("xl/sharedStrings.xml", shared_strings),
                ("xl/worksheets/sheet1.xml", sheet),
            ] {
                zip.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(content.as_bytes()).unwrap();
            }
            zip.finish().unwrap();
        }
        xlsx_rows(&mut std::io::Cursor::new(buffer.into_inner()), "Register")
    }

    #[test]
    fn a_workbook_or_relationships_cut_short_after_whats_looked_for_is_a_format_error() {
        let sheet = format!(r#"<worksheet xmlns="{MAIN}"><sheetData/></worksheet>"#);
        let shared = format!(r#"<sst xmlns="{MAIN}"/>"#);
        let workbook = &WORKBOOK[..WORKBOOK.find("</sheets>").unwrap()];
        let relationships = &RELATIONSHIPS[..RELATIONSHIPS.find("</Relationships>").unwrap()];
        assert!(workbook_parts(workbook, RELATIONSHIPS, &sheet, &shared).is_err());
        assert!(workbook_parts(WORKBOOK, relationships, &sheet, &shared).is_err());
    }

    #[test]
    fn a_shared_or_inline_string_longer_than_the_limit_is_a_format_error() {
        let text = "x".repeat(super::MAX_CELL_TEXT + 1);
        let sheet =
            format!(r#"<worksheet xmlns="{MAIN}"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row></sheetData></worksheet>"#);
        let inline = format!(
            r#"<worksheet xmlns="{MAIN}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c></row></sheetData></worksheet>"#
        );
        assert!(workbook(&sheet, &format!(r#"<sst xmlns="{MAIN}"><si><t>{text}</t></si></sst>"#)).is_err());
        assert!(workbook(&inline, &format!(r#"<sst xmlns="{MAIN}"/>"#)).is_err());
    }

    #[test]
    fn cdata_in_values_and_inline_strings_is_read() {
        let sheet = format!(
            r#"<worksheet xmlns="{MAIN}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t><![CDATA[Northwind Trading]]></t></is></c><c r="B1"><v><![CDATA[44638]]></v></c></row></sheetData></worksheet>"#
        );
        assert_eq!(workbook(&sheet, &format!(r#"<sst xmlns="{MAIN}"/>"#)).unwrap(), vec![vec!["Northwind Trading", "44638"]]);
    }

    #[test]
    fn a_workbook_whose_sheet_or_shared_strings_are_cut_short_is_a_format_error() {
        let sheet = r#"<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c></row>"#;
        let strings = r#"<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>a</t></si>"#;
        assert!(workbook(sheet, &format!("{strings}</sst>")).is_err());
        assert!(workbook(&format!("{sheet}</sheetData></worksheet>"), strings).is_err());
    }

    #[test]
    fn a_shared_string_used_past_the_limit_is_a_format_error() {
        // One shared string of 16 KiB in 20,000 cells: about 320 MB of text from under a megabyte.
        let row = format!("<row>{}</row>", r#"<c t="s"><v>0</v></c>"#.repeat(200));
        let sheet = format!(
            r#"<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{}</sheetData></worksheet>"#,
            row.repeat(100)
        );
        let shared = format!(
            r#"<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>{}</t></si></sst>"#,
            "x".repeat(16 * 1024)
        );
        assert!(workbook(&sheet, &shared).is_err());
    }

    #[test]
    fn sparse_rows_repeated_past_the_limit_are_a_format_error() {
        // A thousand rows of 255 empty cells and an "x", each repeated a thousand times: little text, a million rows.
        let row = r#"<table:table-row table:number-rows-repeated="1000"><table:table-cell table:number-columns-repeated="255"/><table:table-cell><text:p>x</text:p></table:table-cell></table:table-row>"#;
        let content = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table>{}</table:table></office:spreadsheet></office:body></office:document-content>",
            row.repeat(1000)
        );
        assert!(ods(&content).is_err());
    }

    #[test]
    fn sparse_rows_opening_cells_past_the_limit_are_a_format_error() {
        // 40,000 rows of one cell in the 256th column: little XML, ten million cell slots.
        let rows: String = (1..=40_000).map(|r| format!(r#"<row><c r="IV{r}" t="inlineStr"><is><t>x</t></is></c></row>"#)).collect();
        let sheet = format!(
            r#"<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>"#
        );
        assert!(workbook(&sheet, r#"<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>"#).is_err());
    }

    #[test]
    fn self_closed_empty_cells_count_against_the_limit_too() {
        // Rows of 256 self-closed empty cells and nothing else would be dropped as empty, so each ends in an "x".
        let row = r#"<table:table-row><table:table-cell table:number-columns-repeated="255"/><table:table-cell><text:p>x</text:p></table:table-cell></table:table-row>"#;
        let content = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table>{}</table:table></office:spreadsheet></office:body></office:document-content>",
            row.repeat(40_000)
        );
        assert!(ods(&content).is_err());
    }

    #[test]
    fn a_cell_longer_than_the_limit_is_a_format_error_as_its_read() {
        // 20,000 runs of a hundred spaces: 500 kB of XML for 2 million characters in one cell.
        let content = format!(
            "<office:document-content {NAMESPACES}><office:body><office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>a{}b</text:p>\
             </table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>",
            r#"<text:s text:c="100"/>"#.repeat(20_000)
        );
        assert!(ods(&content).is_err());
    }

    /// New Zealand's Ships sheet (#69): a vessel per row, its IMO number, other names; revoked and deleted rows out.
    #[test]
    fn new_zealand_ships_are_read_from_their_sheet() {
        let row = |cells: &[&str]| cells.iter().map(|c| c.to_string()).collect::<Vec<_>>();
        let header = row(&[
            "Type",
            "Unique Identifier",
            "IMO Number",
            "Name of Ship as of Date of Sanction",
            "Date of Sanction",
            "Alias/Alternate Names",
            "Date of Additional Sanction",
            "Sanction Status",
            "Record Deleted Flag",
        ]);
        let ships = super::parse_new_zealand_ships(vec![
            row(&["Ships"]),
            header.clone(),
            row(&["Ship", "SHP-1", "9288693", "Andaman Skies", "45827", "Mirador; Ocean", "", "Sanctioned", ""]),
            row(&["Ship", "SHP-2", "9288746", "Udaya", "45827", "", "", "Revoked", ""]),
            row(&["Ship", "SHP-3", "9281011", "Moti", "45827", "", "", "Sanctioned", "Yes"]),
        ])
        .unwrap();
        assert_eq!(ships.len(), 1);
        let ship = &ships[0];
        assert_eq!((ship.kind, ship.primary_name.as_str(), ship.names.len()), (super::SubjectType::Vessel, "Andaman Skies", 3));
        assert_eq!((ship.identifiers[0].kind.as_str(), ship.identifiers[0].value.as_str()), ("IMO number", "9288693"));
        assert!(super::parse_new_zealand_ships(vec![row(&["Ship"])]).is_err());
        let unnamed = row(&["Ship", "", "9288693", "Andaman Skies", "45827", "", "", "Sanctioned", ""]);
        assert!(super::parse_new_zealand_ships(vec![header.clone(), unnamed]).is_err());
        let blank = row(&["", "", "", "", "", "", "", "", ""]);
        assert!(super::parse_new_zealand_ships(vec![header, blank]).unwrap().is_empty());
    }

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
