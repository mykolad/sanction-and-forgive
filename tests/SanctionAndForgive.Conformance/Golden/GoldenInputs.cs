using SanctionAndForgive.Conformance.Feeds;

namespace SanctionAndForgive.Conformance.Golden;

/// <summary>One screening request of the golden set.</summary>
public sealed record GoldenInput(string Reference, string? ReceiverName, string? CompanyName, string? Address);

/// <summary>The golden set's requests, the same on every run: listed parties written the ways people write them
/// (exactly, in Cyrillic, with a typo, words swapped, initials, another legal form, shortened, an address alone), and
/// ordinary names nobody lists. Each one exercises a different part of matching; together they pin every result.</summary>
public static class GoldenInputs
{
    private static readonly string[] OrdinaryPeople =
    [
        "Hannah Becker", "Lucas Moreau", "Sofia Rossi", "James Whitfield", "Maria Gonzalez", "Peter Lindqvist",
        "Olga Nowak", "Tomasz Wisniewski", "Emma Clarke", "Daniel Novak", "Aiko Tanaka", "Ravi Sharma",
    ];

    private static readonly string[] OrdinaryCompanies =
    [
        "Greenfield Bakery Ltd", "Bright Path Consulting LLC", "Nordic Timber AB", "Harbour View Hotels Inc.",
        "Blue River Software GmbH", "Maple Leaf Dental Clinic", "Sunrise Packaging Co.", "Alpine Coffee Roasters",
        "Coastal Freight Services Ltd", "Silver Birch Furniture", "Riverside Pharmacy", "Golden Gate Textiles Inc.",
    ];

    private static readonly string[] OrdinaryAddresses =
    [
        "12 High Street, Oxford OX1 4AA, United Kingdom", "Hauptstrasse 5, 10115 Berlin, Germany",
        "221 Rue de Rivoli, 75001 Paris, France", "Via Roma 10, 00184 Roma, Italy", "1 Infinite Loop, Cupertino, CA, USA",
    ];

    public static IReadOnlyList<GoldenInput> All { get; } = Generate();

    private static List<GoldenInput> Generate()
    {
        var rng = new Rng(Fixtures.Seed + 1);
        var listed = FeedFiles.Feeds.SelectMany(feed => FeedFiles.Subjects(feed.Code)).DistinctBy(subject => subject.Number).OrderBy(subject => subject.Number).ToList();
        var inputs = new List<GoldenInput>();
        void Add(string? receiver, string? company, string? address) =>
            inputs.Add(new GoldenInput($"golden-{inputs.Count + 1:0000}", receiver, company, address));

        for (var i = 0; i < 300; i++)
        {
            var subject = rng.Pick(listed);
            var name = Variation(subject, rng.Next(9), rng);
            if (subject.Person)
            {
                Add(name, null, rng.Chance(25) ? Address(subject, rng) : null);
            }
            else
            {
                Add(null, name, rng.Chance(25) ? Address(subject, rng) : null);
            }
        }

        // Addresses alone, listed and not.
        for (var i = 0; i < 30; i++)
        {
            var subject = rng.Pick(listed.Where(subject => subject.Address is not null).ToList());
            Add(null, null, Address(subject, rng));
        }

        // Ordinary parties: a receiver, a company, both, with an address.
        for (var i = 0; i < 240; i++)
        {
            var person = rng.Pick(OrdinaryPeople);
            var company = rng.Pick(OrdinaryCompanies);
            switch (rng.Next(4))
            {
                case 0: Add(person, null, null); break;
                case 1: Add(null, company, null); break;
                case 2: Add(person, company, rng.Pick(OrdinaryAddresses)); break;
                default: Add(person, null, rng.Pick(OrdinaryAddresses)); break;
            }
        }

        // A listed party's company with an ordinary receiver, and the other way round.
        for (var i = 0; i < 30; i++)
        {
            var subject = rng.Pick(listed);
            Add(subject.Person ? subject.LatinName : rng.Pick(OrdinaryPeople), subject.Person ? rng.Pick(OrdinaryCompanies) : subject.LatinName, null);
        }

        return inputs;
    }

    /// <summary>One of the ways a name is written: 0 exact, 1 Cyrillic, 2 a typo, 3 words swapped, 4 initials or a
    /// legal form dropped, 5 lower case, 6 shortened, 7 an alias, 8 a near miss (another first name).</summary>
    private static string Variation(FixtureSubject subject, int kind, Rng rng)
    {
        var name = subject.LatinName;
        return kind switch
        {
            1 when subject.CyrillicName is { } cyrillic => cyrillic,
            2 => Typo(name, rng),
            3 when subject.Person => $"{subject.Family} {subject.Given}",
            4 when subject.Person => $"{subject.Given![0]}. {subject.Family}",
            4 => string.Join(' ', name.Split(' ').Where(word => word is not ("LLC" or "JSC" or "PJSC" or "OOO" or "Ltd" or "Limited"))),
            5 => name.ToLowerInvariant(),
            6 when !subject.Person => name.Split(' ')[^1] is "LLC" or "JSC" or "PJSC" or "OOO" or "Ltd" or "Limited" ? name.Split(' ')[0] + " " + name.Split(' ')[^1] : name.Split(' ')[^1],
            7 when subject.Aliases.Count > 0 => subject.Aliases[0],
            8 when subject.Person => $"{(subject.Given == "Ivan" ? "Oleg" : "Ivan")} {subject.Family}",
            _ => name,
        };
    }

    private static string Typo(string name, Rng rng)
    {
        var position = 1 + rng.Next(Math.Max(1, name.Length - 2));
        return rng.Next(3) switch
        {
            0 => name.Remove(position, 1),
            1 => name[..position] + name[position + 1] + name[position] + name[(position + 2)..],
            _ => name.Insert(position, name[position].ToString()),
        };
    }

    private static string? Address(FixtureSubject subject, Rng rng) => subject.Address is not { } address ? null : rng.Next(3) switch
    {
        0 => $"{address.Street}, {address.City}, {address.Country}",
        1 => $"{address.City}, {address.Iso}",
        _ => $"{address.Street} {address.City}",
    };
}
