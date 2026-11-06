using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SanctionAndForgive.Cli;
using SanctionAndForgive.Data;
using SanctionAndForgive.Lists;

const string Usage = """
    Usage (tools/lists.ps1 wraps these):
      probe        [--source <code>|all] [--file <path>] [--keep <directory>]
      import       --source <code>|all [--file <path>] [--force]
      status
      source-add   --code <code> --name <name>
      add          --source <code> --file <records.csv>
      remove       --source <code> --external-id <id>
      load-inputs  [--file tests/load/inputs.csv] [--count 2000]
      evaluate     [--file docs/matching-evaluation.md]

    Feeds: eu, ofac-sdn, ofac-cons, un, uk, us-export, ch-seco, ca-sema, au-dfat, ua-nsdc, ua-sfms.
    Configuration (environment variables or user secrets): ConnectionStrings:Lists, and Lists:EuToken
    (or LISTS_EU_TOKEN; default: the public token).
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

if (!Arguments.TryParse(args[1..], out var options, out var error))
{
    Console.Error.WriteLine(error + Environment.NewLine + Usage);
    return 2;
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(Arguments).Assembly, optional: true)
    .AddEnvironmentVariables()
    .Build();
var euToken = configuration["Lists:EuToken"] is { Length: > 0 } configured ? configured
    : Environment.GetEnvironmentVariable("LISTS_EU_TOKEN") is { Length: > 0 } fromEnvironment ? fromEnvironment
    : "dG9rZW4tMjAxNw";

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var httpClient = FeedDownloader.CreateHttpClient(AppVersion.Current);
var downloader = new FeedDownloader(httpClient, TimeSpan.FromMinutes(10));

if (args[0] == "probe")
{
    return await RunProbeAsync();
}

if (configuration.GetConnectionString("Lists") is not { Length: > 0 } connectionString)
{
    Console.Error.WriteLine("No connection string: set ConnectionStrings__Lists (tools/lists.ps1 does).");
    return 2;
}

var services = new ServiceCollection()
    .AddListsDatabase(connectionString)
    .AddSingleton(TimeProvider.System)
    .AddScoped<ImportStore>()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var commands = new ListCommands(scope.ServiceProvider.GetRequiredService<ImportStore>(), downloader, euToken, Console.Out);
var token = cancellation.Token;

return args[0] switch
{
    "import" when options.Get("source") is { } source => await commands.ImportAsync(source, options.Get("file"), options.Has("force"), token),
    "status" => await commands.StatusAsync(token),
    "source-add" when options.Get("code") is { } code && options.Get("name") is { } name => await commands.SourceAddAsync(code, name, token),
    "add" when options.Get("source") is { } source && options.Get("file") is { } file => await commands.AddAsync(source, file, token),
    "remove" when options.Get("source") is { } source && options.Get("external-id") is { } id => await commands.RemoveAsync(source, id, token),
    "evaluate" => await new EvaluateCommand(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), SanctionAndForgive.Matching.MatchingOptions.Default, Console.Out)
        .RunAsync(options.Get("file") ?? "docs/matching-evaluation.md", seed: 20261106, token),
    "load-inputs" when int.TryParse(options.Get("count") ?? "2000", System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0 =>
        await new LoadInputsCommand(scope.ServiceProvider.GetRequiredService<ListsDbContext>(), Console.Out).RunAsync(options.Get("file") ?? "tests/load/inputs.csv", count, seed: 20261106, token),
    _ => ShowUsage(),
};

async Task<int> RunProbeAsync()
{
    var code = options.Get("source") ?? "all";
    IReadOnlyList<ListSource> sources;
    if (code == "all")
    {
        if (options.Get("file") is not null)
        {
            Console.Error.WriteLine("--file needs a single --source.");
            return 2;
        }

        sources = ListSources.All;
    }
    else if (ListSources.Find(code) is { } source)
    {
        sources = [source];
    }
    else
    {
        Console.Error.WriteLine($"Unknown source {code}.{Environment.NewLine}{Usage}");
        return 2;
    }

    return await new ProbeCommand(downloader, euToken, Console.Out).RunAsync(sources, options.Get("file"), options.Get("keep"), cancellation.Token);
}

int ShowUsage()
{
    Console.Error.WriteLine($"Missing or unknown arguments for {args[0]}.{Environment.NewLine}{Usage}");
    return 2;
}
