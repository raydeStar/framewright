using System.Text;
using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class CodexDiagnosticsTests
{
    [Fact]
    public async Task RuntimeSendsPromptBytesAsBomlessUtf8OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:Codex:Executable"] = executable
        }).Build();
        var runtime = new CodexRuntime(configuration);
        const string prompt = "Frastown · café awning — 布";
        const string readBytes = "$bytes = [IO.MemoryStream]::new(); "
            + "[Console]::OpenStandardInput().CopyTo($bytes); "
            + "[Console]::Write([Convert]::ToBase64String($bytes.ToArray()))";

        var result = await runtime.RunAsync(["-NoProfile", "-NonInteractive", "-Command", readBytes],
            Path.GetTempPath(), TimeSpan.FromSeconds(15), CancellationToken.None, prompt);

        Assert.True(result.Started, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(prompt), Convert.FromBase64String(result.StandardOutput));
    }

    [Fact]
    public void FailureReportsCodexErrorMessageInsteadOfItsEchoedBannerAndPrompt()
    {
        var output = string.Join('\n',
            "2026-10-29T22:59:46Z ERROR codex_models_manager::cache: failed to load models cache",
            "OpenAI Codex v0.145.0",
            "--------",
            "model: gpt-6-astra",
            "user",
            "$imagegen " + new string('x', 4000),
            "ERROR: {\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-6-astra' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again.\"}}",
            "ERROR: {\"type\":\"error\",\"status\":400,\"error\":{\"type\":\"invalid_request_error\",\"message\":\"The 'gpt-6-astra' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again.\"}}");

        Assert.Equal(
            "The 'gpt-6-astra' model requires a newer version of Codex. Please upgrade to the latest app or CLI and try again.",
            CodexImageGenerationAdapter.Diagnose(output));
    }

    [Fact]
    public void FailureWithoutErrorLinesKeepsTheTailWhereTheCauseIs()
    {
        var output = new string('a', 3000) + " the actual cause";

        var diagnosis = CodexImageGenerationAdapter.Diagnose(output);

        Assert.StartsWith("…", diagnosis);
        Assert.EndsWith("the actual cause", diagnosis);
    }
}
