using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching;

/// <summary>An allow-list entry: a name known to be a legitimate party, not the listed one it resembles, and why.</summary>
/// <param name="Id">Its id in the database: what the audit line names.</param>
public sealed record AllowListEntry(int Id, string Name, string Reason);

/// <summary>Names whose possible matches (Review) are shown as allowed and don't raise the outcome (owner's decision,
/// 2026-11-06). Only possible matches: a Match is never suppressed, and only when the entered receiver or company is
/// the allow-listed name itself, compared the way screening reads names (case, punctuation, apostrophes and
/// legal-form phrases don't matter; Cyrillic and Latin spellings are separate entries). Immutable, with a version
/// that every result carries, so the same input, lists and allow-list always give the same answer.</summary>
public sealed class AllowList
{
    private readonly Dictionary<string, AllowListEntry> byKey;

    private AllowList(Dictionary<string, AllowListEntry> byKey, string version)
    {
        this.byKey = byKey;
        Version = version;
    }

    public static AllowList Empty { get; } = new([], "none");

    /// <param name="version">The allow-list's version as stored (it changes with every addition or removal).</param>
    public string Version { get; }

    public int Count => byKey.Count;

    /// <summary>Builds the list. Of two entries for the same name, the older (lower id) wins, so the result doesn't
    /// depend on their order.</summary>
    public static AllowList Build(IEnumerable<AllowListEntry> entries, string version)
    {
        var tokenizer = new Tokenizer(MatcherData.Default, cache: null);
        var byKey = new Dictionary<string, AllowListEntry>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderBy(entry => entry.Id))
        {
            var key = Key(tokenizer, entry.Name);
            if (key.Length > 0)
            {
                byKey.TryAdd(key, entry);
            }
        }

        return new AllowList(byKey, version);
    }

    /// <summary>The entry for exactly this name, if there is one.</summary>
    public AllowListEntry? Find(string name) =>
        byKey.Count == 0 ? null : byKey.GetValueOrDefault(Key(new Tokenizer(MatcherData.Default, cache: null), name));

    internal AllowListEntry? Find(IReadOnlyList<RawToken> tokens) =>
        byKey.Count == 0 ? null : byKey.GetValueOrDefault(Key(tokens));

    private static string Key(Tokenizer tokenizer, string name) => Key(tokenizer.Tokenize(name, joinInitials: true));

    private static string Key(IReadOnlyList<RawToken> tokens) => string.Join(' ', tokens.Select(token => token.Token.Text));
}
