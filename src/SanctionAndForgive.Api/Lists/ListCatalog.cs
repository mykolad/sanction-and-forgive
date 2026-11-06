using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Lists;

/// <summary>One list as the API holds it in memory: what the database said at the last refresh, and the index built
/// from its active import (null if that failed or there is none).</summary>
public sealed record LoadedList(
    string Code,
    string Name,
    string? Url,
    int? MaxAgeHours,
    int? ActiveImportId,
    DateTimeOffset? LastCheckedAt,
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

    public bool IsReady => snapshot.Ready;

    public IReadOnlyList<LoadedList> Lists => snapshot.Lists;

    public void Replace(IReadOnlyList<LoadedList> lists) => snapshot = new Snapshot(lists, Ready: true);

    private sealed record Snapshot(IReadOnlyList<LoadedList> Lists, bool Ready);
}
