using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Confirms or rejects an update held for review, then reloads the lists so the result shows at once.</summary>
public interface IImportReview
{
    Task<ReviewOutcome> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken);
}

/// <param name="Reloaded">Whether the lists were reloaded after the review. False: the review stands (it's
/// committed), and the next scheduled refresh, within 15 minutes, picks it up.</param>
public sealed record ReviewOutcome(ReviewResult Result, bool Reloaded);

/// <summary>Through <see cref="ImportStore.ReviewAsync"/> (the source's lock, a conditional flip), then one refresh.
/// The refresh is <see cref="ListLoader"/>'s own, which never runs twice at once. The review is committed before the
/// refresh starts, so a refresh that fails is reported as such, never as a failed review.</summary>
public sealed partial class ImportReview(IServiceScopeFactory scopes, ListLoader loader, TimeProvider clock, ILogger<ImportReview> logger) : IImportReview
{
    public async Task<ReviewOutcome> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken)
    {
        ReviewResult result;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var store = new ImportStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock);
            result = await store.ReviewAsync(importId, confirm, reviewer, cancellationToken);
        }

        if (result is not (ReviewResult.Confirmed or ReviewResult.Rejected))
        {
            return new ReviewOutcome(result, Reloaded: false);
        }

        try
        {
            await loader.RefreshAsync(cancellationToken);
            return new ReviewOutcome(result, Reloaded: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReloadFailed(logger, exception, importId);
            return new ReviewOutcome(result, Reloaded: false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Import {ImportId} was reviewed, but reloading the lists afterwards failed; the next refresh picks it up")]
    private static partial void ReloadFailed(ILogger logger, Exception exception, int importId);
}
