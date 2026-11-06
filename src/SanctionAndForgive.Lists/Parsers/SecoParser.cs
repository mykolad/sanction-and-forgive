using System.Xml.Linq;

namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Switzerland's SECO consolidated list (SESAM <c>swiss-sanctions-list</c>). The whole-list file also keeps
/// de-listed targets and their history: a target whose newest modification (the first) is <c>de-listed</c> is left
/// out, and so are objects (vessels, aircraft). Addresses point at <c>place</c> elements that follow the targets, so
/// this parser holds the parsed targets until the end of the file.</summary>
public sealed class SecoParser : IListParser
{
    public string SourceCode => "ch-seco";

    public IEnumerable<ParsedSubject> Parse(Stream stream, CancellationToken cancellationToken)
    {
        var programmes = new Dictionary<string, string>(StringComparer.Ordinal);
        var places = new Dictionary<string, (string? Text, string? CountryCode)>(StringComparer.Ordinal);
        var pending = new List<PendingTarget>();

        foreach (var record in Xml.Records(stream, "swiss-sanctions-list", 1, cancellationToken))
        {
            switch (record.Name.LocalName)
            {
                case "sanctions-program":
                    var key = record.Els("program-key").FirstOrDefault(element => element.Attr("lang") == "eng")?.Value;
                    foreach (var set in record.Els("sanctions-set"))
                    {
                        if (set.Attr("ssid") is { } ssid && key is not null)
                        {
                            programmes.TryAdd(ssid, key);
                        }
                    }

                    break;
                case "target":
                    if (Read(record, programmes) is { } target)
                    {
                        pending.Add(target);
                    }

                    break;
                case "place":
                    if (record.Attr("ssid") is { } placeId)
                    {
                        var country = record.El("country");
                        places[placeId] = (Text.Join(", ", record.Val("location"), record.Val("area"), country?.Value), country?.Attr("iso-code"));
                    }

                    break;
            }
        }

        foreach (var target in pending)
        {
            foreach (var (details, placeId) in target.Addresses)
            {
                var place = placeId is not null && places.TryGetValue(placeId, out var found) ? found : (null, null);
                target.Builder.AddAddress(Text.Join(", ", details, place.Text), place.CountryCode);
            }

            if (target.Builder.Build() is { } subject)
            {
                yield return subject;
            }
        }
    }

    private static PendingTarget? Read(XElement target, Dictionary<string, string> programmes)
    {
        if (target.Els("modification").FirstOrDefault()?.Attr("modification-type") == "de-listed")
        {
            return null;
        }

        var subject = target.El("individual") ?? target.El("entity");
        if (subject is null)
        {
            return null;
        }

        var type = subject.Name.LocalName == "individual" ? SubjectType.Individual : SubjectType.Entity;
        var id = target.Attr("ssid") ?? throw new ListFormatException("A SECO target has no ssid.");
        var setId = target.Val("sanctions-set-id");
        var listed = target.Els("modification").LastOrDefault(modification => modification.Attr("modification-type") == "listed");
        var builder = new SubjectBuilder(id, type)
        {
            Programmes = setId is not null && programmes.TryGetValue(setId, out var programme) ? programme : null,
            ListedOn = Text.ParseDate(listed?.Attr("effective-date"), "yyyy-MM-dd"),
            Remarks = Text.Join(" ", subject.Els("justification").Select(justification => justification.Value)),
            SourceUrl = "https://www.sesam.search.admin.ch/sesam-search-web/pages/search.xhtml?lang=en",
        };
        var addresses = new List<(string? Details, string? PlaceId)>();

        foreach (var identity in subject.Els("identity"))
        {
            var mainIdentity = identity.Attr("main") == "true";
            foreach (var name in identity.Els("name"))
            {
                var isWeak = name.Attr("quality") == "low";
                var isPrimary = mainIdentity && name.Attr("name-type") == "primary-name";
                var parts = name.Els("name-part").OrderBy(part => int.TryParse(part.Attr("order"), out var order) ? order : 0).ToList();
                builder.AddName(Text.Join(" ", parts.Select(part => part.Val("value"))), isWeak, isPrimary);

                // Each spelling variant (language and script) of the parts makes another full name.
                var variants = parts.SelectMany(part => part.Els("spelling-variant"))
                    .Select(variant => (Lang: variant.Attr("lang"), Script: variant.Attr("script")))
                    .Distinct();
                foreach (var (lang, script) in variants)
                {
                    var variantName = Text.Join(" ", parts.Select(part =>
                        part.Els("spelling-variant").FirstOrDefault(variant => variant.Attr("lang") == lang && variant.Attr("script") == script)?.Value
                        ?? part.Val("value")));
                    builder.AddName(variantName, isWeak, isPrimary: false);
                }
            }

            foreach (var address in identity.Els("address"))
            {
                addresses.Add((Text.Join(", ", address.Val("c-o"), address.Val("p-o-box"), address.Val("address-details"), address.Val("zip-code")), address.Attr("place-id")));
            }
        }

        return builder.HasNames ? new PendingTarget(builder, addresses) : null;
    }

    private sealed record PendingTarget(SubjectBuilder Builder, List<(string? Details, string? PlaceId)> Addresses);
}
