using System.Globalization;
using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching.Names;

/// <summary>A name's percentage against one listed alias, and why. <paramref name="Ceiling"/> is the lowest cap that
/// applied (or none): a later adjustment, such as a corroborating address, never lifts the result above it.</summary>
internal sealed record NameScore(int Percent, List<string> Explanation, int Ceiling)
{
    public static NameScore Zero(string reason) => new(0, [reason], 0);
}

/// <summary>Sections 5.5 and 5.6: aligns the core tokens (order-independent, best pairs first), turns the weighted
/// score into a percentage, applies the caps (lowest wins), contained names and initials.</summary>
internal sealed class NameScorer(TokenComparer comparer, MatchingOptions options)
{
    public NameScore Score(NameForm query, NameForm listed, bool weakAlias, Func<Token, double> weight)
    {
        if (query.LegalOnly)
        {
            return NameScore.Zero("Legal form only: nothing distinctive to compare");
        }

        if (query.IsEmpty || listed.IsEmpty)
        {
            return NameScore.Zero("Nothing distinctive to compare");
        }

        // Initials on either side, or both (partly expanded names): each side with initials is tried as the initials
        // side, and the better reading wins, so the result doesn't depend on which name is listed.
        var hasInitials = query.IsPerson && listed.IsPerson && (query.Initials.Length > 0 || listed.Initials.Length > 0);
        if (hasInitials)
        {
            NameScore?[] readings =
            [
                query.Initials.Length > 0 ? ScoreInitials(query, listed, weakAlias, queryHasInitials: true) : null,
                listed.Initials.Length > 0 ? ScoreInitials(listed, query, weakAlias, queryHasInitials: false) : null,
            ];
            if (readings.OfType<NameScore>().MaxBy(reading => reading.Percent) is { } initials)
            {
                return initials;
            }
        }

        var pairs = Align(query.Core, listed.Core, weight);
        var explanation = new List<string>();
        var aligned = 0.0;
        foreach (var pair in pairs)
        {
            aligned += pair.Alignment.Similarity * (weight(query.Core[pair.Query]) + weight(listed.Core[pair.Listed]));
            explanation.Add(Describe(query.Core[pair.Query], listed.Core[pair.Listed], pair.Alignment));
        }

        var total = query.Core.Sum(weight) + listed.Core.Sum(weight);
        var score = aligned / total;
        var percent = Bands.ToPercent(score, options);
        var caps = new List<(int Cap, string Reason)>();

        var unalignedQuery = Enumerable.Range(0, query.Core.Length).Except(pairs.Select(pair => pair.Query)).Select(i => query.Core[i]).ToList();
        var unalignedListed = Enumerable.Range(0, listed.Core.Length).Except(pairs.Select(pair => pair.Listed)).Select(i => listed.Core[i]).ToList();
        explanation.AddRange(unalignedQuery.Select(token => $"{token}: not in the listed name"));
        explanation.AddRange(unalignedListed.Select(token => $"{token}: listed, not entered"));

        // Contained names: every core token of the shorter name (at least 2) aligned, and the longer has more.
        var shorter = Math.Min(query.Core.Length, listed.Core.Length);
        if (shorter >= 2 && pairs.Count == shorter && query.Core.Length != listed.Core.Length)
        {
            percent = options.ContainedNamePercent;
            explanation.Add("One name contains the other");
        }
        else if (unalignedQuery.Count > 0 || unalignedListed.Count > 0)
        {
            caps.Add((options.NotAllAlignedCap, "Not every distinctive word aligned"));
        }

        if (!query.IsPerson && !listed.IsPerson && query.Core.Length == 1 && listed.Core.Length == 1 && pairs.Count == 1
            && query.Core[0].Length < 4 && !query.LegalClasses.Intersect(listed.LegalClasses).Any())
        {
            caps.Add((options.ThinCoreCap, query.LegalClasses.Length == 0 ? "Short name with no legal form" : "Short name with a different legal form"));
        }

        if (!query.IsPerson && !listed.IsPerson && !query.Generic.SequenceEqual(listed.Generic))
        {
            caps.Add((options.GenericCap, "Generic words differ"));
        }

        if (weakAlias)
        {
            caps.Add((options.WeakAliasCap, "The list marks this alias as low quality"));
        }

        if (pairs.Any(pair => pair.Alignment.Capped))
        {
            caps.Add((options.CappedMethodCap, "A word aligned only approximately"));
        }

        // Initials the initials rule couldn't use (a short surname, an unaligned word) still count as evidence:
        // never a full match on the surname alone, and a contradiction is a contradiction.
        if (hasInitials)
        {
            caps.Add((options.NotAllAlignedCap, "Initials couldn't be checked against given names"));
            if (Contradicts(query.Initials, [.. unalignedListed, .. listed.Initials]) || Contradicts(listed.Initials, [.. unalignedQuery, .. query.Initials]))
            {
                caps.Add((options.ContradictingInitialsPercent, "An initial contradicts a given name or initial"));
            }
        }

        foreach (var legal in query.LegalClasses.Union(listed.LegalClasses))
        {
            explanation.Add($"Legal form {legal} not compared");
        }

        return Finish(percent, caps, explanation);
    }

