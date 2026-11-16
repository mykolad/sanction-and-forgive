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
    loop {
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
    use std::io::Write;

    use super::{dutch_surname, ods_rows, parse_date};

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
    fn a_dutch_surname_with_its_particle_after_a_comma_puts_it_first() {
        assert_eq!(dutch_surname("Dusseldorp, van"), "van Dusseldorp");
        assert_eq!(dutch_surname("Jansen"), "Jansen");
    }

    #[test]
    fn a_decision_date_without_leading_zeros_reads() {
        assert_eq!(parse_date(Some("7-12-2016"), "%d-%m-%Y").map(|d| d.to_string()), Some("2016-12-07".to_string()));
    }
}
