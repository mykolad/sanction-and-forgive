//! Destinations under broad restrictions (#67; owner's decisions, 2026-11-18; the Rust port first): a table curated by
//! administrators from the official sources, of countries, regions and settlements. An entered address is read for
//! their names, whatever country it names ("Simferopol, Russia" is in Crimea), and for its country. The most specific
//! place decides within its own region (a settlement over its region, a region over its country), the most severe
//! otherwise. Never a percentage and never a list's cell; deterministic: the same address and table version give the
//! same notice.

use chrono::NaiveDate;

use super::addresses::{address_parts, lookup, names_a_street, AddressForm};
use super::data::{keys, MatcherData};
use super::text::{words, TokenRef, Tokenizer};
use super::HashMap;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DestinationKind {
    Country,
    Region,
    Settlement,
}

impl DestinationKind {
    pub const ALL: [DestinationKind; 3] = [DestinationKind::Country, DestinationKind::Region, DestinationKind::Settlement];

    pub fn as_str(&self) -> &'static str {
        match self {
            DestinationKind::Country => "Country",
            DestinationKind::Region => "Region",
            DestinationKind::Settlement => "Settlement",
        }
    }

    /// Matched ignoring case.
    pub fn parse(value: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|k| k.as_str().eq_ignore_ascii_case(value.trim()))
    }
}

/// Ordered by severity: Outside < Partly < Restricted.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum DestinationStatus {
    /// Outside the restricted areas (a settlement inside a partly restricted region).
    Outside,
    /// Partly restricted: an address that names no place inside it can't tell on which side of the line it is.
    Partly,
    Restricted,
}

impl DestinationStatus {
    pub const ALL: [DestinationStatus; 3] = [DestinationStatus::Restricted, DestinationStatus::Partly, DestinationStatus::Outside];

    pub fn as_str(&self) -> &'static str {
        match self {
            DestinationStatus::Outside => "Outside",
            DestinationStatus::Partly => "Partly",
            DestinationStatus::Restricted => "Restricted",
        }
    }

    /// Matched ignoring case.
    pub fn parse(value: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|s| s.as_str().eq_ignore_ascii_case(value.trim()))
    }
}

/// The words that name a region of some country, as an address is read ("Vinnytsia Oblast", "Ростовская обл.").
const REGION_WORDS: [&str; 12] =
    ["oblast", "oblasti", "region", "province", "krai", "kray", "область", "обл", "області", "край", "краю", "провінція"];

/// One row of the table in force.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Destination {
    pub id: i32,
    /// Stable across changes ("ua-crimea"); a settlement names its region by it.
    pub code: String,
    pub kind: DestinationKind,
    pub name: String,
    pub other_names: Vec<String>,
    /// ISO alpha-2: the country itself for a Country, the one it lies in otherwise (may be empty).
    pub country: String,
    /// A settlement's region (its code).
    pub parent: Option<String>,
    /// Recognised only when the address names its region or no other region (a name that repeats).
    pub needs_region: bool,
    pub status: DestinationStatus,
    pub restricted_by: String,
    pub legal_source: String,
    pub as_of: NaiveDate,
    pub review_by: NaiveDate,
}

impl Destination {
    /// Its name and other names.
    pub fn names(&self) -> impl Iterator<Item = &str> {
        std::iter::once(self.name.as_str()).chain(self.other_names.iter().map(String::as_str))
    }

    /// Whether other lies inside this one: a settlement in this region, or a region or settlement in this country.
    fn contains(&self, other: &Destination) -> bool {
        match self.kind {
            DestinationKind::Country => other.kind != DestinationKind::Country && !self.country.is_empty() && other.country == self.country,
            DestinationKind::Region => other.kind == DestinationKind::Settlement && other.parent.as_deref() == Some(self.code.as_str()),
            DestinationKind::Settlement => false,
        }
    }

    fn specificity(&self) -> u8 {
        match self.kind {
            DestinationKind::Country => 0,
            DestinationKind::Region => 1,
            DestinationKind::Settlement => 2,
        }
    }
}

