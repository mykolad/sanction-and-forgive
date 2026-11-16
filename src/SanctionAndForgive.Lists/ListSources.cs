using SanctionAndForgive.Lists.Parsers;

namespace SanctionAndForgive.Lists;

/// <summary>Whether a feed is imported. Only <see cref="Active"/> feeds are imported automatically; the others
/// wait for a decision or a file (docs/sources.md).</summary>
public enum FeedStatus
{
    Active,

    /// <summary>The publisher's terms seem to rule out commercial reuse; waiting for the owner's decision.</summary>
    TermsUnclear,

    /// <summary>The feed can't be fetched automatically (needs an API key, or the site refuses non-browser
    /// clients). It can still be imported from a file downloaded by hand.</summary>
    NeedsAccess,
}

/// <summary>A public feed: where it lives, how old it may get before its row shows <c>Not checked</c>, and its
/// parser. Manual (CSV) sources aren't here: they live only in the database.</summary>
public sealed record ListSource(string Code, string Name, string? Url, int MaxAgeHours, FeedStatus Status, Func<IListParser>? CreateParser)
{
    /// <summary>The publisher's page for the list, for people (the status page links it; <see cref="Url"/> is the
    /// file).</summary>
    public string? Homepage { get; init; }

    /// <summary>The terms the list is reused under, or the terms that stop it being reused (docs/sources.md).</summary>
    public string? TermsUrl { get; init; }

    /// <summary>The file's address as anyone may use it: the EU's with its public token, never a configured one.
    /// (The database keeps the URL with the token replaced by an ellipsis.)</summary>
    public string? PublicUrl => Url?.Replace("{token}", ListSources.EuPublicToken, StringComparison.Ordinal);

    /// <summary>Why a feed that isn't <see cref="FeedStatus.Active"/> isn't imported, in a sentence for the status
    /// page.</summary>
    public string? Note { get; init; }

    /// <summary>For a feed published under a new name each time (a date in it): <see cref="Url"/> is the publisher's
    /// page, and the file is the link on it whose address matches this pattern (<see cref="FeedDownloader.FindLink"/>).
    /// Written so .NET, Go (RE2) and Rust read it alike.</summary>
    public string? LinkPattern { get; init; }
}

public static class ListSources
{
    /// <summary>The EU's published public token for the FSF files (<c>LISTS_EU_TOKEN</c> overrides it for imports).</summary>
    public const string EuPublicToken = "dG9rZW4tMjAxNw";

    /// <summary>Daily feeds may miss a day (a failed import, a late publication) before turning stale.</summary>
    private const int DailyMaxAge = 72;

