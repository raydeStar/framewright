using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

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
            foreach (var leftover in new[] { Path, Path + ".args" })
            {
                try { File.Delete(leftover); } catch (IOException) { }
            }
        }
    }

    private static StubCompiler Stub(params string[] lines)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"rac-stub-{Guid.NewGuid():N}.cmd");
        // CRLF: a batch file is read line by line by cmd itself, and one
        // written with bare newlines runs some lines and silently mangles
        // others — a redirect on the first line is where it shows.
        File.WriteAllText(path, "@echo off\r\n" + string.Join("\r\n", lines) + "\r\n");
        return new StubCompiler { Path = path };
    }

    /// <summary>A Blender that has already been found, or not, without looking.</summary>
    private sealed class FixedBlenderLocator(BlenderInstallSummary answer) : IBlenderLocator
    {
        public Task<BlenderInstallSummary> LocateAsync(CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private static readonly BlenderInstallSummary NoBlender =
        new(null, null, "none", "not found", BlenderLocator.OverrideAdvice, "No Blender was found.");

    private static CompilerGateway Gateway(params (string Key, string Value)[] settings) =>
        Gateway(NoBlender, settings);

    private static CompilerGateway Gateway(BlenderInstallSummary blender, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>($"Integrations:ReferenceAssetCompiler:{setting.Key}", setting.Value)))
            .Build();
        return new CompilerGateway(configuration, TimeProvider.System, new FixedBlenderLocator(blender));
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

    /// <summary>
    /// The geometry route needs weights and an environment that are a separate
    /// install from the compiler checkout. The compiler is told where they are
    /// rather than left to find them, because a guessed tree is a different set
    /// of weights from the one an asset was gated with, and nothing in a
    /// receipt would show the difference.
    /// </summary>
    [Fact]
    public async Task TheStudioTreeIsPassedToTheCompilerWhenOneIsConfigured()
    {
        // The stub records its arguments beside itself rather than reporting
        // them in its JSON: they contain quotes and backslashes, which is
        // exactly what makes smuggling them through the answer unreliable.
        // Appended, not overwritten: describing also asks the compiler its
        // version, and the last call would otherwise be the only one recorded.
        using var recording = Stub(
            "echo %* >> \"%~f0.args\"",
            "echo {\"ok\":true,\"stages\":[]}");

        var capabilities = await Gateway(
            ("Executable", recording.Path),
            ("StudioTreePath", @"C:\studio\tree")).DescribeAsync(CancellationToken.None);

        Assert.True(capabilities.Installed);
        var passed = File.ReadAllText(recording.Path + ".args");
        Assert.Contains("--legacy-root", passed, StringComparison.Ordinal);
        Assert.Contains(@"C:\studio\tree", passed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningAStageAlsoNamesTheStudioTree()
    {
        using var recording = Stub(
            "echo %* >> \"%~f0.args\"",
            "echo {\"ok\":true,\"stage\":\"geometry\",\"exit_code\":0,\"seconds\":1}");

        var run = await Gateway(
            ("Executable", recording.Path),
            ("StudioTreePath", @"C:\studio\tree")).RunStageAsync(
                "geometry", "reference.png", "candidate.glb", "receipt.json",
                CancellationToken.None);

        // Describing and running must agree about which tree is in use, or the
        // capability answer describes a machine the run does not happen on.
        // The run itself is not asserted to have succeeded: this stub writes no
        // receipt, and a run with no receipt is correctly not a success.
        Assert.False(run.Ok);
        var passed = File.ReadAllText(recording.Path + ".args");
        Assert.Contains("--legacy-root", passed, StringComparison.Ordinal);
        Assert.Contains(@"C:\studio\tree", passed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStudioTreeIsAnOrdinaryAnswerRatherThanAFailure()
    {
        using var listing = Stub(
            "echo {\"ok\":true,\"legacy_root\":null,\"stages\":[" +
            "{\"stage\":\"geometry\",\"runner\":\"powershell\",\"summary\":\"s\",\"produces\":\"p\"," +
            "\"available\":false,\"missing\":[\"legacy-root\"]}]}");

        var capabilities = await Gateway(("Executable", listing.Path)).DescribeAsync(CancellationToken.None);

        // A workstation without the weights is the common case, and it must
        // read as "this machine cannot do that", not as a broken compiler.
        Assert.True(capabilities.Installed);
        Assert.Null(capabilities.StudioTree);
        Assert.False(capabilities.CanRun("geometry"));
        var geometry = Assert.Single(capabilities.Stages);
        Assert.Equal(["legacy-root"], geometry.Missing);
    }

    [Fact]
    public async Task AConfiguredStudioTreeIsReportedBackFromTheCompilersOwnAnswer()
    {
        using var listing = Stub(
            "echo {\"ok\":true,\"legacy_root\":\"C:\\\\studio\\\\tree\",\"stages\":[" +
            "{\"stage\":\"geometry\",\"runner\":\"powershell\",\"summary\":\"s\",\"produces\":\"p\"," +
            "\"available\":true,\"missing\":[]}]}");

        var capabilities = await Gateway(("Executable", listing.Path)).DescribeAsync(CancellationToken.None);

        // Reported from the compiler's answer rather than echoed back from this
        // studio's configuration: what matters is the tree it actually used.
        Assert.Equal(@"C:\studio\tree", capabilities.StudioTree);
        Assert.True(capabilities.CanRun("geometry"));
    }

    /// <summary>
    /// The compiler does no searching of its own, so a Blender this studio
    /// found has to reach it the same way a configured one always did: as
    /// --blender with the exact path, on describing and on running alike.
    /// </summary>
    [Fact]
    public async Task AFoundBlenderIsPassedExactlyAsAConfiguredOneWouldBe()
    {
        using var recording = Stub(
            "echo %* >> \"%~f0.args\"",
            "echo {\"ok\":true,\"stages\":[]}");
        var found = new BlenderInstallSummary(@"D:\Games\steamapps\common\Blender\blender.exe", "5.2.2 LTS",
            "steam", "found in your Steam library", BlenderLocator.OverrideAdvice, null);
        var gateway = Gateway(found, ("Executable", recording.Path));

        var capabilities = await gateway.DescribeAsync(CancellationToken.None);
        await gateway.RunStageAsync("review-views", "model.glb", "views", "receipt.json", CancellationToken.None);

        var passed = File.ReadAllLines(recording.Path + ".args");
        const string Flag = @"--blender D:\Games\steamapps\common\Blender\blender.exe";
        Assert.Contains(passed, line => line.Contains("--list", StringComparison.Ordinal)
                                        && line.Contains(Flag, StringComparison.Ordinal));
        Assert.Contains(passed, line => line.Contains("review-views", StringComparison.Ordinal)
                                        && line.Contains(Flag, StringComparison.Ordinal));
        // And readiness can say which one, how it was found, and how to change it.
        Assert.Equal(found, capabilities.BlenderInstall);
    }

    [Fact]
    public async Task NoBlenderMeansTheCompilerIsNotHandedOne()
    {
        using var recording = Stub(
            "echo %* >> \"%~f0.args\"",
            "echo {\"ok\":true,\"stages\":[]}");

        var capabilities = await Gateway(("Executable", recording.Path)).DescribeAsync(CancellationToken.None);

        // Not an empty --blender, which the compiler would read as the next
        // flag's value: no flag at all, and the compiler says what is missing.
        Assert.DoesNotContain("--blender", File.ReadAllText(recording.Path + ".args"), StringComparison.Ordinal);
        Assert.True(capabilities.Installed);
        Assert.Equal("none", capabilities.BlenderInstall?.Source);
    }

    [Fact]
    public async Task AMissingCompilerStillSaysWhichBlenderItWouldHaveBeenGiven()
    {
        var found = new BlenderInstallSummary("/usr/bin/blender", "4.2.3 LTS", "system", "found in /usr/bin",
            BlenderLocator.OverrideAdvice, null);

        var capabilities = await Gateway(found, ("Executable", "rac-that-is-not-installed-anywhere"))
            .DescribeAsync(CancellationToken.None);

        Assert.False(capabilities.Installed);
        Assert.Equal("/usr/bin/blender", capabilities.BlenderInstall?.Executable);
    }
}
