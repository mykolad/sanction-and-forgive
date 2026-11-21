//! Ukraine's State Register of Sanctions (the NSDC's, https://drs.nsdc.gov.ua/), uploaded by an administrator
//! (FeedStatus::Upload; owner's decision, 2026-11-21): the register's subjects' exports ("Завантаження суб'єктів"), one
//! tab-separated file each for people, companies and vessels, each row a subject with its sanctions in force. A file is
//! told by its columns; the upload takes all three. Rust only.

use std::io::BufRead;

use super::manual::delimited_rows;
use super::parse::{birth_date_text, clean, format_error, join, parse_date, Builder, ParseError, ParsedSubject, SubjectType};

pub const NSDC_PAGE: &str = "https://drs.nsdc.gov.ua/";

/// The labels in additional_info whose value is an address (where a company is registered or located, where a person
/// lives).
const ADDRESS_LABELS: [&str; 4] = ["місце розташування", "місце реєстрації", "місце проживання", "адреса"];

/// One of the register's subjects' exports: people (it has birthdate), companies (country and tax_id) or vessels
/// (country without tax_id). A file with other columns, a row without its sid or name, or a status the register doesn't
/// use is a format error; a subject whose status isn't "active" is left out.
pub fn parse_nsdc(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let text = String::from_utf8(data).map_err(|_| format_error("The register's file isn't UTF-8 text."))?;
    let mut header: Option<Vec<String>> = None;
    let mut kind = SubjectType::Unknown;
    let mut out = Vec::new();
    delimited_rows(&text, '\t', |row| {
        if row.iter().all(|v| v.trim().is_empty()) {
            return Ok(());
        }
        let Some(columns) = &header else {
            let names: Vec<String> = row.iter().map(|c| c.trim().to_lowercase()).collect();
            kind = header_kind(&names)?;
            header = Some(names);
            return Ok(());
        };
        let cell = |name: &str| columns.iter().position(|c| c == name).and_then(|i| row.get(i)).and_then(|v| clean(Some(v)));
        let sid = cell("sid").ok_or_else(|| format_error("A row of the register's file has no sid."))?;
        match cell("status").as_deref() {
            Some("active") => {}
            Some("inactive" | "excluded") => return Ok(()),
            other => return Err(format_error(format!("Subject {sid} has a status the register's files don't use: {other:?}."))),
        }
        let name = cell("name").ok_or_else(|| format_error(format!("Subject {sid} has no name.")))?;
        let mut b = Builder::new(&sid, kind);
        b.source_url = Some(NSDC_PAGE.to_string());
        b.add_name(Some(&name), false, true);
        b.add_name(cell("translit_name").as_deref(), false, false);
        for alias in cell("aliases").unwrap_or_default().split(';') {
            b.add_name(Some(alias), false, false);
        }
        b.programmes = cell("last_action_decree").map(|d| format!("Decree of the President of Ukraine {d}"));
        b.listed_on = parse_date(cell("decree_date").as_deref(), "%Y-%m-%d");
        let term = join(", until ", &[cell("sanctions_term"), cell("sanctions_end_date")]);
        let information = cell("additional_info");
        b.remarks = join("; ", &[term.map(|t| format!("Sanctions: {t}")), information.clone()]);
        for date in cell("birthdate").unwrap_or_default().split(',') {
            b.add_birth_date(birth_date_text(date));
        }
        for field in ["identity_docs", "reg_id", "tax_id"] {
            for document in labelled(&cell(field).unwrap_or_default()) {
                let Some((label, rest)) = document.split_once(':') else { continue };
                let (value, country) = value_and_country(rest);
                if label.contains("(IMO)") {
                    b.add_imo_number(Some(&value), label.trim());
                } else {
                    b.add_identifier(Some(&short_label(label)), Some(&value), country.as_deref());
                }
            }
        }
        let mut addressed = false;
        for item in labelled(&information.unwrap_or_default()) {
            if let Some((label, value)) = item.split_once(':') {
                if ADDRESS_LABELS.iter().any(|a| label.trim().to_lowercase().starts_with(a)) {
                    b.add_address(Some(value), None);
                    addressed = true;
                }
            }
        }
        // A company's or a vessel's country, where no address is given.
        if !addressed && kind != SubjectType::Individual {
            for country in cell("country").unwrap_or_default().split(',') {
                b.add_address(Some(country), None);
            }
        }
        out.extend(b.build());
        Ok(())
    })?;
    if header.is_none() {
        return Err(format_error("The register's file is empty."));
    }
    Ok(out)
}

/// A subjects' export's kind from its columns: people have birthdate, companies tax_id, vessels country and reg_id.
fn header_kind(names: &[String]) -> Result<SubjectType, ParseError> {
    let has = |n: &str| names.iter().any(|c| c == n);
    if !has("sid") || !has("name") || !has("status") {
        return Err(format_error("No header row (sid, name, …, status, …) in the register's file: is it a subjects' export?"));
    }
    if has("birthdate") {
        Ok(SubjectType::Individual)
    } else if has("tax_id") {
        Ok(SubjectType::Entity)
    } else if has("country") && has("reg_id") {
        Ok(SubjectType::Vessel)
    } else {
        Err(format_error("The register's file has neither a person's, a company's nor a vessel's columns."))
    }
}

/// Which of the register's exports a file is, from its header row alone (whatever its rows hold); None if it isn't one.
pub fn file_kind(file: &[u8]) -> Option<SubjectType> {
    let end = file.iter().position(|b| *b == b'\n').unwrap_or(file.len());
    let line = std::str::from_utf8(&file[..end]).ok()?;
    let names: Vec<String> =
        line.trim_start_matches('\u{FEFF}').split('\t').map(|c| c.trim().trim_matches('"').trim().to_lowercase()).collect();
    header_kind(&names).ok()
}

