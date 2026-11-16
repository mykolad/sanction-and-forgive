using System.Diagnostics;
using System.Globalization;
using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Cli;

/// <summary><c>probe</c>: downloads every feed (or reads a file), parses it and reports what it holds. Writes
/// nothing anywhere except, with <c>--keep</c>, the downloaded files. With <c>--scripts</c>, a line per feed counts its
/// names by script, and those with Cyrillic letters by alphabet (a .NET measurement, like <c>evaluate</c>). Exit code 1
/// if an active feed failed.</summary>
internal sealed class ProbeCommand(FeedDownloader downloader, string euToken, TextWriter output)
{
    private static readonly string[] KeptExtensions = [".csv", ".ods", ".xlsx", ".json", ".xml"];

    public async Task<int> RunAsync(IReadOnlyList<ListSource> sources, string? file, string? keepDirectory, bool scripts, CancellationToken cancellationToken)
    {
        var failed = false;
        output.WriteLine("source     status        bytes  root                   subjects  indiv  entity  unknown   names   weak  cyrillic  other  addresses  seconds  result");
        foreach (var source in sources)
        {
            var row = await ProbeAsync(source, file, keepDirectory, cancellationToken);
            output.WriteLine(row.Line);
            if (scripts && row.Statistics is { } statistics)
            {
                output.WriteLine($"           scripts: {Counts(statistics.NamesByScript)}");
                if (statistics.CyrillicByAlphabet.Count > 0)
                {
                    output.WriteLine($"           Cyrillic by letters: {Counts(statistics.CyrillicByAlphabet)}");
                }
            }
            failed |= source.Status == FeedStatus.Active && !row.Ok;
        }

        return failed ? 1 : 0;
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        string.Join("; ", counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} {pair.Value:N0}")));

    private async Task<(string Line, bool Ok, FeedStatistics? Statistics)> ProbeAsync(ListSource source, string? file, string? keepDirectory, CancellationToken cancellationToken)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{source.Code,-10} {source.Status,-12}");
        if (source.CreateParser is null)
        {
            return ($"{prefix} {"-",7}  no parser yet: needs access (docs/sources.md)", false, null);
        }

        if (file is null && source.Url is null)
        {
            return ($"{prefix} {"-",7}  no URL: pass --file", false, null);
        }

        var clock = Stopwatch.StartNew();
        string? path = file;
        try
        {
            path ??= await downloader.DownloadAsync(source, euToken, cancellationToken);
            var bytes = new FileInfo(path).Length;
            var root = FeedStatistics.RootElement(path) ?? "?";
            FeedStatistics statistics;
            await using (var stream = File.OpenRead(path))
            {
                statistics = FeedStatistics.Of(source.CreateParser().Parse(stream, cancellationToken));
            }

            if (keepDirectory is not null && file is null)
            {
                Directory.CreateDirectory(keepDirectory);
                // The file's own extension where its address (or the pattern that finds it) names one, else by what it holds.
                var address = source.LinkPattern ?? source.Url!;
                var extension = KeptExtensions.FirstOrDefault(known => address.Contains(known, StringComparison.OrdinalIgnoreCase))
                    ?? (root == "{json}" ? ".json" : root == "?" ? ".html" : ".xml");
                File.Copy(path, Path.Combine(keepDirectory, source.Code + extension), overwrite: true);
            }

            var ok = statistics.Subjects > 0;
            return (string.Create(CultureInfo.InvariantCulture,
                $"{prefix} {bytes,12:N0}  {root,-20} {statistics.Subjects,9:N0} {statistics.Individuals,6:N0} {statistics.Entities,7:N0} {statistics.Unknown,8:N0} {statistics.Names,7:N0} {statistics.WeakNames,6:N0} {statistics.CyrillicNames,9:N0} {statistics.OtherScriptNames,6:N0} {statistics.Addresses,10:N0} {clock.Elapsed.TotalSeconds,8:N1}  {(ok ? "parsed" : "parsed, but no subjects")}"
                + $" | chars: names {statistics.NameCharacters:N0}, addresses {statistics.AddressCharacters:N0}, other {statistics.OtherCharacters:N0}"), ok, statistics);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException or ListFormatException or System.Xml.XmlException or System.Text.Json.JsonException)
        {
            return ($"{prefix} {"-",7}  FAILED after {clock.Elapsed.TotalSeconds:N1} s: {exception.GetType().Name}: {exception.Message}", false, null);
        }
        finally
        {
            if (file is null && path is not null)
            {
                File.Delete(path);
            }
        }
    }
}
