//! Destinations under broad restrictions (#67; owner's decisions, 2026-11-18; the Rust port first): a table curated by
//! administrators from the official sources, of countries, regions and settlements. An entered address is read for
//! their names, whatever country it names ("Simferopol, Russia" is in Crimea), and for its country. The most specific
//! place decides within its own region (a settlement over its region, a region over its country), the most severe
//! otherwise. Never a percentage and never a list's cell; deterministic: the same address and table version give the
//! same notice.

use chrono::NaiveDate;

use super::addresses::{address_parts, lookup, names_a_street, trailing_code};
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

/// Street types beyond the matcher's (words.json, which the matcher version hashes): abbreviations written after a
/// street's name ("5 Kherson Rd") and Ukrainian and Russian ones written before it ("вул. Симферопольська" is the
/// usual form, but the name right after one is the street's either way).
const STREET_AFTER: [&str; 9] = ["rd", "st", "str", "ave", "av", "blvd", "ln", "dr", "hwy"];
const STREET_BEFORE: [&str; 15] =
    ["vul", "vulytsia", "ul", "ulitsa", "prospekt", "prosp", "pr", "per", "provulok", "вул", "вулиця", "ул", "улица", "просп", "проспект"];

/// Whether the name of length words at start names a street: the matcher's rule, or one of the street types above
/// right after or right before it, in the same part of the address.
fn names_a_street_here(data: &MatcherData, tokens: &[TokenRef], parts: &[i64], start: usize, length: usize) -> bool {
    let end = start + length;
    let is = |t: &TokenRef, words: &[&str]| words.contains(&t.text.as_str());
    names_a_street(data, tokens, parts, start, length, false)
        || (end < tokens.len() && parts[end] == parts[end - 1] && is(&tokens[end], &STREET_AFTER))
        || (start > 0 && parts[start - 1] == parts[start] && is(&tokens[start - 1], &STREET_BEFORE))
}

