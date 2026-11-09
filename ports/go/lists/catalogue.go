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
