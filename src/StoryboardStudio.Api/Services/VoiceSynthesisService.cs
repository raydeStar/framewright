using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed class VoiceSynthesisService(
    StudioDbContext db,
    AssetStore assets,
    IHttpClientFactory clients,
    IConfiguration configuration,
    IProviderCredentialStore credentials,
    QwenVoiceService qwen,
    TimeProvider timeProvider)
{
    private const int MaxSpeechBytes = 50 * 1024 * 1024;
    private bool Enabled => configuration.GetValue("Integrations:OpenAI:SpeechSubmissionEnabled", false);
    private string Model => configuration["Integrations:OpenAI:SpeechModel"] ?? "tts-1-hd";

    public VoiceSynthesisStatus Status()
    {
        var hasCredential = credentials.GetOpenAiStatus().IsConfigured;
        var endpoint = OpenAiEndpointPolicy.Resolve(configuration, "Integrations:OpenAI:SpeechEndpoint", "https://api.openai.com/v1/audio/speech", out var endpointError);
        var ready = Enabled && hasCredential && endpoint is not null;
        var local = qwen.Status();
        return new(Enabled, hasCredential, ready, Model,
            !Enabled ? $"OpenAI speech is protected. Enable {Model} only after reviewing voice, consent, and spend policy."
            : endpoint is null ? endpointError!
            : !hasCredential ? "Speech is enabled, but no server-side OpenAI project credential is configured."
            : $"OpenAI {Model} preset-voice synthesis is ready. Custom voice cloning is not claimed or routed through this adapter.",
            local.Ready, QwenVoiceService.CloneModel, local.Detail);
    }

    public async Task<RepositoryResult<VoiceSynthesisResult>> SynthesizeAsync(
        Guid clipId,
        SynthesizeVoiceRequest request,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? onProviderAccepted = null)
    {
        var clip = await db.TimelineClips.SingleOrDefaultAsync(x => x.Id == clipId, cancellationToken);
        if (clip is null) return RepositoryResult<VoiceSynthesisResult>.NotFound();
        if (clip.UpdatedAt != request.ExpectedUpdatedAt) return RepositoryResult<VoiceSynthesisResult>.Conflict("The voice clip changed. Refresh before synthesizing it.");
        if (clip.Track != TimelineTrackKind.Voice.ToString() || clip.VoiceProfileId is null)
            return RepositoryResult<VoiceSynthesisResult>.Invalid("Only a saved Voice clip with a reusable profile can be synthesized.");
        if (string.IsNullOrWhiteSpace(clip.Text)) return RepositoryResult<VoiceSynthesisResult>.Invalid("Voice synthesis requires dialogue text.");
        var profile = await db.VoiceProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clip.VoiceProfileId, cancellationToken);
        if (profile is null) return RepositoryResult<VoiceSynthesisResult>.Invalid("The voice profile no longer exists.");
        if (profile.Provider.Equals(QwenVoiceService.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            if (onProviderAccepted is not null)
                await onProviderAccepted($"local-qwen:{clip.Id:N}:{request.ExpectedUpdatedAt.UtcTicks}", cancellationToken);
            return await qwen.SynthesizeAsync(clip, profile, cancellationToken);
        }
        if (profile.Kind != VoiceProfileKind.ProviderPreset.ToString() || !profile.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return RepositoryResult<VoiceSynthesisResult>.Invalid("This adapter supports OpenAI preset voices only. Consented clones require an explicitly configured external provider adapter.");
        var status = Status();
        if (!status.CanSynthesize) return RepositoryResult<VoiceSynthesisResult>.Invalid(status.Detail);

        var key = credentials.GetOpenAiApiKey();
        var endpoint = OpenAiEndpointPolicy.Resolve(configuration, "Integrations:OpenAI:SpeechEndpoint", "https://api.openai.com/v1/audio/speech", out _)!;
        var clientRequestId = $"voice-{clip.Id:N}-{request.ExpectedUpdatedAt.UtcTicks}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { model = Model, voice = profile.ProviderVoiceId, input = clip.Text, response_format = "wav" })
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Headers.Add("X-Client-Request-Id", clientRequestId);
        if (configuration["Integrations:OpenAI:ProjectId"] is { Length: > 0 } projectId) httpRequest.Headers.Add("OpenAI-Project", projectId);
        var client = clients.CreateClient("openai-generation");
        // Persist the crossing of the provider boundary before the HTTP request.
        // Speech has no portable resume endpoint, so an interrupted request is
        // deliberately left for explicit retry instead of being duplicated.
        if (onProviderAccepted is not null)
            await onProviderAccepted(clientRequestId, cancellationToken);
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        if (onProviderAccepted is not null && !string.IsNullOrWhiteSpace(requestId))
            await onProviderAccepted(requestId, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await AuditFailureAsync(clip, requestId, $"HTTP {(int)response.StatusCode}", cancellationToken);
            return RepositoryResult<VoiceSynthesisResult>.Invalid($"OpenAI speech failed with {(int)response.StatusCode}: {Bound(body)}. Request {requestId ?? "id unavailable"}; it was not retried.");
        }
        if (response.Content.Headers.ContentLength is > MaxSpeechBytes)
        {
            await AuditFailureAsync(clip, requestId, "Response exceeded declared size limit", cancellationToken);
            return RepositoryResult<VoiceSynthesisResult>.Invalid("OpenAI speech exceeded the 50 MB local safety limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var block = new byte[81_920];
        int read;
        while ((read = await source.ReadAsync(block, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxSpeechBytes)
            {
                await AuditFailureAsync(clip, requestId, "Stream exceeded size limit", cancellationToken);
                return RepositoryResult<VoiceSynthesisResult>.Invalid("OpenAI speech exceeded the 50 MB local safety limit.");
            }
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        var imported = await assets.ImportGeneratedMediaAsync(buffer, $"{clip.Label}-openai.wav", "audio/wav", buffer.Length, AssetKind.Audio, cancellationToken);
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
        {
            await AuditFailureAsync(clip, requestId, $"Import rejected: {imported.Error}", cancellationToken);
            return imported.Kind == RepositoryResultKind.Conflict
                ? RepositoryResult<VoiceSynthesisResult>.Conflict(imported.Error ?? "The generated speech conflicted with an existing asset.")
                : RepositoryResult<VoiceSynthesisResult>.Invalid(imported.Error ?? "The generated speech could not be imported.");
        }

        clip.AssetId = imported.Value.Id;
        clip.UpdatedAt = timeProvider.GetUtcNow();
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(),
            Type = "VoiceSpeechSynthesized",
            TargetType = "TimelineClip",
            TargetId = clip.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { model = Model, profile.Id, profile.ProviderVoiceId, assetId = imported.Value.Id, providerRequestId = requestId, providerCallMade = true }),
            CreatedAt = clip.UpdatedAt
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RepositoryResult<VoiceSynthesisResult>.Conflict("The voice clip changed after synthesis. The generated audio remains safely imported but was not attached; refresh before choosing it."); }
        return RepositoryResult<VoiceSynthesisResult>.Ok(new(Map(clip), Model, requestId, true));
    }

    private static TimelineClipSummary Map(TimelineClipRecord x) => new(x.Id, Enum.Parse<TimelineTrackKind>(x.Track), x.Label, x.StartFrame, x.DurationFrames, x.TrimStartSeconds, x.Volume, x.AssetId, x.AssetId is null ? null : $"/api/assets/{x.AssetId}/content", x.VoiceProfileId, x.Text, x.CreatedAt, x.UpdatedAt);
    private async Task AuditFailureAsync(TimelineClipRecord clip, string? requestId, string outcome, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "VoiceSpeechSynthesisFailed", TargetType = "TimelineClip", TargetId = clip.Id.ToString(), PayloadJson = JsonSerializer.Serialize(new { model = Model, providerRequestId = requestId, providerCallMade = true, outcome = Bound(outcome) }), CreatedAt = timeProvider.GetUtcNow() });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { /* Preserve the provider failure as the user-facing result. */ }
    }
    private static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "...";
}
