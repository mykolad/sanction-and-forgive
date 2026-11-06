namespace SanctionAndForgive.Matching;

/// <summary>What a list says a subject is. <see cref="Unknown"/> subjects are matched both as a person and as an
/// entity, and the better reading wins.</summary>
public enum SubjectKind
{
    Individual,
    Entity,
    Unknown,
}

/// <summary>A cell's outcome, from its percentage (<see cref="MatchingOptions"/> bands).</summary>
public enum Outcome
{
    NoMatch,
    Review,
    Match,
}

/// <summary>What comparing the entered address with the listed ones showed.</summary>
public enum AddressState
{
    /// <summary>Either side has no address, or nothing in them could be compared. Changes nothing.</summary>
    NotCompared,

    /// <summary>A word or place aligned, or both resolve to the same country. Adds a little, within the band.</summary>
    Corroborated,

    /// <summary>Both resolve to a country, and they differ. Caps the percentage, but never below Review.</summary>
    Conflicting,
}

public sealed record ListedName(string FullName, bool IsWeak);

/// <summary>An address as one free-text string, with the ISO 3166-1 alpha-2 code when the source gives one.</summary>
public sealed record ListedAddress(string Raw, string? CountryCode);

/// <summary>A listed party as the matcher sees it. <see cref="Names"/> includes the primary name.</summary>
public sealed record ListedSubject(
    string ExternalId,
    SubjectKind Kind,
    string PrimaryName,
    IReadOnlyList<ListedName> Names,
    IReadOnlyList<ListedAddress> Addresses,
    string? Programme,
    string? SourceUrl);

/// <summary>One listed subject's match for one entry: the best of its names.</summary>
/// <param name="Alias">The listed name or alias that matched best.</param>
/// <param name="Alignment">How the tokens aligned, and what wasn't compared, in plain words (quoting the entry).</param>
/// <param name="Words">The same alignment without the entered text, for responses that never echo input: each
/// entered word by position, the listed word it aligned with, and how.</param>
/// <param name="Notes">Why the percentage is what it is beyond the words (a cap, or how many address parts aligned),
/// in sentences that never quote the entry.</param>
public sealed record SubjectMatch(
    ListedSubject Subject,
    int Percent,
    Outcome Outcome,
    string Alias,
    IReadOnlyList<string> Alignment,
    AddressState AddressState,
    IReadOnlyList<WordAlignment> Words,
    IReadOnlyList<string> Notes);

/// <summary>One word of a name alignment, without the entered text. <paramref name="Entered"/>: the entered word's
/// 1-based position among the words compared (legal forms, generic words and honorifics aside), or null for an
/// initial. <paramref name="Listed"/>: the listed word (normalised), or null. <paramref name="How"/>: <c>Same</c>,
/// <c>Transliterated</c>, <c>SpellingVariant</c>, <c>Romanisation</c>, <c>Cognate</c>, <c>OneLetterDiffers</c>,
/// <c>TwoLettersDiffer</c>, <c>Initial</c>, <c>InitialContradicts</c>, <c>InitialUnchecked</c>, <c>NotListed</c>
/// (entered, no listed partner) or <c>NotEntered</c> (listed, no entered partner); for an address-only hit, the
/// address words by the same methods, plus <c>SamePlace</c> and <c>SameNumber</c> (positions among the entered
/// address's words; null for places and numbers).</summary>
public sealed record WordAlignment(int? Entered, string? Listed, string How);
