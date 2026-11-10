//! One parser per feed format (IListParser in the reference; ports/go/lists/parsers.go): a file reduced to subjects.

use std::collections::{HashMap, HashSet};
use std::io::BufRead;

use regex::Regex;
use serde_json::Value;

use super::parse::{
    eq, format_error, join, parse_date, records, Builder, Element, OptionalElement, ParseError, ParsedSubject, SubjectType,
};

pub type Parser = fn(&mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError>;

/// The parser for a catalogued feed's code.
pub fn parser_for(code: &str) -> Option<Parser> {
    Some(match code {
        "eu" => parse_eu,
        "ofac-sdn" | "ofac-cons" => parse_ofac,
        "un" => parse_un,
        "uk" => parse_uk,
        "us-export" => parse_us_export,
        "ch-seco" => parse_seco,
        "ca-sema" => parse_canada,
        "ua-sfms" => parse_sfms,
        _ => return None,
    })
}

fn s(value: &str) -> Option<String> {
    Some(value.to_string())
}

fn parse_eu(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut out = Vec::new();
    records(reader, "export", 1, |entity| {
        if entity.name != "sanctionEntity" {
            return Ok(());
        }
        let code = entity.el("subjectType").attr("code");
        let kind = if eq(&code, "person") {
            SubjectType::Individual
        } else if eq(&code, "enterprise") {
            SubjectType::Entity
        } else {
            SubjectType::Unknown
        };
        let id = entity.attr("logicalId").ok_or_else(|| format_error("An EU sanctionEntity has no logicalId."))?;
        let regulations: Vec<&Element> = entity.els("regulation").collect();
        let mut programmes = Vec::new();
        let mut seen = HashSet::new();
        for r in &regulations {
            let p = r.attr("programme");
            if seen.insert(p.clone().unwrap_or_else(|| "\0".into())) {
                programmes.push(p);
            }
        }
        let mut b = Builder::new(&id, kind);
        b.programmes = join(", ", &programmes);
        b.listed_on = parse_date(entity.attr("designationDate").as_deref(), "%Y-%m-%d");
        b.remarks = join(" ", &entity.els("remark").map(|r| s(r.value())).collect::<Vec<_>>());
        b.source_url = regulations.iter().find_map(|r| r.val("publicationUrl"));
        for alias in entity.els("nameAlias") {
            let name =
                alias.attr("wholeName").or_else(|| join(" ", &[alias.attr("firstName"), alias.attr("middleName"), alias.attr("lastName")]));
            b.add_name(name.as_deref(), eq(&alias.attr("strong"), "false"), false);
        }
        for a in entity.els("address") {
            let raw = join(
                ", ",
                &[
                    a.attr("street"),
                    a.attr("poBox"),
                    a.attr("zipCode"),
                    a.attr("city"),
                    a.attr("place"),
                    a.attr("region"),
                    a.attr("countryDescription"),
                ],
            );
            b.add_address(raw.as_deref(), a.attr("countryIso2Code").as_deref());
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

fn parse_ofac(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut out = Vec::new();
    records(reader, "sanctionsData", 2, |entity| {
        if entity.name != "entity" {
            return Ok(());
        }
        let t = entity.el("generalInfo").val("entityType");
        let kind = match t.as_deref() {
            Some("Individual") => SubjectType::Individual,
            Some("Entity") => SubjectType::Entity,
            Some("Vessel") | Some("Aircraft") => return Ok(()),
            _ => SubjectType::Unknown,
        };
        let id = entity.attr("id").ok_or_else(|| format_error("An OFAC entity has no id."))?;
        let mut b = Builder::new(&id, kind);
        let mut programmes = Vec::new();
        let mut seen = HashSet::new();
        for p in entity.el("sanctionsPrograms").els("sanctionsProgram") {
            if seen.insert(p.value().to_string()) {
                programmes.push(s(p.value()));
            }
        }
        b.programmes = join(", ", &programmes);
        for list in entity.el("sanctionsLists").els("sanctionsList") {
            if let Some(date) = parse_date(list.attr("datePublished").as_deref(), "%Y-%m-%d") {
                if b.listed_on.is_none_or(|current| date < current) {
                    b.listed_on = Some(date);
                }
            }
        }
        b.source_url = Some(format!("https://sanctionssearch.ofac.treas.gov/Details.aspx?id={id}"));
        for name in entity.el("names").els("name") {
            let is_primary = eq(&name.val("isPrimary"), "true");
            let is_weak = eq(&name.val("isLowQuality"), "true");
            for t in name.el("translations").els("translation") {
                let full_name =
                    join(" ", &[t.val("formattedFirstName"), t.val("formattedLastName")]).or_else(|| t.val("formattedFullName"));
                b.add_name(full_name.as_deref(), is_weak, is_primary && eq(&t.val("isPrimary"), "true"));
            }
        }
        for a in entity.el("addresses").els("address") {
            let country = a.val("country");
            let translations: Vec<&Element> = a.el("translations").els("translation");
            if translations.is_empty() {
                b.add_address(country.as_deref(), None);
            }
            for t in translations {
                let mut parts: Vec<Option<String>> = t.el("addressParts").els("addressPart").iter().map(|p| p.val("value")).collect();
                parts.push(country.clone());
                b.add_address(join(", ", &parts).as_deref(), None);
            }
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

fn parse_un(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut out = Vec::new();
    records(reader, "CONSOLIDATED_LIST", 2, |record| {
        let kind = match record.name.as_str() {
            "INDIVIDUAL" => SubjectType::Individual,
            "ENTITY" => SubjectType::Entity,
            _ => return Ok(()),
        };
        let id = record
            .val("REFERENCE_NUMBER")
            .or_else(|| record.val("DATAID"))
            .ok_or_else(|| format_error("A UN record has neither REFERENCE_NUMBER nor DATAID."))?;
        let mut b = Builder::new(&id, kind);
        b.programmes = record.val("UN_LIST_TYPE");
        b.listed_on = parse_date(record.val("LISTED_ON").as_deref(), "%Y-%m-%d");
        b.remarks = record.val("COMMENTS1");
        b.source_url = s("https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list");
        let name = join(" ", &[record.val("FIRST_NAME"), record.val("SECOND_NAME"), record.val("THIRD_NAME"), record.val("FOURTH_NAME")]);
        b.add_name(name.as_deref(), false, true);
        b.add_name(record.val("NAME_ORIGINAL_SCRIPT").as_deref(), false, false);
        let prefix = if kind == SubjectType::Individual { "INDIVIDUAL" } else { "ENTITY" };
        let alias_name = format!("{prefix}_ALIAS");
        for alias in record.els(&alias_name) {
            let quality = alias.val("QUALITY");
            b.add_name(alias.val("ALIAS_NAME").as_deref(), eq(&quality, "Low") || eq(&quality, "Very Low"), false);
        }
        let address_name = format!("{prefix}_ADDRESS");
        for a in record.els(&address_name) {
            let raw = join(", ", &[a.val("STREET"), a.val("ZIP_CODE"), a.val("CITY"), a.val("STATE_PROVINCE"), a.val("COUNTRY")]);
            b.add_address(raw.as_deref(), None);
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

fn parse_uk(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut out = Vec::new();
    records(reader, "Designations", 1, |d| {
        if d.name != "Designation" {
            return Ok(());
        }
        let kind = match d.val("IndividualEntityShip").as_deref() {
            Some("Individual") => SubjectType::Individual,
            Some("Entity") => SubjectType::Entity,
            Some("Ship") => return Ok(()),
            _ => SubjectType::Unknown,
        };
        let id = d.val("UniqueID").ok_or_else(|| format_error("A UK Designation has no UniqueID."))?;
        let mut b = Builder::new(&id, kind);
        b.programmes = d.val("RegimeName");
        b.listed_on = parse_date(d.val("DateDesignated").as_deref(), "%d/%m/%Y");
        b.remarks = d.val("OtherInformation");
        b.source_url = s("https://www.gov.uk/government/publications/the-uk-sanctions-list");
        for n in d.el("Names").els("Name") {
            let full_name = join(" ", &[n.val("Name1"), n.val("Name2"), n.val("Name3"), n.val("Name4"), n.val("Name5"), n.val("Name6")]);
            let is_weak = n.val("AliasStrength").is_some_and(|strength| strength.to_lowercase().starts_with("low"));
            b.add_name(full_name.as_deref(), is_weak, eq(&n.val("NameType"), "Primary Name"));
        }
        for n in d.el("NonLatinNames").els("NonLatinName") {
            if let Some(value) = n.val("NameNonLatinScript") {
                for spelling in value.split(" / ") {
                    b.add_name(Some(spelling), false, false);
                }
            }
        }
        for a in d.el("Addresses").els("Address") {
            let raw = join(
                ", ",
                &[
                    a.val("AddressLine1"),
                    a.val("AddressLine2"),
                    a.val("AddressLine3"),
                    a.val("AddressLine4"),
                    a.val("AddressLine5"),
                    a.val("AddressLine6"),
                    a.val("AddressPostalCode"),
                    a.val("AddressCountry"),
                ],
            );
            b.add_address(raw.as_deref(), None);
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

/// A JSON field that must be a string or null (or missing).
fn json_text(record: &Value, key: &str) -> Result<Option<String>, ParseError> {
    match record.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone())),
        Some(_) => Err(format_error(format!("\"{key}\" isn't a string."))),
    }
}

fn json_texts(record: &Value, key: &str) -> Result<Vec<String>, ParseError> {
    match record.get(key) {
        None | Some(Value::Null) => Ok(Vec::new()),
        Some(Value::Array(items)) => items
            .iter()
            .map(|i| i.as_str().map(str::to_string).ok_or_else(|| format_error(format!("\"{key}\" isn't a list of strings."))))
            .collect(),
        Some(_) => Err(format_error(format!("\"{key}\" isn't a list."))),
    }
}

fn parse_us_export(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    reader.read_to_end(&mut data)?;
    let document: Value =
        serde_json::from_slice(&data).map_err(|_| format_error("The JSON document isn't valid, or doesn't end after its root object."))?;
    let Value::Object(root) = document else { return Err(format_error("Expected a JSON object.")) };
    // The last "results" wins, as the reference's reader keeps reading.
    let results =
        root.iter().filter(|(k, _)| *k == "results").map(|(_, v)| v).next_back().ok_or_else(|| format_error("No \"results\" array."))?;
    let Value::Array(results) = results else { return Err(format_error("Expected \"results\" to be an array.")) };

    let mut out = Vec::new();
    for record in results {
        if !record.is_object() {
            return Err(format_error("Expected an object in \"results\"."));
        }
        let source = json_text(record, "source")?;
        let kind_text = json_text(record, "type")?;
        let kept = ["Bureau of Industry and Security", "State Department"]
            .iter()
            .any(|agency| source.as_deref().is_some_and(|s| s.ends_with(agency)));
        if !kept || eq(&kind_text, "Vessel") || eq(&kind_text, "Aircraft") {
            continue;
        }
        let id = super::parse::clean(json_text(record, "id")?.as_deref()).ok_or_else(|| format_error("A US export record has no id."))?;
        let kind = match kind_text.as_deref() {
            Some("Individual") => SubjectType::Individual,
            Some("Entity") => SubjectType::Entity,
            _ => SubjectType::Unknown,
        };
        let mut b = Builder::new(&id, kind);
        let mut parts = vec![source.clone()];
        parts.extend(json_texts(record, "programs")?.into_iter().map(Some));
        b.programmes = join(", ", &parts);
        b.listed_on = parse_date(json_text(record, "start_date")?.as_deref(), "%Y-%m-%d");
        b.remarks = json_text(record, "remarks")?;
        b.source_url = super::parse::clean(json_text(record, "source_list_url")?.as_deref())
            .or(super::parse::clean(json_text(record, "source_information_url")?.as_deref()));
        b.add_name(json_text(record, "name")?.as_deref(), false, true);
        for alt in json_texts(record, "alt_names")? {
            b.add_name(Some(&alt), false, false);
        }
        if let Some(addresses) = record.get("addresses").and_then(Value::as_array) {
            for a in addresses {
                let country = json_text(a, "country")?;
                let raw = join(
                    ", ",
                    &[
                        json_text(a, "address")?,
                        json_text(a, "city")?,
                        json_text(a, "state")?,
                        json_text(a, "postal_code")?,
                        country.clone(),
                    ],
                );
                b.add_address(raw.as_deref(), country.as_deref());
            }
        }
        out.extend(b.build());
    }
    Ok(out)
}

struct SecoPlace {
    text: Option<String>,
    country_code: Option<String>,
}

struct SecoPending {
    builder: Builder,
    addresses: Vec<(Option<String>, Option<String>)>,
}

fn parse_seco(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut programmes: HashMap<String, String> = HashMap::new();
    let mut places: HashMap<String, SecoPlace> = HashMap::new();
    let mut pending: Vec<SecoPending> = Vec::new();
    records(reader, "swiss-sanctions-list", 1, |record| {
        match record.name.as_str() {
            "sanctions-program" => {
                let key = record.els("program-key").find(|k| eq(&k.attr("lang"), "eng")).map(|k| k.value().to_string());
                for set in record.els("sanctions-set") {
                    if let (Some(ssid), Some(key)) = (set.attr("ssid"), &key) {
                        programmes.entry(ssid).or_insert_with(|| key.clone());
                    }
                }
            }
            "target" => {
                if let Some(target) = read_seco_target(&record, &programmes)? {
                    pending.push(target);
                }
            }
            "place" => {
                if let Some(id) = record.attr("ssid") {
                    let country = record.el("country");
                    let text = join(", ", &[record.val("location"), record.val("area"), country.map(|c| c.value().to_string())]);
                    places.insert(id, SecoPlace { text, country_code: country.attr("iso-code") });
                }
            }
            _ => {}
        }
        Ok(())
    })?;
    let mut out = Vec::new();
    for mut target in pending {
        for (details, place_id) in &target.addresses {
            let place = place_id.as_ref().and_then(|id| places.get(id));
            let raw = join(", ", &[details.clone(), place.and_then(|p| p.text.clone())]);
            target.builder.add_address(raw.as_deref(), place.and_then(|p| p.country_code.as_deref()));
        }
        out.extend(target.builder.build());
    }
    Ok(out)
}

fn read_seco_target(target: &Element, programmes: &HashMap<String, String>) -> Result<Option<SecoPending>, ParseError> {
    let modifications: Vec<&Element> = target.els("modification").collect();
    if modifications.first().is_some_and(|m| eq(&m.attr("modification-type"), "de-listed")) {
        return Ok(None);
    }
    let Some(subject) = target.el("individual").or_else(|| target.el("entity")) else { return Ok(None) };
    let kind = if subject.name == "individual" { SubjectType::Individual } else { SubjectType::Entity };
    let id = target.attr("ssid").ok_or_else(|| format_error("A SECO target has no ssid."))?;
    let mut b = Builder::new(&id, kind);
    if let Some(p) = target.val("sanctions-set-id").and_then(|set| programmes.get(&set)) {
        b.programmes = Some(p.clone());
    }
    let listed = modifications.iter().rev().find(|m| eq(&m.attr("modification-type"), "listed")).copied();
    b.listed_on = parse_date(listed.attr("effective-date").as_deref(), "%Y-%m-%d");
    b.remarks = join(" ", &subject.els("justification").map(|j| s(j.value())).collect::<Vec<_>>());
    b.source_url = s("https://www.sesam.search.admin.ch/sesam-search-web/pages/search.xhtml?lang=en");
    let mut addresses = Vec::new();
    for identity in subject.els("identity") {
        let main_identity = eq(&identity.attr("main"), "true");
        for name in identity.els("name") {
            let is_weak = eq(&name.attr("quality"), "low");
            let is_primary = main_identity && eq(&name.attr("name-type"), "primary-name");
            let mut parts: Vec<&Element> = name.els("name-part").collect();
            let order = |e: &Element| e.attr("order").and_then(|o| o.parse::<i64>().ok()).unwrap_or(0);
            parts.sort_by_key(|p| order(p));
            let values: Vec<Option<String>> = parts.iter().map(|p| p.val("value")).collect();
            b.add_name(join(" ", &values).as_deref(), is_weak, is_primary);

            let mut languages: Vec<(String, String)> = Vec::new();
            for p in &parts {
                for v in p.els("spelling-variant") {
                    let language = (v.attr("lang").unwrap_or_default(), v.attr("script").unwrap_or_default());
                    if !languages.contains(&language) {
                        languages.push(language);
                    }
                }
            }
            for (lang, script) in &languages {
                let spellings: Vec<Vec<String>> = parts
                    .iter()
                    .map(|p| {
                        let mut in_language: Vec<String> = Vec::new();
                        for v in p.els("spelling-variant") {
                            if v.attr("lang").unwrap_or_default() == *lang && v.attr("script").unwrap_or_default() == *script {
                                if let Some(c) = super::parse::clean(Some(v.value())) {
                                    if !in_language.contains(&c) {
                                        in_language.push(c);
                                    }
                                }
                            }
                        }
                        if in_language.is_empty() {
                            in_language.push(p.val("value").unwrap_or_default());
                        }
                        in_language
                    })
                    .collect();
                for full_name in full_names(&spellings) {
                    let parts: Vec<Option<String>> = full_name.into_iter().map(Some).collect();
                    b.add_name(join(" ", &parts).as_deref(), is_weak, false);
                }
            }
        }
        for a in identity.els("address") {
            addresses
                .push((join(", ", &[a.val("c-o"), a.val("p-o-box"), a.val("address-details"), a.val("zip-code")]), a.attr("place-id")));
        }
    }
    if !b.has_names() {
        return Ok(None);
    }
    Ok(Some(SecoPending { builder: b, addresses }))
}

const MAX_COMBINATIONS: i64 = 8;

/// Every combination of the parts' spellings, up to 8; beyond that, the first spelling of every part plus each other
/// spelling swapped in alone, so no spelling is ever dropped.
fn full_names(spellings: &[Vec<String>]) -> Vec<Vec<String>> {
    let count: i64 = spellings.iter().map(|c| c.len() as i64).product();
    if count <= MAX_COMBINATIONS {
        let mut combinations: Vec<Vec<String>> = vec![Vec::new()];
        for choice in spellings {
            combinations = combinations
                .iter()
                .flat_map(|prefix| {
                    choice.iter().map(move |option| {
                        let mut c = prefix.clone();
                        c.push(option.clone());
                        c
                    })
                })
                .collect();
        }
        return combinations;
    }
    let first: Vec<String> = spellings.iter().map(|c| c[0].clone()).collect();
    let mut names = vec![first.clone()];
    for (part, choice) in spellings.iter().enumerate() {
        for other in &choice[1..] {
            let mut name = first.clone();
            name[part] = other.clone();
            names.push(name);
        }
    }
    names
}

fn parse_canada(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    static LABEL: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    let label = LABEL.get_or_init(|| Regex::new(r"^\s*\p{L}+:\s*").unwrap());
    let mut out = Vec::new();
    records(reader, "data-set", 1, |record| {
        if record.name != "record" || record.val("ShipIMONumber-NumeroOMIDuNavire").is_some() {
            return Ok(());
        }
        let entity_name = record.val("EntityOrShip-EntiteOuNavire");
        let person_name = join(" ", &[record.val("GivenName-Prenom"), record.val("LastName-NomDeFamille")]);
        let kind = if entity_name.is_some() { SubjectType::Entity } else { SubjectType::Individual };
        let country = record.val("Country-Pays");
        let id = join("/", &[country.clone(), record.val("Schedule-Annexe"), record.val("Item-NumeroDarticle")])
            .ok_or_else(|| format_error("A Canada SEMA record has no country, schedule or item."))?;
        let mut b = Builder::new(&id, kind);
        b.programmes = country;
        b.listed_on = parse_date(record.val("DateOfListing-DateDinscription").as_deref(), "%Y-%m-%d");
        b.source_url = s("https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng");
        b.add_name(entity_name.as_deref().or(person_name.as_deref()), false, true);
        if let Some(aliases) = record.val("Aliases-Alias") {
            for alias in aliases.split([',', ';']).filter(|a| !a.is_empty()) {
                b.add_name(Some(&label.replace(alias, "")), false, false);
            }
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

fn parse_sfms(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut out = Vec::new();
    records(reader, "list-terror", 1, |record| {
        if record.name != "acount-list" {
            return Ok(());
        }
        let kind = match record.val("type-entry").as_deref() {
            Some("1") => SubjectType::Entity,
            Some("2") => SubjectType::Individual,
            _ => SubjectType::Unknown,
        };
        let id = record.val("number-entry").ok_or_else(|| format_error("An SFMS record has no number-entry."))?;
        let mut b = Builder::new(&id, kind);
        b.programmes = record.val("program-entry");
        b.listed_on = parse_date(record.val("date-entry").as_deref(), "%Y%m%d");
        b.remarks = record.val("comments");
        b.source_url = s("https://fiu.gov.ua/");
        for aka in record.els("aka-list") {
            let name = join(" ", &[aka.val("aka-name1"), aka.val("aka-name2"), aka.val("aka-name3"), aka.val("aka-name4")]);
            b.add_name(name.as_deref(), eq(&aka.val("quality-aka"), "2"), eq(&aka.val("type-aka"), "N"));
        }
        for a in record.els("address-list") {
            b.add_address(a.val("address").as_deref(), None);
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}
