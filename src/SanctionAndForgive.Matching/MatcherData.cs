using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching;

/// <summary>The dictionaries (<c>Data/*.json</c>, embedded): transliteration tables, legal forms and phrases,
/// generic and stop words, spelling pairs, cognates, countries and places. Loaded once; immutable. Every key is
/// normalised the same way as the text it's compared with.</summary>
internal sealed class MatcherData
{
    private static readonly Lazy<MatcherData> Instance = new(Load);

    /// <summary>The transliteration tables, in the order their forms are listed. A new language is a new file here.</summary>
    private static readonly string[] TableFiles = ["translit.ru.json", "translit.uk.json"];

    private MatcherData()
    {
    }

    public static MatcherData Default => Instance.Value;

    /// <summary>SHA-256 of every dictionary file, in a fixed order (hex, first 16 characters): part of
    /// <see cref="MatcherVersion"/>.</summary>
    public required string Hash { get; init; }

    public required IReadOnlyList<TransliterationTable> Tables { get; init; }

    public required TokenFactory Tokens { get; init; }

    /// <summary>Legal form spellings → class, and whether it's also plausible as a name.</summary>
    public required Dictionary<string, (string Class, bool Ambiguous)> LegalForms { get; init; }

    /// <summary>Multi-word legal forms → their short form, by first word, longest first.</summary>
    public required Dictionary<string, List<(string[] Words, string Short)>> LegalPhrases { get; init; }

    /// <summary>Generic word spellings → canonical English word.</summary>
    public required Dictionary<string, string> Generic { get; init; }

    public required HashSet<string> StopWords { get; init; }

    public required HashSet<string> Honorifics { get; init; }

    public required HashSet<string> AddressWords { get; init; }

    /// <summary>Latin spelling → spelling group.</summary>
    public required Dictionary<string, int> SpellingGroups { get; init; }

    /// <summary>Each spelling group's members.</summary>
    public required List<string[]> SpellingGroupMembers { get; init; }

    /// <summary>Fold key → the cognate groups it belongs to.</summary>
    public required Dictionary<string, int[]> CognateGroups { get; init; }

    /// <summary>Each cognate group's fold keys (for initials: <c>М</c> matches <c>Nikolay</c> through <c>Mykola</c>).</summary>
    public required List<string[]> CognateFolds { get; init; }

    /// <summary>Country name (normalised words joined by spaces, in any script) → ISO alpha-2.</summary>
    public required Dictionary<string, string> Countries { get; init; }

    public required HashSet<string> CountryCodes { get; init; }

    public required int MaxCountryWords { get; init; }

    /// <summary>Place name (normalised words joined by spaces) → place id and country.</summary>
    public required Dictionary<string, (int Id, string Country, string Name)> Places { get; init; }

    public required int MaxPlaceWords { get; init; }

    /// <summary>The canonical generic word one edit from <paramref name="form"/> (same first letter, 7 letters and up,
    /// Latin spellings only), or null. Short words are left out: a one-letter change there is often another word.</summary>
    public string? NearGeneric(string form)
    {
        if (form.Length < 7)
        {
            return null;
        }

        foreach (var (spelling, canonical) in Generic)
        {
            if (spelling.Length >= 7 && spelling[0] == form[0] && Math.Abs(spelling.Length - form.Length) <= 1
                && Names.EditDistance.DamerauLevenshtein(spelling, form, 2) == 1)
            {
                return canonical;
            }
        }

        return null;
    }

    /// <summary>The other spellings of <paramref name="spelling"/>'s group, or none.</summary>
    public IEnumerable<string> SpellingMembers(string spelling) =>
        SpellingGroups.TryGetValue(spelling, out var group) ? SpellingGroupMembers[group] : [];

