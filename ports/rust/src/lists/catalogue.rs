//! The feed catalogue (ListSources.cs in the reference). Manual sources live only in the database.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FeedStatus {
    Active,
    TermsUnclear,
    NeedsAccess,
    /// Uploaded by an administrator from the publisher's file (the admin page): never downloaded by the app, every
    /// upload held for review (Rust first).
    Upload,
}

impl FeedStatus {
    pub fn as_str(&self) -> &'static str {
        match self {
            FeedStatus::Active => "Active",
            FeedStatus::TermsUnclear => "TermsUnclear",
            FeedStatus::NeedsAccess => "NeedsAccess",
            FeedStatus::Upload => "Upload",
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
    /// For a feed published under a new name each time: url is the publisher's page, and the file is the link on it
    /// whose address matches this (manual::find_link).
    pub link_pattern: &'static str,
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

const fn source(
    code: &'static str,
    name: &'static str,
    url: &'static str,
    status: FeedStatus,
    homepage: &'static str,
    terms_url: &'static str,
    note: &'static str,
) -> ListSource {
    ListSource { code, name, url, max_age_hours: DAILY, status, homepage, terms_url, note, link_pattern: "" }
}

/// An uploaded list's allowed age: a year (owner's decision, 2026-11-21). Past it, the list is searched as last
/// uploaded, but Overdue.
pub const UPLOADED: i32 = 365 * 24;

/// The files a list published as several must be uploaded with, one per kind of party, so none of its parties drop out
/// because a file was left behind: the names of the kinds none of `files` is (each file's kind from its header, so a
/// file whose entries are all gone still counts).
pub fn missing_upload_files(code: &str, files: &[&[u8]]) -> Vec<&'static str> {
    use crate::lists::parse::SubjectType;
    type KindOf = fn(&[u8]) -> Option<SubjectType>;
    let (required, kind_of): (&[(SubjectType, &str)], KindOf) = match code {
        "ua-nsdc" => (
            &[(SubjectType::Individual, "people"), (SubjectType::Entity, "companies"), (SubjectType::Vessel, "vessels")],
            crate::lists::nsdc::file_kind,
        ),
        _ => return Vec::new(),
    };
    let kinds: Vec<SubjectType> = files.iter().filter_map(|file| kind_of(file)).collect();
    required.iter().filter(|(kind, _)| !kinds.contains(kind)).map(|(_, name)| *name).collect()
}

/// The most an upload of the list may send, its files together: far above the list's real size (Australia's file is
/// 1.3 MB; Ukraine's three exports about 110 MB, most of it their sanctions' text), so a replica's memory holds it.
pub fn max_upload_bytes(code: &str) -> u64 {
    match code {
        "ua-nsdc" => 256 << 20,
        _ => 16 << 20,
    }
}

/// A list an administrator uploads (FeedStatus::Upload): no address to download, a year's allowed age.
const fn uploaded(
    code: &'static str,
    name: &'static str,
    homepage: &'static str,
    terms_url: &'static str,
    note: &'static str,
) -> ListSource {
    ListSource { code, name, url: "", max_age_hours: UPLOADED, status: FeedStatus::Upload, homepage, terms_url, note, link_pattern: "" }
}

/// The catalogue, in the reference's order.
pub static SOURCES: [ListSource; 19] = [
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
    uploaded(
        "au-dfat",
        "Australia consolidated list",
        "https://www.dfat.gov.au/international-relations/security/sanctions/consolidated-list",
        "",
        "Uploaded by an administrator: the publisher's site refuses automated downloads from a client that identifies itself honestly. The Consolidated List (XLSX) is downloaded in a browser and uploaded on the admin page.",
    ),
    uploaded(
        "ua-nsdc",
        "Ukraine NSDC State Register of Sanctions",
        "https://drs.nsdc.gov.ua/",
        "",
        "Uploaded by an administrator: the register's API needs a key, and its site's exports aren't open to an automated client. Its subjects' exports (people, companies, vessels) are downloaded in a browser and uploaded together on the admin page.",
    ),
    source(
        "fr-tresor",
        "France national asset-freeze register (DG Trésor)",
        "https://gels-avoirs.dgtresor.gouv.fr/ApiPublic/api/v1/publication/derniere-publication-fichier-json",
        FeedStatus::Active,
        "https://gels-avoirs.dgtresor.gouv.fr/",
        "https://www.etalab.gouv.fr/licence-ouverte-open-licence/",
        "",
    ),
    source(
        "nl-terrorism",
        "Netherlands national terrorism list",
        "https://www.government.nl/site/binaries/site-content/collections/documents/2016/01/15/national-terrorism-list/eng-terrorismelijst.ods",
        FeedStatus::Active,
        "https://www.government.nl/documents/2016/01/15/national-terrorism-list",
        "https://www.government.nl/copyright",
        "",
    ),
    source(
        "nz-mfat",
        "New Zealand Russia Sanctions Register (MFAT)",
        "https://www.mfat.govt.nz/assets/Countries-and-Regions/Europe/Ukraine/Russia-Sanctions-Register.xlsx",
        FeedStatus::Active,
        "https://www.mfat.govt.nz/en/countries-and-regions/europe/ukraine/russian-invasion-of-ukraine/sanctions/russia-sanctions-register",
        "https://www.mfat.govt.nz/en/copyright",
        "",
    ),
    ListSource {
        link_pattern: r"/[0-9]{8}[^/]*[Nn]ationale[^/]*\.xlsx$",
        ..source(
            "be-terrorism",
            "Belgium national terrorism list (FPS Finance)",
            "https://finance.belgium.be/en/about_fps/structure_and_services/general_administrations/treasury/financial-sanctions/national",
            FeedStatus::Active,
            "https://finance.belgium.be/en/about_fps/structure_and_services/general_administrations/treasury/financial-sanctions/national",
            "https://fin.belgium.be/en/terms-use",
            "",
        )
    },
    ListSource {
        link_pattern: r"/shisantouketsu[0-9]{8}\.csv$",
        ..source(
            "jp-mof",
            "Japan asset-freeze list (Ministry of Finance)",
            "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html",
            FeedStatus::Active,
            "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html",
            "https://www.mof.go.jp/about_mof/notice/index.html",
            "",
        )
    },
    source(
        "us-uflpa",
        "US UFLPA Entity List (forced-labour import ban)",
        "https://www.dhs.gov/uflpa-entity-list",
        FeedStatus::Active,
        "https://www.dhs.gov/uflpa-entity-list",
        "https://www.usa.gov/government-copyright",
        "",
    ),
    source("ua-sfms", "Ukraine SFMS terrorism list", "https://fiu.gov.ua/assets/userfiles/Terror/BlackListFull.xml", FeedStatus::Active, "https://fiu.gov.ua/", "", ""),
    // Rust only (owner's decision, 2026-11-22): Poland's national list, uploaded; Latvia's frozen assets, imported daily
    // from the open data portal (CC0).
    uploaded(
        "pl-mswia",
        "Poland national sanctions list (MSWiA)",
        "https://www.gov.pl/web/mswia/lista-osob-i-podmiotow-objetych-sankcjami",
        "https://www.gov.pl/web/gov/warunki-korzystania",
        "Uploaded by an administrator: the ministry's table (XLSX) is downloaded in a browser from the list's page and uploaded on the admin page. People and entities under Poland's own measures (an asset freeze, or for some people an entry ban only, which a hit says).",
    ),
    source(
        "lv-fid",
        "Latvia: assets frozen in Latvia (FIU)",
        "https://data.gov.lv/dati/dataset/de595e8e-218f-4f43-8722-c6b9c66e94b1/resource/00f557d4-3096-4d8f-b0bb-c52f90ad50fb/download/sankciju_subjekti_tabula.xlsx",
        FeedStatus::Active,
        "https://sankcijas.fid.gov.lv/lv/iesaldetie-aktivi/sankciju-subjekti",
        "https://data.gov.lv/dati/dataset/sankciju-subjektu-lidzekli-un-saimnieciskie-resursi-kas-iesaldeti-latvija",
        "",
    ),
];

/// The feeds that list ships, whose parsers keep them (#69): one of these whose import has no ship was imported
/// before vessels were kept, so its vessels can't be checked until it's imported again.
pub const LISTS_WITH_VESSELS: [&str; 6] = ["ofac-sdn", "uk", "ca-sema", "ch-seco", "fr-tresor", "nz-mfat"];

/// A catalogued feed by code, ignoring case.
pub fn find_source(code: &str) -> Option<&'static ListSource> {
    SOURCES.iter().find(|s| s.code.eq_ignore_ascii_case(code))
}

/// None for an empty string.
pub fn optional(value: &str) -> Option<String> {
    (!value.is_empty()).then(|| value.to_string())
}
