using System.Text;

namespace SanctionAndForgive.Matching.Text;

/// <summary>Builds <see cref="Token"/>s: every Latin reading and its fold key (sections 5.1 steps 6-7 and 5.2).
/// Never strips marks before transliterating: decomposition would turn <c>й</c> into <c>и</c>.</summary>
internal sealed class TokenFactory(IReadOnlyList<TransliterationTable> tables)
{
    private const int MaxForms = 8;

    /// <summary>Cyrillic letters that look like Latin ones (by their capitals), for a word typed in the wrong script.</summary>
    private static readonly Dictionary<char, char> LookAlikes = new()
    {
        ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's',
    };

    public Token Create(string text)
    {
        var hasCyrillic = text.Any(Normalizer.IsCyrillic);
        var hasLatin = text.Any(Normalizer.IsLatin);
        var isNumber = text.All(char.IsAsciiDigit);
        var forms = new List<string>();

        if (hasCyrillic)
        {
            forms.AddRange(tables.Select(table => table.Transliterate(text)));
            if (hasLatin || (text.Length <= 4 && text.All(c => LookAlikes.ContainsKey(c) || char.IsAsciiLetter(c))))
            {
                forms.Add(new string([.. text.Select(c => LookAlikes.TryGetValue(c, out var latin) ? latin : c)]));
            }

            forms = [.. forms.Select(Normalizer.StripDiacritics)];
        }
        else if (hasLatin || isNumber)
        {
            forms.Add(Normalizer.StripDiacritics(text));
            if (text.AsSpan().IndexOfAny("üöä") >= 0)
            {
                forms.Add(Normalizer.StripDiacritics(new StringBuilder(text).Replace("ü", "ue").Replace("ö", "oe").Replace("ä", "ae").ToString()));
            }
        }

        var distinct = forms.Where(form => form.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxForms).ToArray();
        var folds = isNumber ? distinct : [.. distinct.Select(FoldKey.Of).Distinct(StringComparer.Ordinal)];
        return new Token(text, distinct, folds, hasCyrillic, isNumber);
    }
}

/// <summary>One language's Cyrillic-to-Latin table (<c>Data/translit/*.json</c>). <see cref="Initial"/> letters
/// apply at the start of a word (Ukrainian <c>є ї й ю я</c>).</summary>
internal sealed record TransliterationTable(string Language, Dictionary<char, string> Letters, Dictionary<char, string> Initial)
{
    public string Transliterate(string text)
    {
        var builder = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var wordStart = i == 0 || !char.IsLetter(text[i - 1]);
            if (wordStart && Initial.TryGetValue(c, out var initial))
            {
                builder.Append(initial);
            }
            else if (Letters.TryGetValue(c, out var latin))
            {
                builder.Append(latin);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
