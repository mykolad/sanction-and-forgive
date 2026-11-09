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

public enum AllowListAddOutcome
{
    /// <summary>Added (<see cref="AllowListAddResult.Id"/>).</summary>
    Added,

    /// <summary>Not added: a list can't be checked right now (loading, or its load failed), so the name couldn't be
    /// checked against it. Try again later.</summary>
    Unavailable,

    /// <summary>Not added: the name is already an entry in force (<see cref="AllowListAddResult.ExistingId"/>), the
    /// same words as screening reads them. A second would stay in force, unnoticed, once the first is removed.</summary>
    Exists,

    /// <summary>Not added, ever: the name is a Match on a list (<see cref="AllowListAddResult.ListedAs"/>), so it
    /// would allow-list a listed party.</summary>
    Listed,

    /// <summary>Not added: the name is a possible match for more listed parties than can be shown and confirmed
    /// (<see cref="AllowListAdmin.MaxPossibleMatches"/>): it's too generic to allow-list.</summary>
    TooBroad,

    /// <summary>Not added yet: the name is a possible match for listed parties that weren't all confirmed
    /// (<see cref="AllowListAddResult.PossibleMatches"/>). Show them, and add again confirming them.</summary>
    Confirm,
}

/// <param name="PossibleMatches">The listed parties this name is a possible match for, all of them, which the entry
/// shows as allowed (with <see cref="AllowListAddOutcome.Confirm"/>, the ones to confirm; with
/// <see cref="AllowListAddOutcome.Added"/>, the ones confirmed).</param>
/// <param name="PossibleMatchCount">How many there are (with <see cref="AllowListAddOutcome.TooBroad"/>, more than are listed).</param>
/// <param name="Reloaded">Added and in use at once; false: added, and in use from the next refresh.</param>
public sealed record AllowListAddResult(
    AllowListAddOutcome Outcome,
    int? Id,
    int? ExistingId,
    string? ListedAs,
    IReadOnlyList<AllowListPossibleMatch> PossibleMatches,
    int PossibleMatchCount,
    bool Reloaded)
{
    public static AllowListAddResult Refused(AllowListAddOutcome outcome) => new(outcome, null, null, null, [], 0, Reloaded: false);
}

/// <summary>A listed party that a name to allow-list is a possible match for (owner's decision, 2026-11-06: shown to the
/// administrator, who decides whether to add the name anyway).</summary>
/// <param name="Key">What the administrator confirms: <c>{list code}/{external id}</c>.</param>
/// <param name="Alias">The listed name or alias that matched.</param>
public sealed record AllowListPossibleMatch(string Key, string List, string ListName, string ListedName, string Alias, int Percent, string? Programme);

/// <param name="Reloaded">Whether the allow-list was reloaded. Either way the entry stopped applying at once.</param>
public sealed record AllowListRemoveResult(bool Removed, bool Reloaded);

public sealed class AllowListAdmin(IServiceScopeFactory scopes, ListLoader loader, ListCatalog catalog, Screener screener, TimeProvider clock) : IAllowListAdmin
{
    /// <summary>The most possible matches a name can have and still be allow-listed: each one is shown and confirmed,
    /// and a name that resembles more listed parties than this is too generic for an entry.</summary>
    public const int MaxPossibleMatches = 100;

    public async Task<IReadOnlyList<AllowListRecord>> HistoryAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).HistoryAsync(cancellationToken);
    }

    public async Task<AllowListAddResult> AddAsync(string name, string reason, string addedBy, IReadOnlySet<string> confirmed, CancellationToken cancellationToken)
    {
        // Checked and stored under the refresh gate, so the lists it was checked against are the ones in use when it's
        // stored: a refresh can't publish a new possible match in between. A later list update can, as for any entry.
        var (result, reloaded) = await loader.AddToAllowListAsync(
            async () =>
            {
                var checkedResult = await CheckAndStoreAsync(name, reason, addedBy, confirmed, cancellationToken);
                return (checkedResult, checkedResult.Outcome == AllowListAddOutcome.Added);
            },
            cancellationToken);
        return result with { Reloaded = reloaded };
    }

    private async Task<AllowListAddResult> CheckAndStoreAsync(string name, string reason, string addedBy, IReadOnlySet<string> confirmed, CancellationToken cancellationToken)
    {
        // A name that is a Match on a list, as a person or as an entity, would allow-list the listed party itself. It
        // can only be checked against lists that can be searched: until every list can, nothing is added.
        var lists = catalog.Lists.Select(list => new ScreenedList(list.Code, list.Name, list.LastCheckedAt, list.Index)).ToList();
        if (!catalog.IsReady || lists.Count == 0 || lists.Any(list => list.Index is null))
        {
            return AllowListAddResult.Refused(AllowListAddOutcome.Unavailable);
        }

        // The quick answer from memory; the store checks again under its lock, against what's stored.
        if (catalog.AllowList.Find(name) is { } inMemory)
        {
            return AllowListAddResult.Refused(AllowListAddOutcome.Exists) with { ExistingId = inMemory.Id };
        }

        // Every listed party the entry would allow, not only the closest few a screening cell shows: an entry allows a
        // whole cell, so each of them must have been seen.
        var hits = screener.NameHits(name, lists);
        if (hits.Where(hit => hit.Match.Outcome == Outcome.Match).MaxBy(hit => hit.Match.Percent) is { } match)
        {
            return AllowListAddResult.Refused(AllowListAddOutcome.Listed) with { ListedAs = $"{ListName(match.List)}: {match.Match.Subject.PrimaryName}" };
        }

        var possible = hits
            .Select(hit => new AllowListPossibleMatch(
                $"{hit.List}/{hit.Match.Subject.ExternalId}", hit.List, ListName(hit.List), hit.Match.Subject.PrimaryName, hit.Match.Alias, hit.Match.Percent, hit.Match.Subject.Programme))
            .DistinctBy(hit => hit.Key, StringComparer.Ordinal)
            .OrderByDescending(hit => hit.Percent)
            .ThenBy(hit => hit.List, StringComparer.Ordinal)
            .ThenBy(hit => hit.ListedName, StringComparer.Ordinal)
            .ToList();
        if (possible.Count > MaxPossibleMatches)
        {
            return AllowListAddResult.Refused(AllowListAddOutcome.TooBroad) with { PossibleMatches = [.. possible.Take(MaxPossibleMatches)], PossibleMatchCount = possible.Count };
        }

        // Possible matches are the administrator's call, made knowingly: every one the name has now must have been
        // shown and confirmed. A list that changed since then can bring a new one, which is shown in turn.
        if (possible.Any(hit => !confirmed.Contains(hit.Key)))
        {
            return AllowListAddResult.Refused(AllowListAddOutcome.Confirm) with { PossibleMatches = possible, PossibleMatchCount = possible.Count };
        }

        AllowListAddition addition;
        await using (var scope = scopes.CreateAsyncScope())
        {
            addition = await new AllowListStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock).AddAsync(
                name, reason, addedBy, inForce => AllowList.Build(inForce.Select(entry => new AllowListEntry(entry.Id, entry.Name, entry.Reason)), "stored").Find(name)?.Id, cancellationToken);
        }

        // Stored: the caller reloads the allow-list, whatever the request's cancellation (a failed reload only delays it).
        return addition.Id is { } id
            ? new AllowListAddResult(AllowListAddOutcome.Added, id, null, null, possible, possible.Count, Reloaded: false)
            : AllowListAddResult.Refused(AllowListAddOutcome.Exists) with { ExistingId = addition.ExistingId };
    }

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
