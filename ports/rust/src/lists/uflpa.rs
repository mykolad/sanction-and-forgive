//! The US UFLPA Entity List (UflpaParser and HtmlTables in the reference): the DHS page itself, one table per section
//! of the Act, aliases written in the name.

use std::collections::HashMap;
use std::io::BufRead;
use std::sync::LazyLock;

use regex::Regex;

use super::manual::decode_entities;
use super::parse::{clean, format_error, join, parse_date, Builder, ParseError, ParsedSubject, SubjectType};

pub const UFLPA_PROGRAMME: &str = "Uyghur Forced Labor Prevention Act";

static TABLE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?is)<table\b[^>]*>(.*?)</table\s*>").unwrap());
static ROW: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?is)<tr\b[^>]*>(.*?)</tr\s*>").unwrap());
static CELL: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]\s*>").unwrap());
static TAG: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)<[^>]*>").unwrap());
static ALIAS_LEAD: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i)^(?:also known as|formerly known as|(?:and|including) (?:\w+ )?alias(?:es)?:)\s*").unwrap());
static ACRONYM: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^[A-Z0-9&-]{2,12}$").unwrap());
static TRAILING: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?i),?\s+(and (?:its subordinate and affiliated entities|subsidiaries))\s*$").unwrap());
static TABLE_OPEN: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)<table\b").unwrap());
static TABLE_CLOSE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)</table\s*>").unwrap());
static DOCUMENT_END: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)</html\s*>\s*$").unwrap());
static SECTION: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"Section\s+\d+\([a-z]\)\(\d+\)\([A-Z]\)\([ivx]+\)").unwrap());

/// A table: the page's text before it (since the previous table), and its rows of cells.
struct Table {
    before: String,
    rows: Vec<Vec<String>>,
}

/// A fragment's text: tags as spaces, character references decoded, whitespace collapsed.
fn text_of(fragment: &str) -> String {
    clean(Some(&decode_entities(&TAG.replace_all(fragment, " ")))).unwrap_or_default()
}

fn read_tables(document: &str) -> Vec<Table> {
    let mut tables = Vec::new();
    let mut previous_end = 0;
    for table in TABLE.captures_iter(document) {
        let whole = table.get(0).unwrap();
        let before = text_of(&document[previous_end..whole.start()]);
        previous_end = whole.end();
        let rows = ROW
            .captures_iter(&table[1])
            .map(|row| CELL.captures_iter(&row[1]).map(|cell| text_of(&cell[1])).collect::<Vec<_>>())
            .filter(|cells| !cells.is_empty())
            .collect();
        tables.push(Table { before, rows });
    }
    tables
}

struct Entity {
    primary: String,
    names: Vec<String>,
    sections: Vec<String>,
    remarks: Vec<String>,
    listed_on: Option<chrono::NaiveDate>,
}

