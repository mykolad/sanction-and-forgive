using System.Globalization;
using System.Text;

namespace SanctionAndForgive.Lists;

/// <summary>Small text helpers the parsers share.</summary>
public static class Text
{
    /// <summary>The script of a name: Cyrillic if it has any Cyrillic letter, Latin if all its letters are Latin,
    /// Other otherwise (Arabic, Chinese …). Mixed Latin and Cyrillic counts as Cyrillic: transliteration needs it.</summary>
    public static NameScript DetectScript(string value)
    {
        var latin = false;
        var other = false;
        foreach (var c in value)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (c is >= 'Ѐ' and <= 'ԯ')
            {
                return NameScript.Cyrillic;
            }

            if (c <= 'ɏ' || c is >= 'Ḁ' and <= 'ỿ')
            {
                latin = true;
            }
            else
            {
                other = true;
            }
        }

        return other || !latin ? NameScript.Other : NameScript.Latin;
    }

    /// <summary>Trims and collapses whitespace; null when nothing is left.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The non-empty parts, cleaned and joined with <paramref name="separator"/>; null if there are none.</summary>
    public static string? Join(string separator, params IEnumerable<string?> parts)
    {
        var cleaned = parts.Select(Clean).Where(part => part is not null).ToList();
        return cleaned.Count == 0 ? null : string.Join(separator, cleaned);
    }

    /// <summary>A date in one of the given exact formats, or null.</summary>
    public static DateOnly? ParseDate(string? value, params string[] formats)
    {
        var cleaned = Clean(value);
        return cleaned is not null
            && DateOnly.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : null;
    }
}
