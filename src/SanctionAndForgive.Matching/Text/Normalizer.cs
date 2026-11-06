using System.Globalization;
using System.Text;

namespace SanctionAndForgive.Matching.Text;

/// <summary>The first steps of section 5.1, shared by names, addresses and the dictionaries: NFKC, case-fold,
/// apostrophes inside words deleted (<c>Мар'яна</c> stays one word), quotes and other punctuation as separators,
/// <c>&amp;</c> as "and".</summary>
internal static class Normalizer
{
    private const string Apostrophes = "'’ʼʻ`′‘";

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

    /// <summary>A Latin word without diacritics: <c>Ştefan</c> → <c>stefan</c>, <c>straße</c> → <c>strasse</c>.</summary>
    public static string StripDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            switch (c)
            {
                case 'ß': builder.Append("ss"); break;
                case 'ø': builder.Append('o'); break;
                case 'đ': builder.Append('d'); break;
                case 'ł': builder.Append('l'); break;
                case 'æ': builder.Append("ae"); break;
                case 'œ': builder.Append("oe"); break;
                case 'ı': builder.Append('i'); break;
                default:
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool IsCyrillic(char c) => c is >= 'Ѐ' and <= 'ԯ';

    public static bool IsLatin(char c) => c is >= 'a' and <= 'z' or >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ';

    private static void Flush(List<string> words, StringBuilder word)
    {
        if (word.Length > 0)
        {
            words.Add(word.ToString());
            word.Clear();
        }
    }
}
