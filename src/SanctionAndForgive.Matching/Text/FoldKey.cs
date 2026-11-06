using System.Text;

namespace SanctionAndForgive.Matching.Text;

/// <summary>A key on which romanisation schemes agree, for comparison only: <c>Grigoriy</c> and <c>Hryhorii</c>,
/// <c>Khmelnitsky</c> and <c>Khmelnytskyi</c> share one. The rules apply in the spec's order (section 5.2).</summary>
internal static class FoldKey
{
    private static readonly (string From, string To)[] Rules =
    [
        ("shch", "s"), ("sch", "s"), ("kh", "h"), ("zh", "z"), ("ts", "c"), ("tz", "c"), ("ch", "c"), ("sh", "s"),
        ("ya", "a"), ("ia", "a"), ("yu", "u"), ("iu", "u"), ("ye", "e"), ("ie", "e"), ("yo", "o"), ("io", "o"),
        ("y", "i"), ("j", "i"), ("g", "h"),
    ];

    public static string Of(string latin)
    {
        var key = latin;
        foreach (var (from, to) in Rules)
        {
            key = key.Replace(from, to, StringComparison.Ordinal);
        }

        if (key.EndsWith("ff", StringComparison.Ordinal))
        {
            key = key[..^2] + "v";
        }

        var collapsed = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (collapsed.Length == 0 || collapsed[^1] != c)
            {
                collapsed.Append(c);
            }
        }

        return collapsed.ToString();
    }
}