/// What a recognised address means: in a restricted area, or possibly (a partly restricted region, the side of the line
/// unknown). An address outside every restricted area has no notice.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DestinationState {
    Restricted,
    PossiblyRestricted,
}

impl DestinationState {
    pub fn as_str(&self) -> &'static str {
        match self {
            DestinationState::Restricted => "restricted",
            DestinationState::PossiblyRestricted => "possiblyRestricted",
        }
    }
}

/// The notice for one address: its state and the row that decided it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DestinationNotice {
    pub state: DestinationState,
    pub destination: Destination,
}

/// The table in force, indexed by name. Immutable, with a version (additions plus removals, as the allow-list's).
#[derive(Debug, Clone)]
pub struct Destinations {
    rows: Vec<Destination>,
    by_key: HashMap<String, Vec<usize>>,
    max_words: usize,
    pub version: String,
}

impl Destinations {
    pub fn empty() -> Self {
        Destinations { rows: Vec::new(), by_key: HashMap::default(), max_words: 0, version: "none".to_string() }
    }

    /// Indexes every name of every row, in the order of their ids.
    pub fn build(mut rows: Vec<Destination>, version: &str) -> Self {
        rows.sort_by_key(|r| r.id);
        let mut by_key: HashMap<String, Vec<usize>> = HashMap::default();
        for (i, row) in rows.iter().enumerate() {
            for name in row.names() {
                for k in keys(name) {
                    if k.is_empty() {
                        continue;
                    }
                    let found = by_key.entry(k).or_default();
                    if !found.contains(&i) {
                        found.push(i);
                    }
                }
            }
        }
        let max_words = by_key.keys().map(|k| k.matches(' ').count() + 1).max().unwrap_or(0);
        Destinations { rows, by_key, max_words, version: version.to_string() }
    }

    /// This table without the row of code ("12-ua-crimea": version 12 without it).
    pub fn without(&self, code: &str) -> Self {
        let rows = self.rows.iter().filter(|r| r.code != code).cloned().collect();
        Destinations::build(rows, &format!("{}-{code}", self.version))
    }

    pub fn rows(&self) -> &[Destination] {
        &self.rows
    }

    /// The earliest review date already passed on `today`, if any: then every result warns, as for an overdue list.
    pub fn review_overdue(&self, today: NaiveDate) -> Option<NaiveDate> {
        self.rows.iter().map(|r| r.review_by).filter(|&d| d < today).min()
    }

    /// Why a name of row may be read as another place: a row in force under another code has it, a place in another
    /// country (places.json) or a country (countries.json) is called that. For the administrator to confirm first.
    pub fn name_conflicts(&self, data: &MatcherData, row: &Destination) -> Vec<String> {
        let mut out: Vec<String> = Vec::new();
        let mut push = |line: String| {
            if !out.contains(&line) {
                out.push(line);
            }
        };
        for name in row.names() {
            for k in keys(name) {
                for &i in self.by_key.get(&k).map(Vec::as_slice).unwrap_or_default() {
                    let other = &self.rows[i];
                    if other.code != row.code {
                        push(format!("{name}: also a name of {} ({})", other.name, other.code));
                    }
                }
                if let Some(place) = data.places.get(&k).filter(|p| p.country != row.country) {
                    push(format!("{name}: also {} ({})", place.name, place.country));
                }
                if let Some(code) = data.countries.get(&k).filter(|c| row.kind != DestinationKind::Country || **c != row.country) {
                    push(format!("{name}: also the name of a country ({code})"));
                }
            }
        }
        out
    }

