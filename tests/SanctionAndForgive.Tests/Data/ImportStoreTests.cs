using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SanctionAndForgive.Data;

namespace SanctionAndForgive.Tests.Data;

// The import rules (spec section 4) against a real SQL Server: each test class gets its own database, built by the
// migrations, and drops it afterwards.
public sealed class ImportStoreTests : IAsyncLifetime
{
    private readonly string connectionString;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-06T03:30:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public ImportStoreTests()
    {
        var server = Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") ?? string.Empty;
        connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "import_tests_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
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
    public async Task FirstImport_BecomesActive_WithNamesAndAddresses()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);

        var outcome = await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);

        Assert.Equal(ImportResult.Imported, outcome.Result);
        var active = await store.ActiveSubjectsAsync(source, CancellationToken.None);
        Assert.Equal(10, active.Count);
        Assert.All(active, subject => Assert.Equal(2, subject.Names.Count));
        Assert.Contains(active, subject => subject.Addresses.Any(address => address.CountryCode == "EE"));
        var status = Assert.Single(await store.StatusAsync(CancellationToken.None));
        Assert.True(status.Current);
        Assert.Equal(10, status.RecordCount);
    }

    [SqlServerFact]
    public async Task SameContent_IsUnchanged_OnlyLastCheckedMoves()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var firstImport = source.ActiveImportId;
        clock.Advance(TimeSpan.FromDays(1));

        // The same records in another order: a regenerated file.
        var outcome = await store.ImportAsync(source, [.. Subjects(10).AsEnumerable().Reverse()], force: false, CancellationToken.None);

        Assert.Equal(ImportResult.Unchanged, outcome.Result);
        Assert.Equal(firstImport, source.ActiveImportId);
        Assert.Equal(clock.GetUtcNow(), source.LastCheckedAt);
        Assert.Equal(1, await context.Imports.CountAsync());
    }

    [SqlServerFact]
    public async Task LargeDrop_IsRejected_UnlessForced()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var firstImport = source.ActiveImportId;

        var rejected = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);
        Assert.Equal(ImportResult.Rejected, rejected.Result);
        Assert.Equal(firstImport, source.ActiveImportId);
        Assert.Equal(10, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);

        var forced = await store.ImportAsync(source, Subjects(7), force: true, CancellationToken.None);
        Assert.Equal(ImportResult.Imported, forced.Result);
        Assert.Equal(7, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);
    }

    [SqlServerFact]
    public async Task ZeroRecords_IsAlwaysRejected()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);

        var outcome = await store.ImportAsync(source, [], force: true, CancellationToken.None);

        Assert.Equal(ImportResult.Rejected, outcome.Result);
        Assert.Null(source.ActiveImportId);
    }

    [SqlServerFact]
    public async Task OnlyTheActiveAndThePreviousImportAreKept()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);

        for (var count = 10; count <= 13; count++)
        {
            await store.ImportAsync(source, Subjects(count), force: false, CancellationToken.None);
        }

        Assert.Equal(2, await context.Imports.CountAsync());
        Assert.Equal(13 + 12, await context.Subjects.CountAsync());
        Assert.Equal((13 + 12) * 2, await context.SubjectNames.CountAsync());
    }

    [SqlServerFact]
    public async Task StaleSource_IsNotCurrent()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(3), force: false, CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(73));

        Assert.False(Assert.Single(await store.StatusAsync(CancellationToken.None)).Current);
    }

    [SqlServerFact]
    public async Task StaleSourceObject_GuardUsesTheCurrentActiveImport()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();
        var staleStore = new ImportStore(first, clock);
        var stale = await staleStore.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await staleStore.ImportAsync(stale, Subjects(10), force: false, CancellationToken.None);

        // Another process grows the list to 20 after this one loaded the source.
        var otherStore = new ImportStore(second, clock);
        var fresh = await otherStore.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await otherStore.ImportAsync(fresh, Subjects(20), force: false, CancellationToken.None);

        // 15 is fine against the stale 10, but more than a 20% drop from the real 20.
        var outcome = await staleStore.ImportAsync(stale, Subjects(15), force: false, CancellationToken.None);

        Assert.Equal(ImportResult.Rejected, outcome.Result);
        Assert.Equal(20, (await staleStore.ActiveSubjectsAsync(stale, CancellationToken.None)).Count);
    }

    [SqlServerFact]
    public async Task ManualChange_SeesTheListUnderTheLock_AndCantEmptyIt()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("ee-mfa", "Estonia", null, null, CancellationToken.None);
        await store.ChangeAsync(source, current => [.. current, .. Subjects(2)], CancellationToken.None);

        var removed = await store.ChangeAsync(source, current => [.. current.Where(subject => subject.ExternalId != "X-1")], CancellationToken.None);
        var emptied = await store.ChangeAsync(source, current => [], CancellationToken.None);

        Assert.Equal((ImportResult.Imported, 1), (removed.Result, removed.RecordCount));
        Assert.Equal(ImportResult.Rejected, emptied.Result);
        Assert.Equal("X-2", Assert.Single(await store.ActiveSubjectsAsync(source, CancellationToken.None)).ExternalId);
    }

    [SqlServerFact]
    public async Task ManualChangeThatChangesNothing_WritesNothing()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("ee-mfa", "Estonia", null, null, CancellationToken.None);
        await store.ChangeAsync(source, current => [.. current, .. Subjects(2)], CancellationToken.None);
        var active = source.ActiveImportId;

        var outcome = await store.ChangeAsync(source, current => [.. current.Where(subject => subject.ExternalId != "X-99")], CancellationToken.None);

        Assert.Equal(ImportResult.Unchanged, outcome.Result);
        Assert.Equal(active, source.ActiveImportId);
        Assert.Equal(1, await context.Imports.CountAsync());
    }

    [SqlServerFact]
    public async Task TooLongExternalId_IsRefused_NotTruncated()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        var subjects = Subjects(1);
        subjects[0].ExternalId = new string('x', 201);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportAsync(source, subjects, force: false, CancellationToken.None));
        Assert.Null(source.ActiveImportId);
    }

    [SqlServerFact]
    public async Task RecordedFailure_ShowsInStatus_ActiveListUntouched()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(3), force: false, CancellationToken.None);
        var checkedAt = source.LastCheckedAt;
        clock.Advance(TimeSpan.FromHours(1));

        await store.RecordFailureAsync(source, "HttpRequestException: 503", CancellationToken.None);

        var status = Assert.Single(await store.StatusAsync(CancellationToken.None));
        Assert.Equal((ImportStatus.Failed, 3, checkedAt), (status.LastImportStatus, status.RecordCount, status.LastCheckedAt));
    }

    private ListsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ListsDbContext>().UseSqlServer(connectionString).Options);

    private static List<Subject> Subjects(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i =>
        {
            var subject = new Subject { ExternalId = $"X-{i}", Type = SubjectType.Entity, PrimaryName = $"Fictional Company {i}", Programmes = "TEST" };
            subject.Names.Add(new SubjectName { FullName = $"Fictional Company {i}" });
            subject.Names.Add(new SubjectName { FullName = $"Вымышленная компания {i}", Script = NameScript.Cyrillic });
            subject.Addresses.Add(new SubjectAddress { Raw = $"Street {i}, Tallinn", CountryCode = "EE" });
            return subject;
        }),
    ];
}
