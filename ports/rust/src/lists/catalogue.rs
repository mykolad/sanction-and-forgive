//! The feed catalogue (ListSources.cs in the reference). Manual sources live only in the database.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FeedStatus {
    Active,
    TermsUnclear,
    NeedsAccess,
}

impl FeedStatus {
    pub fn as_str(&self) -> &'static str {
        match self {
            FeedStatus::Active => "Active",
            FeedStatus::TermsUnclear => "TermsUnclear",
            FeedStatus::NeedsAccess => "NeedsAccess",
        }
    }
}

/// A public feed in the catalogue.
#[derive(Debug, Clone)]
pub struct ListSource {
    pub code: &'static str,
    pub name: &'static str,
    pub url: &'static str,
    pub max_age_hours: i32,
    pub status: FeedStatus,
    pub homepage: &'static str,
    pub terms_url: &'static str,
    pub note: &'static str,
}

/// The EU's published public token for the FSF files.
pub const EU_PUBLIC_TOKEN: &str = "dG9rZW4tMjAxNw";

impl ListSource {
    /// The file's address as anyone may use it (the EU's with its public token).
    pub fn public_url(&self) -> Option<String> {
        (!self.url.is_empty()).then(|| self.url.replace("{token}", EU_PUBLIC_TOKEN))
    }
}

const DAILY: i32 = 72;

const fn source(code: &'static str, name: &'static str, url: &'static str, status: FeedStatus, homepage: &'static str, terms_url: &'static str, note: &'static str) -> ListSource {
    ListSource { code, name, url, max_age_hours: DAILY, status, homepage, terms_url, note }
}

/// The catalogue, in the reference's order.
pub static SOURCES: [ListSource; 11] = [
    source(
        "eu",
        "EU consolidated financial sanctions",
        "https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token={token}",
        FeedStatus::Active,
        "https://data.europa.eu/data/datasets/consolidated-list-of-persons-groups-and-entities-subject-to-eu-financial-sanctions",
        "https://commission.europa.eu/legal-notice_en",
        "",
    ),
    source(
        "ofac-sdn",
        "OFAC SDN",
        "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN_ENHANCED.XML",
        FeedStatus::Active,
        "https://ofac.treasury.gov/specially-designated-nationals-and-blocked-persons-list-sdn-human-readable-lists",
        "https://sanctionslist.ofac.treas.gov/Home/static/index.html",
        "",
    ),
    source(
        "ofac-cons",
        "OFAC consolidated (non-SDN)",
        "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/CONS_ENHANCED.XML",
        FeedStatus::Active,
        "https://ofac.treasury.gov/consolidated-sanctions-list-non-sdn-lists",
        "https://sanctionslist.ofac.treas.gov/Home/static/index.html",
        "",
    ),
    source(
        "un",
        "UN Security Council consolidated",
        "https://scsanctions.un.org/resources/xml/en/consolidated.xml",
        FeedStatus::TermsUnclear,
        "https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list",
        "https://www.un.org/en/about-us/terms-of-use",
        "The UN's terms allow personal, non-commercial use only. Waiting for a decision. Its listings are also on the EU and UK lists, which implement them.",
    ),
    source(
        "uk",
        "UK Sanctions List",
        "https://sanctionslist.fcdo.gov.uk/docs/UK-Sanctions-List.xml",
        FeedStatus::Active,
        "https://www.gov.uk/government/publications/the-uk-sanctions-list",
        "https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/",
        "",
    ),
    source(
        "us-export",
        "US export screening (BIS, State)",
        "https://data.trade.gov/downloadable_consolidated_screening_list/v1/consolidated.json",
        FeedStatus::Active,
        "https://www.trade.gov/consolidated-screening-list",
        "https://developer.trade.gov/terms-of-service",
        "",
    ),
    source(
        "ch-seco",
        "Switzerland SECO",
        "https://www.sesam.search.admin.ch/sesam-search-web/pages/downloadXmlGesamtliste.xhtml?lang=en&action=downloadXmlGesamtlisteAction",
        FeedStatus::TermsUnclear,
        "https://www.seco.admin.ch/seco/en/home/Aussenwirtschaftspolitik_Wirtschaftliche_Zusammenarbeit/Wirtschaftsbeziehungen/exportkontrollen-und-sanktionen/sanktionen-embargos.html",
        "https://www.admin.ch/gov/en/start/terms-and-conditions.html",
        "The Swiss federal administration's terms require written consent for any reproduction. Waiting for permission from SECO.",
    ),
    source(
        "ca-sema",
        "Canada autonomous sanctions (SEMA)",
        "https://www.international.gc.ca/world-monde/assets/office_docs/international_relations-relations_internationales/sanctions/sema-lmes.xml",
        FeedStatus::TermsUnclear,
        "https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng",
        "https://www.canada.ca/en/transparency/terms.html",
        "Canada's terms allow non-commercial reproduction only; commercial redistribution needs written permission. Waiting for a decision.",
    ),
    source(
        "au-dfat",
        "Australia consolidated list",
        "",
        FeedStatus::NeedsAccess,
        "https://www.dfat.gov.au/international-relations/security/sanctions/consolidated-list",
        "",
        "The publisher's site refuses automated downloads from a client that identifies itself honestly, so its file and format couldn't be checked, and there is no parser for it yet.",
    ),
    source(
        "ua-nsdc",
        "Ukraine NSDC State Register of Sanctions",
        "https://api-drs.nsdc.gov.ua/v2/subjects",
        FeedStatus::NeedsAccess,
        "https://drs.nsdc.gov.ua/",
        "",
        "The register's API needs a key, which takes a registration. Waiting for one.",
    ),
    source("ua-sfms", "Ukraine SFMS terrorism list", "https://fiu.gov.ua/assets/userfiles/Terror/BlackListFull.xml", FeedStatus::Active, "https://fiu.gov.ua/", "", ""),
];

/// A catalogued feed by code, ignoring case.
pub fn find_source(code: &str) -> Option<&'static ListSource> {
    SOURCES.iter().find(|s| s.code.eq_ignore_ascii_case(code))
}

/// None for an empty string.
pub fn optional(value: &str) -> Option<String> {
    (!value.is_empty()).then(|| value.to_string())
}
