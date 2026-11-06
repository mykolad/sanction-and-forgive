using System.Runtime.CompilerServices;
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
    // One comparer serves one search, on one thread. A search compares the same few entered tokens with the same listed
    // tokens (an index interns its tokens) thousands of times, so each pair is compared once, and each token's groups
    // are looked up once. By reference: a cached answer is the answer for those very tokens, so nothing changes but time.
    private readonly Dictionary<(Token Query, Token Listed), Alignment> compared = new(TokenPairComparer.Instance);
    private readonly Dictionary<Token, HashSet<int>> spellingGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Token, HashSet<int>> cognateGroups = new(ReferenceEqualityComparer.Instance);

    public Alignment Compare(Token query, Token listed)
    {
        if (!compared.TryGetValue((query, listed), out var alignment))
        {
            alignment = CompareOnce(query, listed);
            compared[(query, listed)] = alignment;
        }

        return alignment;
    }

    private Alignment CompareOnce(Token query, Token listed)
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
    public IReadOnlySet<int> CognateGroups(Token token)
    {
        if (!cognateGroups.TryGetValue(token, out var groups))
        {
            groups = [];
            foreach (var fold in token.Folds)
            {
                if (data.CognateGroups.TryGetValue(fold, out var found))
                {
                    groups.UnionWith(found);
                }
            }

            cognateGroups[token] = groups;
        }

        return groups;
    }

    /// <summary>Every fold key of a cognate group's members.</summary>
    public string[] CognateFoldsOf(int group) => data.CognateFolds[group];

    private HashSet<int> SpellingGroups(Token token)
    {
        if (!spellingGroups.TryGetValue(token, out var groups))
        {
            groups = [];
            foreach (var form in token.Forms)
            {
                if (data.SpellingGroups.TryGetValue(form, out var group))
                {
                    groups.Add(group);
                }
            }

            spellingGroups[token] = groups;
        }

        return groups;
    }

    private sealed class TokenPairComparer : IEqualityComparer<(Token Query, Token Listed)>
    {
        public static TokenPairComparer Instance { get; } = new();

        public bool Equals((Token Query, Token Listed) x, (Token Query, Token Listed) y) =>
            ReferenceEquals(x.Query, y.Query) && ReferenceEquals(x.Listed, y.Listed);

        public int GetHashCode((Token Query, Token Listed) obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Query), RuntimeHelpers.GetHashCode(obj.Listed));
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
                if (edits is not (1 or 2))
                {
                    // No rule below aligns any other distance, whatever the Jaro-Winkler similarity: don't compute it.
                    continue;
                }

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

        // Three rows on the stack for a word (a name token is short); a long string gets them on the heap.
        var width = b.Length + 1;
        Span<int> previous2 = width <= MaxStackWidth ? stackalloc int[width] : new int[width];
        Span<int> previous = width <= MaxStackWidth ? stackalloc int[width] : new int[width];
        Span<int> current = width <= MaxStackWidth ? stackalloc int[width] : new int[width];
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

            var oldest = previous2;
            previous2 = previous;
            previous = current;
            current = oldest;
        }

        return Math.Min(previous[b.Length], limit);
    }

    private const int MaxStackWidth = 128;

    public static double JaroWinkler(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var window = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);
        Span<bool> aMatched = a.Length <= MaxStackWidth ? stackalloc bool[a.Length] : new bool[a.Length];
        Span<bool> bMatched = b.Length <= MaxStackWidth ? stackalloc bool[b.Length] : new bool[b.Length];
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
