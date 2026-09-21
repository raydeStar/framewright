using System.Diagnostics;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>What the Reference Asset Compiler can do on this workstation right now.</summary>
public sealed record CompilerCapabilities(
    bool Installed,
    bool Commissioned,
    string? Version,
    string? Checkout,
    string? Blender,
    IReadOnlyList<CompilerStage> Stages,
    string? Detail = null,
    string? StudioTree = null)
{
    public bool CanRun(string stage) =>
        Installed && Stages.Any(candidate => candidate.Stage == stage && candidate.Available);

    public static CompilerCapabilities Absent(string detail) =>
        new(false, false, null, null, null, [], detail);
}

public sealed record CompilerStage(
    string Stage, string Runner, string Summary, string Produces, bool Available, string[] Missing,
    CompilerSize[]? Sizes = null, string OutputSuffix = ".glb");

/// <summary>
/// One size a stage will accept, as the compiler describes it. The vocabulary
/// and the metres behind it belong to the compiler; this studio offers what it
/// is told and keeps no list of its own.
/// </summary>
public sealed record CompilerSize(string Size, string Description, double Metres);

/// <summary>What one stage run produced, or why it did not.</summary>
public sealed record CompilerStageRun(
    bool Ok, string Stage, int ExitCode, double Seconds,
    string? PayloadPath, string? ReceiptJson, string? Error, IReadOnlyList<string> Log);

/// <summary>
/// The one place this studio talks to the Reference Asset Compiler.
///
/// The compiler owns geometry, rigs and clips; this studio owns the library,
/// the scenes and the review. So nothing here re-derives a verdict: it names a
/// stage, waits, and reads the receipt the compiler wrote. Everything about
/// where the compiler lives, which Blender it uses, and whether the route is
/// commissioned at all stays behind this interface, which is also what lets the
/// application be proved end to end against controlled output rather than a GPU.
/// </summary>
public interface ICompilerGateway
{
    Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken);

    Task<CompilerStageRun> RunStageAsync(
        string stage, string sourcePath, string outputPath, string reportPath,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? options = null);
}

/// <summary>
/// Drives the compiler's own `rac run-stage` command, so this studio binds to a
/// stage name rather than to a file layout it does not own.
/// </summary>
public sealed class CompilerGateway(IConfiguration configuration, TimeProvider timeProvider) : ICompilerGateway
{
    private const string Section = "Integrations:ReferenceAssetCompiler";

    private string Executable => configuration.GetValue($"{Section}:Executable", "rac") ?? "rac";
    private string? Checkout => Trimmed(configuration.GetValue<string>($"{Section}:CheckoutPath"));
    private string? Blender => Trimmed(configuration.GetValue<string>($"{Section}:BlenderPath"));

    /// <summary>
    /// The studio tree holding the geometry weights and their environment. It
    /// is a separate install from the compiler checkout, and most workstations
    /// have the one without the other, which is exactly why the compiler is
    /// told where it is rather than left to find it. Unset is an ordinary
    /// answer: the compiler then reports geometry as unavailable, by name.
    /// </summary>
    private string? StudioTree => Trimmed(configuration.GetValue<string>($"{Section}:StudioTreePath"));

    /// <summary>
    /// False until the artist commissions the route, exactly as provider
    /// submission is. An uncommissioned route still reports what it could do.
    /// </summary>
    private bool Commissioned => configuration.GetValue($"{Section}:SubmissionEnabled", false);

    private TimeSpan Timeout => TimeSpan.FromMinutes(
        Math.Clamp(configuration.GetValue($"{Section}:StageTimeoutMinutes", 60), 1, 600));

    public async Task<CompilerCapabilities> DescribeAsync(CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "run-stage", "--list" };
        if (Checkout is { } checkout) { arguments.Add("--repo-root"); arguments.Add(checkout); }
        if (Blender is { } blender) { arguments.Add("--blender"); arguments.Add(blender); }
        if (StudioTree is { } studio) { arguments.Add("--legacy-root"); arguments.Add(studio); }

        var run = await RunAsync(arguments, TimeSpan.FromSeconds(60), cancellationToken);
        if (!run.Started)
            return CompilerCapabilities.Absent(
                $"The Reference Asset Compiler was not found. Configure {Section}:Executable, or install it with pip.");
        if (run.ExitCode != 0)
        {
            // An installed compiler that predates run-stage is a version skew,
            // not a missing tool, and saying so is the difference between
            // "install it" and "update it".
            var complaint = (run.StandardError ?? "") + (run.StandardOutput ?? "");
            if (complaint.Contains("invalid choice", StringComparison.OrdinalIgnoreCase)
                && complaint.Contains("run-stage", StringComparison.OrdinalIgnoreCase))
                return CompilerCapabilities.Absent(
                    "The installed Reference Asset Compiler is older than this studio needs: it has no "
                    + "run-stage command. Update it from a checkout with pip install -e .");
            return CompilerCapabilities.Absent(
                $"The compiler refused to describe itself: {Tail(run.StandardError) ?? $"exit code {run.ExitCode}"}");
        }

