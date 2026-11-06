using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Matching;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Editing the allow-list: every change is kept (an entry is removed, never deleted), and takes effect for
/// the next screening (the catalog's allow-list is reloaded at once).</summary>
public interface IAllowListAdmin
{
    Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken);

    Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken);
}

/// <param name="Id">The new entry's id, when it was added.</param>
/// <param name="ListedAs">Why it wasn't: the name is a Match on a list as it stands ("EU: Romashka LLC"), so it would
/// allow-list a listed party. Null when added.</param>
public sealed record AllowListAddResult(int? Id, string? ListedAs);

public sealed class AllowListAdmin(IServiceScopeFactory scopes, ListLoader loader, ListCatalog catalog, Screener screener, TimeProvider clock) : IAllowListAdmin
{
    public async Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).HistoryAsync(cancellationToken);
    }

    public async Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, CancellationToken cancellationToken)
    {
        // A name that is a Match on a list, as a person or as an entity, would allow-list the listed party itself.
        var now = clock.GetUtcNow();
        var lists = catalog.Lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? list.Index : null)).ToList();
        var screening = screener.Screen(new ScreeningRequest(name, name, null), lists);
        if (screening.Entries.SelectMany(entry => entry.Cells).FirstOrDefault(cell => cell.Outcome == Outcome.Match) is { } match)
        {
            return new AllowListAddResult(null, $"{catalog.Lists.First(list => list.Code == match.List).Name}: {match.Best!.Subject.PrimaryName}");
        }

        int id;
        await using (var scope = scopes.CreateAsyncScope())
        {
            id = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).AddAsync(name, reason, addedBy, cancellationToken);
        }

        await loader.RefreshAllowListAsync(cancellationToken);
        return new AllowListAddResult(id, null);
    }

    public async Task<bool> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken)
    {
        bool removed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            removed = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).RemoveAsync(id, removedBy, cancellationToken);
        }

        if (removed)
        {
            await loader.RefreshAllowListAsync(cancellationToken);
        }

        return removed;
    }
}
