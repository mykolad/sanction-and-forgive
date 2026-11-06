namespace SanctionAndForgive.Lists.Parsers;

/// <summary>Collects one subject's names and addresses, dropping empty and repeated ones. The first name added is
/// the primary name unless one is added as primary.</summary>
internal sealed class SubjectBuilder(string externalId, SubjectType type)
{
    private readonly List<ParsedName> names = [];
    private readonly Dictionary<string, int> nameIndexes = new(StringComparer.Ordinal);
    private readonly List<ParsedAddress> addresses = [];
    private readonly HashSet<string> seenAddresses = new(StringComparer.Ordinal);
    private string? primaryName;
    private bool primaryIsFlagged;

    public string? Programmes { get; set; }

    public DateOnly? ListedOn { get; set; }

    public string? Remarks { get; set; }

    public string? SourceUrl { get; set; }

    public bool HasNames => names.Count > 0;

    public void AddName(string? fullName, bool isWeak, bool isPrimary)
    {
        var cleaned = Text.Clean(fullName);
        if (cleaned is null)
        {
            return;
        }

        if (isPrimary && !primaryIsFlagged)
        {
            primaryName = cleaned;
            primaryIsFlagged = true;
        }
        else
        {
            primaryName ??= cleaned;
        }

        // A spelling listed more than once is weak only if every listing of it is weak.
        if (nameIndexes.TryGetValue(cleaned, out var index))
        {
            if (names[index].IsWeak && !isWeak)
            {
                names[index] = names[index] with { IsWeak = false };
            }

            return;
        }

        nameIndexes[cleaned] = names.Count;
        names.Add(new ParsedName(cleaned, isWeak, Text.DetectScript(cleaned)));
    }

    public void AddAddress(string? raw, string? countryCode)
    {
        var cleaned = Text.Clean(raw);
        var code = Text.Clean(countryCode)?.ToUpperInvariant();
        if (cleaned is null && code is null)
        {
            return;
        }

        cleaned ??= code!;
        if (seenAddresses.Add(cleaned + "|" + code))
        {
            addresses.Add(new ParsedAddress(cleaned, code is { Length: 2 } && code.All(char.IsAsciiLetterUpper) ? code : null));
        }
    }

    /// <summary>The subject, or null if it has no usable name (nothing to match against).</summary>
    public ParsedSubject? Build() =>
        primaryName is null
            ? null
            : new ParsedSubject(externalId, type, primaryName, names, addresses, Programmes, ListedOn, Remarks, SourceUrl);
}