pub fn parse_uflpa(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let text = String::from_utf8_lossy(&data);
    let document = text.strip_prefix('\u{FEFF}').unwrap_or(&text);
    let tables = read_tables(document);
    if tables.is_empty() {
        return Err(format_error("The UFLPA page has no entity tables."));
    }
    // A page cut short loses its last rows or tables without a trace: the document must end, and every table close.
    let complete = TABLE_OPEN.find_iter(document).count() == tables.len()
        && TABLE_CLOSE.find_iter(document).count() == tables.len()
        && DOCUMENT_END.is_match(document);
    if !complete {
        return Err(format_error("The UFLPA page is cut short: a table or the document doesn't close."));
    }
    let mut entities: Vec<Entity> = Vec::new();
    let mut by_key: HashMap<String, usize> = HashMap::new();
    for table in &tables {
        let header_ok = matches!(table.rows.first(), Some(header) if header.len() == 2
            && header[0].to_lowercase().contains("entity") && header[1].to_lowercase().contains("effective date"));
        if !header_ok {
            return Err(format_error("A UFLPA table has no header (Name of Entity, Effective Date)."));
        }
        let section = SECTION.find_iter(&table.before).last().map(|m| m.as_str().to_string());
        for row in &table.rows[1..] {
            let written = clean(row.first().map(String::as_str));
            let date = row.get(1).and_then(|cell| parse_date(Some(cell), "%B %d, %Y"));
            let (2, Some(written), Some(date)) = (row.len(), written, date) else {
                return Err(format_error("A UFLPA row isn't a name and an effective date."));
            };
            let (primary, mut aliases, remark) = split_name(&written);
            // "Co., Ltd" and "Co., Ltd." in two tables are one entity: the first spelling is its id.
            let key = primary.trim_end_matches(['.', ' ']).to_string();
            let index = *by_key.entry(key).or_insert_with(|| {
                entities.push(Entity {
                    primary: primary.clone(),
                    names: Vec::new(),
                    sections: Vec::new(),
                    remarks: Vec::new(),
                    listed_on: None,
                });
                entities.len() - 1
            });
            let entity = &mut entities[index];
            aliases.push(primary);
            entity.names.extend(aliases);
            if let Some(section) = &section {
                if !entity.sections.contains(section) {
                    entity.sections.push(section.clone());
                }
            }
            if let Some(remark) = remark {
                if !entity.remarks.contains(&remark) {
                    entity.remarks.push(remark);
                }
            }
            if entity.listed_on.is_none_or(|listed| date < listed) {
                entity.listed_on = Some(date);
            }
        }
    }
    let mut out = Vec::new();
    for entity in entities {
        let mut b = Builder::new(&entity.primary, SubjectType::Entity);
        b.programmes = Some(if entity.sections.is_empty() {
            UFLPA_PROGRAMME.to_string()
        } else {
            format!("{UFLPA_PROGRAMME}, {}", entity.sections.join("; "))
        });
        b.listed_on = entity.listed_on;
        b.remarks = join("; ", &entity.remarks.iter().cloned().map(Some).collect::<Vec<_>>());
        b.add_name(Some(&entity.primary), false, true);
        for name in &entity.names {
            b.add_name(Some(name), false, false);
        }
        out.extend(b.build());
    }
    Ok(out)
}

/// A name as written: the name without its alias parentheses or trailing remark, the aliases, and the remark.
fn split_name(written: &str) -> (String, Vec<String>, Option<String>) {
    let bytes = written.as_bytes();
    let mut aliases = Vec::new();
    let mut name: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        let close = if bytes[i] == b'(' { closing_parenthesis(bytes, i) } else { None };
        let Some(close) = close else {
            name.push(bytes[i]);
            i += 1;
            continue;
        };
        let inside = written[i + 1..close].trim();
        if let Some(lead) = ALIAS_LEAD.find(inside) {
            for part in inside[lead.end()..].split(';') {
                let mut alias = clean(Some(part));
                if let Some(a) = &alias {
                    if a.get(..4).is_some_and(|start| start.eq_ignore_ascii_case("and ")) {
                        alias = clean(Some(&a[4..]));
                    }
                }
                aliases.extend(alias);
            }
        } else if ACRONYM.is_match(inside) {
            aliases.push(inside.to_string());
        } else {
            name.extend_from_slice(&bytes[i..=close]);
        }
        i = close + 1;
    }
    // Only whole runs between ASCII parentheses were copied, so the bytes are still UTF-8.
    let name = String::from_utf8(name).expect("UTF-8 cut at ASCII parentheses only");
    let mut primary = clean(Some(&name)).unwrap_or_else(|| written.to_string());
    let mut remark = None;
    if let Some(trailing) = TRAILING.captures(&primary) {
        let whole = trailing.get(0).unwrap();
        if whole.start() > 0 {
            remark = clean(Some(&trailing[1]));
            primary = clean(Some(&primary[..whole.start()])).unwrap_or(primary.clone());
        }
    }
    (primary, aliases, remark)
}

