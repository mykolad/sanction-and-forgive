using System.Diagnostics;
using System.Globalization;
using SanctionAndForgive.Lists;

namespace SanctionAndForgive.Cli;

/// <summary><c>probe</c>: downloads every feed (or reads a file), parses it and reports what it holds. Writes
/// nothing anywhere except, with <c>--keep</c>, the downloaded files. Exit code 1 if an active feed failed.</summary>
internal sealed class ProbeCommand(FeedDownloader downloader, string euToken, TextWriter output)
{
    public async Task<int> RunAsync(IReadOnlyList<ListSource> sources, string? file, string? keepDirectory, CancellationToken cancellationToken)
    {
        var failed = false;
        output.WriteLine("source     status        bytes  root                   subjects  indiv  entity  unknown   names   weak  cyrillic  other  addresses  seconds  result");
        foreach (var source in sources)
        {
            var row = await ProbeAsync(source, file, keepDirectory, cancellationToken);
            output.WriteLine(row.Line);
            failed |= source.Status == FeedStatus.Active && !row.Ok;
        }

        return failed ? 1 : 0;
    }

    private async Task<(string Line, bool Ok)> ProbeAsync(ListSource source, string? file, string? keepDirectory, CancellationToken cancellationToken)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{source.Code,-10} {source.Status,-12}");
        if (source.CreateParser is null)
        {
            return ($"{prefix} {"-",7}  no parser yet: needs access (docs/sources.md)", false);
        }

        if (file is null && source.Url is null)
        {
            return ($"{prefix} {"-",7}  no URL: pass --file", false);
        }

        var clock = Stopwatch.StartNew();
        string? path = file;
        try
        {
            path ??= await downloader.DownloadAsync(new Uri(source.Url!.Replace("{token}", euToken, StringComparison.Ordinal)), cancellationToken);
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
                File.Copy(path, Path.Combine(keepDirectory, source.Code + (root == "{json}" ? ".json" : ".xml")), overwrite: true);
            }

            var ok = statistics.Subjects > 0;
            return (string.Create(CultureInfo.InvariantCulture,
                $"{prefix} {bytes,12:N0}  {root,-20} {statistics.Subjects,9:N0} {statistics.Individuals,6:N0} {statistics.Entities,7:N0} {statistics.Unknown,8:N0} {statistics.Names,7:N0} {statistics.WeakNames,6:N0} {statistics.CyrillicNames,9:N0} {statistics.OtherScriptNames,6:N0} {statistics.Addresses,10:N0} {clock.Elapsed.TotalSeconds,8:N1}  {(ok ? "parsed" : "parsed, but no subjects")}"
                + $" | chars: names {statistics.NameCharacters:N0}, addresses {statistics.AddressCharacters:N0}, other {statistics.OtherCharacters:N0}"), ok);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException or ListFormatException or System.Xml.XmlException or System.Text.Json.JsonException)
        {
            return ($"{prefix} {"-",7}  FAILED after {clock.Elapsed.TotalSeconds:N1} s: {exception.GetType().Name}: {exception.Message}", false);
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
