using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Api.Admin;

/// <summary>Confirms or rejects an update held for review, then reloads the lists so the result shows at once.</summary>
public interface IImportReview
{
    Task<ReviewResult> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken);
}

/// <summary>Through <see cref="ImportStore.ReviewAsync"/> (the source's lock, a conditional flip), then one refresh.
/// The refresh is <see cref="ListLoader"/>'s own, which never runs twice at once.</summary>
public sealed class ImportReview(IServiceScopeFactory scopes, ListLoader loader, TimeProvider clock) : IImportReview
{
    public async Task<ReviewResult> ReviewAsync(int importId, bool confirm, string reviewer, CancellationToken cancellationToken)
    {
        ReviewResult result;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var store = new ImportStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), clock);
            result = await store.ReviewAsync(importId, confirm, reviewer, cancellationToken);
        }

        if (result is ReviewResult.Confirmed or ReviewResult.Rejected)
        {
            await loader.RefreshAsync(cancellationToken);
        }

        return result;
    }
}
