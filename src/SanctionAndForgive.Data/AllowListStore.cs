using Microsoft.EntityFrameworkCore;

namespace SanctionAndForgive.Data;

/// <summary>One allow-list entry as stored, with its history.</summary>
public sealed record AllowListRecord(int Id, string Name, string Reason, string AddedBy, DateTimeOffset AddedAt, string? RemovedBy, DateTimeOffset? RemovedAt);

/// <summary>The allow-list's entries in force, and its version: the number of additions plus removals, so it changes
/// with every edit and never repeats.</summary>
public sealed record AllowListSnapshot(IReadOnlyList<AllowListRecord> Entries, string Version);

/// <summary>Reads and edits the allow-list. Entries are never deleted: a removal is recorded, so the history stays.</summary>
public sealed class AllowListStore(ListsDbContext context, TimeProvider clock)
{
    public const int MaxName = 300;

    public const int MaxReason = 500;

    /// <summary>The entries in force (not removed), oldest first, and the version.</summary>
    public async Task<AllowListSnapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        var all = await HistoryAsync(cancellationToken);
        var version = (all.Count + all.Count(item => item.RemovedAt is not null)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new AllowListSnapshot([.. all.Where(item => item.RemovedAt is null).OrderBy(item => item.Id)], version);
    }

    /// <summary>Every entry ever added, newest first, removed ones included.</summary>
    public async Task<List<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken) =>
        await context.AllowList.AsNoTracking().OrderByDescending(item => item.Id)
            .Select(item => new AllowListRecord(item.Id, item.Name, item.Reason, item.AddedBy, item.AddedAt, item.RemovedBy, item.RemovedAt))
            .ToListAsync(cancellationToken);

    public async Task<int> AddAsync(string name, string reason, string addedBy, CancellationToken cancellationToken)
    {
        var item = new AllowListItem { Name = name.Trim(), Reason = reason.Trim(), AddedBy = addedBy, AddedAt = clock.GetUtcNow() };
        context.AllowList.Add(item);
        await context.SaveChangesAsync(cancellationToken);
        return item.Id;
    }

    /// <summary>Marks an entry removed. False if there's no such entry, or it was removed already.</summary>
    public async Task<bool> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken) =>
        await context.AllowList.Where(item => item.Id == id && item.RemovedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RemovedBy, removedBy).SetProperty(item => item.RemovedAt, clock.GetUtcNow()), cancellationToken) == 1;
}
