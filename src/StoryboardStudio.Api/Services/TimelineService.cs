using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

public sealed class TimelineService(StudioDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<TimelineClipSummary>> ListClipsAsync(CancellationToken cancellationToken)
        => (await db.TimelineClips.AsNoTracking().OrderBy(x => x.Track).ThenBy(x => x.StartFrame).ToListAsync(cancellationToken)).Select(MapClip).ToArray();

    public async Task<RepositoryResult<TimelineClipSummary>> CreateClipAsync(SaveTimelineClipRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateClipAsync(request, cancellationToken);
        if (error is not null) return RepositoryResult<TimelineClipSummary>.Invalid(error);
        var now = timeProvider.GetUtcNow();
        var record = new TimelineClipRecord { Id = Guid.NewGuid(), Track = request.Track.ToString(), Label = request.Label.Trim(), StartFrame = request.StartFrame, DurationFrames = request.DurationFrames, TrimStartSeconds = request.TrimStartSeconds, Volume = request.Volume, AssetId = request.AssetId, VoiceProfileId = request.VoiceProfileId, Text = request.Text.Trim(), CreatedAt = now, UpdatedAt = now };
        db.TimelineClips.Add(record); AddAudit("TimelineClipCreated", record.Id, new { record.Track, record.StartFrame, record.DurationFrames, record.AssetId, record.VoiceProfileId });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<TimelineClipSummary>.Ok(MapClip(record));
    }

    public async Task<RepositoryResult<TimelineClipSummary>> UpdateClipAsync(Guid clipId, SaveTimelineClipRequest request, CancellationToken cancellationToken)
    {
        var record = await db.TimelineClips.SingleOrDefaultAsync(x => x.Id == clipId, cancellationToken);
        if (record is null) return RepositoryResult<TimelineClipSummary>.NotFound();
        if (request.ExpectedUpdatedAt is null || record.UpdatedAt != request.ExpectedUpdatedAt.Value)
            return RepositoryResult<TimelineClipSummary>.Conflict("The timeline clip changed. Refresh before editing it again.");
        var error = await ValidateClipAsync(request, cancellationToken);
        if (error is not null) return RepositoryResult<TimelineClipSummary>.Invalid(error);
        record.Track = request.Track.ToString(); record.Label = request.Label.Trim(); record.StartFrame = request.StartFrame; record.DurationFrames = request.DurationFrames; record.TrimStartSeconds = request.TrimStartSeconds; record.Volume = request.Volume; record.AssetId = request.AssetId; record.VoiceProfileId = request.VoiceProfileId; record.Text = request.Text.Trim(); record.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("TimelineClipUpdated", record.Id, new { record.Track, record.StartFrame, record.DurationFrames, record.TrimStartSeconds, record.Volume });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RepositoryResult<TimelineClipSummary>.Conflict("The timeline clip changed while it was being saved."); }
        return RepositoryResult<TimelineClipSummary>.Ok(MapClip(record));
    }

    public async Task<bool> DeleteClipAsync(Guid clipId, CancellationToken cancellationToken)
    {
        var record = await db.TimelineClips.SingleOrDefaultAsync(x => x.Id == clipId, cancellationToken);
        if (record is null) return false;
        db.TimelineClips.Remove(record); AddAudit("TimelineClipDeleted", record.Id, new { record.Track, record.Label }); await db.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<IReadOnlyList<VoiceProfileSummary>> ListVoiceProfilesAsync(CancellationToken cancellationToken)
        => (await db.VoiceProfiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken)).Select(MapVoice).ToArray();

    public async Task<RepositoryResult<VoiceProfileSummary>> CreateVoiceProfileAsync(CreateVoiceProfileRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120) return RepositoryResult<VoiceProfileSummary>.Invalid("Voice profile name must contain 1 to 120 characters.");
        if (string.IsNullOrWhiteSpace(request.Provider) || request.Provider.Trim().Length > 120 || string.IsNullOrWhiteSpace(request.ProviderVoiceId) || request.ProviderVoiceId.Trim().Length > 240) return RepositoryResult<VoiceProfileSummary>.Invalid("Provider and provider voice id are required.");
        if (string.IsNullOrWhiteSpace(request.CharacterReferenceId)) return RepositoryResult<VoiceProfileSummary>.Invalid("Choose the approved character reference this voice belongs to.");
        var character = await db.References.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CharacterReferenceId && x.Category == "Character", cancellationToken);
        if (character is null) return RepositoryResult<VoiceProfileSummary>.Invalid("Voice profiles must cite an existing Character reference.");
        if (request.SampleAssetId is null || !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == request.SampleAssetId && x.Kind == AssetKind.Audio.ToString() && !x.IsArchived, cancellationToken))
            return RepositoryResult<VoiceProfileSummary>.Invalid("Voice profiles require a playable, non-archived audio sample.");
        if (request.Kind == VoiceProfileKind.ConsentedClone && (!request.ConsentConfirmed || string.IsNullOrWhiteSpace(request.ConsentAttestation) || request.ConsentAttestation.Trim().Length is < 20 or > 2_000))
            return RepositoryResult<VoiceProfileSummary>.Invalid("A cloned voice requires explicit consent confirmation and a 20 to 2,000 character consent attestation.");
        if (await db.VoiceProfiles.AsNoTracking().AnyAsync(x => x.Name == request.Name.Trim(), cancellationToken)) return RepositoryResult<VoiceProfileSummary>.Conflict("A voice profile with this name already exists.");
        var now = timeProvider.GetUtcNow();
        var attestation = request.Kind == VoiceProfileKind.ProviderPreset ? "Provider preset; no person's voice was cloned." : request.ConsentAttestation.Trim();
        var record = new VoiceProfileRecord { Id = Guid.NewGuid(), Name = request.Name.Trim(), Kind = request.Kind.ToString(), Provider = request.Provider.Trim(), ProviderVoiceId = request.ProviderVoiceId.Trim(), CharacterName = character.Name, CharacterReferenceId = character.Id, SampleAssetId = request.SampleAssetId, ConsentAttestation = attestation, ConsentedAt = request.Kind == VoiceProfileKind.ConsentedClone ? now : null, CreatedAt = now };
        db.VoiceProfiles.Add(record); AddAudit("VoiceProfileCreated", record.Id, new { record.Kind, record.Provider, record.CharacterName, consentRecorded = record.ConsentedAt is not null }); await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<VoiceProfileSummary>.Ok(MapVoice(record));
    }

    private async Task<string?> ValidateClipAsync(SaveTimelineClipRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > 120) return "Clip label must contain 1 to 120 characters.";
        if (request.StartFrame is < 0 or > 864_000 || request.DurationFrames is < 1 or > 864_000) return "Clip timing must be positive and within ten hours at 24 fps.";
        if (!double.IsFinite(request.TrimStartSeconds) || request.TrimStartSeconds is < 0 or > 36_000) return "Trim start must be between 0 and 36,000 seconds.";
        if (!double.IsFinite(request.Volume) || request.Volume is < 0 or > 2) return "Volume must be between 0 and 2.";
        if (request.Text is null || request.Text.Trim().Length > 4_000) return "Clip text must be 4,000 characters or fewer.";
        if (request.AssetId is not null && !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == request.AssetId && x.Kind == AssetKind.Audio.ToString(), cancellationToken)) return "Timeline audio clips require a valid audio asset.";
        if (request.VoiceProfileId is not null && !await db.VoiceProfiles.AsNoTracking().AnyAsync(x => x.Id == request.VoiceProfileId, cancellationToken)) return "The selected voice profile does not exist.";
        if (request.Track == TimelineTrackKind.Voice && request.VoiceProfileId is null) return "Voice clips require a reusable voice profile.";
        if (request.Track != TimelineTrackKind.Voice && request.VoiceProfileId is not null) return "Only voice clips can cite a voice profile.";
        return null;
    }

    private void AddAudit(string type, Guid id, object payload) => db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = type, TargetType = "TimelineClip", TargetId = id.ToString(), PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload), CreatedAt = timeProvider.GetUtcNow() });
    private static TimelineClipSummary MapClip(TimelineClipRecord x) => new(x.Id, Enum.Parse<TimelineTrackKind>(x.Track), x.Label, x.StartFrame, x.DurationFrames, x.TrimStartSeconds, x.Volume, x.AssetId, x.AssetId is null ? null : $"/api/assets/{x.AssetId}/content", x.VoiceProfileId, x.Text, x.CreatedAt, x.UpdatedAt);
    private static VoiceProfileSummary MapVoice(VoiceProfileRecord x) => new(x.Id, x.Name, Enum.Parse<VoiceProfileKind>(x.Kind), x.Provider, x.ProviderVoiceId, x.CharacterName, x.ConsentAttestation, x.ConsentedAt, x.CreatedAt, x.CharacterReferenceId, x.SampleAssetId, x.SampleAssetId is null ? null : $"/api/assets/{x.SampleAssetId}/content");
}
