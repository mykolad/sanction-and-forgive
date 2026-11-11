using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SanctionAndForgive.Conformance.Feeds;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCollectionOrderer("SanctionAndForgive.Conformance.Harness.ByName", "SanctionAndForgive.Conformance")]

namespace SanctionAndForgive.Conformance.Harness;

/// <summary>Where the implementation under test is, from the environment (docs/conformance.md). Without
/// <c>CONFORMANCE_BASE_URL</c> every test is skipped, so a plain <c>dotnet test</c> of the solution passes.</summary>
public static class Settings
{
    public static string? BaseUrl => Environment.GetEnvironmentVariable("CONFORMANCE_BASE_URL") is { Length: > 0 } url ? url.TrimEnd('/') : null;

    /// <summary>The app's database (the same schema whatever the language), for checks and for setting up states
    /// the API can't (a list's last check three days ago).</summary>
    public static string Sql => Required("CONFORMANCE_SQL");

    /// <summary>The command that runs the implementation's CLI with its database configured: the suite appends the
    /// arguments (<c>import --source eu --file …</c>).</summary>
    public static string Cli => Required("CONFORMANCE_CLI");

    /// <summary>Where the suite writes the feeds; the CLI must read them at the same path (mount it).</summary>
    public static string Feeds => Environment.GetEnvironmentVariable("CONFORMANCE_FEEDS") is { Length: > 0 } feeds ? feeds : Path.Combine(Path.GetTempPath(), "conformance-feeds");

    /// <summary>Where the CLI sees <see cref="Feeds"/>, if not at the same path (a container with it mounted:
    /// <c>/feeds</c>).</summary>
    public static string CliFeeds => Environment.GetEnvironmentVariable("CONFORMANCE_CLI_FEEDS") is { Length: > 0 } feeds ? feeds : Feeds;

    /// <summary>Optional: the files the API's console output goes to (standard output and error, separated by
    /// <c>;</c>), for the logging checks.</summary>
    public static IReadOnlyList<string> ApiLogs => Environment.GetEnvironmentVariable("CONFORMANCE_API_LOG") is { Length: > 0 } logs
        ? logs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : [];

    /// <summary>Set to 1 to write the golden files from this implementation instead of comparing with them.</summary>
    public static bool UpdateGolden => Environment.GetEnvironmentVariable("CONFORMANCE_UPDATE_GOLDEN") == "1";

    public static string GoldenDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var golden = Path.Combine(directory.FullName, "tests", "conformance", "golden");
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "conformance")))
                {
                    Directory.CreateDirectory(golden);
                    return golden;
                }
            }

            throw new InvalidOperationException("tests/conformance not found above the test assembly.");
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name} (docs/conformance.md).");
}

/// <summary>A conformance test: skipped unless an implementation is configured.</summary>
public sealed class ConformanceFactAttribute : FactAttribute
{
    public ConformanceFactAttribute()
    {
        if (Settings.BaseUrl is null)
        {
            Skip = "No implementation to test: set CONFORMANCE_BASE_URL (docs/conformance.md).";
        }
    }
}

/// <summary>Collections run one at a time in name order: "1 Golden" compares results with the suite's base state
/// first, "2 Behaviour" doesn't change anything, and "3 Changes" changes state (and puts it back).</summary>
public sealed class ByName : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(collection => collection.DisplayName, StringComparer.Ordinal);
}

[CollectionDefinition("1 Golden")]
public sealed class GoldenRun;

[CollectionDefinition("2 Behaviour")]
public sealed class BehaviourRun;

[CollectionDefinition("3 Changes")]
public sealed class ChangesRun;

[CollectionDefinition("4 Reference")]
public sealed class ReferenceRun;

public sealed record CliResult(int ExitCode, string Output);

/// <summary>The implementation under test: its API, its CLI and its database, set up once with the fictional lists.</summary>
public static class Suite
{
    public const string AdminName = "conformance-admin@example.invalid";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<Task> BaseState = new(EstablishBaseStateAsync);

    // Cookies only as each request sets them: a session is sent on purpose, never left over from another test.
    public static HttpClient Http { get; } = new(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri((Settings.BaseUrl ?? "http://localhost") + "/"), Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>The same, but a redirect is the answer, not followed: the sign-in's pages.</summary>
    public static HttpClient NoRedirects { get; } = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri((Settings.BaseUrl ?? "http://localhost") + "/"), Timeout = TimeSpan.FromMinutes(1) };

