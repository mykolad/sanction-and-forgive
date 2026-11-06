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
public sealed record ListSource(string Code, string Name, string? Url, int MaxAgeHours, FeedStatus Status, Func<IListParser>? CreateParser);

public static class ListSources
{
    /// <summary>Daily feeds may miss a day (a failed import, a late publication) before turning stale.</summary>
    private const int DailyMaxAge = 72;

    public static IReadOnlyList<ListSource> All { get; } =
    [
        new("eu", "EU consolidated financial sanctions", "https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token={token}", DailyMaxAge, FeedStatus.Active, () => new EuParser()),
        new("ofac-sdn", "OFAC SDN", "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN_ENHANCED.XML", DailyMaxAge, FeedStatus.Active, () => new OfacParser("ofac-sdn")),
        new("ofac-cons", "OFAC consolidated (non-SDN)", "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/CONS_ENHANCED.XML", DailyMaxAge, FeedStatus.Active, () => new OfacParser("ofac-cons")),
        new("un", "UN Security Council consolidated", "https://scsanctions.un.org/resources/xml/en/consolidated.xml", DailyMaxAge, FeedStatus.TermsUnclear, () => new UnParser()),
        new("uk", "UK Sanctions List", "https://sanctionslist.fcdo.gov.uk/docs/UK-Sanctions-List.xml", DailyMaxAge, FeedStatus.Active, () => new UkParser()),
        new("us-export", "US export screening (BIS, State)", "https://data.trade.gov/downloadable_consolidated_screening_list/v1/consolidated.json", DailyMaxAge, FeedStatus.Active, () => new UsExportParser()),
        new("ch-seco", "Switzerland SECO", "https://www.sesam.search.admin.ch/sesam-search-web/pages/downloadXmlGesamtliste.xhtml?lang=en&action=downloadXmlGesamtlisteAction", DailyMaxAge, FeedStatus.TermsUnclear, () => new SecoParser()),
        new("ca-sema", "Canada autonomous sanctions (SEMA)", "https://www.international.gc.ca/world-monde/assets/office_docs/international_relations-relations_internationales/sanctions/sema-lmes.xml", DailyMaxAge, FeedStatus.TermsUnclear, () => new CanadaSemaParser()),
        new("au-dfat", "Australia consolidated list", null, DailyMaxAge, FeedStatus.NeedsAccess, CreateParser: null),
        new("ua-nsdc", "Ukraine NSDC State Register of Sanctions", "https://api-drs.nsdc.gov.ua/v2/subjects", DailyMaxAge, FeedStatus.NeedsAccess, CreateParser: null),
        new("ua-sfms", "Ukraine SFMS terrorism list", "https://fiu.gov.ua/assets/userfiles/Terror/BlackListFull.xml", DailyMaxAge, FeedStatus.Active, () => new SfmsParser()),
    ];

    public static ListSource? Find(string code) =>
        All.FirstOrDefault(source => string.Equals(source.Code, code, StringComparison.OrdinalIgnoreCase));
}
