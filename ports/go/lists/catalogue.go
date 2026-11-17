// Package lists is the Go port of SanctionAndForgive.Lists: the feed catalogue, downloading, the parsers.
package lists

import "strings"

// FeedStatus says whether a feed is imported automatically (ListSources.cs in the reference).
type FeedStatus string

const (
	Active       FeedStatus = "Active"
	TermsUnclear FeedStatus = "TermsUnclear"
	NeedsAccess  FeedStatus = "NeedsAccess"
)

// ListSource is a public feed in the catalogue. Manual sources live only in the database.
type ListSource struct {
	Code        string
	Name        string
	URL         string
	MaxAgeHours int
	Status      FeedStatus
	Homepage    string
	TermsURL    string
	Note        string
	// LinkPattern: for a feed published under a new name each time, URL is the publisher's page and the file is the
	// link on it whose address matches this (FindLink).
	LinkPattern string
}

// EuPublicToken is the EU's published public token for the FSF files.
const EuPublicToken = "dG9rZW4tMjAxNw"

// PublicURL is the file's address as anyone may use it (the EU's with its public token).
func (s *ListSource) PublicURL() *string {
	if s.URL == "" {
		return nil
	}
	url := strings.ReplaceAll(s.URL, "{token}", EuPublicToken)
	return &url
}

const dailyMaxAge = 72

