using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SanctionAndForgive.Api.Lists;
using SanctionAndForgive.Data;
using SanctionAndForgive.Tests.Data;

namespace SanctionAndForgive.Tests.Api;

// The loader against a real SQL Server: what it makes searchable, and what it refuses to.
public sealed class ListLoaderTests : IAsyncLifetime
{
    private readonly string connectionString = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") ?? string.Empty)
    {
        InitialCatalog = "loader_tests_" + Guid.NewGuid().ToString("N")[..8],
    }.ConnectionString;

    private ServiceProvider services = null!;

    public async Task InitializeAsync()
    {
        services = new ServiceCollection().AddListsDatabase(connectionString).BuildServiceProvider();
        if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") is { Length: > 0 })
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ListsDbContext>().Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") is { Length: > 0 })
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ListsDbContext>().Database.EnsureDeletedAsync();
        }

        await services.DisposeAsync();
    }

    [SqlServerFact]
    public async Task Refresh_MakesTheActiveImportSearchable_WithItsVersion()
    {
        var importId = await ImportAsync(3);
        var catalog = new ListCatalog();

        await new ListLoader(services.GetRequiredService<IServiceScopeFactory>(), catalog, TimeProvider.System, NullLogger<ListLoader>.Instance).RefreshAsync(CancellationToken.None);

        Assert.True(catalog.IsReady);
        var list = Assert.Single(catalog.Lists);
        Assert.Equal((importId, 3), (list.ActiveImportId, list.Index!.Subjects.Count));
    }

    [SqlServerFact]
    public async Task Refresh_LoadsTheHistory_AHeldUpdateFirst_AndKeepsServingTheActiveList()
    {
        var active = await ImportAsync(10);
        await ImportAsync(7);
        var catalog = new ListCatalog();

        await new ListLoader(services.GetRequiredService<IServiceScopeFactory>(), catalog, TimeProvider.System, NullLogger<ListLoader>.Instance).RefreshAsync(CancellationToken.None);

        Assert.Equal((active, 10), (catalog.Lists[0].ActiveImportId, catalog.Lists[0].Index!.Subjects.Count));
        var history = catalog.History["eu"];
        Assert.Equal([ImportStatus.PendingReview, ImportStatus.Completed], history.Select(entry => entry.Status));
        Assert.Equal((7, false), (history[0].RecordCount, history[0].Active));
    }

    [SqlServerFact]
    public async Task ImportThatDoesntAddUp_StaysNotChecked()
    {
        await ImportAsync(3);
        await using (var scope = services.CreateAsyncScope())
        {
            // Half a list: what a cleanup running during the load could leave.
            var context = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            await context.SubjectNames.Where(name => name.FullName.EndsWith('1')).ExecuteDeleteAsync();
            await context.Subjects.Where(subject => subject.ExternalId == "X-1").ExecuteDeleteAsync();
        }

        var catalog = new ListCatalog();
        await new ListLoader(services.GetRequiredService<IServiceScopeFactory>(), catalog, TimeProvider.System, NullLogger<ListLoader>.Instance).RefreshAsync(CancellationToken.None);

        Assert.Null(Assert.Single(catalog.Lists).Index);
    }

    [SqlServerFact]
    public async Task ImportTheSourceMovedOnFrom_IsNotBuilt_EvenIfItsRowsLookComplete()
    {
        var first = await ImportAsync(3);
        await ImportAsync(4);
        await using (var scope = services.CreateAsyncScope())
        {
            // What a cleanup of the old import leaves for a moment: its names gone, its subjects still there.
            var context = scope.ServiceProvider.GetRequiredService<ListsDbContext>();
            await context.SubjectNames.Where(name => context.Subjects.Any(subject => subject.Id == name.SubjectId && subject.ImportId == first)).ExecuteDeleteAsync();
        }

        await using var buildScope = services.CreateAsyncScope();
        var loader = new ListLoader(services.GetRequiredService<IServiceScopeFactory>(), new ListCatalog(), TimeProvider.System, NullLogger<ListLoader>.Instance);
        var index = await loader.BuildAsync(buildScope.ServiceProvider.GetRequiredService<ListsDbContext>(), "eu", first, 3, CancellationToken.None);

        Assert.Null(index);
    }

    private async Task<int> ImportAsync(int count)
    {
        await using var scope = services.CreateAsyncScope();
        var store = new ImportStore(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), TimeProvider.System);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        var subjects = Enumerable.Range(1, count).Select(i =>
        {
            var subject = new Subject { ExternalId = $"X-{i}", Type = SubjectType.Entity, PrimaryName = $"Fictional Company {i}" };
            subject.Names.Add(new SubjectName { FullName = $"Fictional Company {i}" });
            return subject;
        }).ToList();
        await store.ImportAsync(source, subjects, force: false, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ListsDbContext>().Entry(source).ReloadAsync();
        return source.ActiveImportId!.Value;
    }
}
