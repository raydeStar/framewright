using System.Diagnostics;
using System.Text;

namespace StoryboardStudio.Api.Services;

public sealed record CodexRuntimeStatus(bool Installed, bool Authenticated, string Detail);
public sealed record CodexProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError);

public interface ICodexRuntime
{
    CodexRuntimeStatus Inspect();
    Task<CodexProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null);
}

/// <summary>Runs the official Codex CLI under its own ChatGPT login.</summary>
public sealed class CodexRuntime(IConfiguration configuration) : ICodexRuntime
{
    private readonly object statusLock = new();
    private CodexRuntimeStatus? cachedStatus;
    private DateTimeOffset cachedAt;

    public CodexRuntimeStatus Inspect()
    {
        lock (statusLock)
        {
            if (cachedStatus is not null && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromSeconds(30)) return cachedStatus;
            var command = ResolveCommand();
            try
            {
                var result = Run(command, [.. command.PrefixArguments, "login", "status"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(8));
                var output = $"{result.StandardOutput}\n{result.StandardError}".Trim();
                var authenticated = result.Started && result.ExitCode == 0 && output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
                cachedStatus = new(result.Started, authenticated,
                    authenticated ? output : result.Started ? "Codex is installed, but ChatGPT sign-in is required." : "Codex CLI was not found.");
            }
            catch { cachedStatus = new(false, false, "Codex CLI was not found."); }
            cachedAt = DateTimeOffset.UtcNow;
            return cachedStatus;
        }
    }

    public async Task<CodexProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        var command = ResolveCommand();
        try
        {
            var startInfo = CreateStartInfo(command, arguments, workingDirectory);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return new(false, -1, "", "Codex could not be started.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token);
                    await process.StandardInput.FlushAsync(timeoutSource.Token);
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            return new(true, process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, -1, "", ex.Message); }
    }

    private static CodexProcessResult Run(CodexCommand command, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var startInfo = CreateStartInfo(command, arguments, workingDirectory);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new(false, -1, "", "Codex could not be started.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return new(true, -1, "", "Codex login status timed out.");
        }
        Task.WaitAll(stdout, stderr);
        return new(true, process.ExitCode, stdout.Result, stderr.Result);
    }

    private static ProcessStartInfo CreateStartInfo(CodexCommand command, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // See RunProcessAsync in IntegrationDiscoveryService: an inherited
            // stdin that never closes lets the CLI block indefinitely. Callers
            // close this pipe immediately after starting the process.
            RedirectStandardInput = true,
            // Redirected streams default to the console codepage on Windows, which
            // corrupts the CLI's UTF-8 output. See RunProcessAsync for the detail.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        IntegrationDiscoveryService.SanitizeChildEnvironment(startInfo.Environment);
        foreach (var prefix in command.PrefixArguments) startInfo.ArgumentList.Add(prefix);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private CodexCommand ResolveCommand()
    {
        var configured = configuration["Integrations:Codex:Executable"];
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured, []);
        if (OperatingSystem.IsWindows())
        {
            var npmCli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(npmCli)) return new("node", [npmCli]);
            var executable = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, "codex.exe")).FirstOrDefault(File.Exists);
            if (executable is not null) return new(executable, []);
        }
        return new("codex", []);
    }

    private sealed record CodexCommand(string Executable, IReadOnlyList<string> PrefixArguments);
}
