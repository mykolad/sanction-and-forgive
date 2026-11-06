namespace SanctionAndForgive.Matching;

/// <summary>Every number the matcher uses: starting values from the spec, to be calibrated (Phase 6). Part of
/// <see cref="MatcherVersion"/>, so a change shows in every response.</summary>
public sealed record MatchingOptions
{
    public static MatchingOptions Default { get; } = new();

    /// <summary>Scores from here up are Match (90-99%).</summary>
    public double MatchScore { get; init; } = 0.95;

    /// <summary>Scores from here up are Review (50-89%); below, NoMatch (0-49%).</summary>
    public double ReviewScore { get; init; } = 0.85;

    public int MatchMinPercent { get; init; } = 90;

    public int MatchMaxPercent { get; init; } = 99;

    public int ReviewMinPercent { get; init; } = 50;

    public int ReviewMaxPercent { get; init; } = 89;

    public int NoMatchMaxPercent { get; init; } = 49;

    /// <summary>Not every core token aligned.</summary>
    public int NotAllAlignedCap { get; init; } = 89;

    /// <summary>One short core token (under 4 letters) and the legal class differs or is missing: "AB", "AB Corp".</summary>
    public int ThinCoreCap { get; init; } = 60;

    /// <summary>The generic words differ ("AB Trading Inc" against "AB Incorporated").</summary>
    public int GenericCap { get; init; } = 75;

    /// <summary>The source marks the alias as low quality.</summary>
    public int WeakAliasCap { get; init; } = 75;

    /// <summary>A token aligned only by a capped method: fold key with both sides Latin, cognate, short edits.</summary>
    public int CappedMethodCap { get; init; } = 75;

    /// <summary>The addresses resolve to different countries.</summary>
    public int ConflictingAddressCap { get; init; } = 80;

    /// <summary>All core tokens of the shorter name (2 or more) align, and the longer name has more (a patronymic).</summary>
    public int ContainedNamePercent { get; init; } = 70;

    public int TwoInitialsPercent { get; init; } = 70;

    public int OneInitialPercent { get; init; } = 60;

    /// <summary>The surname aligned, but no initial had a given name to compare with.</summary>
    public int UncheckedInitialsPercent { get; init; } = 50;

    /// <summary>The surname aligned, but an initial contradicts a given name.</summary>
    public int ContradictingInitialsPercent { get; init; } = 30;

    /// <summary>Added for a corroborating address, staying inside the band.</summary>
    public int CorroboratedBonus { get; init; } = 5;

    /// <summary>An address alone that aligns with a listed address (3 tokens, a number and a word).</summary>
    public int AddressOnlyPercent { get; init; } = 50;

    /// <summary>The lowest token weight: common name parts count less, but never nothing.</summary>
    public double MinTokenWeight { get; init; } = 1.0;

    /// <summary>Most aliases scored per entry and list, chosen by how many tokens they share with the entry.</summary>
    public int CandidateLimit { get; init; } = 2000;

    /// <summary>Subjects kept per cell: the best plus up to this many minus one at Review or above.</summary>
    public int HitsPerCell { get; init; } = 5;

    /// <summary>A best candidate below this is not shown.</summary>
    public int BestCandidateMinPercent { get; init; } = 20;
}