    /// <summary>Every feed imported from the base variant and the manual list added; the API serves all of them.</summary>
    public static Task ReadyAsync() => BaseState.Value;

    public static string FeedPath(string code, string variant) => Path.Combine(Settings.Feeds, FeedFiles.FileName(code, variant));

    public static string ManualPath(string variant) => Path.Combine(Settings.Feeds, $"{FeedFiles.ManualCode}.{variant}.csv");

    private static async Task EstablishBaseStateAsync()
    {
        Directory.CreateDirectory(Settings.Feeds);
        foreach (var (code, _) in FeedFiles.Feeds)
        {
            FeedFiles.Write(code, FeedFiles.Subjects(code), FeedPath(code, "base"));
        }

        FeedFiles.WriteManual(ManualSubjects, ManualPath("base"));
        await RestoreBaseAsync();
    }

    public static IReadOnlyList<FixtureSubject> ManualSubjects => Fixtures.For(FeedFiles.ManualCode, 25);

    /// <summary>Imports every feed's base file and the manual CSV (unchanged ones only move their last check), then
    /// waits until the API serves exactly that: every list Current at the import now active in the database (its
    /// version), with the base record count and no update held, nothing switched off, no allow-list entry.</summary>
    public static async Task RestoreBaseAsync()
    {
        foreach (var (code, _) in FeedFiles.Feeds)
        {
            var import = await CliAsync($"import --source {code} --file {Quote(FeedPath(code, "base"))} --force");
            Assert.True(import.ExitCode == 0, $"Importing {code}: exit {import.ExitCode}{Environment.NewLine}{import.Output}");
        }

        var add = await CliAsync($"source-add --code {FeedFiles.ManualCode} --name {Quote(FeedFiles.ManualName)}");
        Assert.True(add.ExitCode == 0, $"source-add: exit {add.ExitCode}{Environment.NewLine}{add.Output}");
        var manual = await CliAsync($"add --source {FeedFiles.ManualCode} --file {Quote(ManualPath("base"))}");
        Assert.True(manual.ExitCode == 0, $"add: exit {manual.ExitCode}{Environment.NewLine}{manual.Output}");

        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var connection = await OpenSqlAsync())
        {
            await ExecuteAsync(connection, "UPDATE Sources SET SwitchedOffAt = NULL, SwitchedOffBy = NULL, SwitchedOffReason = NULL WHERE SwitchedOffAt IS NOT NULL");
            await using var command = new SqlCommand("SELECT Code, ActiveImportId FROM Sources WHERE ActiveImportId IS NOT NULL", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                active[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        var expected = FeedFiles.Feeds.Append((FeedFiles.ManualCode, ManualSubjects.Count)).ToList();
        await WaitForListsAsync(lists => expected.All(feed => lists.TryGetValue(feed.Item1, out var list)
                && list.GetProperty("state").GetString() == "Current"
                && list.GetProperty("version").ValueKind == JsonValueKind.Number && list.GetProperty("version").GetInt32() == active[feed.Item1]
                && list.GetProperty("recordCount").GetInt32() == feed.Item2
                && (!list.TryGetProperty("held", out var held) || held.ValueKind == JsonValueKind.Null))
            && lists.Values.All(list => list.GetProperty("state").GetString() is "Current" or "NotImported"));
        await RemoveAllowListEntriesAsync();
    }

    /// <summary>Waits (up to 60 s: the API reloads every few seconds in a conformance run) until <c>/api/v1/sources</c>,
    /// by list code, satisfies <paramref name="condition"/>.</summary>
    public static async Task<Dictionary<string, JsonElement>> WaitForListsAsync(Func<Dictionary<string, JsonElement>, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        Dictionary<string, JsonElement> lists = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            using var response = await Http.GetAsync(new Uri("api/v1/sources", UriKind.Relative));
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                lists = body.GetProperty("lists").EnumerateArray().ToDictionary(list => list.GetProperty("code").GetString()!);
                if (condition(lists))
                {
                    return lists;
                }
            }

            await Task.Delay(500);
        }

        throw new XunitException($"The API didn't reach the expected state within 60 s. Lists now: {string.Join(", ", lists.Select(pair => $"{pair.Key} {pair.Value.GetProperty("state")}"))}");
    }

