using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Editing the allow-list: every change is kept (an entry is removed, never deleted), and takes effect for
/// the next screening (the catalog's allow-list is reloaded at once). A committed edit is reported as committed,
/// whatever happens to the reload after it.</summary>
public interface IAllowListAdmin
{
    Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken);

    Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, CancellationToken cancellationToken);

    Task<AllowListRemoveResult> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken);
}

/// <param name="Id">The new entry's id, when it was added.</param>
/// <param name="ListedAs">Why it wasn't: the name is a Match on a list as it stands ("EU: Romashka LLC"), so it would
/// allow-list a listed party.</param>
/// <param name="Unavailable">Why it wasn't: a list can't be checked right now (loading, stale, or not built), so the
/// name couldn't be checked against it. Try again later.</param>
/// <param name="Reloaded">Added and in use at once; false: added, and in use from the next refresh.</param>
public sealed record AllowListAddResult(int? Id, string? ListedAs, bool Unavailable, bool Reloaded);

/// <param name="Reloaded">Whether the allow-list was reloaded. Either way the entry stopped applying at once.</param>
public sealed record AllowListRemoveResult(bool Removed, bool Reloaded);

public sealed class AllowListAdmin(IServiceScopeFactory scopes, ListLoader loader, ListCatalog catalog, Screener screener, TimeProvider clock) : IAllowListAdmin
{
    public async Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).HistoryAsync(cancellationToken);
    }

    public async Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, CancellationToken cancellationToken)
    {
        // A name that is a Match on a list, as a person or as an entity, would allow-list the listed party itself. It
        // can only be checked against lists that can be searched: until every list can, nothing is added.
        var now = clock.GetUtcNow();
        var lists = catalog.Lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? list.Index : null)).ToList();
        if (!catalog.IsReady || lists.Count == 0 || lists.Any(list => list.Index is null))
        {
            return new AllowListAddResult(null, null, Unavailable: true, Reloaded: false);
        }

        var screening = screener.Screen(new ScreeningRequest(name, name, null), lists);
        if (screening.Entries.SelectMany(entry => entry.Cells).FirstOrDefault(cell => cell.Outcome == Outcome.Match) is { } match)
        {
            return new AllowListAddResult(null, $"{catalog.Lists.First(list => list.Code == match.List).Name}: {match.Best!.Subject.PrimaryName}", Unavailable: false, Reloaded: false);
        }

        int id;
        await using (var scope = scopes.CreateAsyncScope())
        {
            id = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).AddAsync(name, reason, addedBy, cancellationToken);
        }

        // Committed: from here the request's cancellation no longer matters. A failed reload only delays the entry.
        var reloaded = await loader.ReloadAllowListAsync(withoutId: null, CancellationToken.None);
        return new AllowListAddResult(id, null, Unavailable: false, reloaded);
    }

    public async Task<AllowListRemoveResult> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken)
    {
        bool removed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            removed = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).RemoveAsync(id, removedBy, cancellationToken);
        }

        if (!removed)
        {
            return new AllowListRemoveResult(Removed: false, Reloaded: false);
        }

        // Committed: the entry stops applying in memory first, whatever the reload does.
        var reloaded = await loader.ReloadAllowListAsync(id, CancellationToken.None);
        return new AllowListRemoveResult(Removed: true, reloaded);
    }
}
