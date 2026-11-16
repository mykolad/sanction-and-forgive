using System.Globalization;
using System.Text;

namespace SanctionAndForgive.Matching.Text;

/// <summary>The first steps of section 5.1, shared by names, addresses and the dictionaries: NFKC, case-fold,
/// apostrophes inside words deleted (<c>Мар'яна</c> stays one word), quotes and other punctuation as separators,
/// <c>&amp;</c> as "and".</summary>
internal static class Normalizer
{
    private const string Apostrophes = "'’ʼʻ`′‘";

    /// <summary>The letters a word's marks don't come off by decomposing (<c>Data/letters.json</c>, hashed into the
    /// matcher version with the other dictionaries): what each folds to, and the second spellings.</summary>
    private static readonly (Dictionary<char, string> Folds, Dictionary<char, string> SecondForms) Letters = LoadLetters();

    /// <summary>The words of <paramref name="value"/>, normalised, in order.</summary>
    public static List<string> Words(string value)
    {
        var text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                word.Append(c);
            }
            else if (Apostrophes.Contains(c) && word.Length > 0 && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                // Inside a word: deleted, so the word stays whole.
            }
            else
            {
                Flush(words, word);
                if (c == '&')
                {
                    words.Add("and");
                }
            }
        }

        Flush(words, word);
        return words;
    }

    /// <summary>A Latin word without diacritics: <c>Ştefan</c> → <c>stefan</c>, <c>straße</c> → <c>strasse</c>,
    /// <c>þór</c> → <c>thor</c>. The folds (letters.json) apply first, then the marks decomposition leaves are dropped.</summary>
    public static string StripDiacritics(string value)
    {
        var folded = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (Letters.Folds.TryGetValue(c, out var fold))
            {
                folded.Append(fold);
            }
            else
            {
                folded.Append(c);
            }
        }

        var decomposed = folded.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A word's second spelling (letters.json): <c>müller</c> → <c>mueller</c>, <c>jørgen</c> → <c>joergen</c>,
    /// <c>đorđe</c> → <c>djordje</c>; null if it has none of those letters.</summary>
    public static string? SecondForm(string value)
    {
        if (!value.Any(Letters.SecondForms.ContainsKey))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (Letters.SecondForms.TryGetValue(c, out var second))
            {
                builder.Append(second);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ԯ';

    public static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ';

    private static (Dictionary<char, string> Folds, Dictionary<char, string> SecondForms) LoadLetters()
    {
        using var stream = typeof(Normalizer).Assembly.GetManifestResourceStream("SanctionAndForgive.Matching.Data.letters.json")
            ?? throw new InvalidOperationException("The matcher's letters.json isn't embedded.");
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        static Dictionary<char, string> Table(System.Text.Json.JsonElement table) =>
            table.EnumerateObject().ToDictionary(
                letter => letter.Name.Length == 1 ? letter.Name[0] : throw new InvalidOperationException($"letters.json: '{letter.Name}' isn't one letter."),
                letter => letter.Value.GetString()!);
        return (Table(document.RootElement.GetProperty("folds")), Table(document.RootElement.GetProperty("secondForms")));
    }

    private static void Flush(List<string> words, StringBuilder word)
    {
        if (word.Length > 0)
        {
            words.Add(word.ToString());
            word.Clear();
        }
    }
}
