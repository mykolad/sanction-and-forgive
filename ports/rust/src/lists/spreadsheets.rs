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
