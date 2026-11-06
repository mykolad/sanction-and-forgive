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

    /// <param name="confirmed">The possible matches the administrator has seen and accepts (<see cref="AllowListPossibleMatch.Key"/>):
    /// the name is added only if every possible match it has now is among them.</param>
    Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, IReadOnlySet<string> confirmed, CancellationToken cancellationToken);

    Task<AllowListRemoveResult> RemoveAsync(int id, string removedBy, CancellationToken cancellationToken);
}

/// <param name="Id">The new entry's id, when it was added.</param>
/// <param name="ListedAs">Why it wasn't: the name is a Match on a list as it stands ("EU: Romashka LLC"), so it would
/// allow-list a listed party.</param>
/// <param name="Unavailable">Why it wasn't: a list can't be checked right now (loading, stale, or not built), so the
/// name couldn't be checked against it. Try again later.</param>
/// <param name="PossibleMatches">The listed parties this name is a possible match for, which the entry shows as allowed.
/// When it wasn't added, these weren't all confirmed: show them, and add again confirming them.</param>
/// <param name="Reloaded">Added and in use at once; false: added, and in use from the next refresh.</param>
/// <param name="ExistingId">Why it wasn't: the name is already an entry in force (the same words as screening reads them).</param>
public sealed record AllowListAddResult(int? Id, string? ListedAs, bool Unavailable, IReadOnlyList<AllowListPossibleMatch> PossibleMatches, bool Reloaded, int? ExistingId);

/// <summary>A listed party that a name to allow-list is a possible match for (owner's decision, 2026-11-06: shown to the
/// administrator, who decides whether to add the name anyway).</summary>
/// <param name="Key">What the administrator confirms: <c>{list code}/{external id}</c>.</param>
/// <param name="ReadAs">How the name is a possible match: read as a <c>Receiver</c> (a person), a <c>Company</c>, or both (an
/// entry applies to both).</param>
public sealed record AllowListPossibleMatch(string Key, string List, string ListName, string ListedName, string Alias, int Percent, IReadOnlyList<EntryKind> ReadAs, string? Programme);

/// <param name="Reloaded">Whether the allow-list was reloaded. Either way the entry stopped applying at once.</param>
public sealed record AllowListRemoveResult(bool Removed, bool Reloaded);

public sealed class AllowListAdmin(IServiceScopeFactory scopes, ListLoader loader, ListCatalog catalog, Screener screener, TimeProvider clock) : IAllowListAdmin
{
    public async Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).HistoryAsync(cancellationToken);
    }

    public async Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, IReadOnlySet<string> confirmed, CancellationToken cancellationToken)
    {
        // A name that is a Match on a list, as a person or as an entity, would allow-list the listed party itself. It
        // can only be checked against lists that can be searched: until every list can, nothing is added.
        var now = clock.GetUtcNow();
        var lists = catalog.Lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.IsCurrent(now) ? list.Index : null)).ToList();
        if (!catalog.IsReady || lists.Count == 0 || lists.Any(list => list.Index is null))
        {
            return new AllowListAddResult(null, null, Unavailable: true, PossibleMatches: [], Reloaded: false, ExistingId: null);
        }

        // One entry per name: a second would stay in force, unnoticed, once the first is removed.
        if (catalog.AllowList.Find(name) is { } existing)
        {
            return new AllowListAddResult(null, null, Unavailable: false, PossibleMatches: [], Reloaded: false, existing.Id);
        }

        var screening = screener.Screen(new ScreeningRequest(name, name, null), lists);
        if (screening.Entries.SelectMany(entry => entry.Cells).FirstOrDefault(cell => cell.Outcome == Outcome.Match) is { } match)
        {
            return new AllowListAddResult(null, $"{ListName(match.List)}: {match.Best!.Subject.PrimaryName}", Unavailable: false, PossibleMatches: [], Reloaded: false, ExistingId: null);
        }

        // Possible matches are the administrator's call, made knowingly: every one the name has now must have been
        // shown and confirmed. A list that changed since then can bring a new one, which is shown in turn.
        var possible = PossibleMatches(screening);
        if (possible.Any(hit => !confirmed.Contains(hit.Key)))
        {
            return new AllowListAddResult(null, null, Unavailable: false, possible, Reloaded: false, ExistingId: null);
        }

        int id;
        await using (var scope = scopes.CreateAsyncScope())
        {
            id = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).AddAsync(name, reason, addedBy, cancellationToken);
        }

        // Committed: from here the request's cancellation no longer matters. A failed reload only delays the entry.
        var reloaded = await loader.ReloadAllowListAsync(withoutId: null, CancellationToken.None);
        return new AllowListAddResult(id, null, Unavailable: false, possible, reloaded, ExistingId: null);
    }

    // Every listed party at Review in a cell the entry would allow (the best and the others the cell keeps), once each,
    // at its highest percentage, the closest first.
    private List<AllowListPossibleMatch> PossibleMatches(ScreeningResult screening) =>
        [.. screening.Entries
            .SelectMany(entry => entry.Cells
                .Where(cell => cell.Outcome == Outcome.Review)
                .SelectMany(cell => cell.Others.Prepend(cell.Best!)
                    .Where(hit => hit.Outcome == Outcome.Review)
                    .Select(hit => new AllowListPossibleMatch(
                        $"{cell.List}/{hit.Subject.ExternalId}", cell.List, ListName(cell.List), hit.Subject.PrimaryName, hit.Alias, hit.Percent, [entry.Kind], hit.Subject.Programme))))
            .GroupBy(hit => hit.Key, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(hit => hit.Percent).First() with { ReadAs = [.. group.SelectMany(hit => hit.ReadAs).Distinct().Order()] })
            .OrderByDescending(hit => hit.Percent)
            .ThenBy(hit => hit.List, StringComparer.Ordinal)
            .ThenBy(hit => hit.ListedName, StringComparer.Ordinal)];

    private string ListName(string code) => catalog.Lists.First(list => list.Code == code).Name;

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
