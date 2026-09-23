using System.Text.Json;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace StoryboardStudio.Api.Services;

public sealed record ComfyUiGenerationSetup(string Endpoint, bool ImagesEnabled, bool VideoEnabled, IReadOnlyList<string> Locked);
public sealed record CodexGenerationSetup(bool OneClickImages, IReadOnlyList<string> Locked);
public sealed record GenerationSetupSummary(bool CanManageHere, ComfyUiGenerationSetup ComfyUi, CodexGenerationSetup Codex);
public sealed record GenerationSetupChange(string ComfyUiEndpoint, bool ComfyUiImagesEnabled, bool ComfyUiVideoEnabled, bool CodexOneClickImages);
public sealed record ComfyUiConnectionTestRequest(string Endpoint);
public sealed record ComfyUiConnectionTest(bool Reachable, string Detail);

/// <summary>
/// The generation switches an artist sets from Production setup, kept beside the
/// project database as <c>generation-settings.json</c>.
///
/// Precedence, for every key: a value set by an environment variable or the
/// command line wins and is reported as locked, because whoever launched the
/// service chose it deliberately (the e2e harness pins ComfyUI off this way).
/// Otherwise the artist's saved choice wins over the shipped and local JSON
/// files. Every consumer reads through here, so a switch takes effect for the
/// next dispatch without a restart, and nothing is ever sent until the artist
/// presses Generate.
/// </summary>
public sealed class GenerationSettingsStore(IConfiguration configuration, IWebHostEnvironment environment)
{
    public const string ComfyUiEndpointKey = "Integrations:ComfyUi:Endpoint";
    public const string ComfyUiImagesKey = "Integrations:ComfyUi:SubmissionEnabled";
    public const string ComfyUiVideoKey = "Integrations:ComfyUi:VideoSubmissionEnabled";
    public const string CodexOneClickKey = "Integrations:Codex:NonInteractiveImageEnabled";
    private const string DefaultComfyUiEndpoint = "http://127.0.0.1:8188";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock gate = new();
    private StoredSettings? cached;
    private DateTime cachedStamp;

    private string SettingsPath => Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "generation-settings.json");

    public string ComfyUiEndpoint => ResolveString(ComfyUiEndpointKey, Read().ComfyUiEndpoint) ?? DefaultComfyUiEndpoint;
    public bool ComfyUiImagesEnabled => ResolveBool(ComfyUiImagesKey, Read().ComfyUiImagesEnabled);
    public bool ComfyUiVideoEnabled => ResolveBool(ComfyUiVideoKey, Read().ComfyUiVideoEnabled);
    public bool CodexOneClickImages => ResolveBool(CodexOneClickKey, Read().CodexOneClickImages);

    public GenerationSetupSummary Describe(bool canManageHere)
    {
        var comfyLocked = new List<string>();
        if (IsExternallySet(ComfyUiEndpointKey)) comfyLocked.Add("endpoint");
        if (IsExternallySet(ComfyUiImagesKey)) comfyLocked.Add("imagesEnabled");
        if (IsExternallySet(ComfyUiVideoKey)) comfyLocked.Add("videoEnabled");
        var codexLocked = IsExternallySet(CodexOneClickKey) ? new[] { "oneClickImages" } : [];
        return new(canManageHere,
            new ComfyUiGenerationSetup(ComfyUiEndpoint, ComfyUiImagesEnabled, ComfyUiVideoEnabled, comfyLocked),
            new CodexGenerationSetup(CodexOneClickImages, codexLocked));
    }

    /// <summary>Why an endpoint cannot be used, or null. Shared by save and test.</summary>
    public string? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Trim().Length > 500
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return "Enter the ComfyUI address as a web address, such as http://127.0.0.1:8188.";
        if (!uri.IsLoopback && !configuration.GetValue("Integrations:ComfyUi:AllowRemote", false))
            return "ComfyUI must run on this computer (an address like 127.0.0.1 or localhost). A ComfyUI on another machine has to be allowed in the workstation configuration first.";
        return null;
    }

    public string? Save(GenerationSetupChange change)
    {
        var endpointProblem = ValidateEndpoint(change.ComfyUiEndpoint);
        if (endpointProblem is not null) return endpointProblem;
        var next = new StoredSettings(change.ComfyUiEndpoint.Trim().TrimEnd('/'), change.ComfyUiImagesEnabled, change.ComfyUiVideoEnabled, change.CodexOneClickImages);
        lock (gate)
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write beside, then swap, so a crash mid-write never leaves a
            // half-file that would silently read back as "everything off".
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json));
            File.Move(temporary, path, overwrite: true);
            cached = next;
            cachedStamp = File.GetLastWriteTimeUtc(path);
        }
        return null;
    }

    private StoredSettings Read()
    {
        lock (gate)
        {
            var path = SettingsPath;
            if (!File.Exists(path)) { cached = null; return StoredSettings.Empty; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (cached is not null && stamp == cachedStamp) return cached;
            try { cached = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path), Json) ?? StoredSettings.Empty; }
            // An unreadable file must fail closed: every switch reads as its
            // configured default (off on a new workstation), never on.
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { cached = StoredSettings.Empty; }
            cachedStamp = stamp;
            return cached;
        }
    }

    private string? ResolveString(string key, string? saved)
        => IsExternallySet(key) ? configuration[key] : string.IsNullOrWhiteSpace(saved) ? configuration[key] : saved;

    private bool ResolveBool(string key, bool? saved)
        => IsExternallySet(key) || saved is null ? configuration.GetValue(key, false) : saved.Value;

    private bool IsExternallySet(string key)
        => configuration is IConfigurationRoot root && root.Providers.Any(provider =>
            provider is EnvironmentVariablesConfigurationProvider or CommandLineConfigurationProvider
            && provider.TryGet(key, out _));

    private sealed record StoredSettings(string? ComfyUiEndpoint, bool? ComfyUiImagesEnabled, bool? ComfyUiVideoEnabled, bool? CodexOneClickImages)
    {
        public static readonly StoredSettings Empty = new(null, null, null, null);
    }
}
