using SanctionAndForgive.Matching.Names;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching.Addresses;

internal sealed record AddressComparison(AddressState State, string Explanation);

/// <summary>Section 5.8. Only countries can conflict: unaligned words never do, since addresses are partial and
/// cross scripts. A number alone never counts as agreement.</summary>
internal sealed class AddressComparer(TokenComparer comparer, MatcherData data)
{
    public AddressComparison Compare(AddressForm? entered, IReadOnlyList<AddressForm> listed)
    {
        if (entered is null || entered.IsEmpty)
        {
            return new AddressComparison(AddressState.NotCompared, "Name only: no address entered");
        }

        var comparable = listed.Where(address => !address.IsEmpty).ToList();
        if (comparable.Count == 0)
        {
            return new AddressComparison(AddressState.NotCompared, "Address not compared: none listed");
        }

        var results = comparable.Select(address => CompareOne(entered, address)).ToList();
        return results.FirstOrDefault(result => result.State == AddressState.Corroborated)
            ?? results.FirstOrDefault(result => result.State == AddressState.Conflicting)
            ?? new AddressComparison(AddressState.NotCompared, "Address compared, nothing in common");
    }

    /// <summary>Address-only screening: at least 3 tokens align, with a number and a word, and the countries don't
    /// conflict. Returns the number of aligned tokens, and whether the rule is met.</summary>
    public (int Aligned, bool Matches, string Explanation) CompareAddressOnly(AddressForm entered, AddressForm listed)
    {
        if (entered.EffectiveCountry is { } a && listed.EffectiveCountry is { } b && a != b)
        {
            return (0, false, $"Countries differ: {a}, {b}");
        }

        var places = entered.Places.Intersect(listed.Places).Count();
        var words = AlignedWords(entered.Words, listed.Words).Count;
        var numbers = entered.Numbers.Select(number => number.Text).Intersect(listed.Numbers.Select(number => number.Text)).Count();
        var aligned = places + words + numbers;
        var matches = aligned >= 3 && numbers >= 1 && places + words >= 1;
        return (aligned, matches, $"Address only: {aligned} parts align ({places + words} words or places, {numbers} numbers)");
    }

    private AddressComparison CompareOne(AddressForm entered, AddressForm listed)
    {
        var enteredCountry = entered.EffectiveCountry;
        var listedCountry = listed.EffectiveCountry;
        if (enteredCountry is not null && listedCountry is not null && enteredCountry != listedCountry)
        {
            return new AddressComparison(AddressState.Conflicting, $"Address: countries differ ({enteredCountry}, listed {listedCountry})");
        }

        var place = entered.Places.Intersect(listed.Places).Select(PlaceName).FirstOrDefault();
        if (place is not null)
        {
            return new AddressComparison(AddressState.Corroborated, $"Address: same place ({place})");
        }

        var words = AlignedWords(entered.Words, listed.Words);
        if (words.Count > 0)
        {
            return new AddressComparison(AddressState.Corroborated, $"Address: {string.Join(", ", words.Select(pair => $"{pair.Entered} ~ {pair.Listed}"))}");
        }

        return enteredCountry is not null && enteredCountry == listedCountry
            ? new AddressComparison(AddressState.Corroborated, $"Address: country only ({enteredCountry})")
            : new AddressComparison(AddressState.NotCompared, "Address compared, nothing in common");
    }

    private List<(Token Entered, Token Listed)> AlignedWords(Token[] entered, Token[] listed)
    {
        var used = new HashSet<Token>();
        var pairs = new List<(Token, Token)>();
        foreach (var word in entered)
        {
            var match = listed.FirstOrDefault(candidate => !used.Contains(candidate) && comparer.Compare(word, candidate).Aligned);
            if (match is not null)
            {
                used.Add(match);
                pairs.Add((word, match));
            }
        }

        return pairs;
    }

    private string PlaceName(int id) => data.Places.Values.First(place => place.Id == id).Name;
}
