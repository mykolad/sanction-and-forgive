using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Tests.Data;

// The allow-list's storage against a real SQL Server: entries are removed, never deleted; the version moves with
// every change.
public sealed class AllowListStoreTests : IAsyncLifetime
{
    private readonly string connectionString;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public AllowListStoreTests()
    {
        var server = Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") ?? string.Empty;
        connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "allow_tests_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") is { Length: > 0 })
        {
            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") is { Length: > 0 })
        {
            await using var context = CreateContext();
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlServerFact]
    public async Task AddingAndRemoving_KeepsTheHistory_AndMovesTheVersion()
    {
        await using var context = CreateContext();
        var store = new AllowListStore(context, clock);
        var empty = await store.CurrentAsync(CancellationToken.None);

        var huamei = (await store.AddAsync("  Huamei Trading ", "A customer since 2019", "owner@example.invalid", NoneExisting, CancellationToken.None)).Id!.Value;
        var other = (await store.AddAsync("Northwind Freight", "Our own carrier", "owner@example.invalid", NoneExisting, CancellationToken.None)).Id!.Value;
        var afterAdding = await store.CurrentAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await store.RemoveAsync(huamei, "second@example.invalid", CancellationToken.None));
        Assert.False(await store.RemoveAsync(huamei, "second@example.invalid", CancellationToken.None));
        var afterRemoving = await store.CurrentAsync(CancellationToken.None);

        Assert.Equal(("0", "2", "3"), (empty.Version, afterAdding.Version, afterRemoving.Version));
        Assert.Equal(["Huamei Trading", "Northwind Freight"], afterAdding.Entries.Select(entry => entry.Name));
        Assert.Equal(other, Assert.Single(afterRemoving.Entries).Id);
        var removed = (await store.HistoryAsync(CancellationToken.None)).Single(entry => entry.Id == huamei);
        Assert.Equal(("second@example.invalid", clock.GetUtcNow()), (removed.RemovedBy, removed.RemovedAt));
    }

    [SqlServerFact]
    public async Task ANameTheCheckFinds_IsNotAddedAgain_AndNothingIsWritten()
    {
        await using var context = CreateContext();
        var store = new AllowListStore(context, clock);
        var first = await store.AddAsync("Huamei Trading", "A customer", "owner@example.invalid", NoneExisting, CancellationToken.None);
        IReadOnlyList<AllowListRecord>? seen = null;

        var second = await store.AddAsync("HUAMEI TRADING", "Again", "second@example.invalid", inForce =>
        {
            seen = inForce;
            return inForce.FirstOrDefault(entry => string.Equals(entry.Name, "Huamei Trading", StringComparison.OrdinalIgnoreCase))?.Id;
        }, CancellationToken.None);

        Assert.Equal((null, first.Id), (second.Id, second.ExistingId));
        Assert.Equal([first.Id!.Value], seen!.Select(entry => entry.Id));
        Assert.Single(await store.HistoryAsync(CancellationToken.None));
    }

    private static int? NoneExisting(IReadOnlyList<AllowListRecord> inForce) => null;

    private ListsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ListsDbContext>().UseSqlServer(connectionString).Options);
}
