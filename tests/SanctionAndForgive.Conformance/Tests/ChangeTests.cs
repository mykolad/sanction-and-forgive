using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanctionAndForgive.Conformance.Feeds;
using SanctionAndForgive.Conformance.Harness;

namespace SanctionAndForgive.Conformance.Tests;

// Administrators' changes and the import rules, each put back afterwards (the base state is restored by importing the
// base files again, which only moves a list's last check when nothing changed).
[Collection("3 Changes")]
public sealed class ChangeTests
{
    private const string Sfms = "ua-sfms";

    private static string Name(string code) => code switch
    {
        "uk" => "UK Sanctions List",
        "eu" => "EU consolidated financial sanctions",
        _ => code,
    };

    /// <summary>A company that is a Match on <paramref name="code"/> and no more than a No match on every other list
    /// (the fictional names repeat, so this asks the implementation rather than the fixtures).</summary>
    private static async Task<string> OnlyOnAsync(string code)
    {
        foreach (var subject in FeedFiles.Subjects(code).Where(subject => !subject.Person))
        {
            var result = await Suite.ScreenAsync(new { companyName = subject.LatinName });
            var cells = result.GetProperty("entries")[0].GetProperty("cells").EnumerateArray().ToList();
            if (cells.All(cell => cell.GetProperty("list").GetString() == code ? cell.GetProperty("outcome").GetString() == "Match" : cell.GetProperty("outcome").GetString() == "NoMatch"))
            {
                return subject.LatinName;
            }
        }

        throw new InvalidOperationException($"No company is listed on {code} alone.");
    }

    [ConformanceFact]
    public async Task SwitchingAList_NeedsTheRoleAndTheSitesHeader()
    {
        await Suite.ReadyAsync();
        var cases = new (string? Principal, bool FromThisSite, HttpStatusCode Expected)[]
        {
            (null, true, HttpStatusCode.Unauthorized),
            (Suite.Principal("visitor@example.invalid", admin: false), true, HttpStatusCode.Forbidden),
            (Suite.Principal(Suite.AdminName, admin: true), false, HttpStatusCode.BadRequest),
        };

        foreach (var (principal, fromThisSite, expected) in cases)
        {
            using var off = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-off", new { reason = "Conformance." }, principal, fromThisSite);
            using var review = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/imports/1/confirm", body: null, principal, fromThisSite);
            using var allow = await Suite.SendAsync(HttpMethod.Post, "api/v1/admin/allow-list", new { name = "Hannah Becker", reason = "Conformance." }, principal, fromThisSite);
            Assert.Equal((expected, expected, expected), (off.StatusCode, review.StatusCode, allow.StatusCode));
        }

        await Suite.WaitForListsAsync(lists => lists["uk"].GetProperty("state").GetString() == "Current");
    }

