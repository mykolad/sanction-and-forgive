namespace SanctionAndForgive.Matching;

/// <summary>Score → percentage → outcome (section 5.5). Linear within each band, so a percentage and its outcome
/// never contradict each other.</summary>
internal static class Bands
{
    public static int ToPercent(double score, MatchingOptions options)
    {
        if (score >= options.MatchScore)
        {
            return Linear(score, options.MatchScore, 1.0, options.MatchMinPercent, options.MatchMaxPercent);
        }

        return score >= options.ReviewScore
            ? Linear(score, options.ReviewScore, options.MatchScore, options.ReviewMinPercent, options.ReviewMaxPercent)
            : Linear(score, 0, options.ReviewScore, 0, options.NoMatchMaxPercent);
    }

    public static Outcome ToOutcome(int percent, MatchingOptions options) =>
        percent >= options.MatchMinPercent ? Outcome.Match
        : percent >= options.ReviewMinPercent ? Outcome.Review
        : Outcome.NoMatch;

    /// <summary>The highest percentage in <paramref name="percent"/>'s band.</summary>
    public static int BandMax(int percent, MatchingOptions options) => ToOutcome(percent, options) switch
    {
        Outcome.Match => options.MatchMaxPercent,
        Outcome.Review => options.ReviewMaxPercent,
        _ => options.NoMatchMaxPercent,
    };

    private static int Linear(double score, double low, double high, int lowPercent, int highPercent)
    {
        var fraction = Math.Clamp((score - low) / (high - low), 0, 1);
        return Math.Clamp((int)Math.Floor(lowPercent + (fraction * (highPercent - lowPercent))), lowPercent, highPercent);
    }
}
