namespace SanctionAndForgive.Cli;

/// <summary>The image's commit (App__Version, baked in by the Dockerfile), or "dev" for a local build.</summary>
internal static class AppVersion
{
    public static string Current { get; } =
        Environment.GetEnvironmentVariable("App__Version") is { Length: > 0 } version ? version : "dev";
}