    /// The notice for an entered address, read as `form` (its country) and word by word for the table's names.
    pub fn find(&self, data: &MatcherData, tokenizer: &mut Tokenizer, raw: &str, form: &AddressForm) -> Option<DestinationNotice> {
        if self.rows.is_empty() {
            return None;
        }
        let ws = words(raw);
        let tokens: Vec<TokenRef> = ws.iter().map(|w| tokenizer.get(w)).collect();
        let parts = address_parts(raw, tokens.len());
        let mut matched: Vec<usize> = Vec::new();
        let add = |i: usize, matched: &mut Vec<usize>| {
            if !matched.contains(&i) {
                matched.push(i);
            }
        };
        let mut uncovered: Vec<&TokenRef> = Vec::new();
        let mut i = 0;
        while i < tokens.len() {
            match lookup(&tokens, i, self.max_words, &self.by_key) {
                Some((found, length)) => {
                    // A name that names a street ("Crimea Road", "rue de Cuba") is the street's.
                    if !names_a_street(data, &tokens, &parts, i, length, false) {
                        for &row in found {
                            add(row, &mut matched);
                        }
                    }
                    i += length;
                }
                None => {
                    uncovered.push(&tokens[i]);
                    i += 1;
                }
            }
        }
        let country = form.effective_country();
        if !country.is_empty() {
            for (row, destination) in self.rows.iter().enumerate() {
                if destination.kind == DestinationKind::Country && destination.country == country {
                    add(row, &mut matched);
                }
            }
        }

        // A name that repeats counts only when the address names its region, or names no region at all: neither one of
        // the table's nor another ("Vasylivka, Vinnytsia Oblast" is another Vasylivka).
        let regions: Vec<&str> =
            matched.iter().map(|&r| &self.rows[r]).filter(|d| d.kind == DestinationKind::Region).map(|d| d.code.as_str()).collect();
        let another_region = regions.is_empty() && uncovered.iter().any(|t| REGION_WORDS.contains(&t.text.as_str()));
        matched.retain(|&r| {
            let d = &self.rows[r];
            !d.needs_region || d.parent.as_deref().is_some_and(|p| regions.contains(&p)) || (regions.is_empty() && !another_region)
        });
        // A place named inside another decides for it; of the rest, the most severe.
        let decisive = matched
            .iter()
            .map(|&r| &self.rows[r])
            .filter(|d| !matched.iter().any(|&other| self.rows[other].id != d.id && d.contains(&self.rows[other])))
            .max_by(|a, b| {
                (a.status, a.specificity(), std::cmp::Reverse(a.id)).cmp(&(b.status, b.specificity(), std::cmp::Reverse(b.id)))
            })?;
        let state = match decisive.status {
            DestinationStatus::Restricted => DestinationState::Restricted,
            DestinationStatus::Partly => DestinationState::PossiblyRestricted,
            DestinationStatus::Outside => return None,
        };
        Some(DestinationNotice { state, destination: decisive.clone() })
    }
}

#[cfg(test)]
mod tests {
    use super::super::addresses::parse_address;
    use super::super::data::data;
    use super::*;

    fn row(
        id: i32,
        code: &str,
        kind: DestinationKind,
        names: &[&str],
        country: &str,
        parent: Option<&str>,
        status: DestinationStatus,
    ) -> Destination {
        Destination {
            id,
            code: code.to_string(),
            kind,
            name: names[0].to_string(),
            other_names: names[1..].iter().map(|n| n.to_string()).collect(),
            country: country.to_string(),
            parent: parent.map(str::to_string),
            needs_region: false,
            status,
            restricted_by: "EU, UK".to_string(),
            legal_source: "Fictional regulation".to_string(),
            as_of: NaiveDate::from_ymd_opt(2026, 11, 18).unwrap(),
            review_by: NaiveDate::from_ymd_opt(2027, 11, 18).unwrap(),
        }
    }

    fn table() -> Destinations {
        use DestinationKind::*;
        use DestinationStatus::*;
        let mut rows = vec![
            row(1, "ir", Country, &["Iran"], "IR", None, Restricted),
            row(2, "ua-crimea", Region, &["Crimea", "Krym", "Крим", "Крым"], "UA", None, Restricted),
            row(3, "ua-simferopol", Settlement, &["Simferopol", "Сімферополь", "Симферополь"], "UA", Some("ua-crimea"), Restricted),
            row(4, "ua-kherson-oblast", Region, &["Kherson Oblast", "Kherson region", "Херсонська область"], "UA", None, Partly),
            row(5, "ua-kherson", Settlement, &["Kherson", "Херсон"], "UA", Some("ua-kherson-oblast"), Outside),
            row(6, "ua-nova-kakhovka", Settlement, &["Nova Kakhovka", "Нова Каховка"], "UA", Some("ua-kherson-oblast"), Restricted),
        ];
        let mut repeated = row(7, "ua-kamianka", Settlement, &["Kamianka"], "UA", Some("ua-kherson-oblast"), Restricted);
        repeated.needs_region = true;
        rows.push(repeated);
        Destinations::build(rows, "7")
    }

