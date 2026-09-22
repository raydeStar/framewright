using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed partial class WebMcpStoryboardService
{
    private static readonly string[] AssetImageActions = ["get_director_context", "observe_current_frame"];
    private static readonly string[] AssetMediaActions = ["get_director_context"];
    /// <summary>
    /// Read the displayed library revision, including an older one. These tools
    /// describe saved media and never turn viewing into approval or generation.
    /// A model orbit or a video playhead is not a stored still image.
    /// </summary>
    public async Task<WebMcpEnvelope> AssetDirectorAsync(
        Guid assetId, string? contentHash, bool directorMode, bool observation,
        string? stateToken, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return Failure("asset_not_found", "That asset is not part of the active project.");
        if (!string.Equals(contentHash, asset.ContentHash, StringComparison.Ordinal))
            return new(false, "conflict", "stale_asset", "Reopen this asset before reading its director context.", null, true);

        var notes = (await db.AssetReviewNotes.AsNoTracking()
            .Where(x => x.AssetId == assetId && x.State == "Open").ToArrayAsync(cancellationToken))
            .OrderBy(x => x.Id).ToArray();
        var token = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            contextVersion = 1, asset.ProjectId, asset.Id, asset.ContentHash, asset.UpdatedAt,
            asset.IsCurrentRevision, asset.IsArchived, notes
        }, JsonOptions)));
        var visual = new
        {
            kind = asset.Kind.ToLowerInvariant(), assetId = asset.Id, asset.ContentHash,
            contentUrl = $"/api/assets/{asset.Id}/content", asset.Width, asset.Height, asset.DurationSeconds,
            describes = asset.Kind == "Image"
                ? "The stored image for the displayed revision. Annotation coordinates are normalized to this image."
                : "The stored media for the displayed revision. This is not a capture of the model camera or media playhead."
        };

        if (observation)
        {
            if (stateToken?.Length != 64 || !stateToken.All(Uri.IsHexDigit))
                return Failure("invalid_state_token", "Read the director context first, then pass back its exact state token.");
            if (!string.Equals(stateToken, token, StringComparison.OrdinalIgnoreCase))
                return new(false, "conflict", "stale_context", "This asset or its notes changed. Read the director context again.", null, true);
            if (asset.Kind != "Image")
                return Failure("observation_unavailable", "This asset has no stored still frame for the current camera or playhead. Inspect its media in the workspace.");
            return Success("asset_observation", $"The displayed image is {asset.DisplayName} v{asset.RevisionNumber ?? 1}.", new
            {
                contextVersion = 1, stateToken = token, visual,
                annotations = notes.Select(note => new { note.Id, note.X, note.Y, note.Body }).Take(20).ToArray(),
                openNoteCount = notes.Length
            });
        }

        return Success("asset_director_context", $"{asset.DisplayName} v{asset.RevisionNumber ?? 1} is open.", new
        {
            contextVersion = 1, stateToken = token, projectId = asset.ProjectId,
            subject = new
            {
                kind = "asset", assetId = asset.Id, mediaKind = asset.Kind, asset.DisplayName,
                asset.RevisionFamilyId, displayedVersion = asset.RevisionNumber ?? 1,
                asset.IsCurrentRevision, asset.IsArchived, asset.UpdatedAt
            },
            view = new { directorMode, fullView = directorMode }, visual,
            annotations = notes.Select(note => new { note.Id, note.X, note.Y, note.Body }).Take(20).ToArray(),
            openNoteCount = notes.Length,
            availableActions = asset.Kind == "Image" ? AssetImageActions : AssetMediaActions
        });
    }
}
