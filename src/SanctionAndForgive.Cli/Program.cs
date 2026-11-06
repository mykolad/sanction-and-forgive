using SanctionAndForgive.Cli;
using SanctionAndForgive.Lists;

// Commands so far: probe. Imports, manual records and evaluate arrive with their phases.
const string Usage = """
    Usage:
      SanctionAndForgive.Cli probe [--source <code>|all] [--file <path>] [--keep <directory>]

    Sources: eu, ofac-sdn, ofac-cons, un, uk, us-export, ch-seco, ca-sema, au-dfat, ua-nsdc, ua-sfms.
    The EU feed's token comes from LISTS_EU_TOKEN (default: the public token).
    """;

if (args.Length == 0 || args[0] != "probe")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string? sourceCode = null, file = null, keep = null;
for (var i = 1; i < args.Length; i++)
{
    var value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "--source": sourceCode = value; i++; break;
        case "--file": file = value; i++; break;
        case "--keep": keep = value; i++; break;
        default:
            Console.Error.WriteLine($"Unknown option {args[i]}.{Environment.NewLine}{Usage}");
            return 2;
    }
}

IReadOnlyList<ListSource> sources;
if (sourceCode is null or "all")
{
    if (file is not null)
    {
        Console.Error.WriteLine("--file needs a single --source.");
        return 2;
    }

    sources = ListSources.All;
}
else if (ListSources.Find(sourceCode) is { } source)
{
    sources = [source];
}
else
{
    Console.Error.WriteLine($"Unknown source {sourceCode}.{Environment.NewLine}{Usage}");
    return 2;
}

var euToken = Environment.GetEnvironmentVariable("LISTS_EU_TOKEN") is { Length: > 0 } token ? token : "dG9rZW4tMjAxNw";
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var httpClient = FeedDownloader.CreateHttpClient(AppVersion.Current);
var probe = new ProbeCommand(new FeedDownloader(httpClient), euToken, Console.Out);
return await probe.RunAsync(sources, file, keep, cancellation.Token);