    public static IReadOnlyList<ListSource> All { get; } =
    [
        new("eu", "EU consolidated financial sanctions", "https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token={token}", DailyMaxAge, FeedStatus.Active, () => new EuParser())
        {
            Homepage = "https://data.europa.eu/data/datasets/consolidated-list-of-persons-groups-and-entities-subject-to-eu-financial-sanctions",
            TermsUrl = "https://commission.europa.eu/legal-notice_en",
        },
        new("ofac-sdn", "OFAC SDN", "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN_ENHANCED.XML", DailyMaxAge, FeedStatus.Active, () => new OfacParser("ofac-sdn"))
        {
            Homepage = "https://ofac.treasury.gov/specially-designated-nationals-and-blocked-persons-list-sdn-human-readable-lists",
            TermsUrl = "https://sanctionslist.ofac.treas.gov/Home/static/index.html",
        },
        new("ofac-cons", "OFAC consolidated (non-SDN)", "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/CONS_ENHANCED.XML", DailyMaxAge, FeedStatus.Active, () => new OfacParser("ofac-cons"))
        {
            Homepage = "https://ofac.treasury.gov/consolidated-sanctions-list-non-sdn-lists",
            TermsUrl = "https://sanctionslist.ofac.treas.gov/Home/static/index.html",
        },
        new("un", "UN Security Council consolidated", "https://scsanctions.un.org/resources/xml/en/consolidated.xml", DailyMaxAge, FeedStatus.TermsUnclear, () => new UnParser())
        {
            Homepage = "https://main.un.org/securitycouncil/en/content/un-sc-consolidated-list",
            TermsUrl = "https://www.un.org/en/about-us/terms-of-use",
            Note = "The UN's terms allow personal, non-commercial use only. Waiting for a decision. Its listings are also on the EU and UK lists, which implement them.",
        },
        new("uk", "UK Sanctions List", "https://sanctionslist.fcdo.gov.uk/docs/UK-Sanctions-List.xml", DailyMaxAge, FeedStatus.Active, () => new UkParser())
        {
            Homepage = "https://www.gov.uk/government/publications/the-uk-sanctions-list",
            TermsUrl = "https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/",
        },
        new("us-export", "US export screening (BIS, State)", "https://data.trade.gov/downloadable_consolidated_screening_list/v1/consolidated.json", DailyMaxAge, FeedStatus.Active, () => new UsExportParser())
        {
            Homepage = "https://www.trade.gov/consolidated-screening-list",
            TermsUrl = "https://developer.trade.gov/terms-of-service",
        },
        new("ch-seco", "Switzerland SECO", "https://www.sesam.search.admin.ch/sesam-search-web/pages/downloadXmlGesamtliste.xhtml?lang=en&action=downloadXmlGesamtlisteAction", DailyMaxAge, FeedStatus.TermsUnclear, () => new SecoParser())
        {
            Homepage = "https://www.seco.admin.ch/seco/en/home/Aussenwirtschaftspolitik_Wirtschaftliche_Zusammenarbeit/Wirtschaftsbeziehungen/exportkontrollen-und-sanktionen/sanktionen-embargos.html",
            TermsUrl = "https://www.admin.ch/gov/en/start/terms-and-conditions.html",
            Note = "The Swiss federal administration's terms require written consent for any reproduction. Waiting for permission from SECO.",
        },
        new("ca-sema", "Canada autonomous sanctions (SEMA)", "https://www.international.gc.ca/world-monde/assets/office_docs/international_relations-relations_internationales/sanctions/sema-lmes.xml", DailyMaxAge, FeedStatus.TermsUnclear, () => new CanadaSemaParser())
        {
            Homepage = "https://www.international.gc.ca/world-monde/international_relations-relations_internationales/sanctions/consolidated-consolide.aspx?lang=eng",
            TermsUrl = "https://www.canada.ca/en/transparency/terms.html",
            Note = "Canada's terms allow non-commercial reproduction only; commercial redistribution needs written permission. Waiting for a decision.",
        },
        new("au-dfat", "Australia consolidated list", null, DailyMaxAge, FeedStatus.NeedsAccess, CreateParser: null)
        {
            Homepage = "https://www.dfat.gov.au/international-relations/security/sanctions/consolidated-list",
            Note = "The publisher's site refuses automated downloads from a client that identifies itself honestly, so its file and format couldn't be checked, and there is no parser for it yet.",
        },
        new("ua-nsdc", "Ukraine NSDC State Register of Sanctions", "https://api-drs.nsdc.gov.ua/v2/subjects", DailyMaxAge, FeedStatus.NeedsAccess, CreateParser: null)
        {
            Homepage = "https://drs.nsdc.gov.ua/",
            Note = "The register's API needs a key, which takes a registration. Waiting for one.",
        },
        new("fr-tresor", "France national asset-freeze register (DG Trésor)", "https://gels-avoirs.dgtresor.gouv.fr/ApiPublic/api/v1/publication/derniere-publication-fichier-json", DailyMaxAge, FeedStatus.Active, () => new FranceParser())
        {
            Homepage = "https://gels-avoirs.dgtresor.gouv.fr/",
            TermsUrl = "https://www.etalab.gouv.fr/licence-ouverte-open-licence/",
        },
        new("nl-terrorism", "Netherlands national terrorism list", "https://www.government.nl/site/binaries/site-content/collections/documents/2016/01/15/national-terrorism-list/eng-terrorismelijst.ods", DailyMaxAge, FeedStatus.Active, () => new NetherlandsParser())
        {
            Homepage = "https://www.government.nl/documents/2016/01/15/national-terrorism-list",
            TermsUrl = "https://www.government.nl/copyright",
        },
        new("nz-mfat", "New Zealand Russia Sanctions Register (MFAT)", "https://www.mfat.govt.nz/assets/Countries-and-Regions/Europe/Ukraine/Russia-Sanctions-Register.xlsx", DailyMaxAge, FeedStatus.Active, () => new NewZealandParser())
        {
            Homepage = "https://www.mfat.govt.nz/en/countries-and-regions/europe/ukraine/russian-invasion-of-ukraine/sanctions/russia-sanctions-register",
            TermsUrl = "https://www.mfat.govt.nz/en/copyright",
        },
        new("jp-mof", "Japan asset-freeze list (Ministry of Finance)", "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html", DailyMaxAge, FeedStatus.Active, () => new JapanParser())
        {
            Homepage = "https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html",
            TermsUrl = "https://www.mof.go.jp/about_mof/notice/index.html",
            LinkPattern = @"/shisantouketsu[0-9]{8}\.csv$",
        },
        new("ua-sfms", "Ukraine SFMS terrorism list", "https://fiu.gov.ua/assets/userfiles/Terror/BlackListFull.xml", DailyMaxAge, FeedStatus.Active, () => new SfmsParser())
        {
            Homepage = "https://fiu.gov.ua/",
        },
    ];

    public static ListSource? Find(string code) =>
        All.FirstOrDefault(source => string.Equals(source.Code, code, StringComparison.OrdinalIgnoreCase));
}
