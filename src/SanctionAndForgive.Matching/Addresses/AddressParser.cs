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
        if (TrailingCode().Match(raw) is { Success: true } trailing && data.CountryCodes.Contains(trailing.Groups[1].Value))
        {
            country ??= trailing.Groups[1].Value;
            words.RemoveAt(words.Count - 1);
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
                namedCountry = countryMatch.Value;
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

            var token = tokens[i];
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
