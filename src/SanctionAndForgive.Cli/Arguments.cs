namespace SanctionAndForgive.Cli;

/// <summary><c>--name value</c> options, and <c>--force</c>, the only flag. An option without a value is an error,
/// never silently ignored.</summary>
internal sealed class Arguments
{
    private static readonly HashSet<string> Flags = ["force"];
    private static readonly HashSet<string> Known = ["source", "file", "keep", "force", "code", "name", "external-id", "count"];
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    public static bool TryParse(string[] args, out Arguments parsed, out string? error)
    {
        parsed = new Arguments();
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !Known.Contains(args[i][2..]))
            {
                error = $"Unknown option {args[i]}.";
                return false;
            }

            var name = args[i][2..];
            if (Flags.Contains(name))
            {
                parsed.values[name] = null;
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{args[i]} needs a value.";
                return false;
            }

            parsed.values[name] = args[++i];
        }

        return true;
    }

    public string? Get(string name) => values.GetValueOrDefault(name);

    public bool Has(string name) => values.ContainsKey(name);
}
