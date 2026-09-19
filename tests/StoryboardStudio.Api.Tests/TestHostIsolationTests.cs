using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class TestHostIsolationTests(StudioApiFactory factory) : IClassFixture<StudioApiFactory>
{
    [Fact]
    public void OrdinaryTestHostDisablesExternalCodexAndWindowsEventLog()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var runtime = factory.Services.GetRequiredService<ICodexRuntime>();
        var loggingProviders = factory.Services.GetServices<ILoggerProvider>().ToArray();

        Assert.Equal(factory.DisabledCodexExecutable, configuration["Integrations:Codex:Executable"]);
        Assert.False(File.Exists(factory.DisabledCodexExecutable));
        Assert.IsType<UnavailableTestCodexRuntime>(runtime);
        Assert.DoesNotContain(loggingProviders,
            provider => provider.GetType().Name.Contains("EventLog", StringComparison.OrdinalIgnoreCase));
    }
}