    [ConformanceFact]
    public async Task ASwitchedOffList_IsNotSearched_TheSummarySaysWhy_AndSwitchingItOnSearchesItAgain()
    {
        await Suite.ReadyAsync();
        var company = await OnlyOnAsync("uk");
        try
        {
            using (var noReason = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-off", new { reason = " " }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            }

            using (var off = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-off", new { reason = "Conformance check." }))
            {
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
                Assert.Equal("SwitchedOff", (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
            }

            using (var again = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-off", new { reason = "Another reason." }))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }

            var lists = await Suite.WaitForListsAsync(lists => lists["uk"].GetProperty("state").GetString() == "SwitchedOff");
            Assert.StartsWith("Switched off by an administrator on ", lists["uk"].GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.Contains("Conformance check.", lists["uk"].GetProperty("reason").GetString(), StringComparison.Ordinal);

            var result = await Suite.ScreenAsync(new { companyName = company });
            Assert.Null(Suite.Cell(result, "uk"));
            Assert.Equal("NoMatch", result.GetProperty("outcome").GetString());
            Assert.Contains($"Not searched: {Name("uk")} (switched off by an administrator: Conformance check).", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
            Assert.Equal("SwitchedOff", result.GetProperty("lists").EnumerateArray().Single(list => list.GetProperty("code").GetString() == "uk").GetProperty("state").GetString());

            using (var on = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-on", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, on.StatusCode);
            }

            using (var onAgain = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/uk/switch-on", body: null))
            {
                Assert.Equal(HttpStatusCode.Conflict, onAgain.StatusCode);
            }

            using (var unknown = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/lists/no-such-list/switch-on", body: null))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            await Suite.WaitForListsAsync(lists => lists["uk"].GetProperty("state").GetString() == "Current");
            var searched = await Suite.ScreenAsync(new { companyName = company });
            Assert.Equal("Match", Suite.Cell(searched, "uk")!.Value.GetProperty("outcome").GetString());
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    [ConformanceFact]
    public async Task AListPastItsAllowedAge_IsStillSearched_WithAWarning_UntilACheckSucceeds()
    {
        await Suite.ReadyAsync();
        var company = await OnlyOnAsync("eu");
        try
        {
            await using (var connection = await Suite.OpenSqlAsync())
            {
                await Suite.ExecuteAsync(connection, "UPDATE Sources SET LastCheckedAt = DATEADD(hour, -80, SYSDATETIMEOFFSET()) WHERE Code = 'eu'");
            }

            var lists = await Suite.WaitForListsAsync(lists => lists["eu"].GetProperty("state").GetString() == "Overdue");
            Assert.Contains("within 72 hours", lists["eu"].GetProperty("reason").GetString(), StringComparison.Ordinal);

            var result = await Suite.ScreenAsync(new { reference = "overdue-1", companyName = company });
            Assert.Equal("Match", result.GetProperty("outcome").GetString());
            Assert.Equal("Match", Suite.Cell(result, "eu")!.Value.GetProperty("outcome").GetString());
            Assert.Matches($"Warning: couldn't be updated within 72 hours, so searched as last downloaded: {Name("eu")}, as of \\d{{1,2}} [A-Z][a-z]{{2}} \\d{{4}} \\d{{2}}:\\d{{2}} UTC\\.$", result.GetProperty("summary").GetString());
            Assert.Equal("Overdue", result.GetProperty("lists").EnumerateArray().Single(list => list.GetProperty("code").GetString() == "eu").GetProperty("state").GetString());
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    [ConformanceFact]
    public async Task TheAllowList_ShowsPossibleMatchesFirst_AllowsThemOnceConfirmed_AndNeverAListedParty()
    {
        await Suite.ReadyAsync();
        var (name, kind) = await PossibleMatchAsync();
        object Screening() => kind == "Company" ? new { companyName = name } : new { receiverName = name };
        try
        {
            using (var listed = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/allow-list", new { name = await OnlyOnAsync("uk"), reason = "Conformance." }))
            {
                Assert.Equal(HttpStatusCode.Conflict, listed.StatusCode);
                Assert.Equal("listed", (await listed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }

            using var first = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/allow-list", new { name, reason = "A different party (conformance)." });
            Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
            var refusal = await first.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("confirm", refusal.GetProperty("code").GetString());
            var keys = refusal.GetProperty("possibleMatches").EnumerateArray().Select(match => match.GetProperty("key").GetString()).ToList();
            Assert.NotEmpty(keys);

            using var added = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/allow-list", new { name, reason = "A different party (conformance).", confirmed = keys });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

            using (var exists = await Suite.AdminAsync(HttpMethod.Post, "api/v1/admin/allow-list", new { name, reason = "Again.", confirmed = keys }))
            {
                Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
                Assert.Equal("exists", (await exists.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }

            var allowed = await Suite.ScreenAsync(Screening());
            Assert.Equal("NoMatch", allowed.GetProperty("outcome").GetString());
            var entry = allowed.GetProperty("entries")[0];
            Assert.Equal(id, entry.GetProperty("allowedBy").GetProperty("id").GetInt32());
            Assert.Contains(entry.GetProperty("cells").EnumerateArray(), cell => cell.GetProperty("allowed").GetBoolean() && cell.GetProperty("outcome").GetString() == "Review");

            using (var removed = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/allow-list/{id}/remove", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            }

            using (var history = await Suite.AdminAsync(HttpMethod.Get, "api/v1/admin/allow-list", body: null))
            {
                var row = (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("id").GetInt32() == id);
                Assert.Equal((Suite.AdminName, Suite.AdminName), (row.GetProperty("addedBy").GetString(), row.GetProperty("removedBy").GetString()));
            }

            Assert.Equal("Review", (await Suite.ScreenAsync(Screening())).GetProperty("outcome").GetString());
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    /// <summary>Another replica's administrator changes only the database: this replica applies it within seconds
    /// (<c>Lists:ChangeCheckSeconds</c>), long before its full refresh (<c>Lists:RefreshMinutes</c>, an hour in a
    /// conformance run). An allow-list entry added, then removed (never looser for long), and a list switched off.</summary>
    [ConformanceFact]
    public async Task AnAdministratorsChange_MadeOnAnotherReplica_AppliesHereWithinSeconds()
    {
        await Suite.ReadyAsync();
        var (name, kind) = await PossibleMatchAsync();
        object Screening() => kind == "Company" ? new { companyName = name } : new { receiverName = name };
        async Task<bool> AllowedAsync() => (await Suite.ScreenAsync(Screening())).GetProperty("entries")[0].GetProperty("cells").EnumerateArray()
            .Any(cell => cell.GetProperty("allowed").GetBoolean());
        await using var connection = await Suite.OpenSqlAsync();
        try
        {
            Assert.False(await AllowedAsync());
            await Suite.ExecuteAsync(connection, "INSERT INTO AllowListEntries (Name, Reason, AddedBy, AddedAt) VALUES (@name, N'Another replica (conformance).', N'conformance', SYSDATETIMEOFFSET())", ("name", name));
            await WithinSecondsAsync(10, AllowedAsync, "an allow-list entry added elsewhere");

            await Suite.ExecuteAsync(connection, "UPDATE AllowListEntries SET RemovedBy = N'conformance', RemovedAt = SYSDATETIMEOFFSET() WHERE Name = @name AND RemovedAt IS NULL", ("name", name));
            await WithinSecondsAsync(10, async () => !await AllowedAsync(), "an allow-list entry removed elsewhere");

            await Suite.ExecuteAsync(connection, "UPDATE Sources SET SwitchedOffAt = SYSDATETIMEOFFSET(), SwitchedOffBy = N'conformance', SwitchedOffReason = N'Another replica (conformance).' WHERE Code = @code", ("code", Sfms));
            await WithinSecondsAsync(10, async () => (await Suite.GetAsync("api/v1/status")).GetProperty("lists").EnumerateArray()
                .Any(list => list.GetProperty("code").GetString() == Sfms && list.GetProperty("state").GetString() == "SwitchedOff"), "a list switched off elsewhere");
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    private static async Task WithinSecondsAsync(int seconds, Func<Task<bool>> condition, string what)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), $"The API didn't apply {what} within {seconds} s.");
            await Task.Delay(250);
        }
    }

    /// <summary>A name from the golden set that is a possible match (Review) and no Match: an allow-list candidate.</summary>
    private static async Task<(string Name, string Kind)> PossibleMatchAsync()
    {
        foreach (var input in Golden.GoldenInputs.All.Where(input => (input.ReceiverName is null) != (input.CompanyName is null) && input.Address is null))
        {
            var result = await Suite.ScreenAsync(new { receiverName = input.ReceiverName, companyName = input.CompanyName });
            if (result.GetProperty("outcome").GetString() == "Review")
            {
                return (input.ReceiverName ?? input.CompanyName!, input.CompanyName is null ? "Receiver" : "Company");
            }
        }

        throw new InvalidOperationException("No golden input is a possible match.");
    }

    [ConformanceFact]
    public async Task AnUpdate_RecordsWhatItAdded_Removed_AndChanged()
    {
        await Suite.ReadyAsync();
        var original = FeedFiles.Subjects(Sfms);
        var newcomers = Fixtures.All.Where(subject => original.All(listed => listed.Number != subject.Number)).Take(3).ToList();
        var changed = original.Skip(2).Select((subject, index) => index < 4 ? subject with { Aliases = [.. subject.Aliases, $"Changed Alias {index}"] } : subject)
            .Concat(newcomers).OrderBy(subject => subject.Number).ToList();
        var path = Suite.FeedPath(Sfms, "changed");
        FeedFiles.Write(Sfms, changed, path);
        try
        {
            var import = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(path)}");
            Assert.True(import.ExitCode == 0, import.Output);
            Assert.Contains("Imported", import.Output, StringComparison.Ordinal);

            var latest = await LatestAsync(Sfms, row => row.GetProperty("recordCount").GetInt32() == original.Count + 1 && row.GetProperty("result").GetString() == "Active");
            Assert.Equal((3, 2, 4), (latest.GetProperty("added").GetInt32(), latest.GetProperty("removed").GetInt32(), latest.GetProperty("changed").GetInt32()));
            await Suite.WaitForListsAsync(lists => lists[Sfms].GetProperty("recordCount").GetInt32() == original.Count + 1);

            var unchanged = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(path)}");
            Assert.True(unchanged.ExitCode == 0, unchanged.Output);
            Assert.Contains("Unchanged", unchanged.Output, StringComparison.Ordinal);
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }

        var back = await LatestAsync(Sfms, row => row.GetProperty("recordCount").GetInt32() == original.Count && row.GetProperty("result").GetString() == "Active");
        Assert.Equal((2, 3, 4), (back.GetProperty("added").GetInt32(), back.GetProperty("removed").GetInt32(), back.GetProperty("changed").GetInt32()));
    }

    [ConformanceFact]
    public async Task ADropOfMoreThanAFifth_IsHeldForReview_AndAnAdministratorRejectsOrConfirmsIt()
    {
        await Suite.ReadyAsync();
        var original = FeedFiles.Subjects(Sfms);
        var path = Suite.FeedPath(Sfms, "dropped");
        FeedFiles.Write(Sfms, [.. original.Take(original.Count * 6 / 10)], path);
        try
        {
            var held = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(path)}");
            Assert.True(held.ExitCode == 1, held.Output);
            Assert.Contains("HeldForReview", held.Output, StringComparison.Ordinal);

            var lists = await Suite.WaitForListsAsync(lists => lists[Sfms].TryGetProperty("held", out var hold) && hold.ValueKind == JsonValueKind.Object);
            var hold = lists[Sfms].GetProperty("held");
            Assert.Equal((original.Count * 6 / 10, 0, original.Count - (original.Count * 6 / 10), 0),
                (hold.GetProperty("recordCount").GetInt32(), hold.GetProperty("added").GetInt32(), hold.GetProperty("removed").GetInt32(), hold.GetProperty("changed").GetInt32()));
            Assert.Equal(original.Count, lists[Sfms].GetProperty("recordCount").GetInt32());

            using (var rejected = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/imports/{hold.GetProperty("importId").GetInt32()}/reject", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            }

            await Suite.WaitForListsAsync(lists => !lists[Sfms].TryGetProperty("held", out var gone) || gone.ValueKind == JsonValueKind.Null);

            var again = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(path)}");
            Assert.True(again.ExitCode == 1, again.Output);
            var heldAgain = (await Suite.WaitForListsAsync(lists => lists[Sfms].TryGetProperty("held", out var hold) && hold.ValueKind == JsonValueKind.Object))[Sfms].GetProperty("held");
            using (var confirmed = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/imports/{heldAgain.GetProperty("importId").GetInt32()}/confirm", body: null))
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            }

            using (var twice = await Suite.AdminAsync(HttpMethod.Post, $"api/v1/admin/imports/{heldAgain.GetProperty("importId").GetInt32()}/confirm", body: null))
            {
                Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            }

            await Suite.WaitForListsAsync(lists => lists[Sfms].GetProperty("recordCount").GetInt32() == original.Count * 6 / 10);
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    [ConformanceFact]
    public async Task ForcingADrop_ImportsIt_ButAnEmptyFeedIsRejected_AndABrokenOneFails_KeepingTheList()
    {
        await Suite.ReadyAsync();
        var original = FeedFiles.Subjects(Sfms);
        var dropped = Suite.FeedPath(Sfms, "forced");
        var empty = Suite.FeedPath(Sfms, "empty");
        var broken = Suite.FeedPath(Sfms, "broken");
        FeedFiles.Write(Sfms, [.. original.Take(10)], dropped);
        FeedFiles.Write(Sfms, [], empty);
        await File.WriteAllTextAsync(broken, "<html><body>Maintenance</body></html>");
        try
        {
            var none = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(empty)} --force");
            Assert.True(none.ExitCode == 1, none.Output);
            Assert.Contains("Rejected", none.Output, StringComparison.Ordinal);

            var failed = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(broken)}");
            Assert.True(failed.ExitCode == 1, failed.Output);
            Assert.Contains("FAILED", failed.Output, StringComparison.Ordinal);

            var history = (await Suite.GetAsync($"api/v1/sources/{Sfms}/history")).GetProperty("history");
            await LatestAsync(Sfms, row => row.GetProperty("result").GetString() == "Failed");
            Assert.True(history.GetArrayLength() > 0);
            Assert.Equal(original.Count, (await Suite.WaitForListsAsync(_ => true))[Sfms].GetProperty("recordCount").GetInt32());

            var forced = await Suite.CliAsync($"import --source {Sfms} --file {Suite.Quote(dropped)} --force");
            Assert.True(forced.ExitCode == 0, forced.Output);
            await Suite.WaitForListsAsync(lists => lists[Sfms].GetProperty("recordCount").GetInt32() == 10);
        }
        finally
        {
            await Suite.RestoreBaseAsync();
        }
    }

    [ConformanceFact]
    public async Task AManualList_TakesRecordsFromACsv_AndRemovesThemById()
    {
        await Suite.ReadyAsync();
        var extra = Fixtures.All.First(subject => subject.Person && Suite.ManualSubjects.All(listed => listed.Number != subject.Number));
        var path = Suite.ManualPath("extra");
        FeedFiles.WriteManual([extra], path);
        try
        {
            var add = await Suite.CliAsync($"add --source {FeedFiles.ManualCode} --file {Suite.Quote(path)}");
            Assert.True(add.ExitCode == 0, add.Output);
            await Suite.WaitForListsAsync(lists => lists[FeedFiles.ManualCode].GetProperty("recordCount").GetInt32() == Suite.ManualSubjects.Count + 1);
            Assert.Equal("Match", Suite.Cell(await Suite.ScreenAsync(new { receiverName = extra.LatinName }), FeedFiles.ManualCode)!.Value.GetProperty("outcome").GetString());

            var remove = await Suite.CliAsync($"remove --source {FeedFiles.ManualCode} --external-id M-{extra.Number}");
            Assert.True(remove.ExitCode == 0, remove.Output);
            await Suite.WaitForListsAsync(lists => lists[FeedFiles.ManualCode].GetProperty("recordCount").GetInt32() == Suite.ManualSubjects.Count);
        }
        finally
        {
            // Adding upserts, so restoring the base CSV wouldn't take the extra record out: remove it (again) first.
            await Suite.CliAsync($"remove --source {FeedFiles.ManualCode} --external-id M-{extra.Number}");
            await Suite.RestoreBaseAsync();
        }
    }

    [ConformanceFact]
    public async Task TheCliReports_StatusAndHistory()
    {
        await Suite.ReadyAsync();

        var status = await Suite.CliAsync("status");
        var history = await Suite.CliAsync($"history --source {Sfms}");
        var unknown = await Suite.CliAsync("import --source no-such-list");

        Assert.True(status.ExitCode == 0, status.Output);
        Assert.All(FeedFiles.Feeds, feed => Assert.Contains(feed.Code, status.Output, StringComparison.Ordinal));
        Assert.True(history.ExitCode == 0, history.Output);
        Assert.NotEqual(0, unknown.ExitCode);
    }

    /// <summary>Waits until the newest row of <paramref name="code"/>'s history satisfies <paramref name="condition"/>.</summary>
    private static async Task<JsonElement> LatestAsync(string code, Func<JsonElement, bool> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var history = (await Suite.GetAsync($"api/v1/sources/{code}/history")).GetProperty("history");
            if (history.GetArrayLength() > 0 && condition(history[0]))
            {
                return history[0];
            }

            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException($"{code}'s newest update never matched.");
    }
}
