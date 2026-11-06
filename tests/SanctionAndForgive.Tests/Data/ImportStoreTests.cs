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
    public async Task LargeDrop_IsHeldForReview_WrittenButNotActive_UnlessForced()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var (firstImport, checkedAt) = (source.ActiveImportId, source.LastCheckedAt);
        clock.Advance(TimeSpan.FromDays(1));

        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);
        Assert.Equal(ImportResult.HeldForReview, held.Result);
        Assert.Equal((firstImport, checkedAt), (source.ActiveImportId, source.LastCheckedAt));
        Assert.Equal(10, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);
        var import = await context.Imports.AsNoTracking().SingleAsync(i => i.Id == held.ImportId);
        Assert.Equal((ImportStatus.PendingReview, true, 7), (import.Status, import.HasData, await context.Subjects.CountAsync(s => s.ImportId == import.Id)));
        Assert.Equal(import.Id, Assert.Single(await store.StatusAsync(CancellationToken.None)).HeldImportId);

        var forced = await store.ImportAsync(source, Subjects(7), force: true, CancellationToken.None);
        Assert.Equal(ImportResult.Imported, forced.Result);
        Assert.Equal(7, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);
    }

    [SqlServerFact]
    public async Task TheSameHeldUpdateAgain_StaysHeld_WritesNothing_AndTheListAgesOut()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var first = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(4));

        var again = await store.ImportAsync(source, [.. Subjects(7).AsEnumerable().Reverse()], force: false, CancellationToken.None);

        Assert.Equal((ImportResult.HeldForReview, first.ImportId), (again.Result, again.ImportId));
        Assert.Equal(2, await context.Imports.CountAsync());
        Assert.False(Assert.Single(await store.StatusAsync(CancellationToken.None)).Current);
    }

    [SqlServerFact]
    public async Task ConfirmingAHeldUpdate_MakesItActive_CheckedWhenItWasDownloaded_ByTheReviewer()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var downloadedAt = clock.GetUtcNow();
        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(5));

        var result = await store.ReviewAsync(held.ImportId!.Value, confirm: true, "owner@example.invalid", CancellationToken.None);

        Assert.Equal(ReviewResult.Confirmed, result);
        await context.Entry(source).ReloadAsync();
        Assert.Equal((held.ImportId, downloadedAt), (source.ActiveImportId, source.LastCheckedAt));
        Assert.Equal(7, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);
        var entry = (await store.HistoryAsync("eu", perSource: 10, CancellationToken.None))[0];
        Assert.Equal((held.ImportId!.Value, true, "owner@example.invalid", clock.GetUtcNow()), (entry.Id, entry.Active, entry.ReviewedBy, entry.ReviewedAt));
        Assert.Equal(ReviewResult.NotPending, await store.ReviewAsync(held.ImportId.Value, confirm: false, "owner@example.invalid", CancellationToken.None));
        Assert.Equal(ReviewResult.NotFound, await store.ReviewAsync(999_999, confirm: true, "owner@example.invalid", CancellationToken.None));
    }

    [SqlServerFact]
    public async Task AHold_IsSupersededWithTheActivation_AndAStaleOneCanNeverRollTheListBack()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);

        var forced = await store.ImportAsync(source, Subjects(12), force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Rejected, (await context.Imports.AsNoTracking().SingleAsync(i => i.Id == held.ImportId)).Status);

        // As if the superseding had been lost: the hold looks pending again, still with its records.
        await context.Imports.Where(i => i.Id == held.ImportId).ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, ImportStatus.PendingReview).SetProperty(i => i.HasData, true));
        var result = await store.ReviewAsync(held.ImportId!.Value, confirm: true, "owner@example.invalid", CancellationToken.None);

        Assert.Equal(ReviewResult.NotPending, result);
        await context.Entry(source).ReloadAsync();
        Assert.Equal(forced.ImportId, source.ActiveImportId);
        Assert.Equal(12, (await store.ActiveSubjectsAsync(source, CancellationToken.None)).Count);
    }

    [SqlServerFact]
    public async Task AHeldUpdate_StaysFindable_AfterManyLaterChecks()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);
        for (var i = 0; i < 25; i++)
        {
            await store.RecordFailureAsync(source, "HttpRequestException: 503", CancellationToken.None);
        }

        Assert.DoesNotContain(await store.HistoryAsync("eu", perSource: 20, CancellationToken.None), entry => entry.Id == held.ImportId);
        Assert.Equal(held.ImportId, Assert.Single(await store.HeldAsync(CancellationToken.None)).Id);
    }

    [SqlServerFact]
    public async Task RejectingAHeldUpdate_DeletesItsRecords_KeepsItInTheHistory()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var active = source.ActiveImportId;
        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);

        Assert.Equal(ReviewResult.Rejected, await store.ReviewAsync(held.ImportId!.Value, confirm: false, "owner@example.invalid", CancellationToken.None));

        await context.Entry(source).ReloadAsync();
        Assert.Equal(active, source.ActiveImportId);
        Assert.Equal(0, await context.Subjects.CountAsync(s => s.ImportId == held.ImportId));
        var entry = (await store.HistoryAsync("eu", perSource: 10, CancellationToken.None))[0];
        Assert.Equal(ImportStatus.Rejected, entry.Status);
        Assert.Contains("rejected on review", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldUpdate_IsSuperseded_WhenTheFeedReturnsToTheActiveList_OrALaterUpdateNeedsNoReview(bool laterUpdate)
    {
        if (Environment.GetEnvironmentVariable("SQLSERVER_TEST_CONNECTION") is not { Length: > 0 })
        {
            return;
        }

        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);
        await store.ImportAsync(source, Subjects(10), force: false, CancellationToken.None);
        var held = await store.ImportAsync(source, Subjects(7), force: false, CancellationToken.None);

        var outcome = await store.ImportAsync(source, Subjects(laterUpdate ? 11 : 10), force: false, CancellationToken.None);

        Assert.Equal(laterUpdate ? ImportResult.Imported : ImportResult.Unchanged, outcome.Result);
        var superseded = await context.Imports.AsNoTracking().SingleAsync(i => i.Id == held.ImportId);
        Assert.Equal((ImportStatus.Rejected, false), (superseded.Status, superseded.HasData));
        Assert.Contains("superseded", superseded.Message, StringComparison.Ordinal);
        Assert.Equal(0, await context.Subjects.CountAsync(s => s.ImportId == held.ImportId));
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
    public async Task OnlyTheActiveAndThePreviousImportKeepTheirRecords_EveryImportStaysInTheHistory()
    {
        await using var context = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("eu", "EU", "https://example.invalid/eu", 72, CancellationToken.None);

        for (var count = 10; count <= 13; count++)
        {
            await store.ImportAsync(source, Subjects(count), force: false, CancellationToken.None);
        }

        Assert.Equal(13 + 12, await context.Subjects.CountAsync());
        Assert.Equal((13 + 12) * 2, await context.SubjectNames.CountAsync());
        var history = await store.HistoryAsync("eu", perSource: 10, CancellationToken.None);
        Assert.Equal([13, 12, 11, 10], history.Select(entry => entry.RecordCount));
        Assert.Equal([true, false, false, false], history.Select(entry => entry.Active));
        Assert.Equal(2, await context.Imports.CountAsync(i => i.HasData));
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

        Assert.Equal(ImportResult.HeldForReview, outcome.Result);
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
    public async Task ActiveImportChangedMidEdit_FlipIsRefused_NothingLost()
    {
        await using var context = CreateContext();
        await using var other = CreateContext();
        var store = new ImportStore(context, clock);
        var source = await store.EnsureSourceAsync("ee-mfa", "Estonia", null, null, CancellationToken.None);
        await store.ChangeAsync(source, current => [.. Subjects(2)], CancellationToken.None);
        var first = source.ActiveImportId;
        await store.ChangeAsync(source, current => [.. Subjects(3)], CancellationToken.None);

        // While this edit holds the lock, the active import moves anyway: what a retried command on a new session,
        // which no longer holds the lock, could let another importer do.
        var edit = store.ChangeAsync(source, current =>
        {
            other.Sources.Where(s => s.Code == "ee-mfa").ExecuteUpdate(set => set.SetProperty(s => s.ActiveImportId, first));
            return [.. current, .. Subjects(5).Skip(3)];
        }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => edit);
        Assert.Equal(first, (await other.Sources.AsNoTracking().SingleAsync(s => s.Code == "ee-mfa")).ActiveImportId);
        Assert.Equal(ImportStatus.Failed, (await other.Imports.AsNoTracking().OrderByDescending(i => i.Id).FirstAsync()).Status);
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
