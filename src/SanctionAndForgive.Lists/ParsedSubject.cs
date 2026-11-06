namespace SanctionAndForgive.Lists;

/// <summary>What a list says a subject is. <see cref="Unknown"/> when the source doesn't say (US export lists):
/// matching then reads the name both as a person's and as a company's.</summary>
public enum SubjectType
{
    Individual,
    Entity,
    Unknown,
}

public enum NameScript
{
    Latin,
    Cyrillic,
    Other,
}

/// <summary>One name of a subject, as listed. <paramref name="IsWeak"/>: the source marks it low quality (OFAC
/// <c>isLowQuality</c>, EU <c>strong="false"</c>, UN and SECO "low").</summary>
public sealed record ParsedName(string FullName, bool IsWeak, NameScript Script);

/// <summary>An address flattened to one free-text string, plus the ISO 3166-1 alpha-2 country code when the source
/// gives one. Country names in <paramref name="Raw"/> are resolved at index build.</summary>
public sealed record ParsedAddress(string Raw, string? CountryCode);

/// <summary>A listed party, in the shape every source is reduced to. Vessels and aircraft are left out: the MVP
/// doesn't screen them.</summary>
public sealed record ParsedSubject(
    string ExternalId,
    SubjectType Type,
    string PrimaryName,
    IReadOnlyList<ParsedName> Names,
    IReadOnlyList<ParsedAddress> Addresses,
    string? Programmes,
    DateOnly? ListedOn,
    string? Remarks,
    string? SourceUrl);
