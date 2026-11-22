//! National lists read from spreadsheets (Rust only): Poland's list of people and entities under sanctions (the
//! Ministry of the Interior and Administration's, uploaded by an administrator; owner's decision, 2026-11-22) and
//! Latvia's table of parties whose assets are frozen in Latvia (the Financial Intelligence Unit's, from the open data
//! portal; owner's decision, 2026-11-22).

use std::io::BufRead;

use super::parse::{clean, format_error, join, Builder, ParseError, ParsedSubject, SubjectType};
use super::spreadsheets::{excel_date, xlsx_rows};

pub const POLAND_PAGE: &str = "https://www.gov.pl/web/mswia/lista-osob-i-podmiotow-objetych-sankcjami";
pub const LATVIA_PAGE: &str = "https://sankcijas.fid.gov.lv/lv/iesaldetie-aktivi/sankciju-subjekti";

/// The index of the header cell that starts with name (trimmed, case ignored).
fn column(header: &[String], name: &str) -> Option<usize> {
    let name = name.to_lowercase();
    header.iter().position(|cell| cell.trim().to_lowercase().starts_with(&name))
}

/// Poland's list (XLSX, the table on the ministry's page): the sheet "osoby" (people) and "podmioty" (entities), a row
/// per party: its name (other names in brackets), identifying data (date of birth, PESEL, KRS, NIP, REGON, registered
/// office), the grounds, the measures applied, the date listed and the date removed. A removed party and the notes
/// after the table (rows starting "[1]") are left out; a row listed without a date is a format error.
pub fn parse_poland(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let mut out: Vec<ParsedSubject> = Vec::new();
    for (sheet, kind, name_header) in
        [("osoby", SubjectType::Individual, "Nazwisko i imię"), ("podmioty", SubjectType::Entity, "Nazwa podmiotu")]
    {
        let rows = xlsx_rows(&mut data.as_slice(), sheet)?;
        let header_at = rows
            .iter()
            .position(|row| column(row, name_header).is_some())
            .ok_or_else(|| format_error(format!("Poland's list has no header row ({name_header}, …) in the sheet {sheet}.")))?;
        let header = &rows[header_at];
        let required =
            |name: &str| column(header, name).ok_or_else(|| format_error(format!("Poland's list has no \"{name}\" column in {sheet}.")));
        let (name, identity, grounds, measures, listed, removed) = (
            required(name_header)?,
            required("Dane identyfikacyjne")?,
            required("Uzasadnienie")?,
            required("Zastosowane środki")?,
            required("Data umieszczenia")?,
            required("Data wykreślenia")?,
        );
        for row in &rows[header_at + 1..] {
            let cell = |i: usize| row.get(i).and_then(|v| clean(Some(v)));
            let Some(full_name) = cell(name) else {
                if row.iter().all(|v| clean(Some(v)).is_none()) {
                    continue;
                }
                return Err(format_error(format!("A row of Poland's list ({sheet}) has no name.")));
            };
            // The notes under the table: "[1] W rozumieniu rozporządzenia…".
            if full_name.starts_with('[') && cell(listed).is_none() {
                continue;
            }
            let Some(listed_on) = polish_date(cell(listed).as_deref()) else {
                return Err(format_error(format!("A party of Poland's list ({sheet}) has no date it was listed.")));
            };
            if cell(removed).is_some() {
                continue;
            }
            let (primary, others) = polish_names(&full_name);
            // Its kind and name; one shared with another party gets its identifying data (below).
            let id = format!("{}:{}", if kind == SubjectType::Individual { "P" } else { "E" }, primary.to_uppercase());
            let mut b = Builder::new(&id, kind);
            b.source_url = Some(POLAND_PAGE.to_string());
            b.listed_on = Some(listed_on);
            b.add_name(Some(&primary), false, true);
            for other in &others {
                b.add_name(Some(other), false, false);
            }
            // As written, its lines kept: the registered office ends where its register numbers start.
            let identity_text = row.get(identity).cloned().unwrap_or_default();
            for (kind, value) in polish_identifiers(&identity_text) {
                b.add_identifier(Some(kind), Some(&value), Some("PL"));
            }
            if kind == SubjectType::Individual {
                for date in polish_birth_dates(&identity_text) {
                    b.add_birth_date(Some(date));
                }
            } else if let Some(office) = registered_office(&identity_text) {
                b.add_address(Some(&office), None);
            }
            b.programmes = Some(polish_measures(cell(measures).as_deref()).to_string());
            b.remarks = cell(grounds);
            out.extend(b.build());
        }
    }
    // A name several parties share: each one's id gets what tells it apart, whatever the rows' order (its first
    // identifier, else its date of birth, else the date it was listed); parties still alike are a format error, never
    // one party.
    let mut counts: std::collections::HashMap<String, usize> = std::collections::HashMap::new();
    for party in &out {
        *counts.entry(party.external_id.clone()).or_default() += 1;
    }
    for party in out.iter_mut().filter(|p| counts[&p.external_id] > 1) {
        let apart = party
            .identifiers
            .first()
            .map(|i| format!("{} {}", i.kind, i.value))
            .or_else(|| party.birth_dates.first().cloned())
            .or_else(|| party.listed_on.map(|d| d.format("%Y-%m-%d").to_string()))
            .unwrap_or_default();
        party.external_id = format!("{} ({apart})", party.external_id);
    }
    let mut seen: std::collections::HashSet<&str> = std::collections::HashSet::new();
    // The refusal names no party: an upload's refusal never shows an entry's text.
    if out.iter().any(|p| !seen.insert(p.external_id.as_str())) {
        return Err(format_error("Two parties of Poland's list share a name and all their identifying data."));
    }
    Ok(out)
}

