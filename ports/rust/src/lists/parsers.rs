//! One parser per feed format (IListParser in the reference; ports/go/lists/parsers.go): a file reduced to subjects.

use std::collections::{HashMap, HashSet};
use std::io::BufRead;

use regex::Regex;
use serde_json::Value;

use super::parse::{
    birth_date, birth_date_parts, birth_date_text, eq, format_error, free_text_identifiers, identifier_values, join, parse_date, records,
    Builder, Element, OptionalElement, ParseError, ParsedSubject, SubjectType,
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
        "fr-tresor" => parse_france,
        "nl-terrorism" => super::spreadsheets::parse_netherlands,
        "nz-mfat" => super::spreadsheets::parse_new_zealand,
        "be-terrorism" => super::spreadsheets::parse_belgium,
        "au-dfat" => super::spreadsheets::parse_australia,
        "ua-nsdc" => super::nsdc::parse_nsdc,
        "jp-mof" => super::japan::parse_japan,
        "us-uflpa" => super::uflpa::parse_uflpa,
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
        // Identifiers and dates of birth (#68). "00" is the EU's code for an unknown country.
        for i in entity.els("identification") {
            let country =
                i.attr("countryIso2Code").filter(|c| c != "00").or_else(|| i.attr("countryDescription").filter(|d| d != "UNKNOWN"));
            let kind = i.attr("identificationTypeDescription").or_else(|| i.attr("identificationTypeCode"));
            b.add_identifier(kind.as_deref(), i.attr("number").as_deref(), country.as_deref());
        }
        for d in entity.els("birthdate") {
            let date = match d.attr("birthdate") {
                Some(full) => birth_date_text(&full),
                None => match (d.attr("year"), d.attr("monthOfYear")) {
                    (Some(y), Some(m)) => birth_date_text(&format!("{m}/{y}")),
                    (Some(y), None) => birth_date_text(&y),
                    _ => None,
                },
            };
            b.add_birth_date(date);
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
            Some("Vessel") => SubjectType::Vessel,
            Some("Aircraft") => return Ok(()),
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
        // Identifiers and dates of birth (#68): each identity document, and each Birthdate feature's range of days.
        for d in entity.el("identityDocuments").els("identityDocument") {
            let (kind, number) = (d.val("type"), d.val("documentNumber"));
            // A vessel's IMO number (#69) is a "Vessel Registration Identification" written "IMO 9187629".
            if kind.as_deref() == Some("Vessel Registration Identification") && number.as_deref().is_some_and(|n| n.contains("IMO")) {
                b.add_imo_number(number.as_deref(), "Vessel Registration Identification");
                continue;
            }
            b.add_identifier(kind.as_deref(), number.as_deref(), d.val("issuingCountry").as_deref());
        }
        let mut vessel = Vec::new();
        for f in entity.el("features").els("feature") {
            // A vessel's details (#69), said in the remarks.
            for (feature, label) in
                [("Vessel Flag", "Flag"), ("VESSEL TYPE", "Type"), ("Vessel Year of Build", "Built"), ("Vessel Owner", "Owner")]
            {
                if eq(&f.val("type"), feature) {
                    vessel.push(f.val("value").map(|v| format!("{label}: {v}")));
                }
            }
            if eq(&f.val("type"), "Birthdate") {
                let v = f.el("valueDate");
                let first = parse_date(v.val("fromDateBegin").as_deref(), "%Y-%m-%d");
                let last = parse_date(v.val("toDateEnd").as_deref(), "%Y-%m-%d");
                if let (Some(first), Some(last)) = (first, last) {
                    b.add_birth_date(birth_date(first, last));
                }
            }
        }
        if kind == SubjectType::Vessel {
            b.remarks = join("; ", &vessel);
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
        // Identifiers and dates of birth (#68).
        for d in record.els(&format!("{prefix}_DOCUMENT")) {
            let kind = join(": ", &[d.val("TYPE_OF_DOCUMENT"), d.val("TYPE_OF_DOCUMENT2")]);
            b.add_identifier(kind.as_deref(), d.val("NUMBER").as_deref(), d.val("ISSUING_COUNTRY").as_deref());
        }
        for d in record.els(&format!("{prefix}_DATE_OF_BIRTH")) {
            let date = match (d.val("DATE"), d.val("YEAR"), d.val("FROM_YEAR"), d.val("TO_YEAR")) {
                (Some(date), _, _, _) => birth_date_text(date.get(..10).unwrap_or(&date)),
                (None, Some(year), _, _) => birth_date_text(&year),
                (None, None, Some(from), Some(to)) => match (from.trim().parse::<i32>(), to.trim().parse::<i32>()) {
                    (Ok(from), Ok(to)) if from < to => Some(format!("{from}..{to}")),
                    (Ok(from), Ok(_)) => Some(from.to_string()),
                    _ => None,
                },
                _ => None,
            };
            b.add_birth_date(date);
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
            Some("Ship") => SubjectType::Vessel,
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
        // Identifiers and dates of birth (#68). A business registration number is free text, often several numbers
        // with their kinds and countries in words: each identifier found in it, whole.
        let individual = d.el("IndividualDetails").el("Individual");
        for p in individual.el("PassportDetails").els("Passport") {
            b.add_identifier(Some("Passport"), p.val("PassportNumber").as_deref(), None);
        }
        for n in individual.el("NationalIdentifierDetails").els("NationalIdentifier") {
            b.add_identifier(Some("National identifier"), n.val("NationalIdentifierNumber").as_deref(), None);
        }
        for dob in individual.el("DOBs").els("DOB") {
            b.add_birth_date(birth_date_text(dob.value()));
        }
        for r in d.el("EntityDetails").el("Entity").el("BusinessRegistrationNumbers").els("BusinessRegistrationNumber") {
            for number in identifier_values(r.value()) {
                b.add_identifier(Some("Business registration number"), Some(&number), None);
            }
        }
        // A ship (#69): its IMO numbers, and its flag, type and owner or operator in the remarks.
        let ship = d.el("ShipDetails").el("Ship");
        for n in ship.el("IMONumbers").els("IMONumber") {
            b.add_imo_number(Some(n.value()), "IMO number as listed");
        }
        if kind == SubjectType::Vessel {
            let all = |outer: &str, inner: &'static str| {
                join(", ", &ship.el(outer).els(inner).into_iter().map(|e| s(e.value())).collect::<Vec<_>>())
            };
            let details = [
                all("CurrentBelievedFlagOfShips", "CurrentBelievedFlagOfShip").map(|v| format!("Flag: {v}")),
                all("TypeOfShipDetails", "TypeOfShip").map(|v| format!("Type: {v}")),
                all("YearsBuilt", "YearBuilt").map(|v| format!("Built: {v}")),
                all("CurrentOwnerOperators", "CurrentOwnerOperator").map(|v| format!("Owner or operator: {v}")),
            ];
            b.remarks = join("; ", &[b.remarks.take(), join("; ", &details)]);
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
        if !kept || eq(&kind_text, "Aircraft") {
            continue;
        }
        let id = super::parse::clean(json_text(record, "id")?.as_deref()).ok_or_else(|| format_error("A US export record has no id."))?;
        let kind = match kind_text.as_deref() {
            Some("Individual") => SubjectType::Individual,
            Some("Entity") => SubjectType::Entity,
            Some("Vessel") => SubjectType::Vessel,
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
    // A vessel (#69) is an object of type vessel; other objects stay out.
    let vessel = target.el("object").filter(|o| eq(&o.attr("object-type"), "vessel"));
    let Some(subject) = target.el("individual").or_else(|| target.el("entity")).or(vessel) else { return Ok(None) };
    let kind = match subject.name.as_str() {
        "individual" => SubjectType::Individual,
        "entity" => SubjectType::Entity,
        _ => SubjectType::Vessel,
    };
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
        // Identifiers and dates of birth (#68): each identity's documents and dates.
        for d in identity.els("identification-document") {
            let kind = match d.attr("document-type").as_deref() {
                Some("passport") => "Passport",
                Some("diplomatic-passport") => "Diplomatic passport",
                Some("id-card") => "ID card",
                Some("residence-permit") => "Residence permit",
                Some("driving-license") => "Driving licence",
                Some("travel-document") => "Travel document",
                _ => "Identification document",
            };
            let issuer = d.el("issuer");
            b.add_identifier(
                Some(kind),
                d.val("number").as_deref(),
                issuer.attr("code").or_else(|| issuer.map(|i| i.value().to_string())).as_deref(),
            );
        }
        for date in identity.els("day-month-year") {
            b.add_birth_date(birth_date_parts(date.attr("year").as_deref(), date.attr("month").as_deref(), date.attr("day").as_deref()));
        }
    }
    // Numbers the record gives in its other information, labelled ("Registration number: 1027700132195", "Tax
    // Identification Number: …", "Personal ID: …"): each under its label. Telephones, dates and places aren't read.
    static LABEL: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    let label = LABEL.get_or_init(|| Regex::new(r"(?i)^\s*([^:]{2,60}?)\s*:\s*(.+)$").unwrap());
    static KINDS: std::sync::OnceLock<(Regex, Regex)> = std::sync::OnceLock::new();
    let (wanted, unwanted) = KINDS.get_or_init(|| {
        (
            Regex::new(r"(?i)registration|registrierung|\btax|\btin\b|\binn\b|\bkpp\b|\bogrn|\bunp\b|\bokpo|passport|\bid\b|identification|identity|\bimo\b|fiscal|company number").unwrap(),
            Regex::new(r"(?i)date|place|phone|\btel|fax|mail|web|address").unwrap(),
        )
    });
    for info in subject.els("other-information") {
        for line in info.value().lines() {
            if let Some(found) = label.captures(line) {
                let kind = found[1].trim();
                // A vessel's IMO number is read below, as its own.
                if wanted.is_match(kind)
                    && !unwanted.is_match(kind)
                    && !(subject.name == "object" && kind.to_lowercase().starts_with("imo"))
                {
                    for number in free_text_identifiers(&found[2]) {
                        b.add_identifier(Some(kind), Some(&number), None);
                    }
                }
            }
        }
    }
    if kind == SubjectType::Vessel {
        // "IMO number: 8405270" (and once "IMO Numbe: …") in the vessel's other information.
        static IMO: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
        let imo = IMO.get_or_init(|| Regex::new(r"(?i)\bIMO\b[^0-9]{0,12}([0-9]{7})\b").unwrap());
        for info in subject.els("other-information") {
            for found in imo.captures_iter(info.value()) {
                b.add_imo_number(Some(&found[1]), "IMO number as listed");
            }
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
        if record.name != "record" {
            return Ok(());
        }
        let entity_name = record.val("EntityOrShip-EntiteOuNavire");
        let person_name = join(" ", &[record.val("GivenName-Prenom"), record.val("LastName-NomDeFamille")]);
        // A ship (#69) is a record with an IMO number.
        let imo = record.val("ShipIMONumber-NumeroOMIDuNavire");
        let kind = match (&imo, &entity_name) {
            (Some(_), _) => SubjectType::Vessel,
            (None, Some(_)) => SubjectType::Entity,
            (None, None) => SubjectType::Individual,
        };
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
        // A person's date of birth (#68), "1965-04-29" or "1972"; the same column is a ship's year built.
        if kind == SubjectType::Individual {
            b.add_birth_date(
                record.val("DateOfBirthOrShipBuildDate-DateDeNaissanceOuDateDeConstructionDuNavire").and_then(|d| birth_date_text(&d)),
            );
        }
        if kind == SubjectType::Vessel {
            b.add_imo_number(imo.as_deref(), "IMO number as listed");
            // "General Cargo | Cargo classique": the English half.
            let ship_type =
                record.val("TitleOrShipType-TitreOuTypeDeNavire").map(|t| t.split(" | ").next().unwrap_or_default().to_string());
            let built = record.val("DateOfBirthOrShipBuildDate-DateDeNaissanceOuDateDeConstructionDuNavire");
            b.remarks = join("; ", &[ship_type.map(|t| format!("Type: {t}")), built.map(|y| format!("Built: {y}"))]);
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
        // Identifiers and dates of birth (#68), in prose: "Yemen number 541939", "Approximately 1971; 01.02.1960".
        for d in record.els("document-list") {
            for number in d.val("document-id").map(|t| free_text_identifiers(&t)).unwrap_or_default() {
                b.add_identifier(Some("Document"), Some(&number), d.val("document-country").as_deref());
            }
        }
        for n in record.els("id-number-list") {
            for number in free_text_identifiers(n.value()) {
                b.add_identifier(Some("Identification number"), Some(&number), None);
            }
        }
        for d in record.els("date-of-birth-list") {
            for date in prose_birth_dates(d.value()) {
                b.add_birth_date(Some(date));
            }
        }
        out.extend(b.build());
        Ok(())
    })?;
    Ok(out)
}

/// The dates of birth in prose as SFMS writes them: "19711119", "1971", "Approximately 1971", "Between 1960 and
/// 1962", "12 Jul. 1960", "01.02.1960", several separated by semicolons.
pub fn prose_birth_dates(text: &str) -> Vec<String> {
    const MONTHS: [&str; 12] = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    static FORMS: std::sync::OnceLock<[Regex; 4]> = std::sync::OnceLock::new();
    let [compact, dotted, named, between] = FORMS.get_or_init(|| {
        [
            Regex::new(r"^(\d{4})(\d{2})(\d{2})$").unwrap(),
            Regex::new(r"(\d{1,2})\.(\d{1,2})\.(\d{4})").unwrap(),
            Regex::new(r"(?i)\b(\d{1,2})\s+([a-z]{3})[a-z]*\.?\s+(\d{4})").unwrap(),
            Regex::new(r"(?i)between\s+(\d{4})\s+and\s+(\d{4})").unwrap(),
        ]
    });
    static YEAR: std::sync::OnceLock<Regex> = std::sync::OnceLock::new();
    let year = YEAR.get_or_init(|| Regex::new(r"\b(\d{4})\b").unwrap());
    let mut out: Vec<String> = Vec::new();
    for part in text.split(';').map(str::trim).filter(|p| !p.is_empty()) {
        let date = if let Some(c) = compact.captures(part) {
            birth_date_parts(Some(&c[1]), Some(&c[2]), Some(&c[3]))
        } else if let Some(c) = dotted.captures(part) {
            birth_date_parts(Some(&c[3]), Some(&c[2]), Some(&c[1]))
        } else if let Some(c) = between.captures(part) {
            match (c[1].parse::<i32>(), c[2].parse::<i32>()) {
                (Ok(from), Ok(to)) if from < to && (1800..=2100).contains(&from) && to <= 2100 => Some(format!("{from}..{to}")),
                _ => None,
            }
        } else if let Some(c) = named.captures(part) {
            let month = MONTHS.iter().position(|m| c[2].eq_ignore_ascii_case(m)).map(|m| (m + 1).to_string());
            birth_date_parts(Some(&c[3]), month.as_deref(), month.as_ref().map(|_| &c[1]))
        } else {
            year.captures(part).and_then(|c| birth_date_parts(Some(&c[1]), None, None))
        };
        if let Some(date) = date.filter(|d| !out.contains(d)) {
            out.push(date);
        }
    }
    out
}

/// A register entry's page: its official record.
pub const FRANCE_RECORD_URL: &str = "https://gels-avoirs.dgtresor.gouv.fr/Gels/RegistreDetail?idRegistre=";

/// A string field, or None when it's missing or not a string (as the reference reads them).
fn json_str(value: &Value, key: &str) -> Option<String> {
    value.get(key).and_then(Value::as_str).map(str::to_string)
}

/// Percent-encodes all but the unreserved characters, as the reference's Uri.EscapeDataString does.
fn escape_data(value: &str) -> String {
    let mut out = String::new();
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || b"-._~".contains(&byte) {
            out.push(byte as char);
        } else {
            out.push_str(&format!("%{byte:02X}"));
        }
    }
    out
}

/// France's national asset-freeze register (DG Trésor, JSON): FranceParser in the reference.
fn parse_france(reader: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    reader.read_to_end(&mut data)?;
    // A truncated or malformed file must fail, not pass as a shorter list.
    let document: Value = serde_json::from_slice(&data).map_err(|e| format_error(format!("France's register isn't valid JSON: {e}")))?;
    let details = document
        .get("Publications")
        .filter(|p| p.is_object())
        .and_then(|p| p.get("PublicationDetail"))
        .and_then(Value::as_array)
        .filter(|_| document.is_object())
        .ok_or_else(|| format_error("Expected {\"Publications\": {\"PublicationDetail\": [...]}}."))?;

    let mut out = Vec::new();
    for record in details {
        if !record.is_object() {
            return Err(format_error("A register entry isn't an object."));
        }
        let id = match record.get("IdRegistre") {
            Some(Value::String(text)) => super::parse::clean(Some(text)),
            Some(Value::Number(number)) => super::parse::clean(Some(&number.to_string())),
            _ => None,
        }
        .ok_or_else(|| format_error("A register entry has no IdRegistre."))?;
        let kind = match record.get("Nature").and_then(Value::as_str) {
            Some("Personne physique") => SubjectType::Individual,
            Some("Personne morale") => SubjectType::Entity,
            Some("Navire") => SubjectType::Vessel,
            _ => SubjectType::Unknown,
        };

        // A field that isn't the shape expected is a format error, never skipped: an update must not lose aliases or
        // addresses while its record count stays the same.
        let mut fields: Vec<(&str, &Vec<Value>)> = Vec::new();
        match record.get("RegistreDetail") {
            None | Some(Value::Null) => {}
            Some(Value::Array(detail)) => {
                for field in detail {
                    let kind = field.get("TypeChamp").and_then(Value::as_str);
                    let values = field.get("Valeur").and_then(Value::as_array);
                    let (Some(kind), Some(values)) = (kind, values) else {
                        return Err(format_error(format!("Register entry {id}: a field isn't {{\"TypeChamp\": …, \"Valeur\": [...]}}.")));
                    };
                    if values.iter().any(|v| !v.is_object()) {
                        return Err(format_error(format!("Register entry {id}: a {kind} value isn't an object.")));
                    }
                    // A value the register always gives as text (a name, an address, a legal basis): missing or not text
                    // is a format error, never a name or an address quietly lost.
                    let required: &[&str] = match kind {
                        "ALIAS" => &["Alias"],
                        "PRENOM" => &["Prenom"],
                        "ADRESSE_PM" | "ADRESSE_PP" => &["Adresse", "Pays"],
                        "MOTIFS" => &["Motifs"],
                        "FONDEMENT_JURIDIQUE" => &["FondementJuridiqueLabel"],
                        "NUMERO_OMI" => &["NumeroOMI"],
                        _ => &[],
                    };
                    for key in required {
                        if values.iter().any(|v| !v.get(*key).is_some_and(Value::is_string)) {
                            return Err(format_error(format!("Register entry {id}: a value has no text \"{key}\".")));
                        }
                    }
                    fields.push((kind, values));
                }
            }
            Some(_) => return Err(format_error(format!("Register entry {id}: RegistreDetail isn't a list."))),
        }
        let values = |field_type: &str| -> Vec<&Value> {
            fields.iter().filter(|(kind, _)| *kind == field_type).flat_map(|(_, values)| values.iter()).collect()
        };

        let mut b = Builder::new(&id, kind);
        b.programmes =
            join("; ", &values("FONDEMENT_JURIDIQUE").iter().map(|v| json_str(v, "FondementJuridiqueLabel")).collect::<Vec<_>>());
        b.remarks = join(" ", &values("MOTIFS").iter().map(|v| json_str(v, "Motifs")).collect::<Vec<_>>());
        b.source_url = Some(format!("{FRANCE_RECORD_URL}{}", escape_data(&id)));

        // Every entry has its name: one without is a broken file, not an entry to search by its aliases or to drop.
        let surname = super::parse::clean(json_str(record, "Nom").as_deref());
        if surname.is_none() {
            return Err(format_error(format!("Register entry {id} has no Nom.")));
        }
        let given =
            values("PRENOM").iter().map(|v| json_str(v, "Prenom")).find(|name| super::parse::clean(name.as_deref()).is_some()).flatten();
        let primary = if kind == SubjectType::Individual { join(" ", &[given, surname]) } else { surname };
        b.add_name(primary.as_deref(), false, true);
        for alias in values("ALIAS") {
            let weak = json_str(alias, "Commentaire").is_some_and(|c| c.to_lowercase().contains("alias faible"));
            b.add_name(json_str(alias, "Alias").as_deref(), weak, false);
        }
        for address in values("ADRESSE_PM").into_iter().chain(values("ADRESSE_PP")) {
            let raw = join(", ", &[json_str(address, "Adresse"), json_str(address, "Pays")]);
            b.add_address(raw.as_deref(), None);
        }
        // Identifiers and dates of birth (#68): what each number is, from its comment when that's short.
        let kind_of = |value: &Value, otherwise: &str| {
            json_str(value, "Commentaire")
                .and_then(|c| super::parse::clean(Some(&c)))
                .filter(|c| c.chars().count() <= 60)
                .unwrap_or_else(|| otherwise.to_string())
        };
        for (field, key, otherwise) in [
            ("IDENTIFICATION", "Identification", "Identification"),
            ("PASSEPORT", "NumeroPasseport", "Passeport"),
            ("AUTRE_IDENTITE", "NumeroCarte", "Autre identité"),
        ] {
            for value in values(field) {
                for number in json_str(value, key).map(|t| free_text_identifiers(&t)).unwrap_or_default() {
                    b.add_identifier(Some(&kind_of(value, otherwise)), Some(&number), None);
                }
            }
        }
        for date in values("DATE_DE_NAISSANCE") {
            b.add_birth_date(birth_date_parts(
                json_str(date, "Annee").as_deref(),
                json_str(date, "Mois").as_deref(),
                json_str(date, "Jour").as_deref(),
            ));
        }
        // A ship's IMO number (#69).
        for number in values("NUMERO_OMI") {
            b.add_imo_number(json_str(number, "NumeroOMI").as_deref(), "IMO number as listed");
        }
        out.extend(b.build());
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::{parse_canada, parse_eu, parse_france, parse_ofac, parse_seco, parse_uk, parse_un};
    use crate::lists::parse::ParsedIdentifier;
    use crate::lists::parse::SubjectType;

    fn id(kind: &str, value: &str, country: Option<&str>) -> ParsedIdentifier {
        ParsedIdentifier { kind: kind.to_string(), value: value.to_string(), country: country.map(str::to_string) }
    }

    /// Identifiers and dates of birth (#68), as each format gives them.
    #[test]
    fn ofac_reads_identity_documents_and_birthdates() {
        let subjects = parse_ofac(
            &mut r#"<sanctionsData><entities><entity id="7"><generalInfo><entityType>Individual</entityType></generalInfo>
            <names><name><isPrimary>true</isPrimary><translations><translation><isPrimary>true</isPrimary><formattedFirstName>Ivan</formattedFirstName><formattedLastName>Petrov</formattedLastName></translation></translations></name></names>
            <features>
              <feature><type featureTypeId="8">Birthdate</type><valueDate><fromDateBegin>1948-12-10</fromDateBegin><toDateEnd>1948-12-10</toDateEnd></valueDate></feature>
              <feature><type featureTypeId="8">Birthdate</type><valueDate><fromDateBegin>1955-01-01</fromDateBegin><toDateEnd>1957-12-31</toDateEnd></valueDate></feature>
              <feature><type featureTypeId="14">Website</type><value>example.invalid</value></feature>
            </features>
            <identityDocuments><identityDocument id="1"><type refId="1">Passport</type><documentNumber>AB 123456</documentNumber><issuingCountry>Russia</issuingCountry></identityDocument>
            <identityDocument id="2"><type refId="2">Tax ID No.</type><documentNumber>7704734000</documentNumber></identityDocument></identityDocuments>
            </entity></entities></sanctionsData>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!(subjects[0].identifiers, [id("Passport", "AB 123456", Some("Russia")), id("Tax ID No.", "7704734000", None)]);
        assert_eq!(subjects[0].birth_dates, ["1948-12-10", "1955..1957"]);
    }

    #[test]
    fn uk_reads_passports_national_identifiers_registration_numbers_and_dates_of_birth() {
        let subjects = parse_uk(
            &mut r#"<Designations>
            <Designation><UniqueID>RUS1</UniqueID><IndividualEntityShip>Individual</IndividualEntityShip><Names><Name><Name1>Ivan</Name1><Name6>Petrov</Name6><NameType>Primary Name</NameType></Name></Names>
              <IndividualDetails><Individual><DOBs><DOB>dd/mm/1963</DOB><DOB>12/03/1960</DOB></DOBs>
              <PassportDetails><Passport><PassportNumber>P04581926</PassportNumber></Passport></PassportDetails>
              <NationalIdentifierDetails><NationalIdentifier><NationalIdentifierNumber>2132370</NationalIdentifierNumber></NationalIdentifier></NationalIdentifierDetails></Individual></IndividualDetails></Designation>
            <Designation><UniqueID>BEL1</UniqueID><IndividualEntityShip>Entity</IndividualEntityShip><Names><Name><Name6>MZKT</Name6><NameType>Primary Name</NameType></Name></Names>
              <EntityDetails><Entity><BusinessRegistrationNumbers><BusinessRegistrationNumber>(УНН/ИНН): 190950894 (Belarus), 7704734000/770301001 (Russia)</BusinessRegistrationNumber></BusinessRegistrationNumbers></Entity></EntityDetails></Designation>
            </Designations>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!(subjects[0].identifiers, [id("Passport", "P04581926", None), id("National identifier", "2132370", None)]);
        assert_eq!(subjects[0].birth_dates, ["1963", "1960-03-12"]);
        let numbers: Vec<&str> = subjects[1].identifiers.iter().map(|i| i.value.as_str()).collect();
        assert_eq!(numbers, ["190950894", "7704734000", "770301001"]);
    }

    #[test]
    fn eu_reads_identifications_and_birthdates() {
        let subjects = parse_eu(
            &mut r#"<export><sanctionEntity logicalId="9"><subjectType code="person"/><nameAlias wholeName="Ivan Petrov" strong="true"/>
            <identification number="4510 123456" identificationTypeCode="passport" identificationTypeDescription="National passport" countryIso2Code="RU" countryDescription="RUSSIA"/>
            <identification number="34409/129" identificationTypeCode="other" identificationTypeDescription="Other identification number" countryIso2Code="00" countryDescription="UNKNOWN"/>
            <birthdate birthdate="1937-04-28"/><birthdate year="1960"/><birthdate year="1961" monthOfYear="5"/>
            </sanctionEntity></export>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!(
            subjects[0].identifiers,
            [id("National passport", "4510 123456", Some("RU")), id("Other identification number", "34409/129", None)]
        );
        assert_eq!(subjects[0].birth_dates, ["1937-04-28", "1960", "1961-05"]);
    }

    #[test]
    fn un_reads_documents_and_dates_of_birth() {
        let subjects = parse_un(
            &mut r#"<CONSOLIDATED_LIST><INDIVIDUALS><INDIVIDUAL><DATAID>1</DATAID><REFERENCE_NUMBER>CDi.002</REFERENCE_NUMBER><FIRST_NAME>FRANK</FIRST_NAME><SECOND_NAME>BWAMBALE</SECOND_NAME>
            <INDIVIDUAL_DATE_OF_BIRTH><TYPE_OF_DATE>EXACT</TYPE_OF_DATE><DATE>1971-05-12</DATE></INDIVIDUAL_DATE_OF_BIRTH>
            <INDIVIDUAL_DATE_OF_BIRTH><TYPE_OF_DATE>BETWEEN</TYPE_OF_DATE><FROM_YEAR>1973</FROM_YEAR><TO_YEAR>1974</TO_YEAR></INDIVIDUAL_DATE_OF_BIRTH>
            <INDIVIDUAL_DOCUMENT><TYPE_OF_DOCUMENT>National Identification Number</TYPE_OF_DOCUMENT><TYPE_OF_DOCUMENT2>FARDC ID</TYPE_OF_DOCUMENT2><NUMBER>1-78-09-44621-80</NUMBER></INDIVIDUAL_DOCUMENT>
            <INDIVIDUAL_DOCUMENT/></INDIVIDUAL></INDIVIDUALS></CONSOLIDATED_LIST>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!(subjects[0].identifiers, [id("National Identification Number: FARDC ID", "1-78-09-44621-80", None)]);
        assert_eq!(subjects[0].birth_dates, ["1971-05-12", "1973..1974"]);
    }

    /// The French register's malformed documents are format errors, never a shorter list (FranceParser's tests in the
    /// reference): a field or value that isn't the shape expected, as well as a broken document.
    #[test]
    fn france_rejects_malformed_documents() {
        for document in [
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}]}"#,
            r#"{"Publications": {"PublicationDetail": [{"Nature": "Personne morale", "Nom": "A"}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A"}, 7]}}"#,
            r#"{"Publications": {}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": "A", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}, {"Prenom": 7}]}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale"}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne physique", "Nom": " ", "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}]}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": {"TypeChamp": "ALIAS"}}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": {"Alias": "B"}}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"Valeur": [{"Alias": "B"}]}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": ["B"]}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": ["ALIAS"]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": [{"Alias": 7}]}]}]}}"#,
            r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 1, "Nature": "Personne morale", "Nom": "A", "RegistreDetail": [{"TypeChamp": "ADRESSE_PM", "Valeur": [{"Pays": "FRANCE"}]}]}]}}"#,
        ] {
            let error = parse_france(&mut document.as_bytes()).expect_err(document);
            assert!(error.downcast_ref::<super::super::parse::FormatError>().is_some(), "{document}: {error}");
        }
    }

    /// SFMS's dates of birth in prose (#68).
    #[test]
    fn dates_of_birth_in_prose_are_read() {
        assert_eq!(super::prose_birth_dates("19711119"), ["1971-11-19"]);
        assert_eq!(super::prose_birth_dates("Approximately 1971; 01.02.1960"), ["1971", "1960-02-01"]);
        assert_eq!(super::prose_birth_dates("Between 1960 and 1962"), ["1960..1962"]);
        assert_eq!(super::prose_birth_dates("12 Jul. 1960; 3 May 1961"), ["1960-07-12", "1961-05-03"]);
        assert!(super::prose_birth_dates("unknown").is_empty());
    }

    /// SECO's documents, dates and labelled numbers, France's identification fields and dates (#68).
    #[test]
    fn seco_and_france_read_identifiers_and_dates_of_birth() {
        let seco = parse_seco(
            &mut r#"<swiss-sanctions-list><target ssid="1"><sanctions-set-id>1</sanctions-set-id><individual><identity ssid="2" main="true">
            <name ssid="3" name-type="primary-name" quality="good"><name-part order="1"><value>Leanid Dziadkou</value></name-part></name>
            <day-month-year ssid="4" month="10" year="1964"/><identification-document ssid="5" document-type="id-card"><number>3271064M000PB3</number><issuer code="BY">Belarus</issuer></identification-document>
            </identity><other-information ssid="6">Personal ID: 3281175A014PB8</other-information><other-information ssid="7">Tel.: +375 17 2800264</other-information></individual>
            <modification modification-type="listed" effective-date="2020-01-01"/></target></swiss-sanctions-list>"#
                .as_bytes(),
        )
        .unwrap();
        let ids: Vec<(&str, &str, Option<&str>)> =
            seco[0].identifiers.iter().map(|i| (i.kind.as_str(), i.value.as_str(), i.country.as_deref())).collect();
        assert_eq!(ids, [("ID card", "3271064M000PB3", Some("BY")), ("Personal ID", "3281175A014PB8", None)]);
        assert_eq!(seco[0].birth_dates, ["1964-10"]);

        let france = parse_france(
            &mut r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 7, "Nature": "Personne morale", "Nom": "ROMASHKA",
            "RegistreDetail": [{"TypeChamp": "IDENTIFICATION", "Valeur": [{"Identification": "1023202741781", "Commentaire": "Numéro d’enregistrement"}, {"Identification": "/", "Commentaire": "Type d’entité: société par actions"}]},
            {"TypeChamp": "PASSEPORT", "Valeur": [{"NumeroPasseport": " série 1808 n° 233282", "Commentaire": "ou carte d'identité"}]}]},
            {"IdRegistre": 8, "Nature": "Personne physique", "Nom": "PETROV", "RegistreDetail": [{"TypeChamp": "DATE_DE_NAISSANCE", "Valeur": [{"Jour": "20", "Mois": "10", "Annee": "1976", "Commentaire": ""}]}]}]}}"#
                .as_bytes(),
        )
        .unwrap();
        let ids: Vec<(&str, &str)> = france[0].identifiers.iter().map(|i| (i.kind.as_str(), i.value.as_str())).collect();
        assert_eq!(ids, [("Numéro d’enregistrement", "1023202741781"), ("ou carte d'identité", "1808 233282")]);
        assert_eq!(france[1].birth_dates, ["1976-10-20"]);
    }

    /// Vessels (#69), each format's way: a Vessel subject with its IMO number's seven digits.
    #[test]
    fn vessels_are_kept_with_their_imo_numbers() {
        let imo = |subject: &super::ParsedSubject| {
            assert_eq!(subject.kind, SubjectType::Vessel, "{}", subject.primary_name);
            subject.identifiers.iter().find(|i| i.kind == "IMO number").map(|i| i.value.clone())
        };
        let ofac = parse_ofac(
            &mut r#"<sanctionsData><entities><entity id="9"><generalInfo><entityType>Vessel</entityType></generalInfo>
            <names><name><isPrimary>true</isPrimary><translations><translation><isPrimary>true</isPrimary><formattedFullName>OCEAN FAYE</formattedFullName></translation></translations></name></names>
            <features><feature><type featureTypeId="3">Vessel Flag</type><value>Panama</value></feature><feature><type featureTypeId="2">VESSEL TYPE</type><value>Crude Oil Tanker</value></feature></features>
            <identityDocuments><identityDocument><type>Vessel Registration Identification</type><documentNumber>IMO 9321689</documentNumber></identityDocument>
            <identityDocument><type>MMSI</type><documentNumber>636019825</documentNumber></identityDocument></identityDocuments>
            </entity><entity id="10"><generalInfo><entityType>Aircraft</entityType></generalInfo>
            <names><name><isPrimary>true</isPrimary><translations><translation><isPrimary>true</isPrimary><formattedFullName>EP-ABC</formattedFullName></translation></translations></name></names></entity></entities></sanctionsData>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((ofac.len(), imo(&ofac[0]).as_deref()), (1, Some("9321689")));
        assert_eq!(ofac[0].identifiers[1].kind, "MMSI");
        assert_eq!(ofac[0].remarks.as_deref(), Some("Flag: Panama; Type: Crude Oil Tanker"));

        let uk = parse_uk(
            &mut r#"<Designations><Designation><UniqueID>RUS2001</UniqueID><IndividualEntityShip>Ship</IndividualEntityShip>
            <Names><Name><Name6>Andaman Skies</Name6><NameType>Primary name</NameType></Name></Names>
            <ShipDetails><Ship><IMONumbers><IMONumber>IMO9288693</IMONumber><IMONumber>IMO9288693</IMONumber></IMONumbers>
            <CurrentBelievedFlagOfShips><CurrentBelievedFlagOfShip>Comoros</CurrentBelievedFlagOfShip></CurrentBelievedFlagOfShips></Ship></ShipDetails>
            </Designation></Designations>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((imo(&uk[0]).as_deref(), uk[0].identifiers.len()), (Some("9288693"), 1));
        assert_eq!(uk[0].remarks.as_deref(), Some("Flag: Comoros"));

        let canada = parse_canada(
            &mut r#"<data-set><record><Country-Pays>Russia / Russie</Country-Pays><EntityOrShip-EntiteOuNavire>M/V Maria </EntityOrShip-EntiteOuNavire>
            <TitleOrShipType-TitreOuTypeDeNavire>General Cargo | Cargo classique</TitleOrShipType-TitreOuTypeDeNavire>
            <ShipIMONumber-NumeroOMIDuNavire> 8517839 </ShipIMONumber-NumeroOMIDuNavire><Schedule-Annexe>1.1</Schedule-Annexe><Item-NumeroDarticle>2</Item-NumeroDarticle></record></data-set>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((imo(&canada[0]).as_deref(), canada[0].remarks.as_deref()), (Some("8517839"), Some("Type: General Cargo")));

        let france = parse_france(
            &mut r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 2218, "Nature": "Navire", "Nom": "HUI CHON",
            "RegistreDetail": [{"TypeChamp": "ALIAS", "Valeur": [{"Alias": "HWANG GUM SAN 2"}]}, {"TypeChamp": "NUMERO_OMI", "Valeur": [{"NumeroOMI": "8405270"}]}]}]}}"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((imo(&france[0]).as_deref(), france[0].names.len()), (Some("8405270"), 2));

        let seco = parse_seco(
            &mut r#"<swiss-sanctions-list><target ssid="34461"><sanctions-set-id>1</sanctions-set-id><object object-type="vessel"><identity ssid="2" main="true">
            <name ssid="3" name-type="primary-name" quality="good" lang="eng"><name-part order="1" name-part-type="whole-name"><value>Hui Chon</value></name-part></name></identity>
            <other-information ssid="4">IMO Numbe: 8405270</other-information></object><modification modification-type="listed" effective-date="2016-05-18"/></target>
            <target ssid="5"><object object-type="aircraft"><identity ssid="6" main="true"><name ssid="7" name-type="primary-name" quality="good"><name-part order="1"><value>EP-ABC</value></name-part></name></identity></object></target></swiss-sanctions-list>"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((seco.len(), imo(&seco[0]).as_deref()), (1, Some("8405270")));
    }

    #[test]
    fn france_reads_a_well_formed_entry() {
        let subjects = parse_france(
            &mut r#"{"Publications": {"PublicationDetail": [{"IdRegistre": 101, "Nature": "Personne physique", "Nom": "SOKOLOV",
            "RegistreDetail": [{"TypeChamp": "PRENOM", "Valeur": [{"Prenom": "Ivan"}]}, {"TypeChamp": "ALIAS", "Valeur": [{"Alias": "Vanya", "Commentaire": "alias faible"}]}]}]}}"#
                .as_bytes(),
        )
        .unwrap();
        assert_eq!((subjects.len(), subjects[0].primary_name.as_str(), subjects[0].names.len()), (1, "Ivan SOKOLOV", 2));
        assert!(subjects[0].names[1].is_weak);
    }
}