// Sources is the catalogue, in the reference's order.
var Sources = []ListSource{
	{Code: "eu", Name: "EU consolidated financial sanctions", URL: "https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token={token}", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://data.europa.eu/data/datasets/consolidated-list-of-persons-groups-and-entities-subject-to-eu-financial-sanctions",
		TermsURL: "https://commission.europa.eu/legal-notice_en"},
	{Code: "ofac-sdn", Name: "OFAC SDN", URL: "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN_ENHANCED.XML", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://ofac.treasury.gov/specially-designated-nationals-and-blocked-persons-list-sdn-human-readable-lists",
		TermsURL: "https://sanctionslist.ofac.treas.gov/Home/static/index.html"},
	{Code: "ofac-cons", Name: "OFAC consolidated (non-SDN)", URL: "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/CONS_ENHANCED.XML", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://ofac.treasury.gov/consolidated-sanctions-list-non-sdn-lists",
		TermsURL: "https://sanctionslist.ofac.treas.gov/Home/static/index.html"},
	{Code: "un", Name: "UN Security Council consolidated", URL: "https://scsanctions.un.org/resources/xml/en/consolidated.xml", MaxAgeHours: dailyMaxAge, Status: TermsUnclear,
		Homepage: "https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list",
		TermsURL: "https://www.un.org/en/about-us/terms-of-use",
		Note:     "The UN's terms allow personal, non-commercial use only. Waiting for a decision. Its listings are also on the EU and UK lists, which implement them."},
	{Code: "uk", Name: "UK Sanctions List", URL: "https://sanctionslist.fcdo.gov.uk/docs/UK-Sanctions-List.xml", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://www.gov.uk/government/publications/the-uk-sanctions-list",
		TermsURL: "https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/"},
	{Code: "us-export", Name: "US export screening (BIS, State)", URL: "https://data.trade.gov/downloadable_consolidated_screening_list/v1/consolidated.json", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://www.trade.gov/consolidated-screening-list",
		TermsURL: "https://developer.trade.gov/terms-of-service"},
	{Code: "ch-seco", Name: "Switzerland SECO", URL: "https://www.sesam.search.admin.ch/sesam-search-web/pages/downloadXmlGesamtliste.xhtml?lang=en&action=downloadXmlGesamtlisteAction", MaxAgeHours: dailyMaxAge, Status: TermsUnclear,
		Homepage: "https://www.seco.admin.ch/seco/en/home/Aussenwirtschaftspolitik_Wirtschaftliche_Zusammenarbeit/Wirtschaftsbeziehungen/exportkontrollen-und-sanktionen/sanktionen-embargos.html",
		TermsURL: "https://www.admin.ch/gov/en/start/terms-and-conditions.html",
		Note:     "The Swiss federal administration's terms require written consent for any reproduction. Waiting for permission from SECO."},
	{Code: "ca-sema", Name: "Canada autonomous sanctions (SEMA)", URL: "https://www.international.gc.ca/world-monde/assets/office_docs/international_relations-relations_internationales/sanctions/sema-lmes.xml", MaxAgeHours: dailyMaxAge, Status: TermsUnclear,
		Homepage: "https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng",
		TermsURL: "https://www.canada.ca/en/transparency/terms.html",
		Note:     "Canada's terms allow non-commercial reproduction only; commercial redistribution needs written permission. Waiting for a decision."},
	{Code: "au-dfat", Name: "Australia consolidated list", MaxAgeHours: dailyMaxAge, Status: NeedsAccess,
		Homepage: "https://www.dfat.gov.au/international-relations/security/sanctions/consolidated-list",
		Note:     "The publisher's site refuses automated downloads from a client that identifies itself honestly, so its file and format couldn't be checked, and there is no parser for it yet."},
	{Code: "ua-nsdc", Name: "Ukraine NSDC State Register of Sanctions", URL: "https://api-drs.nsdc.gov.ua/v2/subjects", MaxAgeHours: dailyMaxAge, Status: NeedsAccess,
		Homepage: "https://drs.nsdc.gov.ua/",
		Note:     "The register's API needs a key, which takes a registration. Waiting for one."},
	{Code: "fr-tresor", Name: "France national asset-freeze register (DG Trésor)", URL: "https://gels-avoirs.dgtresor.gouv.fr/ApiPublic/api/v1/publication/derniere-publication-fichier-json", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://gels-avoirs.dgtresor.gouv.fr/",
		TermsURL: "https://www.etalab.gouv.fr/licence-ouverte-open-licence/"},
	{Code: "nl-terrorism", Name: "Netherlands national terrorism list", URL: "https://www.government.nl/site/binaries/site-content/collections/documents/2016/01/15/national-terrorism-list/eng-terrorismelijst.ods", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://www.government.nl/documents/2016/01/15/national-terrorism-list",
		TermsURL: "https://www.government.nl/copyright"},
	{Code: "nz-mfat", Name: "New Zealand Russia Sanctions Register (MFAT)", URL: "https://www.mfat.govt.nz/assets/Countries-and-Regions/Europe/Ukraine/Russia-Sanctions-Register.xlsx", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://www.mfat.govt.nz/en/countries-and-regions/europe/ukraine/russian-invasion-of-ukraine/sanctions/russia-sanctions-register",
		TermsURL: "https://www.mfat.govt.nz/en/copyright"},
	{Code: "be-terrorism", Name: "Belgium national terrorism list (FPS Finance)", URL: "https://finance.belgium.be/en/about_fps/structure_and_services/general_administrations/treasury/financial-sanctions/national", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage:    "https://finance.belgium.be/en/about_fps/structure_and_services/general_administrations/treasury/financial-sanctions/national",
		TermsURL:    "https://fin.belgium.be/en/terms-use",
		LinkPattern: `/[0-9]{8}[^/]*[Nn]ationale[^/]*\.xlsx$`},
	{Code: "jp-mof", Name: "Japan asset-freeze list (Ministry of Finance)", URL: "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage:    "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html",
		TermsURL:    "https://www.mof.go.jp/about_mof/notice/index.html",
		LinkPattern: `/shisantouketsu[0-9]{8}\.csv$`},
	{Code: "us-uflpa", Name: "US UFLPA Entity List (forced-labour import ban)", URL: "https://www.dhs.gov/uflpa-entity-list", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://www.dhs.gov/uflpa-entity-list",
		TermsURL: "https://www.usa.gov/government-copyright"},
	{Code: "ua-sfms", Name: "Ukraine SFMS terrorism list", URL: "https://fiu.gov.ua/assets/userfiles/Terror/BlackListFull.xml", MaxAgeHours: dailyMaxAge, Status: Active,
		Homepage: "https://fiu.gov.ua/"},
}

// FindSource finds a catalogued feed by code, ignoring case.
func FindSource(code string) *ListSource {
	for i := range Sources {
		if strings.EqualFold(Sources[i].Code, code) {
			return &Sources[i]
		}
	}
	return nil
}

// Optional is nil for an empty string.
func Optional(value string) *string {
	if value == "" {
		return nil
	}
	return &value
}
