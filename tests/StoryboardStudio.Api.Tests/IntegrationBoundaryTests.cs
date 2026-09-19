using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class IntegrationBoundaryTests
{
    [Fact]
    public void CodexHelpersDoNotInheritTheOpenAIImageProviderKey()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "safe-path",
            ["OPENAI_API_KEY"] = "sk-must-not-cross-boundary"
        };

        IntegrationDiscoveryService.SanitizeChildEnvironment(environment);

        Assert.Equal("safe-path", environment["PATH"]);
        Assert.False(environment.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public void AdvisoryCodexCallsPermitTheReadOnlyContainerWorkspace()
    {
        var arguments = IntegrationDiscoveryService.BuildCodexExecArguments([], "Return JSON only.");

        Assert.Equal("exec", arguments[0]);
        Assert.Contains("--skip-git-repo-check", arguments);
        Assert.Contains("--ephemeral", arguments);
        Assert.Equal("read-only", arguments[Array.IndexOf(arguments.ToArray(), "--sandbox") + 1]);
        Assert.Equal("Return JSON only.", arguments[arguments.Count - 1]);
    }
}
