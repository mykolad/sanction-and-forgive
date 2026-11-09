using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SanctionAndForgive.Conformance.Harness;
using Xunit.Sdk;

namespace SanctionAndForgive.Conformance.Golden;

/// <summary>Golden files: what the reference implementation (.NET) answered, normalised, in tests/conformance/golden.
/// Another implementation must answer the same, field for field. <c>CONFORMANCE_UPDATE_GOLDEN=1</c> rewrites them
/// (from the reference implementation only, after a deliberate change: the diff is the review).</summary>
public static partial class GoldenFiles
{
    /// <summary>Values that differ from run to run, or by implementation detail rather than behaviour: times, import
    /// ids (database identities), the matcher and allow-list version hashes, the history (it grows with every run).</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal)
    {
        "asOf", "version", "matcherVersion", "allowListVersion", "lastCheckedAt", "updatedAt", "now", "startedAt",
        "completedAt", "downloadedAt", "importId", "history", "historyCount",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A copy of <paramref name="node"/> with volatile values replaced by "*" and times in text by "&lt;time&gt;".</summary>
    public static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key,
            Volatile.Contains(pair.Key) && pair.Value is not null ? JsonValue.Create("*") : Normalize(pair.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Normalize)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Times().Replace(text, "<time>")),
        null => null,
        _ => node.DeepClone(),
    };

    // "7 Nov 2026 03:30 UTC" in summaries and reasons.
    [GeneratedRegex(@"\b\d{1,2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2} UTC\b")]
    private static partial Regex Times();

    /// <summary>Compares <paramref name="actual"/> (already normalised, one item per line) with the golden file
    /// <paramref name="name"/>, or writes it in update mode. Reports every differing item (up to 25) by its key.</summary>
    public static void Match(string name, IReadOnlyList<JsonNode?> actual, Func<JsonNode?, string> key)
    {
        var path = Path.Combine(Settings.GoldenDirectory, name);
        var lines = actual.Select(item => item?.ToJsonString(Compact) ?? "null").ToList();
        if (Settings.UpdateGolden)
        {
            File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"No golden file {name}: generate it from the reference implementation (CONFORMANCE_UPDATE_GOLDEN=1).");
        var expected = File.ReadAllLines(path, Encoding.UTF8).Where(line => line.Length > 0).ToList();
        var differences = new List<string>();
        for (var i = 0; i < Math.Max(expected.Count, lines.Count); i++)
        {
            var want = i < expected.Count ? JsonNode.Parse(expected[i]) : null;
            var got = i < lines.Count ? actual[i] : null;
            if (!JsonNode.DeepEquals(want, got))
            {
                differences.Add($"{key(want ?? got)}: {Difference(want, got, "$")}");
            }
        }

        if (differences.Count > 0)
        {
            throw new XunitException($"{differences.Count} of {Math.Max(expected.Count, lines.Count)} differ from {name}:{Environment.NewLine}"
                + string.Join(Environment.NewLine, differences.Take(25)));
        }
    }

    /// <summary>Compares one JSON document (pretty-printed in the file).</summary>
    public static void MatchDocument(string name, JsonNode? actual)
    {
        var path = Path.Combine(Settings.GoldenDirectory, name);
        if (Settings.UpdateGolden)
        {
            File.WriteAllText(path, actual?.ToJsonString(Indented) + "\n", new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"No golden file {name}: generate it from the reference implementation (CONFORMANCE_UPDATE_GOLDEN=1).");
        var expected = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
        if (!JsonNode.DeepEquals(expected, actual))
        {
            throw new XunitException($"{name} differs: {Difference(expected, actual, "$")}");
        }
    }

    /// <summary>The first place two documents differ, as a JSON path with both values.</summary>
    public static string Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject want, JsonObject got):
                foreach (var key in want.Select(pair => pair.Key).Union(got.Select(pair => pair.Key)))
                {
                    if (!got.ContainsKey(key))
                    {
                        return $"{path}.{key} missing";
                    }

                    if (!want.ContainsKey(key))
                    {
                        return $"{path}.{key} unexpected ({Short(got[key])})";
                    }

                    if (!JsonNode.DeepEquals(want[key], got[key]))
                    {
                        return Difference(want[key], got[key], $"{path}.{key}");
                    }
                }

                return path;
            case (JsonArray want, JsonArray got):
                for (var i = 0; i < Math.Min(want.Count, got.Count); i++)
                {
                    if (!JsonNode.DeepEquals(want[i], got[i]))
                    {
                        return Difference(want[i], got[i], $"{path}[{i}]");
                    }
                }

                return $"{path}: {want.Count} items expected, {got.Count} found";
            default:
                return $"{path}: expected {Short(expected)}, found {Short(actual)}";
        }
    }

    private static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString(Compact) ?? "null";
        return text.Length > 160 ? text[..160] + "…" : text;
    }
}