        try
        {
            using var document = JsonDocument.Parse(run.StandardOutput);
            var root = document.RootElement;
            var stages = root.TryGetProperty("stages", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Select(stage => new CompilerStage(
                    stage.GetProperty("stage").GetString() ?? "",
                    stage.TryGetProperty("runner", out var runner) ? runner.GetString() ?? "" : "",
                    stage.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "",
                    stage.TryGetProperty("produces", out var produces) ? produces.GetString() ?? "" : "",
                    stage.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True,
                    stage.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array
                        ? [.. missing.EnumerateArray().Select(item => item.GetString() ?? "")]
                        : [],
                    stage.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array
                        ? [.. sizes.EnumerateArray().Select(size => new CompilerSize(
                            Text(size, "size") ?? "", Text(size, "description") ?? "",
                            size.TryGetProperty("metres", out var metres) ? metres.GetDouble() : 0))]
                        : null,
                    Text(stage, "output_suffix") ?? ".glb")).ToArray()
                : [];

            return new CompilerCapabilities(
                Installed: true,
                Commissioned: Commissioned,
                Version: await VersionAsync(cancellationToken),
                Checkout: Text(root, "checkout"),
                Blender: Text(root, "blender"),
                Stages: stages,
                StudioTree: Text(root, "legacy_root"));
        }
        catch (JsonException)
        {
            return CompilerCapabilities.Absent("The compiler's capability report could not be read as JSON.");
        }
    }

    public async Task<CompilerStageRun> RunStageAsync(
        string stage, string sourcePath, string outputPath, string reportPath,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? options = null)
    {
        var arguments = new List<string>
        {
            "run-stage", stage,
            "--source", sourcePath, "--output", outputPath, "--report", reportPath,
        };
        // Options are passed through by name rather than interpreted here. The
        // compiler owns what a stage accepts and what each setting means; a
        // studio that second-guessed either would be keeping a second copy of
        // somebody else's contract.
        foreach (var (name, value) in options ?? new Dictionary<string, string>())
        {
            arguments.Add("--" + name);
            // An empty value is a switch rather than a setting. Passing "" as
            // its argument would make the compiler read the next flag as this
            // one's value, which fails in a way that names the wrong option.
            if (value.Length > 0) arguments.Add(value);
        }
        if (Checkout is { } checkout) { arguments.Add("--repo-root"); arguments.Add(checkout); }
        if (Blender is { } blender) { arguments.Add("--blender"); arguments.Add(blender); }
        if (StudioTree is { } studio) { arguments.Add("--legacy-root"); arguments.Add(studio); }

        var started = timeProvider.GetTimestamp();
        var run = await RunAsync(arguments, Timeout, cancellationToken);
        var seconds = Math.Round(timeProvider.GetElapsedTime(started).TotalSeconds, 3);
        if (!run.Started)
            return new CompilerStageRun(false, stage, -1, seconds, null, null,
                "The Reference Asset Compiler could not be started.", []);

        // The compiler answers in one JSON payload carrying its own receipt, so
        // a caller reads one answer rather than an answer and then a file.
        string? receipt = null;
        string? error = run.ExitCode == 0 ? null : Tail(run.StandardError) ?? $"The stage exited with code {run.ExitCode}.";
        try
        {
            using var document = JsonDocument.Parse(run.StandardOutput);
            if (document.RootElement.TryGetProperty("receipt", out var payload))
                receipt = payload.GetRawText();
            if (document.RootElement.TryGetProperty("error", out var reported))
                error ??= reported.GetString();
        }
        catch (JsonException)
        {
            error ??= "The compiler's stage report could not be read as JSON.";
        }

        var ok = run.ExitCode == 0 && receipt is not null;
        return new CompilerStageRun(ok, stage, run.ExitCode, seconds,
            ok ? outputPath : null, receipt, ok ? null : error ?? "The stage produced no receipt.",
            [.. Lines(run.StandardOutput).TakeLast(10), .. Lines(run.StandardError).TakeLast(10)]);
    }

    private async Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync(["--version"], TimeSpan.FromSeconds(30), cancellationToken);
        return run.Started && run.ExitCode == 0 ? run.StandardOutput.Trim() : null;
    }

    private async Task<(bool Started, int ExitCode, string StandardOutput, string StandardError)> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return (false, -1, "", "");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, -1, "", error.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var failure = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A stage that overran is left alone rather than half-killed: the
            // caller is told it did not finish, and the compiler's own receipt
            // stays the record of what happened.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (true, -2, await output, $"The stage did not finish within {timeout.TotalMinutes:0} minutes.");
        }
        return (true, process.ExitCode, await output, await failure);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] Lines(string? text) =>
        (text ?? "").Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();

    private static string? Tail(string? text)
    {
        var lines = Lines(text);
        return lines.Length == 0 ? null : string.Join(" ", lines.TakeLast(3));
    }
}
