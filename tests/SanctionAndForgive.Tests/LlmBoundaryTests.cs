using System.Text.Json;

namespace SanctionAndForgive.Tests;

// Screening must be deterministic: no LLM in the request path, the importer or the scheduled job. This test project
// references Api, Cli, Lists, Matching and Data, so its deps file lists every package any of them pulls in, transitive
// ones included.
public class LlmBoundaryTests
{
    private static readonly string[] LlmPackagePrefixes =
    [
        "Anthropic",
        "Azure.AI.Inference",
        "Azure.AI.OpenAI",
        "Google.Cloud.AIPlatform",
        "LangChain",
        "Microsoft.Extensions.AI",
        "Microsoft.SemanticKernel",
        "Mistral",
        "Mscc.GenerativeAI",
        "OllamaSharp",
        "OpenAI",
    ];

    [Fact]
    public void NoProjectDependsOnAnLlmSdk()
    {
        var depsFile = Path.Combine(AppContext.BaseDirectory, "SanctionAndForgive.Tests.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));

        var packages = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(library => library.Name.Split('/')[0])
            .ToList();

        Assert.Contains("SanctionAndForgive.Matching", packages);
        var llmPackages = packages
            .Where(package => LlmPackagePrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(llmPackages);
    }
}
