using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SanctionAndForgive.Conformance.Feeds;

/// <summary>Writes the fictional lists in each feed's own format, the way its publisher lays it out (the parser
/// fixtures in tests/SanctionAndForgive.Tests/Lists/ParserTests.cs show the same shapes), each with the records a
/// parser must leave out: vessels, delisted targets, another agency's records.</summary>
public static class FeedFiles
{
    /// <summary>Every feed the suite imports, with how many parties it lists. The "TermsUnclear" ones (un, ch-seco,
    /// ca-sema) are imported by naming them, as the CLI allows.</summary>
    public static readonly IReadOnlyList<(string Code, int Count)> Feeds =
    [
        ("eu", 160), ("ofac-sdn", 200), ("ofac-cons", 60), ("un", 80), ("uk", 140), ("us-export", 120),
        ("ch-seco", 90), ("ca-sema", 70), ("ua-sfms", 50),
    ];

    /// <summary>The manual list (a CSV through <c>source-add</c> and <c>add</c>), and its parties.</summary>
    public const string ManualCode = "conformance-manual";

    public const string ManualName = "Conformance manual list";

    public static IReadOnlyList<FixtureSubject> Subjects(string code) =>
        Fixtures.For(code, Feeds.Single(feed => feed.Code == code).Count);

    /// <summary>The file name for a feed (its extension says the format).</summary>
    public static string FileName(string code, string variant) => $"{code}.{variant}.{(code == "us-export" ? "json" : "xml")}";

    /// <summary>Writes <paramref name="subjects"/> as <paramref name="code"/>'s feed to <paramref name="path"/>.</summary>
    public static void Write(string code, IReadOnlyList<FixtureSubject> subjects, string path)
    {
        switch (code)
        {
            case "us-export":
                File.WriteAllText(path, UsExport(subjects), new UTF8Encoding(false));
                return;
            default:
                var document = code switch
                {
                    "eu" => Eu(subjects),
                    "ofac-sdn" => Ofac(subjects, 30000),
                    "ofac-cons" => Ofac(subjects, 60000),
                    "un" => Un(subjects),
                    "uk" => Uk(subjects),
                    "ch-seco" => Seco(subjects),
                    "ca-sema" => Canada(subjects),
                    "ua-sfms" => Sfms(subjects),
                    _ => throw new ArgumentException($"No writer for {code}.", nameof(code)),
                };
                using (var stream = File.Create(path))
                {
                    document.Save(stream);
                }

                return;
        }
    }

    /// <summary>The manual list as the CLI's CSV (external_id,type,name,aliases,address,country,programme,listed_on,remarks).</summary>
    public static void WriteManual(IReadOnlyList<FixtureSubject> subjects, string path)
    {
        var csv = new StringBuilder("external_id,type,name,aliases,address,country,programme,listed_on,remarks\n");
        foreach (var subject in subjects)
        {
            var aliases = string.Join('|', Names(subject).Skip(1));
            csv.Append(CultureInfo.InvariantCulture, $"M-{subject.Number},{(subject.Person ? "Individual" : "Entity")},{Quote(subject.LatinName)},{Quote(aliases)},")
                .Append(CultureInfo.InvariantCulture, $"{Quote(subject.Address is { } a ? $"{a.Street}, {a.City}" : string.Empty)},{subject.Address?.Iso},{subject.Programme},{Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")},Fictional\n");
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }

    private static string Quote(string value) => value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : value;

    /// <summary>The strong names: Latin, Cyrillic, then the other aliases.</summary>
    private static IEnumerable<string> Names(FixtureSubject subject)
    {
        yield return subject.LatinName;
        if (subject.CyrillicName is { } cyrillic)
        {
            yield return cyrillic;
        }

        foreach (var alias in subject.Aliases)
        {
            yield return alias;
        }
    }