/// The register's "label:value; label:value" lists, each item whole: a semicolon starts an item only where a label
/// (words before a colon) follows, so a value's own semicolons stay in it.
fn labelled(text: &str) -> Vec<String> {
    let mut items: Vec<String> = Vec::new();
    for part in text.split(';') {
        let starts_item = part.split_once(':').is_some_and(|(label, _)| {
            let label = label.trim();
            !label.is_empty() && label.chars().count() <= 160 && !label.contains(['[', ']'])
        });
        match items.last_mut() {
            Some(last) if !starts_item => {
                last.push(';');
                last.push_str(part);
            }
            _ => items.push(part.trim().to_string()),
        }
    }
    items.into_iter().filter(|i| !i.is_empty()).collect()
}

/// A document's kind as shown: a long label that names it in brackets at its end is that name ("Відомості згідно з
/// Єдиним державним реєстром юридичних осіб … (основний державний реєстраційний номер)": "основний державний
/// реєстраційний номер").
fn short_label(label: &str) -> String {
    let label = label.trim();
    if label.chars().count() > 60 && label.ends_with(')') {
        if let Some(open) = label.rfind('(') {
            let inner = label[open + 1..label.len() - 1].trim();
            if !inner.is_empty() {
                return inner.to_string();
            }
        }
    }
    label.to_string()
}

/// A document's value and the country in square brackets after it ("ВС757129[Україна](виданий …)").
fn value_and_country(text: &str) -> (String, Option<String>) {
    let value_end = text.find(['[', '(']).unwrap_or(text.len());
    let value = text[..value_end].trim().to_string();
    let country = text[value_end..]
        .strip_prefix('[')
        .and_then(|rest| rest.split_once(']'))
        .map(|(country, _)| country.trim().to_string())
        .filter(|c| !c.is_empty());
    (value, country)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_registers_people_companies_and_vessels_are_read() {
        let people = "sid\tname\ttranslit_name\taliases\tstatus\tbirthdate\tcitizenship\tidentity_docs\treg_id\ttax_id\tadditional_info\tactive_sanctions\tsanctions_term\tsanctions_end_date\tlast_action_type\tlast_action_decree\tdecree_date\tdecree_appendix\tposition_in_appendix\n\
10481\tЖуравльов Олексій Олександрович\tZhuravlov Oleksii Oleksandrovych\tZHURAVLEV ALEKSEI; Журавлев Алексей Александрович\tactive\t1962-06-30,1963\tРосійська Федерація\tПаспорт громадянина:ВС757129[Україна](виданий 1997 р.)\t\tПодатковий номер:151606455563[Російська Федерація]\tЗайнятість:депутат; Місце проживання:м. Воронеж; вул. Мира\t[1] блокування активів\tДесять років\t2036-11-16\tЗастосування санкцій\t1118/2026\t2026-11-16\t1\t7\n\
10482\tGone\t\t\tinactive\t\t\t\t\t\t\t\t\t\t\t\t\t\t\n";
        let subjects = parse_nsdc(&mut people.as_bytes()).unwrap();
        assert_eq!(subjects.len(), 1);
        let s = &subjects[0];
        assert_eq!((s.kind, s.primary_name.as_str(), s.names.len()), (SubjectType::Individual, "Журавльов Олексій Олександрович", 4));
        assert_eq!(s.birth_dates, ["1962-06-30", "1963"]);
        let ids: Vec<(&str, &str, Option<&str>)> =
            s.identifiers.iter().map(|i| (i.kind.as_str(), i.value.as_str(), i.country.as_deref())).collect();
        assert_eq!(
            ids,
            [("Паспорт громадянина", "ВС757129", Some("Україна")), ("Податковий номер", "151606455563", Some("Російська Федерація"))]
        );
        assert_eq!(s.addresses[0].raw, "м. Воронеж; вул. Мира");
        assert_eq!(s.programmes.as_deref(), Some("Decree of the President of Ukraine 1118/2026"));

        let vessels = "sid\tname\ttranslit_name\taliases\tstatus\tcountry\treg_id\tadditional_info\tactive_sanctions\tsanctions_term\tsanctions_end_date\tlast_action_type\tlast_action_decree\tdecree_date\tdecree_appendix\tposition_in_appendix\n\
34090\tENISEY\tENISEY\t\tactive\tРосійська Федерація\tІдентифікатори Морської рухомої служби (MMSI):273212240; Судно з ідентифікаційним номером Міжнародної морської організації (IMO):9079169\tНайменування судновласників:TK NORD PROJECT LLC\t\tДесять років\t2036-11-16\tЗастосування санкцій\t1118/2026\t2026-11-16\t1\t1\n";
        let ships = parse_nsdc(&mut vessels.as_bytes()).unwrap();
        assert_eq!(ships[0].kind, SubjectType::Vessel);
        assert!(ships[0].identifiers.iter().any(|i| i.kind == "IMO number" && i.value == "9079169"));
        assert_eq!(ships[0].addresses[0].raw, "Російська Федерація");

        assert!(parse_nsdc(&mut "id\taction_type\n1\tЗастосування санкцій\n".as_bytes()).is_err());
        // A file's kind is its header's, even when none of its rows is still active.
        let header_only = people.lines().next().unwrap();
        assert_eq!(file_kind(header_only.as_bytes()), Some(SubjectType::Individual));
        assert_eq!(file_kind(vessels.as_bytes()), Some(SubjectType::Vessel));
        assert_eq!(file_kind(b"id\taction_type\n"), None);
        assert!(parse_nsdc(&mut people.replace("inactive", "weird").as_bytes()).is_err());
    }
}