    fn find(address: &str) -> Option<(DestinationState, String)> {
        let data = data();
        let mut tokenizer = Tokenizer::new(data);
        let form = parse_address(data, &mut tokenizer, address, "");
        table().find(data, &mut tokenizer, address, &form).map(|n| (n.state, n.destination.code))
    }

    #[test]
    fn a_place_in_a_restricted_area_is_recognised_whatever_country_the_address_names() {
        for address in ["ul. Pushkina 5, Simferopol, Russia", "Simferopol, Ukraine", "вул. Пушкіна 5, Сімферополь", "Симферополь, Россия"]
        {
            assert_eq!(find(address), Some((DestinationState::Restricted, "ua-simferopol".to_string())), "{address}");
        }
        assert_eq!(find("Krym"), Some((DestinationState::Restricted, "ua-crimea".to_string())));
    }

    #[test]
    fn a_country_is_recognised_by_its_name_or_a_place_in_it() {
        assert_eq!(find("12 Azadi Street, Tehran, Iran"), Some((DestinationState::Restricted, "ir".to_string())));
        assert_eq!(find("Tehran"), Some((DestinationState::Restricted, "ir".to_string())));
        assert_eq!(find("Dubai, United Arab Emirates"), None);
    }

    #[test]
    fn a_settlement_decides_for_its_region_and_an_unknown_place_in_a_partly_restricted_region_is_possibly_restricted() {
        assert_eq!(find("Ushakova 5, Kherson, Kherson Oblast"), None);
        assert_eq!(find("Nova Kakhovka, Kherson Oblast"), Some((DestinationState::Restricted, "ua-nova-kakhovka".to_string())));
        assert_eq!(
            find("Some Village, Kherson region, Ukraine"),
            Some((DestinationState::PossiblyRestricted, "ua-kherson-oblast".to_string()))
        );
        assert_eq!(find("Херсонська область"), Some((DestinationState::PossiblyRestricted, "ua-kherson-oblast".to_string())));
        // Two places, neither inside the other: the most severe.
        assert_eq!(find("Kherson to Simferopol"), Some((DestinationState::Restricted, "ua-simferopol".to_string())));
    }

    #[test]
    fn a_name_that_repeats_counts_only_with_its_region_or_no_region() {
        assert_eq!(find("Kamianka"), Some((DestinationState::Restricted, "ua-kamianka".to_string())));
        assert_eq!(find("Kamianka, Kherson Oblast"), Some((DestinationState::Restricted, "ua-kamianka".to_string())));
        assert_eq!(find("Kamianka, Crimea"), Some((DestinationState::Restricted, "ua-crimea".to_string())));
    }

    #[test]
    fn a_street_named_after_a_place_is_not_the_place_and_nothing_entered_is_no_notice() {
        assert_eq!(find("5 Crimea Road, Aldershot, United Kingdom"), None);
        assert_eq!(find("Kyiv, Ukraine"), None);
        let data = data();
        let mut tokenizer = Tokenizer::new(data);
        let form = parse_address(data, &mut tokenizer, "Simferopol", "");
        assert_eq!(Destinations::empty().find(data, &mut tokenizer, "Simferopol", &form), None);
    }

    #[test]
    fn a_review_date_passed_is_reported() {
        let t = table();
        assert_eq!(t.review_overdue(NaiveDate::from_ymd_opt(2027, 11, 18).unwrap()), None);
        assert_eq!(t.review_overdue(NaiveDate::from_ymd_opt(2027, 11, 19).unwrap()), NaiveDate::from_ymd_opt(2027, 11, 18));
    }
}
