using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Tests.Data;

// Switching a list off and on against a real SQL Server (a database per test class, built by the migrations).
public sealed class SourceSwitchTests : IAsyncLifetime
{
    private readonly string connectionString;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-09T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public SourceSwitchTests()
    {
        var server = Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") ?? string.Empty;
        connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "switch_tests_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
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

    private ListsDbContext CreateContext() => new(new DbContextOptionsBuilder<ListsDbContext>().UseSqlServer(connectionString).Options);

    [SqlServerFact]
    public async Task OffKeepsTheFirstReason_OnClearsIt_AndAnUnknownListIsNotFound()
    {
        await using (var context = CreateContext())
        {
            await new ImportStore(context, clock).EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        }

        await using var store = CreateContext();
        var switches = new SourceSwitch(store, clock);

        Assert.Equal(SwitchResult.Switched, await switches.SwitchAsync("eu", off: true, "Known to be wrong.", "owner@example.invalid", CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(SwitchResult.Unchanged, await switches.SwitchAsync("eu", off: true, "Another reason.", "other@example.invalid", CancellationToken.None));
        var off = await store.Sources.AsNoTracking().SingleAsync(source => source.Code == "eu");
        Assert.Equal((clock.GetUtcNow().AddHours(-1), "owner@example.invalid", "Known to be wrong."), (off.SwitchedOffAt, off.SwitchedOffBy, off.SwitchedOffReason));

        Assert.Equal(SwitchResult.Switched, await switches.SwitchAsync("eu", off: false, reason: null, "owner@example.invalid", CancellationToken.None));
        Assert.Equal(SwitchResult.Unchanged, await switches.SwitchAsync("eu", off: false, reason: null, "owner@example.invalid", CancellationToken.None));
        var on = await store.Sources.AsNoTracking().SingleAsync(source => source.Code == "eu");
        Assert.Equal((null, null, null), (on.SwitchedOffAt, on.SwitchedOffBy, on.SwitchedOffReason));

        Assert.Equal(SwitchResult.NotFound, await switches.SwitchAsync("nope", off: true, "Why.", "owner@example.invalid", CancellationToken.None));
    }
}
