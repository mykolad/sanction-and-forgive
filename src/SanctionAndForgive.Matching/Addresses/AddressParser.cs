using System.Text.RegularExpressions;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching.Addresses;

/// <summary>An address read as section 5.8 says: its country (stated, or implied by a known place), the places it
/// names, and its remaining words and numbers. Street-type words are gone.</summary>
internal sealed record AddressForm(string Raw, Token[] Words, Token[] Numbers, int[] Places, string? Country, string? ImpliedCountry)
{
    public string? EffectiveCountry => Country ?? ImpliedCountry;

    public bool IsEmpty => Words.Length == 0 && Numbers.Length == 0 && Places.Length == 0 && EffectiveCountry is null;
}

/// <summary>Reads a free-text address in any format: <c>Tallinn</c>, <c>Estonia</c>, <c>ул. Ленина 5, Москва</c>,
/// <c>Deliver to warehouse in Riga</c>. A two-letter ISO code counts only as the last word and in capitals, so the
/// word "in" is never India.</summary>
internal sealed partial class AddressParser(MatcherData data, Tokenizer tokenizer)
{
    public AddressForm Parse(string raw, string? sourceCountryCode)
    {
        string? country = sourceCountryCode is { Length: 2 } code && data.CountryCodes.Contains(code.ToUpperInvariant()) ? code.ToUpperInvariant() : null;
        var words = Normalizer.Words(raw);
        var parts = Parts(raw, words.Count);
        var followed = false; // a final ISO code follows the last word, as a country name would

        if (TrailingCode().Match(raw) is { Success: true } trailing && data.CountryCodes.Contains(trailing.Groups[1].Value))
        {
            country ??= trailing.Groups[1].Value;
            words.RemoveAt(words.Count - 1);
            parts.RemoveAt(parts.Count - 1);
            followed = true;
        }

        var tokens = words.Select(tokenizer.Get).ToList();
        var places = new List<int>();
        string? namedCountry = null, impliedCountry = null;
        var remaining = new List<Token>();
        var numbers = new List<Token>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (Lookup(tokens, i, data.MaxCountryWords, data.Countries) is { } countryMatch)
            {
                if (NamesAStreet(tokens, parts, i, countryMatch.Length, followed))
                {
                    // The whole name is the street's: no country or place within it ("rue de la République
                    // démocratique du Congo" is neither the DRC nor the Congo).
                    for (var k = i; k < i + countryMatch.Length; k++)
                    {
                        AddWord(tokens[k]);
                    }
                }
                else
                {
                    namedCountry = countryMatch.Value;
                }

                i += countryMatch.Length - 1;
                continue;
            }

            if (Lookup(tokens, i, data.MaxPlaceWords, data.Places) is { } placeMatch)
            {
                places.Add(placeMatch.Value.Id);
                impliedCountry ??= placeMatch.Value.Country;
                i += placeMatch.Length - 1;
                continue;
            }

            AddWord(tokens[i]);
        }

        void AddWord(Token token)
        {
            if (token.IsNumber)
            {
                numbers.Add(token);
            }
            else if (!data.AddressWords.Contains(token.Text) && !token.Forms.Any(data.AddressWords.Contains) && token.Length > 1)
            {
                remaining.Add(token);
            }
        }

        return new AddressForm(raw, [.. remaining], [.. numbers], [.. places.Distinct()], country ?? namedCountry, impliedCountry);
    }

    /// <summary>Whether the country name of <paramref name="length"/> words starting at <paramref name="start"/> names
    /// a street: after a street type and one or two particles (<c>12 rue de Malte, Paris</c> is in France, not Malta);
    /// right after a street type in the same part of the address (<paramref name="parts"/>), unless it ends the address
    /// (<c>Via Germania 12</c> is a street, <c>12 Example Avenue, Germany, 10115</c> and <c>Some Road Malta</c> name
    /// their countries); or right before a street type written after the name, in the same part (<c>Almanya Caddesi
    /// 12</c>, <c>Jordan Road</c>). A final ISO code (<paramref name="followed"/>) follows the last word as a country
    /// name would: <c>Via Germania, IT</c> is read as <c>Via Germania, Italia</c> is.</summary>
    private bool NamesAStreet(List<Token> tokens, List<int> parts, int start, int length, bool followed)
    {
        var end = start + length;
        if (end < tokens.Count && parts[end] == parts[end - 1] && Is(tokens[end], data.StreetSuffixes))
        {
            return true;
        }

        var i = start - 1;
        var particles = 0;
        while (i >= 0 && particles < 2 && Is(tokens[i], data.StreetParticles))
        {
            i--;
            particles++;
        }

        return i >= 0 && Is(tokens[i], data.StreetWords) && (particles > 0 || (parts[i] == parts[start] && (end < tokens.Count || followed)));
    }

    /// <summary>The parts of an address that commas, semicolons and line breaks separate: each word's part, in order.
    /// Should the parts' words ever differ from the whole's, every word is a part of its own, so a country name never
    /// names a street for want of a comma.</summary>
    private static List<int> Parts(string raw, int count)
    {
        var parts = new List<int>(count);
        var pieces = raw.Split(PartSeparators);
        for (var i = 0; i < pieces.Length; i++)
        {
            parts.AddRange(Enumerable.Repeat(i, Normalizer.Words(pieces[i]).Count));
        }

        return parts.Count == count ? parts : [.. Enumerable.Range(0, count).Select(i => -1 - i)];
    }

    private static readonly char[] PartSeparators = [',', ';', '\n', '\r', '\uFF0C', '\uFF1B', '\u3001'];

    private static bool Is(Token token, HashSet<string> words) => words.Contains(token.Text) || token.Forms.Any(words.Contains);

    /// <summary>The longest name starting at <paramref name="start"/>, matched on the words' own text or on one of
    /// their Latin readings (<c>Таллинн</c> read as <c>tallinn</c>).</summary>
    private static (T Value, int Length)? Lookup<T>(List<Token> tokens, int start, int maxWords, Dictionary<string, T> names)
    {
        for (var length = Math.Min(maxWords, tokens.Count - start); length >= 1; length--)
        {
            var span = tokens.GetRange(start, length);
            var variants = Enumerable.Range(-1, span.Max(token => token.Forms.Length) + 1)
                .Select(variant => string.Join(' ', span.Select(token => variant < 0 || variant >= token.Forms.Length ? token.Text : token.Forms[variant])));
            foreach (var key in variants)
            {
                if (names.TryGetValue(key, out var value))
                {
                    return (value, length);
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b([A-Z]{2})\W*$")]
    private static partial Regex TrailingCode();
}