    private static async Task RemoveAllowListEntriesAsync()
    {
        var entries = await AdminAsync(HttpMethod.Get, "api/v1/admin/allow-list", body: null);
        Assert.Equal(System.Net.HttpStatusCode.OK, entries.StatusCode);
        foreach (var entry in (await entries.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("removedAt").ValueKind == JsonValueKind.Null))
        {
            using var removed = await AdminAsync(HttpMethod.Post, $"api/v1/admin/allow-list/{entry.GetProperty("id").GetInt32()}/remove", body: null);
            Assert.Equal(System.Net.HttpStatusCode.OK, removed.StatusCode);
        }
    }

    /// <summary>A feed's path as the CLI sees it, quoted if it has a space.</summary>
    public static string Quote(string value)
    {
        if (value.StartsWith(Settings.Feeds, StringComparison.Ordinal) && Settings.CliFeeds != Settings.Feeds)
        {
            value = Settings.CliFeeds.TrimEnd('/') + "/" + value[Settings.Feeds.Length..].TrimStart('/', '\\').Replace('\\', '/');
        }

        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    /// <summary>Runs the implementation's CLI with <paramref name="arguments"/>; its standard output and error together.</summary>
    public static async Task<CliResult> CliAsync(string arguments)
    {
        var parts = Split(Settings.Cli + " " + arguments);
        var start = new ProcessStartInfo(parts[0]) { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var part in parts.Skip(1))
        {
            start.ArgumentList.Add(part);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {parts[0]}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await output + await error);
    }

    /// <summary>Splits a command line on spaces, keeping "quoted parts" whole.</summary>
    public static List<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    public static async Task<SqlConnection> OpenSqlAsync()
    {
        var connection = new SqlConnection(Settings.Sql);
        await connection.OpenAsync();
        return connection;
    }

    public static async Task<int> ExecuteAsync(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<JsonElement> GetAsync(string path)
    {
        using var response = await Http.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<HttpResponseMessage> PostAsync(string path, object body) =>
        await Http.PostAsync(new Uri(path, UriKind.Relative), JsonContent.Create(body, options: Json));

    /// <summary>A screening (the reference added if missing); asserts a 200.</summary>
    public static async Task<JsonElement> ScreenAsync(object body)
    {
        var json = JsonSerializer.SerializeToNode(body, Json)!.AsObject();
        json.TryAdd("reference", "conformance-1");
        using var response = await PostAsync("api/v1/screenings", json);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Screening: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The stream endpoint: NDJSON in, every line out.</summary>
    public static async Task<List<JsonElement>> StreamAsync(IEnumerable<object> records)
    {
        var ndjson = string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, Json))) + "\n";
        using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        using var response = await Http.PostAsync(new Uri("api/v1/screenings/stream", UriKind.Relative), content);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Stream: {(int)response.StatusCode} {text}");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    /// <summary>A signed-in user's session: an ID token from the stand-in issuer (<see cref="Issuer"/>), as the sign-in
    /// leaves it in the session cookie.</summary>
    public static string Principal(string name, bool admin) => Issuer.Token(name, admin);

    /// <summary>An administrator's request, from the admin page (its header), as <see cref="AdminName"/>.</summary>
    public static Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body) =>
        SendAsync(method, path, body, Principal(AdminName, admin: true), fromThisSite: true);

    public static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? principal, bool fromThisSite)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (principal is not null)
        {
            request.Headers.Add("Cookie", $"saf-admin={principal}");
        }

        if (fromThisSite)
        {
            request.Headers.Add("X-Requested-By", "sanction-and-forgive-admin");
        }

        return await Http.SendAsync(request);
    }

    /// <summary>The cell for <paramref name="list"/> in the first entry of a screening, or null if there is none.</summary>
    public static JsonElement? Cell(JsonElement result, string list, int entry = 0) =>
        result.GetProperty("entries")[entry].GetProperty("cells").EnumerateArray()
            .Cast<JsonElement?>().FirstOrDefault(cell => cell!.Value.GetProperty("list").GetString() == list);

    public static JsonNode Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!;
}

/// <summary>For tests that print what they compared.</summary>
public abstract class ConformanceTest(ITestOutputHelper output)
{
    protected ITestOutputHelper Output { get; } = output;
}