    private static XDocument Eu(IReadOnlyList<FixtureSubject> subjects)
    {
        XNamespace ns = "http://eu.europa.ec/fpi/fsd/export";
        var root = new XElement(ns + "export", new XAttribute("generationDate", "2026-11-09T10:00:00+01:00"));
        foreach (var subject in subjects)
        {
            var entity = new XElement(ns + "sanctionEntity",
                new XAttribute("designationDate", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")),
                new XAttribute("logicalId", 100000 + subject.Number),
                new XElement(ns + "remark", "Fictional."),
                new XElement(ns + "regulation", new XAttribute("programme", subject.Programme),
                    new XElement(ns + "publicationUrl", $"https://eur-lex.europa.eu/eli/reg/fictional/{subject.Programme.ToLowerInvariant()}")),
                new XElement(ns + "subjectType", new XAttribute("code", subject.Person ? "person" : "enterprise")));
            entity.Add(subject.Person
                ? new XElement(ns + "nameAlias", new XAttribute("firstName", subject.Given!), new XAttribute("lastName", subject.Family), new XAttribute("wholeName", string.Empty), new XAttribute("strong", "true"))
                : new XElement(ns + "nameAlias", new XAttribute("wholeName", subject.LatinName), new XAttribute("strong", "true")));
            foreach (var name in Names(subject).Skip(1))
            {
                entity.Add(new XElement(ns + "nameAlias", new XAttribute("wholeName", name), new XAttribute("strong", "true")));
            }

            if (subject.WeakAlias is { } weak)
            {
                entity.Add(new XElement(ns + "nameAlias", new XAttribute("wholeName", weak), new XAttribute("strong", "false")));
            }

            if (subject.Address is { } address)
            {
                entity.Add(new XElement(ns + "address", new XAttribute("street", address.Street), new XAttribute("city", address.City),
                    new XAttribute("countryIso2Code", address.Iso), new XAttribute("countryDescription", address.Country.ToUpperInvariant())));
            }

            root.Add(entity);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XDocument Ofac(IReadOnlyList<FixtureSubject> subjects, int idBase)
    {
        XNamespace ns = "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/ENHANCED_XML";
        var entities = new XElement(ns + "entities");
        foreach (var subject in subjects)
        {
            var names = new XElement(ns + "names");
            var primary = new XElement(ns + "translations", Translation(ns, subject.Person ? subject.Given : null, subject.Family, subject.LatinName, "Latin", primary: true));
            if (subject.CyrillicName is { } cyrillic)
            {
                primary.Add(Translation(ns, subject.CyrillicGiven, subject.Person ? subject.CyrillicFamily! : cyrillic, cyrillic, "Cyrillic", primary: false));
            }

            names.Add(new XElement(ns + "name", new XElement(ns + "isPrimary", "true"), new XElement(ns + "isLowQuality", "false"), primary));
            foreach (var alias in subject.Aliases)
            {
                names.Add(new XElement(ns + "name", new XElement(ns + "isPrimary", "false"), new XElement(ns + "isLowQuality", "false"),
                    new XElement(ns + "translations", Translation(ns, null, null, alias, "Latin", primary: true))));
            }

            if (subject.WeakAlias is { } weak)
            {
                names.Add(new XElement(ns + "name", new XElement(ns + "isPrimary", "false"), new XElement(ns + "isLowQuality", "true"),
                    new XElement(ns + "translations", Translation(ns, null, null, weak, "Latin", primary: true))));
            }

            var entity = new XElement(ns + "entity", new XAttribute("id", idBase + subject.Number),
                new XElement(ns + "generalInfo", new XElement(ns + "entityType", subject.Person ? "Individual" : "Entity")),
                new XElement(ns + "sanctionsLists", new XElement(ns + "sanctionsList", new XAttribute("datePublished", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")), "SDN List")),
                new XElement(ns + "sanctionsPrograms", new XElement(ns + "sanctionsProgram", $"{subject.Programme}-EO")),
                names);
            if (subject.Address is { } address)
            {
                entity.Add(new XElement(ns + "addresses", new XElement(ns + "address", new XElement(ns + "country", address.Country),
                    new XElement(ns + "translations", new XElement(ns + "translation", new XElement(ns + "addressParts",
                        new XElement(ns + "addressPart", new XElement(ns + "type", "ADDRESS1"), new XElement(ns + "value", address.Street)),
                        new XElement(ns + "addressPart", new XElement(ns + "type", "CITY"), new XElement(ns + "value", address.City))))))));
            }

            entities.Add(entity);
        }

        // A vessel: OFAC lists ships, which the app leaves out.
        entities.Add(new XElement(ns + "entity", new XAttribute("id", idBase + 9999),
            new XElement(ns + "generalInfo", new XElement(ns + "entityType", "Vessel")),
            new XElement(ns + "names", new XElement(ns + "name", new XElement(ns + "isPrimary", "true"),
                new XElement(ns + "translations", new XElement(ns + "translation", new XElement(ns + "formattedFullName", "CONFORMANCE STAR")))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(ns + "sanctionsData", entities));
    }

    private static XElement Translation(XNamespace ns, string? first, string? last, string full, string script, bool primary)
    {
        var translation = new XElement(ns + "translation", new XElement(ns + "isPrimary", primary ? "true" : "false"), new XElement(ns + "script", script));
        if (first is not null && last is not null)
        {
            translation.Add(new XElement(ns + "formattedFirstName", first), new XElement(ns + "formattedLastName", last));
        }

        translation.Add(new XElement(ns + "formattedFullName", full));
        return translation;
    }

    private static XDocument Un(IReadOnlyList<FixtureSubject> subjects)
    {
        var individuals = new XElement("INDIVIDUALS");
        var entities = new XElement("ENTITIES");
        foreach (var subject in subjects)
        {
            var prefix = subject.Person ? "INDIVIDUAL" : "ENTITY";
            var record = new XElement(prefix,
                new XElement("DATAID", 7000 + subject.Number),
                new XElement("FIRST_NAME", subject.Person ? subject.Given : subject.LatinName));
            if (subject.Person)
            {
                record.Add(new XElement("SECOND_NAME", subject.Family));
            }

            record.Add(new XElement("UN_LIST_TYPE", subject.Programme),
                new XElement("REFERENCE_NUMBER", $"{subject.Programme[..2]}{(subject.Person ? "i" : "e")}.{subject.Number:000}"),
                new XElement("LISTED_ON", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")));
            if (subject.CyrillicName is { } cyrillic)
            {
                record.Add(new XElement("NAME_ORIGINAL_SCRIPT", cyrillic));
            }

            foreach (var alias in subject.Aliases)
            {
                record.Add(new XElement($"{prefix}_ALIAS", new XElement("QUALITY", "Good"), new XElement("ALIAS_NAME", alias)));
            }

            if (subject.WeakAlias is { } weak)
            {
                record.Add(new XElement($"{prefix}_ALIAS", new XElement("QUALITY", "Low"), new XElement("ALIAS_NAME", weak)));
            }

            record.Add(subject.Address is { } address
                ? new XElement($"{prefix}_ADDRESS", new XElement("STREET", address.Street), new XElement("CITY", address.City), new XElement("COUNTRY", address.Country))
                : new XElement($"{prefix}_ADDRESS", new XElement("COUNTRY")));
            (subject.Person ? individuals : entities).Add(record);
        }

        return new XDocument(new XElement("CONSOLIDATED_LIST", new XAttribute("dateGenerated", "2026-11-09T00:00:00Z"), individuals, entities));
    }

    private static XDocument Uk(IReadOnlyList<FixtureSubject> subjects)
    {
        var root = new XElement("Designations");
        foreach (var subject in subjects)
        {
            var names = new XElement("Names", UkName(subject.Person ? subject.Given : null, subject.Family, "Primary Name", strength: null));
            foreach (var alias in subject.Aliases)
            {
                names.Add(UkName(null, alias, "Alias", "Good quality a.k.a"));
            }

            if (subject.WeakAlias is { } weak)
            {
                names.Add(UkName(null, weak, "Alias", "Low quality a.k.a"));
            }

            var designation = new XElement("Designation",
                new XElement("DateDesignated", Fixtures.Date(subject.ListedOn, "dd/MM/yyyy")),
                new XElement("UniqueID", $"{subject.Programme}{subject.Number:0000}"),
                names);
            if (subject.CyrillicName is { } cyrillic)
            {
                designation.Add(new XElement("NonLatinNames", new XElement("NonLatinName", new XElement("NameNonLatinScript", cyrillic))));
            }

            designation.Add(new XElement("RegimeName", $"The {subject.Programme} (Sanctions) (EU Exit) Regulations 2019"),
                new XElement("IndividualEntityShip", subject.Person ? "Individual" : "Entity"));
            if (subject.Address is { } address)
            {
                designation.Add(new XElement("Addresses", new XElement("Address", new XElement("AddressLine1", address.Street),
                    new XElement("AddressLine6", address.City), new XElement("AddressCountry", address.Country))));
            }

            root.Add(designation);
        }

        root.Add(new XElement("Designation", new XElement("UniqueID", "SHIP0001"),
            new XElement("Names", UkName(null, "CONFORMANCE STAR", "Primary Name", strength: null)), new XElement("IndividualEntityShip", "Ship")));
        return new XDocument(root);
    }

    private static XElement UkName(string? given, string family, string type, string? strength)
    {
        var name = new XElement("Name");
        if (given is not null)
        {
            name.Add(new XElement("Name1", given));
        }

        name.Add(new XElement("Name6", family), new XElement("NameType", type));
        if (strength is not null)
        {
            name.Add(new XElement("AliasStrength", strength));
        }

        return name;
    }

    private static string UsExport(IReadOnlyList<FixtureSubject> subjects)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("results");
            foreach (var subject in subjects)
            {
                json.WriteStartObject();
                json.WriteString("id", $"cf-{subject.Number:0000}");
                json.WriteString("source", subject.Number % 3 == 0 ? "ITAR Debarred (DTC) - State Department" : "Entity List (EL) - Bureau of Industry and Security");
                json.WriteString("name", subject.LatinName);
                json.WriteStartArray("alt_names");
                foreach (var name in Names(subject).Skip(1))
                {
                    json.WriteStringValue(name);
                }

                json.WriteEndArray();
                json.WriteStartArray("addresses");
                if (subject.Address is { } address)
                {
                    json.WriteStartObject();
                    json.WriteString("address", address.Street);
                    json.WriteString("city", address.City);
                    json.WriteString("state", string.Empty);
                    json.WriteString("postal_code", string.Empty);
                    json.WriteString("country", address.Iso);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("programs");
                json.WriteStringValue(subject.Programme);
                json.WriteEndArray();
                json.WriteString("start_date", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd"));
                json.WriteString("source_list_url", "https://www.bis.gov/fictional-entity-list");
                json.WriteNull("type");
                json.WriteEndObject();
            }

            // Another agency's record (the app takes OFAC's from OFAC) and a vessel: both left out.
            json.WriteStartObject();
            json.WriteString("id", "cf-sdn");
            json.WriteString("source", "Specially Designated Nationals (SDN) - Treasury Department");
            json.WriteString("name", "Repeated From OFAC");
            json.WriteEndObject();
            json.WriteStartObject();
            json.WriteString("id", "cf-vessel");
            json.WriteString("source", "Entity List (EL) - Bureau of Industry and Security");
            json.WriteString("name", "CONFORMANCE STAR");
            json.WriteString("type", "Vessel");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteNumber("total", subjects.Count + 2);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XDocument Seco(IReadOnlyList<FixtureSubject> subjects)
    {
        var root = new XElement("swiss-sanctions-list", new XAttribute("list-type", "whole-list"), new XAttribute("date", "2026-11-09"));
        var programmes = subjects.Select(subject => subject.Programme).Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var (programme, index) in programmes.Select((programme, index) => (programme, index)))
        {
            root.Add(new XElement("sanctions-program", new XAttribute("ssid", 10 + index), new XElement("program-key", new XAttribute("lang", "eng"), programme),
                new XElement("sanctions-set", new XAttribute("ssid", 1000 + index), new XAttribute("lang", "eng"), $"annex {index + 1}")));
        }

        var places = subjects.Select(subject => subject.Address).OfType<FixtureAddress>().Select(address => address.City).Distinct().Order(StringComparer.Ordinal).ToList();
        foreach (var subject in subjects)
        {
            var identity = new XElement("identity", new XAttribute("ssid", 50000 + subject.Number), new XAttribute("main", "true"),
                SecoName(subject.Person ? [(subject.Given!, subject.CyrillicGiven), (subject.Family, subject.CyrillicFamily)] : [(subject.LatinName, subject.CyrillicName)], "primary-name", "good"));
            foreach (var alias in subject.Aliases)
            {
                identity.Add(SecoName([(alias, null)], "alias", "good"));
            }

            if (subject.WeakAlias is { } weak)
            {
                identity.Add(SecoName([(weak, null)], "alias", "low"));
            }

            if (subject.Address is { } address)
            {
                identity.Add(new XElement("address", new XAttribute("place-id", 900 + places.IndexOf(address.City)), new XElement("address-details", address.Street)));
            }

            root.Add(new XElement("target", new XAttribute("ssid", subject.Number),
                new XElement("sanctions-set-id", 1000 + programmes.IndexOf(subject.Programme)),
                new XElement(subject.Person ? "individual" : "entity", identity),
                new XElement("modification", new XAttribute("modification-type", "listed"), new XAttribute("effective-date", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")))));
        }

        // A delisted target and an object (a ship): both left out.
        root.Add(new XElement("target", new XAttribute("ssid", 99001),
            new XElement("entity", new XElement("identity", new XAttribute("main", "true"), SecoName([("Delisted Conformance LLC", null)], "primary-name", "good"))),
            new XElement("modification", new XAttribute("modification-type", "de-listed"), new XAttribute("effective-date", "2020-01-01")),
            new XElement("modification", new XAttribute("modification-type", "listed"))));
        root.Add(new XElement("target", new XAttribute("ssid", 99002),
            new XElement("object", new XElement("identity", new XAttribute("main", "true"), SecoName([("CONFORMANCE STAR", null)], "primary-name", "good")))));
        foreach (var city in places)
        {
            var address = Fixtures.All.Select(subject => subject.Address).OfType<FixtureAddress>().First(address => address.City == city);
            root.Add(new XElement("place", new XAttribute("ssid", 900 + places.IndexOf(city)), new XElement("location", city),
                new XElement("country", new XAttribute("iso-code", address.Iso), address.Country)));
        }

        return new XDocument(root);
    }

    private static XElement SecoName(IReadOnlyList<(string Latin, string? Cyrillic)> parts, string type, string quality)
    {
        var name = new XElement("name", new XAttribute("name-type", type), new XAttribute("quality", quality));
        foreach (var ((latin, cyrillic), order) in parts.Select((part, index) => (part, index + 1)))
        {
            var part = new XElement("name-part", new XAttribute("order", order), new XElement("value", latin));
            if (cyrillic is not null)
            {
                part.Add(new XElement("spelling-variant", new XAttribute("lang", "RUS"), new XAttribute("script", "CYRL"), cyrillic));
            }

            name.Add(part);
        }

        return name;
    }

    private static XDocument Canada(IReadOnlyList<FixtureSubject> subjects)
    {
        var root = new XElement("data-set");
        foreach (var subject in subjects)
        {
            var record = new XElement("record", new XElement("Country-Pays", subject.Address is { } address ? $"{address.Country} / {address.Country}" : "Russia / Russie"));
            if (subject.Person)
            {
                record.Add(new XElement("LastName-NomDeFamille", subject.Family), new XElement("GivenName-Prenom", subject.Given));
            }
            else
            {
                record.Add(new XElement("EntityOrShip-EntiteOuNavire", subject.LatinName));
            }

            var aliases = subject.Aliases.ToList();
            if (subject.CyrillicName is { } cyrillic)
            {
                aliases.Add($"Russian: {cyrillic}");
            }

            if (aliases.Count > 0)
            {
                record.Add(new XElement("Aliases-Alias", string.Join(", ", aliases)));
            }

            record.Add(new XElement("Schedule-Annexe", subject.Person ? "1, Part 1" : "1, Part 2"),
                new XElement("Item-NumeroDarticle", subject.Number),
                new XElement("DateOfListing-DateDinscription", Fixtures.Date(subject.ListedOn, "yyyy-MM-dd")));
            root.Add(record);
        }

        root.Add(new XElement("record", new XElement("Country-Pays", "Russia / Russie"), new XElement("EntityOrShip-EntiteOuNavire", "Conformance Star"),
            new XElement("ShipIMONumber-NumeroOMIDuNavire", "9999999"), new XElement("Item-NumeroDarticle", "9999")));
        return new XDocument(root);
    }

    private static XDocument Sfms(IReadOnlyList<FixtureSubject> subjects)
    {
        var root = new XElement("list-terror", new XElement("count-record", subjects.Count));
        foreach (var subject in subjects)
        {
            var record = new XElement("acount-list",
                new XElement("number-entry", subject.Number),
                new XElement("date-entry", Fixtures.Date(subject.ListedOn, "yyyyMMdd")),
                new XElement("type-entry", subject.Person ? "2" : "1"),
                new XElement("program-entry", subject.Programme));
            // The SFMS file writes a person family name first, in Cyrillic where it has it.
            record.Add(subject switch
            {
                { Person: true, CyrillicFamily: { } family } => Aka(family, subject.CyrillicGiven, "N", weak: false),
                { Person: true } => Aka(subject.Family, subject.Given, "N", weak: false),
                _ => Aka(subject.CyrillicName ?? subject.LatinName, null, "N", weak: false),
            });
            if (subject.CyrillicName is not null)
            {
                record.Add(subject.Person ? Aka(subject.Family, subject.Given, "A", weak: false) : Aka(subject.LatinName, null, "A", weak: false));
            }

            foreach (var alias in subject.Aliases)
            {
                record.Add(Aka(alias, null, "A", weak: false));
            }

            if (subject.WeakAlias is { } weak)
            {
                record.Add(Aka(weak, null, "PE", weak: true));
            }

            if (subject.Address is { } address)
            {
                record.Add(new XElement("address-list", new XElement("country-code", address.Numeric), new XElement("address", $"{address.Street}, {address.City}, {address.Country}")));
            }

            root.Add(record);
        }

        return new XDocument(root);
    }

    private static XElement Aka(string first, string? second, string type, bool weak)
    {
        var aka = new XElement("aka-list", new XElement("aka-name1", first));
        if (second is not null)
        {
            aka.Add(new XElement("aka-name2", second));
        }

        aka.Add(new XElement("type-aka", type));
        if (weak)
        {
            aka.Add(new XElement("quality-aka", "2"));
        }

        return aka;
    }
}
