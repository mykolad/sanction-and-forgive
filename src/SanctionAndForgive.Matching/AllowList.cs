using SanctionAndForgive.Matching.Text;

namespace SanctionAndForgive.Matching;

/// <summary>An allow-list entry: a name known to be a legitimate party, not the listed one it resembles, and why.</summary>
/// <param name="Id">Its id in the database: what the audit line names.</param>
public sealed record AllowListEntry(int Id, string Name, string Reason);

/// <summary>Names whose possible matches (Review) are shown as allowed and don't raise the outcome (owner's decision,
/// 2026-11-06). Only possible matches: a Match is never suppressed, and only when the entered receiver or company is
/// the allow-listed name itself, word for word as screening reads it: case, punctuation, apostrophes and spacing don't
/// matter, but every word does, the legal form included ("Huamei Co., Ltd." is "HUAMEI CO LTD", not "Huamei Limited");
/// Cyrillic and Latin spellings are separate entries. Immutable, with a version
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

    /// <summary>This list without entry <paramref name="id"/>: a removal applies at once, before the stored list is
    /// read again. Its version says so ("12-5": version 12 without entry 5).</summary>
    public AllowList Without(int id) =>
        new(byKey.Where(pair => pair.Value.Id != id).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), $"{Version}-{id}");

    /// <summary>The entry for exactly this name, if there is one.</summary>
    public AllowListEntry? Find(string name) =>
        byKey.Count == 0 ? null : byKey.GetValueOrDefault(Key(new Tokenizer(MatcherData.Default, cache: null), name));

    internal AllowListEntry? Find(IReadOnlyList<RawToken> tokens) =>
        byKey.Count == 0 ? null : byKey.GetValueOrDefault(Key(tokens));

    private static string Key(Tokenizer tokenizer, string name) => Key(tokenizer.Tokenize(name, joinInitials: true));

    // Everything the matcher reads from the tokens: their text, and whether a token is a run of single letters, whose
    // letters can align as initials ("A. B. Corp" can match "Anna Brown", "AB Corp" can't). Two names with one key
    // screen alike, so the possible matches confirmed for one are the other's too.
    private static string Key(IReadOnlyList<RawToken> tokens) =>
        string.Join(' ', tokens.Select(token => token.Initials is { } letters ? string.Join('.', letters.Select(letter => letter.Text)) + "." : token.Token.Text));
}