/// A date as Poland's list writes it: an Excel date (a number of days), or "27.10.2022".
fn polish_date(value: Option<&str>) -> Option<chrono::NaiveDate> {
    let value = value?.trim();
    excel_date(Some(value)).or_else(|| chrono::NaiveDate::parse_from_str(value.trim_end_matches(['.', ' ', 'r']), "%d.%m.%Y").ok())
}

/// What the measures applied come to, for a hit's programme: an asset freeze, or an entry ban only.
fn polish_measures(text: Option<&str>) -> &'static str {
    let text = text.unwrap_or_default().to_lowercase();
    if text.contains("zamroż") {
        "Poland's national list: asset freeze"
    } else if text.contains("niepożądany") {
        "Poland's national list: entry ban only (no asset freeze)"
    } else {
        "Poland's national list"
    }
}

/// A party's name and its other names, from a cell like "BELOV Alexey (BELOV Alexy)", "GO SPORT POLSKA Sp. z o.o.
/// (obecnie: GO SPORT POLSKA Sp. z o.o. w upadłości)" or "KARPOVICH Andrei (w zapisie także „KARPOWICZ Andriej” lub
/// „КАРПОВИЧ Андрей”)": the text before the first bracket, then each bracket's names (quoted ones, else those joined
/// by "lub"), a label before them ("obecnie:", "w języku rosyjskim:", "do 3.03.2022 r. –") dropped.
fn polish_names(cell: &str) -> (String, Vec<String>) {
    let text: String = cell.split_whitespace().collect::<Vec<_>>().join(" ");
    let primary = text.split('(').next().unwrap_or_default().trim().trim_end_matches([',', ' ']).to_string();
    // The brackets' contents, a nested one inside its outer one.
    let mut groups: Vec<String> = Vec::new();
    let (mut depth, mut current) = (0usize, String::new());
    for c in text.chars().skip_while(|&c| c != '(') {
        match c {
            '(' if depth == 0 => depth = 1,
            '(' => {
                depth += 1;
                current.push(c);
            }
            ')' if depth == 1 => {
                depth = 0;
                groups.push(std::mem::take(&mut current));
            }
            ')' if depth > 1 => {
                depth -= 1;
                current.push(c);
            }
            _ if depth > 0 => current.push(c),
            _ => {}
        }
    }
    if depth > 0 && !current.trim().is_empty() {
        groups.push(current);
    }
    let mut others: Vec<String> = Vec::new();
    for group in groups {
        let mut text = group.trim().to_string();
        // A label: words before a colon or a dash, with no quotes in them.
        for separator in [":", " – ", " - "] {
            if let Some((label, rest)) = text.split_once(separator) {
                if !label.contains(['„', '"', '”']) && label.chars().count() <= 60 && !rest.trim().is_empty() {
                    text = rest.trim().to_string();
                    break;
                }
            }
        }
        if let Some(rest) = text.strip_prefix("w zapisie także") {
            text = rest.trim().to_string();
        }
        let quoted: Vec<String> =
            text.split(['„', '”', '"', '“']).enumerate().filter(|(i, _)| i % 2 == 1).map(|(_, s)| s.to_string()).collect();
        let names = if quoted.iter().any(|q| !q.trim().is_empty()) { quoted } else { text.split(" lub ").map(str::to_string).collect() };
        for name in names {
            let name = name.trim().trim_matches([',', ' ', ';']).to_string();
            if !name.is_empty() && name != primary && !others.contains(&name) {
                others.push(name);
            }
        }
    }
    (primary, others)
}

