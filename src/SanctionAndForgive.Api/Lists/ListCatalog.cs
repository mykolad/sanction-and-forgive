using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Lists;

/// <summary>One list as the API holds it in memory: what the database said at the last refresh, and the index built
/// from its active import (null if that failed or there is none). <paramref name="UpdatedAt"/>: when the active
/// import completed, i.e. when the list's content last changed; <paramref name="LastCheckedAt"/>: the last successful
/// check, changed or not, which is what freshness counts from.</summary>
public sealed record LoadedList(
    string Code,
    string Name,
    string? Url,
    int? MaxAgeHours,
    int? ActiveImportId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? UpdatedAt,
    int RecordCount,
    ListIndex? Index)
{
    /// <summary>Searchable now: an index, and (for a feed) a last successful check within its allowed age.
    /// Otherwise its cells say Not checked. Fail closed.</summary>
    public bool IsCurrent(DateTimeOffset now) =>
        Index is not null && (MaxAgeHours is null || (LastCheckedAt is { } checkedAt && now - checkedAt <= TimeSpan.FromHours(MaxAgeHours.Value)));
}

/// <summary>Every list's current index, swapped whole when the refresh changes anything: a request reads one
/// consistent snapshot and never touches the database. Empty and not ready until the first load completes.</summary>
public sealed class ListCatalog
{
    private volatile Snapshot snapshot = new([], Ready: false);

    private volatile IReadOnlyDictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>> history =
        new Dictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>>();

    public bool IsReady => snapshot.Ready;

    public IReadOnlyList<LoadedList> Lists => snapshot.Lists;

    public void Replace(IReadOnlyList<LoadedList> lists) => snapshot = new Snapshot(lists, Ready: true);

    /// <summary>Each source's latest imports, newest first, as of the last refresh: for the status page, so it never
    /// reads the database either.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>> History => history;

    public void ReplaceHistory(IReadOnlyDictionary<string, IReadOnlyList<SanctionAndForgive.Data.ImportHistoryEntry>> latest) => history = latest;

    private volatile IReadOnlyDictionary<string, SanctionAndForgive.Data.ImportHistoryEntry> held =
        new Dictionary<string, SanctionAndForgive.Data.ImportHistoryEntry>();

    /// <summary>Each source's update held for review (the newest, if any), read on its own: it shows however many
    /// checks came after it, not only while it's within <see cref="History"/>.</summary>
    public IReadOnlyDictionary<string, SanctionAndForgive.Data.ImportHistoryEntry> Held => held;

    public void ReplaceHeld(IReadOnlyDictionary<string, SanctionAndForgive.Data.ImportHistoryEntry> latest) => held = latest;

    private sealed record Snapshot(IReadOnlyList<LoadedList> Lists, bool Ready);
}