    private static MatcherData Load()
    {
        var assembly = typeof(MatcherData).Assembly;
        var names = assembly.GetManifestResourceNames().Where(name => name.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            sha.AppendData(buffer.ToArray());
            var key = name["SanctionAndForgive.Matching.Data.".Length..];
            documents[key] = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
        }

        var tables = TableFiles.Select(key => ReadTable(documents[key])).ToList();
        var tokens = new TokenFactory(tables);

        var legalForms = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (var form in documents["legal-forms.json"].GetProperty("forms").EnumerateObject())
        {
            foreach (var (kind, ambiguous) in new[] { ("unambiguous", false), ("ambiguous", true) })
            {
                if (form.Value.TryGetProperty(kind, out var spellings))
                {
                    foreach (var spelling in spellings.EnumerateArray())
                    {
                        legalForms[Key(spelling.GetString()!)] = (form.Name, ambiguous);
                    }
                }
            }
        }

        var phrases = documents["legal-forms.json"].GetProperty("phrases").EnumerateObject()
            .Select(phrase => (Words: Normalizer.Words(phrase.Name).ToArray(), Short: Key(phrase.Value.GetString()!)))
            .OrderByDescending(phrase => phrase.Words.Length)
            .GroupBy(phrase => phrase.Words[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var generic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in documents["generic.json"].GetProperty("words").EnumerateObject())
        {
            foreach (var spelling in word.Value.EnumerateArray())
            {
                generic[Key(spelling.GetString()!)] = word.Name;
            }
        }

        var words = documents["words.json"];
        HashSet<string> Set(string property) => [.. words.GetProperty(property).EnumerateArray().SelectMany(word => Normalizer.Words(word.GetString()!))];

        var spellingGroups = new Dictionary<string, int>(StringComparer.Ordinal);
        var spellingMembers = documents["spelling-pairs.json"].GetProperty("groups").EnumerateArray()
            .Select(group => group.EnumerateArray().Select(spelling => Normalizer.StripDiacritics(Key(spelling.GetString()!))).Distinct(StringComparer.Ordinal).ToArray())
            .ToList();
        for (var group = 0; group < spellingMembers.Count; group++)
        {
            foreach (var spelling in spellingMembers[group])
            {
                spellingGroups[spelling] = group;
            }
        }

        var cognateFolds = documents["cognates.json"].GetProperty("groups").EnumerateArray()
            .Select(group => group.EnumerateArray().SelectMany(name => tokens.Create(Key(name.GetString()!)).Folds).Distinct(StringComparer.Ordinal).ToArray())
            .ToList();
        var cognateGroups = cognateFolds
            .SelectMany((folds, group) => folds.Select(fold => (fold, group)))
            .GroupBy(pair => pair.fold, StringComparer.Ordinal)
            .ToDictionary(pairs => pairs.Key, pairs => pairs.Select(pair => pair.group).Distinct().ToArray(), StringComparer.Ordinal);

        var countries = new Dictionary<string, string>(StringComparer.Ordinal);
        var countryCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var country in documents["countries.json"].EnumerateArray())
        {
            var code = country.GetProperty("code").GetString()!;
            countryCodes.Add(code);
            foreach (var name in country.GetProperty("names").EnumerateArray())
            {
                foreach (var key in Keys(name.GetString()!))
                {
                    countries.TryAdd(key, code);
                }
            }
        }

        var places = new Dictionary<string, (int, string, string)>(StringComparer.Ordinal);
        var placeId = 0;
        foreach (var place in documents["places.json"].EnumerateArray())
        {
            var country = place.GetProperty("country").GetString()!;
            var placeNames = place.GetProperty("names").EnumerateArray().Select(name => name.GetString()!).ToList();
            foreach (var name in placeNames)
            {
                foreach (var key in Keys(name))
                {
                    places.TryAdd(key, (placeId, country, placeNames[0]));
                }
            }

            placeId++;
        }

        return new MatcherData
        {
            Hash = Convert.ToHexStringLower(sha.GetHashAndReset())[..16],
            Tables = tables,
            Tokens = tokens,
            LegalForms = legalForms,
            LegalPhrases = phrases,
            Generic = generic,
            StopWords = Set("stop"),
            Honorifics = Set("honorifics"),
            AddressWords = Set("addressWords"),
            SpellingGroups = spellingGroups,
            SpellingGroupMembers = spellingMembers,
            CognateGroups = cognateGroups,
            CognateFolds = cognateFolds,
            Countries = countries,
            CountryCodes = countryCodes,
            MaxCountryWords = countries.Keys.Max(name => name.Count(c => c == ' ') + 1),
            Places = places,
            MaxPlaceWords = places.Keys.Max(name => name.Count(c => c == ' ') + 1),
        };
    }

    /// <summary>A multi-word name's lookup keys: its normalised words joined by spaces, and for Latin names also
    /// without diacritics (<c>Türkiye</c>, <c>turkiye</c>).</summary>
    private static IEnumerable<string> Keys(string name)
    {
        var key = string.Join(' ', Normalizer.Words(name));
        yield return key;
        if (!key.Any(Normalizer.IsCyrillic))
        {
            yield return Normalizer.StripDiacritics(key);
        }
    }

    /// <summary>A single-word dictionary entry, normalised like a token's text.</summary>
    private static string Key(string value) => string.Concat(Normalizer.Words(value));

    private static TransliterationTable ReadTable(JsonElement table)
    {
        static Dictionary<char, string> Letters(JsonElement letters) =>
            letters.EnumerateObject().ToDictionary(letter => letter.Name[0], letter => letter.Value.GetString()!);

        return new TransliterationTable(table.GetProperty("language").GetString()!, Letters(table.GetProperty("letters")), Letters(table.GetProperty("initial")));
    }
}
