using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed record MusicGenerationStatus(
    bool Enabled,
    bool EndpointAllowed,
    bool ServiceReachable,
    bool CanCompose,
    bool CanRender,
    string Detail,
    string Model,
    string Device);

public sealed record RenderMusicRequest(Guid RevisionId);
public sealed record YuE2PlanResult(string AbcNotation, string ArtifactManifestJson);

/// <summary>
/// Provider boundary for YuE2. Framewright owns compositions and revisions; this
/// service may plan ABC or render one immutable revision into a new audio asset.
/// </summary>
public sealed class MusicGenerationService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    StudioDbContext db,
    AssetStore assets,
    IProjectScope projectScope,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private bool Enabled => configuration.GetValue("YuE2:Enabled", false);
    private Uri? Endpoint => Uri.TryCreate(configuration["YuE2:Endpoint"], UriKind.Absolute, out var uri) ? uri : null;
    private string WorkerToken => configuration["YuE2:WorkerToken"]?.Trim() ?? string.Empty;

    public async Task<MusicGenerationStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return new(false, EndpointIsAllowed(), false, false, false,
                "YuE2 is installed as an optional local renderer but remains disabled until the artist enables it.",
                configuration["YuE2:Model"] ?? "m-a-p/YuE2-3B", configuration["YuE2:Device"] ?? "cuda");
        if (!EndpointIsAllowed())
            return new(true, false, false, false, false,
                "The YuE2 endpoint must be loopback (or host.docker.internal). Explicit remote endpoints also require HTTPS and YuE2:AllowRemote.",
                configuration["YuE2:Model"] ?? "m-a-p/YuE2-3B", configuration["YuE2:Device"] ?? "cuda");

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/health");
            using var response = await clients.CreateClient("yue2-health").SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(true, true, false, false, false,
                    $"YuE2 returned HTTP {(int)response.StatusCode}. Check the local worker and its token.",
                    configuration["YuE2:Model"] ?? "m-a-p/YuE2-3B", configuration["YuE2:Device"] ?? "cuda");
            var health = await response.Content.ReadFromJsonAsync<YuE2Health>(Json, cancellationToken);
            var ready = health?.Ready == true;
            return new(true, true, true, ready, ready,
                ready ? "YuE2 is ready for symbolic planning and rendering. One GPU job runs at a time."
                    : health?.Detail ?? "YuE2 is reachable but its runtime or checkpoints are not ready.",
                health?.Model ?? configuration["YuE2:Model"] ?? "m-a-p/YuE2-3B",
                health?.Device ?? configuration["YuE2:Device"] ?? "cuda");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(true, true, false, false, false,
                "YuE2 is not reachable at the configured endpoint. Start the local worker; no generation was submitted.",
                configuration["YuE2:Model"] ?? "m-a-p/YuE2-3B", configuration["YuE2:Device"] ?? "cuda");
        }
    }

    public async Task<RepositoryResult<YuE2PlanResult>> ComposePlanAsync(
        MusicCompositionDocument composition,
        CancellationToken cancellationToken)
    {
        var status = await StatusAsync(cancellationToken);
        if (!status.CanCompose) return RepositoryResult<YuE2PlanResult>.Unavailable(status.Detail);
        var request = new YuE2ComposeRequest(
            Guid.NewGuid().ToString("N"),
            BuildStyle(composition),
            BuildLyrics(composition),
            StableSeed(JsonSerializer.Serialize(composition, Json)),
            "full");
        var submitted = await SubmitAsync("/compose", request, cancellationToken);
        if (submitted.Kind != RepositoryResultKind.Ok || submitted.Value is null)
            return new(default, submitted.Kind, submitted.Error);
        var completed = await WaitAsync(submitted.Value, cancellationToken);
        if (completed.Kind != RepositoryResultKind.Ok || completed.Value is null)
            return new(default, completed.Kind, completed.Error);
        var result = completed.Value.Result;
        var abc = result?.AbcNotation?.Trim();
        if (string.IsNullOrWhiteSpace(abc))
            return RepositoryResult<YuE2PlanResult>.Invalid("YuE2 completed symbolic planning without returning score.abc.");
        return RepositoryResult<YuE2PlanResult>.Ok(new(abc + "\n", result!.ArtifactManifestJson ?? "{}"));
    }

    public async Task<RepositoryResult<AssetSummary>> RenderAsync(
        RenderMusicRequest request,
        Guid framewrightJobId,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? onProviderAccepted = null,
        Func<string, CancellationToken, Task>? onPhase = null)
    {
        var status = await StatusAsync(cancellationToken);
        if (!status.CanRender) return RepositoryResult<AssetSummary>.Unavailable(status.Detail);
        var revision = await db.MusicCompositionRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.RevisionId, cancellationToken);
        if (revision is null) return RepositoryResult<AssetSummary>.NotFound();
        var composition = JsonSerializer.Deserialize<MusicCompositionDocument>(revision.CompositionJson, Json);
        if (composition is null) return RepositoryResult<AssetSummary>.Invalid("The stored composition document is malformed.");

        var payload = new YuE2RenderRequest(
            revision.Id.ToString("N"),
            BuildStyle(composition),
            BuildLyrics(composition),
            revision.AbcNotation,
            StableSeed(revision.ContentHash),
            "full");
        var submitted = await SubmitAsync("/render", payload, cancellationToken);
        if (submitted.Kind != RepositoryResultKind.Ok || submitted.Value is null)
            return new(default, submitted.Kind, submitted.Error);
        if (onProviderAccepted is not null) await onProviderAccepted(submitted.Value, cancellationToken);
        var completed = await WaitAsync(submitted.Value, cancellationToken, onPhase);
        if (completed.Kind != RepositoryResultKind.Ok || completed.Value is null)
            return new(default, completed.Kind, completed.Error);
        var audioUrl = completed.Value.Result?.AudioUrl;
        if (string.IsNullOrWhiteSpace(audioUrl))
            return RepositoryResult<AssetSummary>.Invalid("YuE2 completed without returning a rendered audio artifact.");
        var expectedAudioUrl = $"/jobs/{Uri.EscapeDataString(submitted.Value)}/audio";
        if (!string.Equals(audioUrl, expectedAudioUrl, StringComparison.Ordinal))
            return RepositoryResult<AssetSummary>.Invalid("YuE2 returned an unexpected audio location; Framewright refused the cross-origin fetch.");

        if (onPhase is not null) await onPhase("Processing audio", cancellationToken);
        using var audioRequest = CreateRequest(HttpMethod.Get, expectedAudioUrl);
        using var response = await clients.CreateClient("yue2-generation")
            .SendAsync(audioRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return RepositoryResult<AssetSummary>.Invalid($"YuE2 rendered the song but audio retrieval returned HTTP {(int)response.StatusCode}.");
        await using var buffered = await BoundedContentBuffer.CopyToTemporaryFileAsync(
            response.Content, AssetStore.MaxMediaBytes, "The YuE2 track exceeded the local media size limit.", submitted.Value, cancellationToken);
        var imported = await assets.ImportGeneratedMediaAsync(
            buffered,
            Slug(composition.Title) + "-v" + revision.RevisionNumber + ".flac",
            "audio/flac",
            buffered.CanSeek ? buffered.Length : null,
            AssetKind.Audio,
            cancellationToken,
            "YuE2 music");
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
            return imported;

        db.MusicRenders.Add(new MusicRenderRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = projectScope.ProjectId,
            CompositionRevisionId = revision.Id,
            JobId = framewrightJobId,
            AssetId = imported.Value.Id,
            Renderer = "YuE2",
            SettingsJson = JsonSerializer.Serialize(new
            {
                model = status.Model,
                device = status.Device,
                cot = "full",
                seed = payload.Seed,
                compositionHash = revision.ContentHash
            }, Json),
            ArtifactManifestJson = completed.Value.Result?.ArtifactManifestJson ?? "{}",
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private async Task<RepositoryResult<string>> SubmitAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, path);
            request.Content = JsonContent.Create(body, options: Json);
            using var response = await clients.CreateClient("yue2-generation").SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return RepositoryResult<string>.Invalid($"YuE2 rejected the request with HTTP {(int)response.StatusCode}: {Bound(text)}");
            var submitted = JsonSerializer.Deserialize<YuE2Submission>(text, Json);
            return string.IsNullOrWhiteSpace(submitted?.JobId)
                ? RepositoryResult<string>.Invalid("YuE2 did not return a provider job id.")
                : RepositoryResult<string>.Ok(submitted.JobId);
        }
        catch (HttpRequestException ex)
        {
            return RepositoryResult<string>.Unavailable("YuE2 could not be reached: " + ex.Message);
        }
    }

    private async Task<RepositoryResult<YuE2Job>> WaitAsync(
        string jobId,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? onPhase = null)
    {
        var deadline = timeProvider.GetUtcNow() + TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("YuE2:TimeoutMinutes", 180), 5, 720));
        var lastPhase = string.Empty;
        try
        {
            while (timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                using var request = CreateRequest(HttpMethod.Get, $"/jobs/{Uri.EscapeDataString(jobId)}");
                using var response = await clients.CreateClient("yue2-generation").SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var job = await response.Content.ReadFromJsonAsync<YuE2Job>(Json, cancellationToken);
                if (job is null) continue;
                if (!string.Equals(job.Phase, lastPhase, StringComparison.Ordinal) && onPhase is not null)
                {
                    lastPhase = job.Phase ?? string.Empty;
                    await onPhase(MapPhase(lastPhase), cancellationToken);
                }
                if (string.Equals(job.State, "completed", StringComparison.OrdinalIgnoreCase))
                    return RepositoryResult<YuE2Job>.Ok(job);
                if (string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase))
                    return RepositoryResult<YuE2Job>.Invalid(MapWorkerError(job.Error));
                if (string.Equals(job.State, "cancelled", StringComparison.OrdinalIgnoreCase))
                    return RepositoryResult<YuE2Job>.Conflict("YuE2 cancelled the generation job.");
            }
            return RepositoryResult<YuE2Job>.Unavailable("YuE2 did not complete before the configured timeout. The provider job may still be running.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                using var cancel = CreateRequest(HttpMethod.Post, $"/jobs/{Uri.EscapeDataString(jobId)}/cancel");
                await clients.CreateClient("yue2-health").SendAsync(cancel, CancellationToken.None);
            }
            catch { /* The durable Framewright job still records cancellation even if the worker vanished. */ }
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint!, path));
        if (WorkerToken.Length > 0) request.Headers.TryAddWithoutValidation("X-Framewright-Worker-Token", WorkerToken);
        return request;
    }

    private bool EndpointIsAllowed()
    {
        var endpoint = Endpoint;
        if (endpoint is null || endpoint.Scheme is not ("http" or "https")) return false;
        if (endpoint.IsLoopback || string.Equals(endpoint.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
            return true;
        return endpoint.Scheme == Uri.UriSchemeHttps && configuration.GetValue("YuE2:AllowRemote", false);
    }

    private static string BuildStyle(MusicCompositionDocument composition) =>
        $"{composition.Style}. {composition.Key}, {composition.Meter}, {composition.Tempo} BPM. {composition.PerformancePrompt}".Trim();

    private static string BuildLyrics(MusicCompositionDocument composition) => string.Join("\n\n",
        composition.Sections.Select(section => $"[{section.Type}]\n{section.Lyrics}"));

    private static int StableSeed(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        var seed = BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF;
        return seed == 0 ? 1 : (int)seed;
    }

    private static string MapPhase(string phase) => phase.ToLowerInvariant() switch
    {
        "queued" => "Queued",
        "loading_model" => "Loading YuE2 model",
        "planning" => "Preparing composition",
        "semantic_generation" => "Rendering",
        "synthesis" => "Rendering",
        "decoding" => "Processing audio",
        "saving" => "Processing audio",
        _ => "Rendering"
    };

    private static string MapWorkerError(string? error)
    {
        var value = Bound(error ?? "YuE2 generation failed.");
        if (value.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
            return "YuE2 ran out of CUDA memory. Close other GPU work or reduce the composition length; Framewright did not silently lower quality.";
        if (value.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) || value.Contains("model", StringComparison.OrdinalIgnoreCase))
            return "YuE2 could not load its configured model or VAE checkpoint: " + value;
        if (value.Contains("ABC", StringComparison.OrdinalIgnoreCase))
            return "YuE2 rejected the symbolic score: " + value;
        return value;
    }

    private static string Bound(string value) => value.Length <= 1_000 ? value : value[..1_000];

    private static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length is 0 ? "song" : slug[..Math.Min(64, slug.Length)];
    }

    private sealed record YuE2Health(bool Ready, string Detail, string Model, string Device);
    private sealed record YuE2Submission(string JobId);
    private sealed record YuE2ComposeRequest(string Id, string Style, string Lyrics, int Seed, string Cot);
    private sealed record YuE2RenderRequest(string Id, string Style, string Lyrics, string Abc, int Seed, string Cot);
    private sealed record YuE2Job(string JobId, string State, string? Phase, string? Error, YuE2JobResult? Result);
    private sealed record YuE2JobResult(string? AbcNotation, string? AudioUrl, string? ArtifactManifestJson);
}