/// The index of the parenthesis closing the one at open, or None if it isn't closed.
fn closing_parenthesis(bytes: &[u8], open: usize) -> Option<usize> {
    let mut depth = 0i32;
    for (i, b) in bytes.iter().enumerate().skip(open) {
        match b {
            b'(' => depth += 1,
            b')' => depth -= 1,
            _ => {}
        }
        if depth == 0 {
            return Some(i);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::{parse_uflpa, UFLPA_PROGRAMME};

    const PAGE: &str = r#"<html><body><main><h1>UFLPA Entity List</h1>
<p>A list of entities that mine or produce goods with forced labor</p><p><strong>Section 2(d)(2)(B)(i)</strong></p>
<table><thead><tr><th>Name of Entity</th><th>Effective Date</th></tr></thead><tbody>
<tr><td>Northwind Silicon (Shanshan) Co., Ltd (including one alias: Northwind Silicon (Shanshan) Co.) and subsidiaries</td><td>June 21, 2022</td></tr>
<tr><td>Contoso&nbsp;Cotton Corps (including three aliases: CCC; Contoso Corps; and Kongtuo) and its subordinate and affiliated entities</td><td>June 21, 2022</td></tr>
</tbody></table>
<p>Section 2(d)(2)(B)(ii)</p>
<table><tr><th>Entity Name</th><th>Effective Date</th></tr>
<tr><td>Fabrikam Textiles Co., Ltd. (also known as Fabrikam Spinning; and Fab Yarn Co., Ltd.)</td><td>January 15, 2025</td></tr>
<tr><td>Tailspin Trading Co., Ltd. (formerly known as Tailspin Toys &amp; Trading Co., Ltd.)</td><td>August 3, 2026</td></tr>
<tr><td>Contoso Cotton Corps (CCC) and its subordinate and affiliated entities</td><td>May 17, 2024</td></tr>
<tr><td>Northwind Silicon (Shanshan) Co., Ltd.</td><td>August 9, 2024</td></tr>
</table></main></body></html>"#;

    #[test]
    fn each_sections_table_is_read_with_aliases_in_the_name_one_record_per_entity() {
        let subjects = parse_uflpa(&mut PAGE.as_bytes()).unwrap();
        let got: Vec<String> = subjects
            .iter()
            .map(|s| {
                let names: Vec<&str> = s.names.iter().map(|n| n.full_name.as_str()).collect();
                format!(
                    "{}|{}|{}|{}|{}",
                    s.external_id,
                    names.join("/"),
                    s.programmes.as_deref().unwrap_or_default(),
                    s.listed_on.map(|d| d.to_string()).unwrap_or_default(),
                    s.remarks.as_deref().unwrap_or_default()
                )
            })
            .collect();
        assert_eq!(
            got,
            vec![
                format!("Northwind Silicon (Shanshan) Co., Ltd|Northwind Silicon (Shanshan) Co., Ltd/Northwind Silicon (Shanshan) Co./Northwind Silicon (Shanshan) Co., Ltd.|{UFLPA_PROGRAMME}, Section 2(d)(2)(B)(i); Section 2(d)(2)(B)(ii)|2022-06-21|and subsidiaries"),
                format!("Contoso Cotton Corps|Contoso Cotton Corps/CCC/Contoso Corps/Kongtuo|{UFLPA_PROGRAMME}, Section 2(d)(2)(B)(i); Section 2(d)(2)(B)(ii)|2022-06-21|and its subordinate and affiliated entities"),
                format!("Fabrikam Textiles Co., Ltd.|Fabrikam Textiles Co., Ltd./Fabrikam Spinning/Fab Yarn Co., Ltd.|{UFLPA_PROGRAMME}, Section 2(d)(2)(B)(ii)|2025-01-15|"),
                format!("Tailspin Trading Co., Ltd.|Tailspin Trading Co., Ltd./Tailspin Toys & Trading Co., Ltd.|{UFLPA_PROGRAMME}, Section 2(d)(2)(B)(ii)|2026-08-03|"),
            ]
        );
    }

    #[test]
    fn a_page_that_isnt_the_list_is_a_format_error() {
        let in_last_table = &PAGE[..PAGE.rfind("<tr>").unwrap()];
        let after_it = &PAGE[..PAGE.rfind("</table>").unwrap() + "</table>".len()];
        for page in [
            "<html><p>No tables here.</p></html>",
            "<html><table><tr><th>Company</th><th>Date</th></tr><tr><td>A</td><td>June 21, 2022</td></tr></table></html>",
            "<html><table><tr><th>Name of Entity</th><th>Effective Date</th></tr><tr><td>A</td></tr></table></html>",
            "<html><table><tr><th>Name of Entity</th><th>Effective Date</th></tr><tr><td> </td><td>June 21, 2022</td></tr></table></html>",
            "<html><table><tr><th>Name of Entity</th><th>Effective Date</th></tr><tr><td>A</td><td> </td></tr></table></html>",
            "<html><table><tr><th>Name of Entity</th><th>Effective Date</th></tr><tr><td>A</td><td>Summer 2022</td></tr></table></html>",
            in_last_table,
            after_it,
        ] {
            assert!(parse_uflpa(&mut page.as_bytes()).is_err(), "{page}");
        }
    }
}
