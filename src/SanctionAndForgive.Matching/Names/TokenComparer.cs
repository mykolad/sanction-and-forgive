using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching.Names;

/// <summary>How two tokens aligned (section 5.4), most certain first.</summary>
internal enum AlignMethod
{
    None,
    Identical,
    Transliteration,
    SpellingPair,
    FoldKey,
    FoldKeyLatin,
    Cognate,
    OneEdit,
    OneEditShort,
    TwoEdits,
}

internal readonly record struct Alignment(double Similarity, AlignMethod Method)
{
    public static Alignment None { get; } = new(0, AlignMethod.None);

    public bool Aligned => Method != AlignMethod.None;

    /// <summary>Aligned by a method that caps the match (fold key with both sides Latin, cognate, short edits).</summary>
    public bool Capped => Method is AlignMethod.FoldKeyLatin or AlignMethod.Cognate or AlignMethod.OneEditShort or AlignMethod.TwoEdits;
}

/// <summary>Compares two tokens; the first rule that applies wins. Short tokens (1-3 letters) align only exactly,
/// by transliteration or by fold key. Edits are Damerau-Levenshtein, with one more for a different first letter, so
/// <c>northwind</c> and <c>southwind</c> don't align.</summary>
internal sealed class TokenComparer(MatcherData data)
{
    public Alignment Compare(Token query, Token listed)
    {
        if (query.Text == listed.Text)
        {
            return new Alignment(1.0, AlignMethod.Identical);
        }

        if (query.IsNumber || listed.IsNumber)
        {
            return Alignment.None;
        }

        if (query.Forms.Any(form => listed.Forms.Contains(form)))
        {
            return new Alignment(0.98, AlignMethod.Transliteration);
        }

        if (SpellingGroups(query).Overlaps(SpellingGroups(listed)))
        {
            return new Alignment(0.98, AlignMethod.SpellingPair);
        }

        if (query.Folds.Any(fold => listed.Folds.Contains(fold)))
        {
            return query.IsCyrillic || listed.IsCyrillic
                ? new Alignment(0.96, AlignMethod.FoldKey)
                : new Alignment(0.96, AlignMethod.FoldKeyLatin);
        }

        if (CognateGroups(query).Overlaps(CognateGroups(listed)))
        {
            return new Alignment(0.96, AlignMethod.Cognate);
        }

        return CompareEdits(query, listed);
    }

    /// <summary>The cognate groups a token belongs to, through any of its fold keys.</summary>
    public HashSet<int> CognateGroups(Token token)
    {
        var groups = new HashSet<int>();
        foreach (var fold in token.Folds)
        {
            if (data.CognateGroups.TryGetValue(fold, out var found))
            {
                groups.UnionWith(found);
            }
        }

        return groups;
    }

    /// <summary>Every fold key of a cognate group's members.</summary>
    public string[] CognateFoldsOf(int group) => data.CognateFolds[group];

    private HashSet<int> SpellingGroups(Token token)
    {
        var groups = new HashSet<int>();
        foreach (var form in token.Forms)
        {
            if (data.SpellingGroups.TryGetValue(form, out var group))
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    private static Alignment CompareEdits(Token query, Token listed)
    {
        var best = Alignment.None;
        foreach (var queryForm in query.Forms)
        {
            foreach (var listedForm in listed.Forms)
            {
                var shorter = Math.Min(queryForm.Length, listedForm.Length);
                if (shorter < 4)
                {
                    continue;
                }

                var edits = EditDistance.DamerauLevenshtein(queryForm, listedForm, 3) + (queryForm[0] == listedForm[0] ? 0 : 1);
                var jaroWinkler = EditDistance.JaroWinkler(queryForm, listedForm);
                var alignment = (edits, shorter) switch
                {
                    (1, >= 8) => new Alignment(jaroWinkler, AlignMethod.OneEdit),
                    (1, _) => new Alignment(jaroWinkler, AlignMethod.OneEditShort),
                    (2, >= 8) when jaroWinkler >= 0.90 => new Alignment(jaroWinkler, AlignMethod.TwoEdits),
                    _ => Alignment.None,
                };
                if (alignment.Aligned && (!best.Aligned || Rank(alignment) < Rank(best) || (Rank(alignment) == Rank(best) && alignment.Similarity > best.Similarity)))
                {
                    best = alignment;
                }
            }
        }

        return best;

        static int Rank(Alignment alignment) => (int)alignment.Method;
    }
}

internal static class EditDistance
{
    /// <summary>Optimal string alignment distance (adjacent transpositions count once), or <paramref name="limit"/>
    /// as soon as it can't be lower.</summary>
    public static int DamerauLevenshtein(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) >= limit)
        {
            return limit;
        }

        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previous2[j - 2] + 1);
                }

                current[j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            if (rowMinimum >= limit)
            {
                return limit;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        return Math.Min(previous[b.Length], limit);
    }

    public static double JaroWinkler(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var window = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);
        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(b.Length - 1, i + window);
            for (var j = start; j <= end; j++)
            {
                if (!bMatched[j] && a[i] == b[j])
                {
                    aMatched[i] = bMatched[j] = true;
                    matches++;
                    break;
                }
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        for (int i = 0, j = 0; i < a.Length; i++)
        {
            if (!aMatched[i])
            {
                continue;
            }

            while (!bMatched[j])
            {
                j++;
            }

            if (a[i] != b[j])
            {
                transpositions++;
            }

            j++;
        }

        double m = matches;
        var jaro = ((m / a.Length) + (m / b.Length) + ((m - (transpositions / 2.0)) / m)) / 3;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * 0.1 * (1 - jaro));
    }
}
