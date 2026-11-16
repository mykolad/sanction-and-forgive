using System.Text;

namespace SanctionAndForgive.Lists;

/// <summary>Which writing systems a name is in, for measuring the lists (<c>probe --scripts</c>, docs/sources.md):
/// the Unicode script of each letter, from the blocks the lists use, and, for a Cyrillic name, the alphabet its
/// letters point to. A measurement, not part of matching: <see cref="Text.DetectScript"/> stays as it is.</summary>
public static class Scripts
{
    private static readonly (int First, int Last, string Script)[] Ranges =
    [
        (0x0041, 0x024F, "Latin"), (0x0250, 0x02AF, "Latin"), (0x1E00, 0x1EFF, "Latin"), (0x2C60, 0x2C7F, "Latin"), (0xA720, 0xA7FF, "Latin"),
        (0xAB30, 0xAB6F, "Latin"), (0xFF21, 0xFF5A, "Latin"),
        (0x0370, 0x03FF, "Greek"), (0x1F00, 0x1FFF, "Greek"),
        (0x0400, 0x052F, "Cyrillic"), (0x1C80, 0x1C8F, "Cyrillic"), (0x2DE0, 0x2DFF, "Cyrillic"), (0xA640, 0xA69F, "Cyrillic"),
        (0x0530, 0x058F, "Armenian"), (0xFB13, 0xFB17, "Armenian"),
        (0x0590, 0x05FF, "Hebrew"), (0xFB1D, 0xFB4F, "Hebrew"),
        (0x0600, 0x06FF, "Arabic"), (0x0750, 0x077F, "Arabic"), (0x0870, 0x08FF, "Arabic"), (0xFB50, 0xFDFF, "Arabic"), (0xFE70, 0xFEFF, "Arabic"),
        (0x0700, 0x074F, "Syriac"), (0x0780, 0x07BF, "Thaana"),
        (0x0900, 0x097F, "Devanagari"), (0x0980, 0x09FF, "Bengali"), (0x0A00, 0x0A7F, "Gurmukhi"), (0x0A80, 0x0AFF, "Gujarati"),
        (0x0B00, 0x0B7F, "Oriya"), (0x0B80, 0x0BFF, "Tamil"), (0x0C00, 0x0C7F, "Telugu"), (0x0C80, 0x0CFF, "Kannada"),
        (0x0D00, 0x0D7F, "Malayalam"), (0x0D80, 0x0DFF, "Sinhala"), (0x0E00, 0x0E7F, "Thai"), (0x0E80, 0x0EFF, "Lao"),
        (0x0F00, 0x0FFF, "Tibetan"), (0x1000, 0x109F, "Myanmar"),
        (0x10A0, 0x10FF, "Georgian"), (0x1C90, 0x1CBF, "Georgian"), (0x2D00, 0x2D2F, "Georgian"),
        (0x1100, 0x11FF, "Hangul"), (0x3130, 0x318F, "Hangul"), (0xA960, 0xA97F, "Hangul"), (0xAC00, 0xD7AF, "Hangul"), (0xD7B0, 0xD7FF, "Hangul"),
        (0xFFA0, 0xFFDC, "Hangul"),
        (0x1200, 0x139F, "Ethiopic"), (0x1780, 0x17FF, "Khmer"), (0x1800, 0x18AF, "Mongolian"),
        (0x3040, 0x309F, "Hiragana"), (0x30A0, 0x30FF, "Katakana"), (0x31F0, 0x31FF, "Katakana"), (0xFF66, 0xFF9F, "Katakana"),
        (0x3100, 0x312F, "Bopomofo"),
        (0x2E80, 0x2FDF, "Han"), (0x3005, 0x3007, "Han"), (0x3021, 0x3029, "Han"), (0x3038, 0x303B, "Han"), (0x3400, 0x4DBF, "Han"),
        (0x4E00, 0x9FFF, "Han"), (0xF900, 0xFAFF, "Han"), (0x20000, 0x323AF, "Han"),
    ];

    /// <summary>The script of a letter, or "Other" for one in no block above.</summary>
    public static string Of(Rune letter)
    {
        var value = letter.Value;
        foreach (var (first, last, script) in Ranges)
        {
            if (value >= first && value <= last)
            {
                return script;
            }
        }

        return "Other";
    }

    /// <summary>The scripts of a name's letters, in alphabetical order, joined with "+" ("Latin", "Han+Katakana");
    /// "None" for a name without letters.</summary>
    public static string InName(string name)
    {
        var scripts = new List<string>();
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && Of(rune) is var script && !scripts.Contains(script))
            {
                scripts.Add(script);
            }
        }

        scripts.Sort(StringComparer.Ordinal);
        return scripts.Count == 0 ? "None" : string.Join('+', scripts);
    }

    /// <summary>For a name with Cyrillic letters, the alphabet its letters point to: letters only one alphabet (or a
    /// few) has. A name with none of them is "Russian or unmarked" (Russian, Bulgarian and others share every letter it
    /// uses).</summary>
    public static string CyrillicAlphabet(string name)
    {
        var lower = name.ToLowerInvariant();
        bool Any(string letters) => lower.AsSpan().IndexOfAny(letters) >= 0;
        return true switch
        {
            _ when Any("ў") => "Belarusian (ў)",
            _ when Any("әғқңұһ") => "Kazakh (ә ғ қ ң ұ һ)",
            _ when Any("ӣӯҳҷ") => "Tajik (ӣ ӯ ҳ ҷ)",
            _ when Any("өү") => "Kyrgyz, Mongolian or Kazakh (ө ү)",
            _ when Any("ћђ") => "Serbian (ћ ђ)",
            _ when Any("ѓќѕ") => "Macedonian (ѓ ќ ѕ)",
            _ when Any("јљњџ") => "Serbian or Macedonian (ј љ њ џ)",
            _ when Any("ґєї") => "Ukrainian (ґ є ї)",
            _ when Any("і") => "Ukrainian, Belarusian or Kazakh (і)",
            _ => "Russian or unmarked",
        };
    }
}