    /// <summary>Whether some initial finds no match among <paramref name="candidates"/> while candidates remain.</summary>
    private bool Contradicts(Token[] initials, List<Token> candidates)
    {
        foreach (var initial in initials)
        {
            var found = candidates.FirstOrDefault(candidate => InitialMatches(initial, candidate));
            if (found is not null)
            {
                candidates.Remove(found);
            }
            else if (candidates.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Surname plus initials (section 5.6), with <paramref name="initialsSide"/>'s initials. Null when the full
    /// tokens don't support it (a full token on the initials side didn't align, or none has 4 letters): then the name
    /// is scored as usual, with the initials as unverified evidence.</summary>
    private NameScore? ScoreInitials(NameForm initialsSide, NameForm otherSide, bool weakAlias, bool queryHasInitials)
    {
        if (initialsSide.Core.Length == 0)
        {
            return null;
        }

        var pairs = Align(initialsSide.Core, otherSide.Core, _ => 1.0);
        if (pairs.Count != initialsSide.Core.Length || !pairs.Any(pair => initialsSide.Core[pair.Query].Length >= 4))
        {
            return null;
        }

        var explanation = pairs
            .Select(pair => queryHasInitials
                ? Describe(initialsSide.Core[pair.Query], otherSide.Core[pair.Listed], pair.Alignment)
                : Describe(otherSide.Core[pair.Listed], initialsSide.Core[pair.Query], pair.Alignment))
            .ToList();
        var candidates = Enumerable.Range(0, otherSide.Core.Length).Except(pairs.Select(pair => pair.Listed))
            .Select(i => otherSide.Core[i]).Concat(otherSide.Initials).ToList();
        int matched = 0, contradicted = 0;
        foreach (var initial in initialsSide.Initials)
        {
            var found = candidates.FirstOrDefault(candidate => InitialMatches(initial, candidate));
            if (found is not null)
            {
                matched++;
                candidates.Remove(found);
                explanation.Add($"Initial {initial.Text.ToUpperInvariant()} matches {found}");
            }
            else if (candidates.Count > 0)
            {
                contradicted++;
                explanation.Add($"Initial {initial.Text.ToUpperInvariant()} doesn't match {string.Join(", ", candidates)}");
            }
            else
            {
                explanation.Add($"Initial {initial.Text.ToUpperInvariant()}: nothing to compare with");
            }
        }

        var percent = contradicted > 0 ? options.ContradictingInitialsPercent
            : matched >= 2 ? options.TwoInitialsPercent
            : matched == 1 ? options.OneInitialPercent
            : options.UncheckedInitialsPercent;
        var caps = new List<(int, string)>();
        if (weakAlias)
        {
            caps.Add((options.WeakAliasCap, "The list marks this alias as low quality"));
        }

        if (pairs.Any(pair => pair.Alignment.Capped))
        {
            caps.Add((options.CappedMethodCap, "A word aligned only approximately"));
        }

        return Finish(percent, caps, explanation);
    }

    /// <summary>An initial matches a given name whose fold key starts with the initial's (<c>Г</c>: <c>Grigoriy</c>
    /// and <c>Hryhorii</c>), directly or through a cognate (<c>М</c>: <c>Nikolay</c>, via <c>Mykola</c>), or the same
    /// initial on the other side.</summary>
    private bool InitialMatches(Token initial, Token candidate)
    {
        if (candidate.Length == 1)
        {
            return initial.Folds.Intersect(candidate.Folds).Any();
        }

        var candidateFolds = candidate.Folds.Concat(comparer.CognateGroups(candidate).SelectMany(comparer.CognateFoldsOf));
        return candidateFolds.Any(fold => initial.Folds.Any(initialFold => initialFold.Length > 0 && fold.StartsWith(initialFold, StringComparison.Ordinal)));
    }

    private static NameScore Finish(int percent, List<(int Cap, string Reason)> caps, List<string> explanation)
    {
        foreach (var (cap, reason) in caps.Where(cap => cap.Cap < percent).OrderBy(cap => cap.Cap))
        {
            explanation.Add(string.Create(CultureInfo.InvariantCulture, $"Capped at {cap}%: {reason}"));
        }

        var ceiling = caps.Count == 0 ? int.MaxValue : caps.Min(cap => cap.Cap);
        return new NameScore(Math.Min(percent, ceiling), explanation, ceiling);
    }

    /// <summary>The best one-to-one pairs: the assignment with the highest total weighted similarity, and among equal
    /// totals the fewest capped alignments. Exact (a bitmask search over the listed tokens), so the order of the
    /// words never changes the result; names longer than <see cref="MaxExactTokens"/> fall back to best pairs first.</summary>
    private List<(int Query, int Listed, Alignment Alignment)> Align(Token[] query, Token[] listed, Func<Token, double> weight)
    {
        var alignments = new Alignment[query.Length, listed.Length];
        var values = new double[query.Length, listed.Length];
        for (var i = 0; i < query.Length; i++)
        {
            for (var j = 0; j < listed.Length; j++)
            {
                alignments[i, j] = comparer.Compare(query[i], listed[j]);
                // A capped alignment is worth a hair less, so equal totals prefer uncapped pairs.
                values[i, j] = alignments[i, j].Aligned
                    ? (alignments[i, j].Similarity * (weight(query[i]) + weight(listed[j]))) - (alignments[i, j].Capped ? 1e-9 : 0)
                    : double.NegativeInfinity;
            }
        }

        var assignment = listed.Length <= MaxExactTokens && query.Length <= MaxExactTokens
            ? ExactAssignment(values, query.Length, listed.Length)
            : GreedyAssignment(values, query.Length, listed.Length);
        return [.. assignment.Select(pair => (pair.Query, pair.Listed, alignments[pair.Query, pair.Listed]))];
    }

    private const int MaxExactTokens = 12;

    private static List<(int Query, int Listed)> ExactAssignment(double[,] values, int queryCount, int listedCount)
    {
        // best[i, mask]: the best total for query tokens i.. with the listed tokens in mask already used.
        var masks = 1 << listedCount;
        var best = new double[queryCount + 1, masks];
        for (var i = queryCount - 1; i >= 0; i--)
        {
            for (var mask = 0; mask < masks; mask++)
            {
                var value = best[i + 1, mask];
                for (var j = 0; j < listedCount; j++)
                {
                    if ((mask & (1 << j)) == 0 && !double.IsNegativeInfinity(values[i, j]))
                    {
                        value = Math.Max(value, values[i, j] + best[i + 1, mask | (1 << j)]);
                    }
                }

                best[i, mask] = value;
            }
        }

        var pairs = new List<(int, int)>();
        for (int i = 0, used = 0; i < queryCount; i++)
        {
            for (var j = 0; j < listedCount; j++)
            {
                if ((used & (1 << j)) == 0 && !double.IsNegativeInfinity(values[i, j])
                    && Math.Abs(values[i, j] + best[i + 1, used | (1 << j)] - best[i, used]) < 1e-12)
                {
                    pairs.Add((i, j));
                    used |= 1 << j;
                    break;
                }
            }
        }

        return pairs;
    }

    private static List<(int Query, int Listed)> GreedyAssignment(double[,] values, int queryCount, int listedCount)
    {
        var candidates = new List<(int Query, int Listed, double Value)>();
        for (var i = 0; i < queryCount; i++)
        {
            for (var j = 0; j < listedCount; j++)
            {
                if (!double.IsNegativeInfinity(values[i, j]))
                {
                    candidates.Add((i, j, values[i, j]));
                }
            }
        }

        var usedQuery = new HashSet<int>();
        var usedListed = new HashSet<int>();
        var pairs = new List<(int, int)>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Value))
        {
            if (!usedQuery.Contains(candidate.Query) && !usedListed.Contains(candidate.Listed))
            {
                usedQuery.Add(candidate.Query);
                usedListed.Add(candidate.Listed);
                pairs.Add((candidate.Query, candidate.Listed));
            }
        }

        return pairs;
    }

    private static string Describe(Token query, Token listed, Alignment alignment) => alignment.Method switch
    {
        AlignMethod.Identical => $"{query} = {listed}",
        AlignMethod.Transliteration => $"{query} read as {Common(query, listed)} = {listed}",
        AlignMethod.SpellingPair => $"{query} ~ {listed} (spelling variant)",
        AlignMethod.FoldKey => $"{query} ~ {listed} (romanisation)",
        AlignMethod.FoldKeyLatin => $"{query} ~ {listed} (romanisation, both Latin)",
        AlignMethod.Cognate => $"{query} ~ {listed} (Russian and Ukrainian forms of one name)",
        AlignMethod.OneEdit or AlignMethod.OneEditShort => $"{query} ~ {listed} (one letter differs)",
        AlignMethod.TwoEdits => $"{query} ~ {listed} (two letters differ)",
        _ => $"{query} ≠ {listed}",
    };

    private static string Common(Token query, Token listed) =>
        query.Forms.FirstOrDefault(form => listed.Forms.Contains(form)) ?? query.Forms.FirstOrDefault() ?? query.Text;
}