/// The first (or, with last, the last) of the names in names that the address has and that doesn't name a street.
fn first_outside_streets<T: Clone>(
    data: &MatcherData,
    tokens: &[TokenRef],
    parts: &[i64],
    names: &HashMap<String, T>,
    max_words: usize,
    last: bool,
) -> Option<T> {
    let (mut found, mut i) = (None, 0);
    while i < tokens.len() {
        match lookup(tokens, i, max_words, names) {
            Some((value, length)) => {
                if !names_a_street_here(data, tokens, parts, i, length) {
                    found = Some(value.clone());
                    if !last {
                        break;
                    }
                }
                i += length;
            }
            None => i += 1,
        }
    }
    found
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
        // Read as an address is read: each name's words, on their own text or one of their Latin readings, so a Cyrillic
        // spelling of a name already in the table ("Атлантис" and "Atlantis") counts.
        let mut tokenizer = Tokenizer::new(data);
        let mut whole = |name: &str| -> Vec<TokenRef> { words(name).iter().map(|w| tokenizer.get(w)).collect() };
        let whole_name = |tokens: &[TokenRef], max: usize| max.min(tokens.len());
        let this = Destinations::build(vec![row.clone()], "new");
        for name in row.names() {
            let tokens = whole(name);
            if tokens.is_empty() {
                continue;
            }
            let full = |found: Option<(&Vec<usize>, usize)>| found.filter(|(_, length)| *length == tokens.len()).map(|(f, _)| f.clone());
            for i in full(lookup(&tokens, 0, whole_name(&tokens, self.max_words), &self.by_key)).unwrap_or_default() {
                let other = &self.rows[i];
                if other.code != row.code {
                    push(format!("{name}: also a name of {} ({})", other.name, other.code));
                }
            }
            if let Some((place, length)) = lookup(&tokens, 0, whole_name(&tokens, data.max_place_words), &data.places) {
                if length == tokens.len() && place.country != row.country {
                    push(format!("{name}: also {} ({})", place.name, place.country));
                }
            }
            if let Some((code, length)) = lookup(&tokens, 0, whole_name(&tokens, data.max_country_words), &data.countries) {
                if length == tokens.len() && (row.kind != DestinationKind::Country || *code != row.country) {
                    push(format!("{name}: also the name of a country ({code})"));
                }
            }
        }
        // And the other way: a name in the table that this row's names would be found for.
        for other in self.rows.iter().filter(|o| o.code != row.code) {
            for name in other.names() {
                let tokens = whole(name);
                if !tokens.is_empty()
                    && lookup(&tokens, 0, whole_name(&tokens, this.max_words), &this.by_key).is_some_and(|(_, l)| l == tokens.len())
                {
                    push(format!("{name}: a name of {} ({}), also read as this row", other.name, other.code));
                }
            }
        }
        out
    }

    /// The notice for an entered address, read as `form` (its country) and word by word for the table's names.
    pub fn find(&self, data: &MatcherData, tokenizer: &mut Tokenizer, raw: &str) -> Option<DestinationNotice> {
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
                    if !names_a_street_here(data, &tokens, &parts, i, length) {
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
        // The address's country, in parse_address's order but with this module's street rule (which knows "Rd" and
        // "vul."): a final ISO code; else the last country named that doesn't name a street ("5 Iran St, Leeds" isn't
        // in Iran); else the first place named that doesn't ("12 Tehran Road" isn't either).
        let mut country = trailing_code(raw).filter(|c| data.country_codes.contains(c)).unwrap_or_default();
        if country.is_empty() {
            country = first_outside_streets(data, &tokens, &parts, &data.countries, data.max_country_words, true).unwrap_or_default();
        }
        if country.is_empty() {
            country = first_outside_streets(data, &tokens, &parts, &data.places, data.max_place_words, false)
                .map(|place| place.country)
                .unwrap_or_default();
        }
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
        table().find(data, &mut tokenizer, address).map(|n| (n.state, n.destination.code))
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
        // A street named after a place is no evidence of its country, abbreviated or not.
        assert_eq!(
            find("5 Kherson Rd, Kherson Oblast, Ukraine"),
            Some((DestinationState::PossiblyRestricted, "ua-kherson-oblast".to_string()))
        );
        assert_eq!(find("vul. Simferopol 5, Kyiv"), None);
        assert_eq!(find("5 Iran St, Leeds"), None);
        assert_eq!(find("5 Iran St, Leeds, IR"), Some((DestinationState::Restricted, "ir".to_string())));
        assert_eq!(find("12 Tehran Road, Leeds"), None);
        assert_eq!(find("12 Tehran Road"), None);
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
        assert_eq!(Destinations::empty().find(data, &mut tokenizer, "Simferopol"), None);
    }

    #[test]
    fn a_name_read_as_another_row_or_place_is_a_conflict_whatever_its_script() {
        use DestinationKind::*;
        use DestinationStatus::*;
        let t = table();
        let cyrillic = row(20, "xx-krym", Settlement, &["Крым"], "UA", Some("ua-crimea"), Outside);
        assert!(t.name_conflicts(data(), &cyrillic).iter().any(|c| c.contains("ua-crimea")), "{:?}", t.name_conflicts(data(), &cyrillic));
        let latin = row(21, "xx-simf", Settlement, &["Simferopol"], "UA", Some("ua-crimea"), Outside);
        assert!(t.name_conflicts(data(), &latin).iter().any(|c| c.contains("ua-simferopol")));
        let moscow = row(22, "xx-moscow", Settlement, &["Москва"], "UA", Some("ua-crimea"), Restricted);
        assert!(t.name_conflicts(data(), &moscow).iter().any(|c| c.contains("(RU)")));
        assert!(t.name_conflicts(data(), &row(23, "xx-new", Settlement, &["Hollowmere"], "UA", Some("ua-crimea"), Restricted)).is_empty());
    }

    #[test]
    fn a_review_date_passed_is_reported() {
        let t = table();
        assert_eq!(t.review_overdue(NaiveDate::from_ymd_opt(2027, 11, 18).unwrap()), None);
        assert_eq!(t.review_overdue(NaiveDate::from_ymd_opt(2027, 11, 19).unwrap()), NaiveDate::from_ymd_opt(2027, 11, 18));
    }
}