/// Polish identifiers in a party's identifying data: PESEL, KRS, NIP and REGON numbers.
fn polish_identifiers(text: &str) -> Vec<(&'static str, String)> {
    static NUMBERS: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let numbers =
        NUMBERS.get_or_init(|| regex::Regex::new(r"(?i)\b(PESEL|KRS|NIP|REGON)\b[\s.:]*(?:nr\s*)?([0-9][0-9 -]{7,16}[0-9])").unwrap());
    let mut out: Vec<(&'static str, String)> = Vec::new();
    for found in numbers.captures_iter(text) {
        let kind = match found[1].to_uppercase().as_str() {
            "PESEL" => "PESEL",
            "KRS" => "KRS",
            "NIP" => "NIP",
            _ => "REGON",
        };
        let value: String = found[2].chars().filter(char::is_ascii_digit).collect();
        if !out.iter().any(|(k, v)| *k == kind && *v == value) {
            out.push((kind, value));
        }
    }
    out
}

/// A person's dates of birth in the identifying data: "urodzony 5 października 1973 r." (1973-10-05), or a year
/// alone ("urodzona w 1965 r.").
fn polish_birth_dates(text: &str) -> Vec<String> {
    const MONTHS: [&str; 12] = [
        "stycznia",
        "lutego",
        "marca",
        "kwietnia",
        "maja",
        "czerwca",
        "lipca",
        "sierpnia",
        "września",
        "października",
        "listopada",
        "grudnia",
    ];
    static FULL: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    static YEAR: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let full = FULL.get_or_init(|| regex::Regex::new(r"(?i)urodzon[ya]\s+(\d{1,2})\s+(\p{L}+)\s+(\d{4})").unwrap());
    let year = YEAR.get_or_init(|| regex::Regex::new(r"(?i)urodzon[ya]\s+w\s+(\d{4})\s*r").unwrap());
    let mut out: Vec<String> = Vec::new();
    for found in full.captures_iter(text) {
        let month = MONTHS.iter().position(|m| m.eq_ignore_ascii_case(&found[2].to_lowercase()));
        let date = month.and_then(|m| chrono::NaiveDate::from_ymd_opt(found[3].parse().ok()?, m as u32 + 1, found[1].parse().ok()?));
        if let Some(date) = date {
            out.push(date.format("%Y-%m-%d").to_string());
        }
    }
    if out.is_empty() {
        out.extend(year.captures_iter(text).map(|found| found[1].to_string()));
    }
    out
}

/// An entity's registered office: the lines after "siedziba:" up to its register numbers.
fn registered_office(text: &str) -> Option<String> {
    let (_, rest) = text.split_once("siedziba:")?;
    let lines: Vec<&str> = rest
        .lines()
        .map(str::trim)
        .take_while(|line| {
            let lower = line.to_lowercase();
            !["krs", "nip", "regon", "rejestr", "numer"].iter().any(|w| lower.starts_with(w) || lower.starts_with(&format!("nr {w}")))
        })
        .filter(|line| !line.is_empty())
        .collect();
    clean(Some(&lines.join(", ")))
}

/// Latvia's table of parties whose assets are frozen in Latvia (XLSX, the sheet "Dati", from the open data portal): a
/// row per frozen party and the listed person it's frozen for: (1) the party, (2) its date of birth or registration
/// number, (3) its nationality or country of registration, (4) what's frozen, (5) why (the listed person's control or
/// ownership, or listed itself), (6) the listed person, (7) since when, (8) the EU regulation, (10) when the FIU first
/// published it. A party's rows (one per listed person) are one party. A row without its party is a format error.
pub fn parse_latvia(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let rows = xlsx_rows(input, "Dati")?;
    let header_at = rows
        .iter()
        .position(|row| column(row, "1. Sankciju subjekts").is_some())
        .ok_or_else(|| format_error("No header row (1. Sankciju subjekts, …) in Latvia's table of frozen assets."))?;
    let header = &rows[header_at];
    let numbered = |n: usize| column(header, &format!("{n}. ")).ok_or_else(|| format_error(format!("Latvia's table has no column {n}.")));
    let (party, born_or_number, country, assets, basis, listed_person, since, act) =
        (numbered(1)?, numbered(2)?, numbered(3)?, numbered(4)?, numbered(5)?, numbered(6)?, numbered(7)?, numbered(8)?);
    let published = column(header, "10. ");

    struct Party {
        id: String,
        name: String,
        kind: SubjectType,
        number: Option<String>,
        birth: Option<String>,
        country: Option<String>,
        reasons: Vec<String>,
        acts: Vec<String>,
        assets: Vec<String>,
        first_published: Option<chrono::NaiveDate>,
    }
    let mut parties: Vec<Party> = Vec::new();
    for row in &rows[header_at + 1..] {
        let cell = |i: usize| row.get(i).and_then(|v| clean(Some(&v.split_whitespace().collect::<Vec<_>>().join(" "))));
        let Some(name) = cell(party) else {
            if row.iter().all(|v| clean(Some(v)).is_none()) {
                continue;
            }
            return Err(format_error("A row of Latvia's table has no party."));
        };
        let second = cell(born_or_number);
        let birth = second.as_deref().and_then(|v| chrono::NaiveDate::parse_from_str(v.trim_end_matches('.'), "%d.%m.%Y").ok());
        let kind = if birth.is_some() { SubjectType::Individual } else { SubjectType::Entity };
        let id = match (&birth, &second) {
            (Some(date), _) => format!("{name} {}", date.format("%Y-%m-%d")),
            (None, Some(number)) => number.clone(),
            (None, None) => name.clone(),
        };
        let index = match parties.iter().position(|p| p.id == id) {
            Some(i) => i,
            None => {
                parties.push(Party {
                    id: id.clone(),
                    name: name.clone(),
                    kind,
                    number: if birth.is_none() { second.clone() } else { None },
                    birth: birth.map(|d| d.format("%Y-%m-%d").to_string()),
                    country: cell(country),
                    reasons: Vec::new(),
                    acts: Vec::new(),
                    assets: Vec::new(),
                    first_published: None,
                });
                parties.len() - 1
            }
        };
        let p = &mut parties[index];
        let why = latvian_basis(cell(basis).as_deref().unwrap_or_default());
        let reason = match (cell(listed_person), cell(since)) {
            (Some(person), Some(since)) => format!("{why}: {person} (listed from {})", since.trim_end_matches('.')),
            (Some(person), None) => format!("{why}: {person}"),
            _ => why,
        };
        if !p.reasons.contains(&reason) {
            p.reasons.push(reason);
        }
        for number in cell(act).unwrap_or_default().split([' ', ';', ',']).filter(|a| !a.is_empty()) {
            let regulation = eu_regulation(number);
            if !p.acts.contains(&regulation) {
                p.acts.push(regulation);
            }
        }
        if let Some(frozen) = cell(assets) {
            if !p.assets.contains(&frozen) {
                p.assets.push(frozen);
            }
        }
        let first = published.and_then(cell).and_then(|v| chrono::NaiveDate::parse_from_str(v.trim_end_matches('.'), "%d.%m.%Y").ok());
        p.first_published = match (p.first_published, first) {
            (Some(a), Some(b)) => Some(a.min(b)),
            (a, b) => a.or(b),
        };
    }

    let mut out = Vec::new();
    for mut p in parties {
        // In a fixed order, whatever the rows' order: a file that only reorders them is the same list.
        p.reasons.sort();
        p.acts.sort();
        p.assets.sort();
        let mut b = Builder::new(&p.id, p.kind);
        b.source_url = Some(LATVIA_PAGE.to_string());
        b.add_name(Some(&p.name), false, true);
        if let Some(number) = &p.number {
            b.add_identifier(Some("Registration number"), Some(number), p.country.as_deref());
        }
        b.add_birth_date(p.birth.clone());
        // A company's country of registration, as its address; a person's nationality isn't one.
        if p.kind == SubjectType::Entity {
            if let Some(country) = &p.country {
                b.add_address(Some(country), latvian_country_code(country));
            }
        }
        // Why and for whom, then the regulation: the programme is what a hit and the list's browsing show.
        b.programmes = join("; ", &p.reasons.iter().chain(&p.acts).cloned().map(Some).collect::<Vec<_>>());
        b.remarks = join("; ", &p.assets.iter().cloned().map(Some).collect::<Vec<_>>()).map(|a| format!("Frozen in Latvia: {a}"));
        b.listed_on = p.first_published;
        out.extend(b.build());
    }
    Ok(out)
}

/// Why a party's assets are frozen, in English: the listed person's control or ownership, or listed itself; any other
/// wording as the FIU writes it.
fn latvian_basis(text: &str) -> String {
    let lower = text.to_lowercase();
    let owned = lower.contains("īpašumtiesības");
    let controlled = lower.contains("kontrole");
    match (owned, controlled) {
        (true, true) => "Owned and controlled by a listed person".to_string(),
        (true, false) => "Owned by a listed person".to_string(),
        (false, true) => "Controlled by a listed person".to_string(),
        _ if lower.contains("iekļauta sankciju sarakstā") => "Listed itself".to_string(),
        _ => text.to_string(),
    }
}

/// The EU regulation a number names ("269/2014"), by its title; another number as written.
fn eu_regulation(number: &str) -> String {
    match number.trim() {
        "269/2014" => "Council Regulation (EU) No 269/2014 (Ukraine's territorial integrity)".to_string(),
        "208/2014" => "Council Regulation (EU) No 208/2014 (misappropriation of Ukrainian state funds)".to_string(),
        "765/2006" => "Council Regulation (EC) No 765/2006 (Belarus)".to_string(),
        other => format!("Regulation {other}"),
    }
}

/// The ISO code of a country as Latvia's table names it, for the countries it lists; None for another.
fn latvian_country_code(name: &str) -> Option<&'static str> {
    Some(match name.trim() {
        "Latvija" => "LV",
        "Krievija" => "RU",
        "Kipra" => "CY",
        "Britu Virdžīnu salas" => "VG",
        "Baltkrievija" => "BY",
        "Apvienotā Karaliste" => "GB",
        "Igaunija" => "EE",
        "Lietuva" => "LT",
        "Šveice" => "CH",
        "Nīderlande" => "NL",
        "Vācija" => "DE",
        "Luksemburga" => "LU",
        "Malta" => "MT",
        "Seišelas" => "SC",
        "Beliza" => "BZ",
        "Panama" => "PA",
        "Apvienotie Arābu Emirāti" => "AE",
        "Kazahstāna" => "KZ",
        "Ukraina" => "UA",
        "Azerbaidžāna" => "AZ",
        "Beļģija" => "BE",
        "Dānija" => "DK",
        "Gruzija" => "GE",
        "Honkonga" => "HK",
        "Jaunzēlande" => "NZ",
        "Kirasao" => "CW",
        "Polija" => "PL",
        "Rumānija" => "RO",
        "Seišelu Salas" | "Seišelu salas" => "SC",
        "Serbija" => "RS",
        "Singapūra" => "SG",
        "Somija" => "FI",
        "Spānija" => "ES",
        "Čehija" => "CZ",
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    /// A workbook of these sheets (name, rows of cells), each cell an inline string.
    fn workbook(sheets: &[(&str, Vec<Vec<&str>>)]) -> Vec<u8> {
        const MAIN: &str = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        let escape = |t: &str| t.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;");
        let mut buffer = std::io::Cursor::new(Vec::new());
        {
            let mut zip = zip::ZipWriter::new(&mut buffer);
            let entries: String = sheets
                .iter()
                .enumerate()
                .map(|(i, (name, _))| format!(r#"<sheet name="{name}" sheetId="{}" r:id="rId{}"/>"#, i + 1, i + 1))
                .collect();
            let relationships: String = (1..=sheets.len())
                .map(|i| format!(r#"<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>"#))
                .collect();
            let mut files = vec![
                (
                    "xl/workbook.xml".to_string(),
                    format!(
                        r#"<workbook xmlns="{MAIN}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{entries}</sheets></workbook>"#
                    ),
                ),
                (
                    "xl/_rels/workbook.xml.rels".to_string(),
                    format!(
                        r#"<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}</Relationships>"#
                    ),
                ),
                ("xl/sharedStrings.xml".to_string(), format!(r#"<sst xmlns="{MAIN}"/>"#)),
            ];
            for (i, (_, rows)) in sheets.iter().enumerate() {
                let body: String = rows
                    .iter()
                    .enumerate()
                    .map(|(r, row)| {
                        let cells: String = row
                            .iter()
                            .enumerate()
                            .map(|(c, t)| {
                                format!(r#"<c r="{}{}" t="inlineStr"><is><t>{}</t></is></c>"#, (b'A' + c as u8) as char, r + 1, escape(t))
                            })
                            .collect();
                        format!("<row>{cells}</row>")
                    })
                    .collect();
                files.push((
                    format!("xl/worksheets/sheet{}.xml", i + 1),
                    format!(r#"<worksheet xmlns="{MAIN}"><sheetData>{body}</sheetData></worksheet>"#),
                ));
            }
            for (name, content) in files {
                zip.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
                zip.write_all(content.as_bytes()).unwrap();
            }
            zip.finish().unwrap();
        }
        buffer.into_inner()
    }

    #[test]
    fn polands_list_is_read_with_its_other_names_identifiers_and_measures() {
        let people_header = vec![
            "Nazwisko i imię",
            "Dane identyfikacyjne osoby",
            "Uzasadnienie wpisu na listę",
            "Zastosowane środki sankcyjne",
            "Data umieszczenia na liście",
            "Data wykreślenia z listy ",
        ];
        let entities_header = vec![
            "Nazwa podmiotu",
            "Dane identyfikacyjne podmiotu",
            "Uzasadnienie wpisu na listę",
            "Zastosowane środki sankcyjne",
            "Data umieszczenia na liście",
            "Data wykreślenia z listy ",
        ];
        let file = workbook(&[
            (
                "osoby",
                vec![
                    people_header,
                    vec![
                        "KOWALSKY Fikcyjny\n(w zapisie także „KOWALSKI Fikcyjny” lub „КОВАЛЬСКИЙ Фиктивный”)",
                        "urodzony 5 października 1973 r. w Wilnie\nnr PESEL: 73100512345",
                        "Fikcyjny powód.",
                        "a. zamrożenie wszystkich środków finansowych",
                        "44861",
                        "",
                    ],
                    vec![
                        "ZAKAZANY Wjazd",
                        "urodzona w 1965 r.",
                        "Powód.",
                        "wpis do wykazu cudzoziemców, których pobyt na terytorium Rzeczypospolitej Polskiej jest niepożądany",
                        "44677",
                        "",
                    ],
                    vec!["WYKREŚLONY Ktoś", "urodzony 1 stycznia 1970 r.", "Powód.", "a. zamrożenie", "44677", "45000"],
                    vec!["[1] W rozumieniu rozporządzenia Rady (UE) nr 269/2014"],
                ],
            ),
            (
                "podmioty",
                vec![
                    entities_header,
                    vec![
                        "FIKCJA TRADE Sp.  z o.o.\n(obecnie: NOWA FIKCJA Sp. z o.o.)",
                        "siedziba:\nul. Fikcyjna 6 lok. 23\n03-543 Warszawa\n\nNr KRS 0000312056\nNIP 522-289-40-64",
                        "Powód.",
                        "a. zamrożenie wszystkich środków finansowych",
                        "44677",
                        "",
                    ],
                ],
            ),
        ]);
        let parties = parse_poland(&mut file.as_slice()).unwrap();
        assert_eq!(parties.len(), 3, "the removed party and the note left out");
        let person = &parties[0];
        assert_eq!(person.primary_name, "KOWALSKY Fikcyjny");
        assert_eq!(
            person.names.iter().map(|n| n.full_name.as_str()).collect::<Vec<_>>(),
            ["KOWALSKY Fikcyjny", "KOWALSKI Fikcyjny", "КОВАЛЬСКИЙ Фиктивный"]
        );
        assert_eq!(person.birth_dates, ["1973-10-05"]);
        assert_eq!((person.identifiers[0].kind.as_str(), person.identifiers[0].value.as_str()), ("PESEL", "73100512345"));
        assert_eq!(person.listed_on, chrono::NaiveDate::from_ymd_opt(2022, 10, 27));
        assert_eq!(person.programmes.as_deref(), Some("Poland's national list: asset freeze"));
        let banned = &parties[1];
        assert_eq!(
            (banned.birth_dates.as_slice(), banned.programmes.as_deref()),
            (["1965".to_string()].as_slice(), Some("Poland's national list: entry ban only (no asset freeze)"))
        );
        let entity = &parties[2];
        assert_eq!((entity.kind, entity.names.len()), (SubjectType::Entity, 2));
        assert_eq!(entity.names[1].full_name, "NOWA FIKCJA Sp. z o.o.");
        assert_eq!(entity.addresses[0].raw, "ul. Fikcyjna 6 lok. 23, 03-543 Warszawa");
        let ids: Vec<(&str, &str)> = entity.identifiers.iter().map(|i| (i.kind.as_str(), i.value.as_str())).collect();
        assert_eq!(ids, [("KRS", "0000312056"), ("NIP", "5222894064")]);

        // Parties sharing a name are told apart by their identifying data, whatever their order; parties alike in
        // every way are a format error, never one party.
        let header =
            vec!["Nazwisko i imię", "Dane identyfikacyjne", "Uzasadnienie", "Zastosowane środki", "Data umieszczenia", "Data wykreślenia"];
        let namesakes = |second_born: &'static str| {
            workbook(&[
                (
                    "osoby",
                    vec![
                        header.clone(),
                        vec!["IMIENNIK Jan", "urodzony 1 stycznia 1970 r.", "", "a. zamrożenie", "44677", ""],
                        vec!["IMIENNIK Jan", second_born, "", "a. zamrożenie", "44677", ""],
                    ],
                ),
                (
                    "podmioty",
                    vec![vec![
                        "Nazwa podmiotu",
                        "Dane identyfikacyjne",
                        "Uzasadnienie",
                        "Zastosowane środki",
                        "Data umieszczenia",
                        "Data wykreślenia",
                    ]],
                ),
            ])
        };
        let ids: Vec<String> =
            parse_poland(&mut namesakes("urodzony 2 lutego 1980 r.").as_slice()).unwrap().into_iter().map(|p| p.external_id).collect();
        assert_eq!(ids, ["P:IMIENNIK JAN (1970-01-01)", "P:IMIENNIK JAN (1980-02-02)"]);
        assert!(parse_poland(&mut namesakes("urodzony 1 stycznia 1970 r.").as_slice()).is_err());

        // A party listed without a date, and a file without the sheets, are format errors.
        let undated = workbook(&[
            (
                "osoby",
                vec![
                    vec![
                        "Nazwisko i imię",
                        "Dane identyfikacyjne",
                        "Uzasadnienie",
                        "Zastosowane środki",
                        "Data umieszczenia",
                        "Data wykreślenia",
                    ],
                    vec!["BEZ Daty", "", "", "", "", ""],
                ],
            ),
            ("podmioty", vec![]),
        ]);
        assert!(parse_poland(&mut undated.as_slice()).is_err());
        assert!(parse_poland(&mut workbook(&[("Arkusz1", vec![])]).as_slice()).is_err());
    }

    #[test]
    fn latvias_table_is_read_a_party_once_with_every_listed_person_behind_it() {
        let header = vec![
            "1. Sankciju subjekts, kā līdzekļi vai saimnieciskie resursi ir iesaldēti (vārds, uzvārds / nosaukums)",
            "2. Dzimšanas datums / reģistrācijas Nr.",
            "3. Valstspiederība / reģistrācijas valsts",
            "4. Iesaldēto līdzekļu vai saimniecisko resursu veids",
            "5. Līdzekļu vai saimniecisko resursu iesaldēšanas pamatojums",
            "6. Sankciju sarakstā iekļautās personas vārds, uzvārds vai nosaukums",
            "7. Datums, no kura spēkā ir sankcijas, pret 6. kolonnā norādīto personu",
            "8. Normatīvais akts, saskaņā ar ko veikta iesaldēšana",
            "9. FID pārbaudīts*",
            "10. Datums, kad FID pirmo reizi publicējis informāciju par sankciju subjektu",
        ];
        let rows = vec![
            header,
            vec![
                "\"Fikcija Baltija SIA\"",
                "50003698011",
                "Latvija",
                "Līdzekļi; kapitāldaļas",
                "Sankciju sarakstā iekļautas personas īpašumtiesības",
                "Ivan FIKTIVNY",
                "25.02.2022.",
                "269/2014",
                "Pārbaudīts",
                "17.04.2024.",
            ],
            vec![
                "\"Fikcija Baltija SIA\"",
                "50003698011",
                "Latvija",
                "Līdzekļi",
                "Sankciju sarakstā iekļautas personas kontrole",
                "Petr FIKTIVNY",
                "28.02.2022.",
                "269/2014\n208/2014",
                "Nē",
                "19.11.2026.",
            ],
            vec![
                "Ivan FIKTIVNY",
                "16.03.1955.",
                "Latvija, Krievija",
                "Līdzekļi",
                "Persona iekļauta sankciju sarakstā",
                "Ivan FIKTIVNY",
                "28.02.2022.",
                "269/2014",
                "Pārbaudīts",
                "17.04.2024.",
            ],
        ];
        let file = workbook(&[("Dati", rows.clone())]);
        let parties = parse_latvia(&mut file.as_slice()).unwrap();
        assert_eq!(parties.len(), 2);
        let company = &parties[0];
        assert_eq!((company.kind, company.external_id.as_str()), (SubjectType::Entity, "50003698011"));
        assert_eq!((company.identifiers[0].kind.as_str(), company.identifiers[0].value.as_str()), ("Registration number", "50003698011"));
        assert_eq!((company.addresses[0].raw.as_str(), company.addresses[0].country_code.as_deref()), ("Latvija", Some("LV")));
        // Why and for whom it's frozen, where a hit shows it: the programme.
        let programme = company.programmes.as_deref().unwrap();
        assert!(programme.starts_with("Controlled by a listed person: Petr FIKTIVNY (listed from 28.02.2022); Owned by a listed person: Ivan FIKTIVNY (listed from 25.02.2022); "), "{programme}");
        assert!(programme.contains("208/2014"), "{programme}");
        assert_eq!(company.remarks.as_deref(), Some("Frozen in Latvia: Līdzekļi; Līdzekļi; kapitāldaļas"));
        // The same rows in another order are the same parties.
        let mut reordered_rows = rows.clone();
        reordered_rows.swap(1, 2);
        let reordered = parse_latvia(&mut workbook(&[("Dati", reordered_rows)]).as_slice()).unwrap();
        assert_eq!(
            (reordered[0].programmes.as_deref(), reordered[0].remarks.as_deref()),
            (company.programmes.as_deref(), company.remarks.as_deref())
        );
        assert_eq!(company.listed_on, chrono::NaiveDate::from_ymd_opt(2024, 4, 17));
        let person = &parties[1];
        assert_eq!((person.kind, person.birth_dates.as_slice()), (SubjectType::Individual, ["1955-03-16".to_string()].as_slice()));
        assert!(person.addresses.is_empty(), "a nationality isn't an address");

        let headless = workbook(&[("Dati", vec![vec!["Nosaukums"], vec!["X"]])]);
        assert!(parse_latvia(&mut headless.as_slice()).is_err());
    }
}
