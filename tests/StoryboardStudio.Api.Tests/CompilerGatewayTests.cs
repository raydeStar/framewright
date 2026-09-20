using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The studio's one door to the Reference Asset Compiler. A missing compiler,
/// an outdated one, and one that answers nonsense are three different answers,
/// and an artist deserves to be told which. Nothing here runs a stage: this is
/// the capability question, which is the one that has to be answered before any
/// work is queued.
/// </summary>
public sealed class CompilerGatewayTests
{
    /// <summary>
    /// A stand-in compiler, so the gateway's own behaviour is tested rather than
    /// whichever compiler happens to be installed on the machine running this.
    /// </summary>
    private sealed class StubCompiler : IDisposable
    {
        public required string Path { get; init; }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }

    private static StubCompiler Stub(params string[] lines)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"rac-stub-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\n" + string.Join("\n", lines) + "\n");
        return new StubCompiler { Path = path };
    }

    private static CompilerGateway Gateway(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>($"Integrations:ReferenceAssetCompiler:{setting.Key}", setting.Value)))
            .Build();
        return new CompilerGateway(configuration, TimeProvider.System);
    }

    [Fact]
    public async Task ACompilerThatIsNotInstalledIsNamedAsMissingRatherThanAsBroken()
    {
        var gateway = Gateway(("Executable", "rac-that-is-not-installed-anywhere"));

        var capabilities = await gateway.DescribeAsync(CancellationToken.None);

        Assert.False(capabilities.Installed);
        Assert.False(capabilities.Commissioned);
        Assert.Empty(capabilities.Stages);
        Assert.Contains("not found", capabilities.Detail, StringComparison.OrdinalIgnoreCase);
        // A missing tool cannot run anything, and says so rather than throwing.
        Assert.False(capabilities.CanRun("browser-payload"));
    }

    [Fact]
    public async Task ACompilerOlderThanThisStudioIsToldToUpdateRatherThanToInstall()
    {
        // A compiler that predates run-stage answers exactly like this, because
        // that is what argparse says about a subcommand it has never heard of.
        // An installed-but-old tool is a different problem from a missing one,
        // and the difference is "update it" rather than "install it".
        using var outdated = Stub(
            "echo usage: rac [-h] {new,plan,promote,audit} ... 1>&2",
            "echo rac: error: argument command: invalid choice: 'run-stage' 1>&2",
            "exit /b 2");

        var capabilities = await Gateway(("Executable", outdated.Path)).DescribeAsync(CancellationToken.None);

        Assert.False(capabilities.Installed);
        Assert.False(capabilities.CanRun("browser-payload"));
        Assert.Contains("older", capabilities.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run-stage", capabilities.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompilerThatAnswersNonsenseIsNotTreatedAsCapable()
    {
        using var confused = Stub("echo not json at all");

        var capabilities = await Gateway(("Executable", confused.Path)).DescribeAsync(CancellationToken.None);

        Assert.False(capabilities.Installed);
        Assert.Empty(capabilities.Stages);
        Assert.Contains("JSON", capabilities.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStageThatIsAvailableIsReportedAsRunnable()
    {
        // The capability answer the real compiler gives, so the parsing is
        // exercised rather than assumed.
        using var capable = Stub(
            "echo {\"ok\": true, \"checkout\": \"C:/checkout\", \"blender\": \"C:/blender.exe\", "
            + "\"stages\": [{\"stage\": \"browser-payload\", \"runner\": \"blender\", "
            + "\"summary\": \"Export a browser GLB.\", \"produces\": \"reference-asset-compiler.browser-payload.v1\", "
            + "\"available\": true, \"missing\": []}]}");

        var capabilities = await Gateway(("Executable", capable.Path)).DescribeAsync(CancellationToken.None);

        Assert.True(capabilities.Installed);
        Assert.Equal("C:/checkout", capabilities.Checkout);
        Assert.Equal("C:/blender.exe", capabilities.Blender);
        Assert.True(capabilities.CanRun("browser-payload"));
        Assert.Equal("reference-asset-compiler.browser-payload.v1", capabilities.Stages[0].Produces);
        // Capability is not permission: the route is still uncommissioned.
        Assert.False(capabilities.Commissioned);
    }

    [Fact]
    public async Task AStageThatIsMissingItsToolsIsReportedAsNotRunnable()
    {
        using var unequipped = Stub(
            "echo {\"ok\": true, \"checkout\": null, \"blender\": null, "
            + "\"stages\": [{\"stage\": \"browser-payload\", \"runner\": \"blender\", \"summary\": \"\", "
            + "\"produces\": \"\", \"available\": false, \"missing\": [\"checkout\", \"blender\"]}]}");

        var capabilities = await Gateway(("Executable", unequipped.Path)).DescribeAsync(CancellationToken.None);

        Assert.True(capabilities.Installed);
        Assert.False(capabilities.CanRun("browser-payload"));
        Assert.Equal(["checkout", "blender"], capabilities.Stages[0].Missing);
    }

    [Fact]
    public async Task AnUncommissionedRouteStillReportsWhatItCouldDo()
    {
        // Submission defaults to false, as provider submission does. The
        // capability answer is separate from the permission to use it.
        var gateway = Gateway(("Executable", "rac-that-is-not-installed-anywhere"));
        var capabilities = await gateway.DescribeAsync(CancellationToken.None);

        Assert.False(capabilities.Commissioned);
        Assert.NotNull(capabilities.Detail);
    }

    [Fact]
    public async Task RunningAStageWithoutACompilerFailsWithAReasonAndNoReceipt()
    {
        var gateway = Gateway(("Executable", "rac-that-is-not-installed-anywhere"));

        var run = await gateway.RunStageAsync(
            "browser-payload", "source.fbx", "payload.glb", "report.json",
            CancellationToken.None);

        Assert.False(run.Ok);
        Assert.Null(run.ReceiptJson);
        Assert.Null(run.PayloadPath);
        Assert.False(string.IsNullOrWhiteSpace(run.Error));
    }
}
