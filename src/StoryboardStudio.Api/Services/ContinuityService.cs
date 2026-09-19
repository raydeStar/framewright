using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed class ContinuityService(StudioDbContext db, TimeProvider timeProvider)
{
    public async Task<ShotContinuityReport?> EvaluateAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var shots = await db.Shots.AsNoTracking().OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var index = shots.FindIndex(x => x.Id == shotId);
        if (index < 0) return null;
        var shot = shots[index];
        // Multiple projects are first-class. The scoped shot already tells us
        // which delivery contract owns this report; querying the whole table
        // made Review fail as soon as a second project existed.
        var project = await db.Projects.AsNoTracking()
            .SingleAsync(x => x.Id == shot.ProjectId, cancellationToken);
        var references = await db.References.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var currentComments = await db.Comments.AsNoTracking().Where(x => x.ShotId == shot.Id && x.Version == shot.Version).ToListAsync(cancellationToken);
        var openComments = currentComments.Count(x => x.State == "Open");
        var ids = Deserialize(shot.ReferenceIdsJson);
        var constraints = Deserialize(shot.ConstraintsJson);
        var checks = new List<ContinuityCheckSummary>();

        Add(checks, shot, "approval", openComments > 0 ? ContinuityCheckState.Block : shot.Approval == nameof(ApprovalState.Ratified) ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
            "Approval gate", openComments > 0 ? "Resolve every note before ratification." : shot.Approval == nameof(ApprovalState.Ratified) ? "This exact candidate is immutable authority." : "Artist ratification is still required.",
            openComments > 0 ? $"{openComments} open note(s) on v{shot.Version}" : $"{shot.Stage} v{shot.Version} is {shot.Approval}");

        var missing = ids.Where(id => !references.ContainsKey(id)).ToArray();
        Add(checks, shot, "authority", missing.Length > 0 ? ContinuityCheckState.Block : ContinuityCheckState.Pass,
            "Authority packet", missing.Length > 0 ? "One or more cited authorities no longer resolve." : "Every cited authority resolves to a versioned canon record.",
            missing.Length > 0 ? string.Join(", ", missing) : $"{ids.Length} authority records");

        var hasStyle = ids.Any(id => references.TryGetValue(id, out var reference) && reference.Category == "Style");
        Add(checks, shot, "style", hasStyle ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
            "Style authority", hasStyle ? "An approved style authority travels with generation." : "Add a style authority before a production render.",
            hasStyle ? string.Join(", ", ids.Where(id => references.TryGetValue(id, out var value) && value.Category == "Style")) : "No Style authority cited");

        Add(checks, shot, "constraints", constraints.Length > 0 ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
            "Explicit locks", constraints.Length > 0 ? "Shot-specific constraints are explicit and manifest-bound." : "Write observable constraints before generation.",
            constraints.Length > 0 ? $"{constraints.Length} locked rule(s)" : "No locked rules");

        var currentAsset = shot.CurrentAssetId is null
            ? null
            : await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shot.CurrentAssetId, cancellationToken);
        var visualContractHash = VisualContractHash.Compute(shot);
        var visualAuditRecords = currentAsset is null ? [] : await db.ShotVisualAudits.AsNoTracking()
            .Where(x => x.ShotId == shot.Id && x.ShotVersion == shot.Version && x.AssetId == currentAsset.Id
                && x.AssetHash == currentAsset.ContentHash && x.ContractHash == visualContractHash)
            .ToListAsync(cancellationToken);
        var visualAudit = visualAuditRecords.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (currentAsset?.Width is > 0 && currentAsset.Height is > 0)
        {
            var sourceRatio = currentAsset.Width.Value / (double)currentAsset.Height.Value;
            var projectRatio = project.DeliveryWidth / (double)project.DeliveryHeight;
            var ratioDrift = Math.Abs(sourceRatio - projectRatio) / projectRatio;
            Add(checks, shot, "delivery-shape", ratioDrift <= .01 ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
                "Project canvas", ratioDrift <= .01
                    ? "The current frame matches the project delivery shape."
                    : "The current frame will need a visible crop or padding choice before production video. Reframe the still if the edges matter.",
                $"Current {currentAsset.Width}×{currentAsset.Height}; project {project.DeliveryWidth}×{project.DeliveryHeight} ({project.AspectRatio})");
            var pixelScale = Math.Min(currentAsset.Width.Value / (double)project.DeliveryWidth, currentAsset.Height.Value / (double)project.DeliveryHeight);
            Add(checks, shot, "delivery-resolution", pixelScale >= 1 ? ContinuityCheckState.Pass : pixelScale >= .5 ? ContinuityCheckState.Info : ContinuityCheckState.Review,
                "Source resolution", pixelScale >= 1
                    ? "The current still meets or exceeds the project delivery dimensions."
                    : pixelScale >= .5
                        ? "This is a review-resolution source. Promote or upscale before final delivery."
                        : "The source is substantially below delivery resolution; use it for blocking, not a production master.",
                $"Source is {pixelScale:P0} of the delivery height/width floor");
        }

        var subjectPins = currentComments
            .Where(comment => comment.ReferenceId is not null
                && references.TryGetValue(comment.ReferenceId, out var reference)
                && reference.Category is "Character" or "Role")
            .ToArray();
        var subjectAuthorities = ids
            .Where(id => references.TryGetValue(id, out var reference) && reference.Category is "Character" or "Role")
            .ToArray();
        if (subjectPins.Length > 1 || subjectAuthorities.Length > 1)
            Add(checks, shot, "multi-subject", ContinuityCheckState.Review,
                "Multi-subject composition", "Confirm subject count, full-body crop where requested, distinct faces for Role authorities, and no pasted reference portraits before approval.",
                $"{subjectPins.Length} positioned subject pin(s); {subjectAuthorities.Length} character/role authority record(s)", true);

        var allText = string.Join(" ", new[] { shot.Description, shot.Action }.Concat(constraints)).ToLowerInvariant();
        var offCamera = allText.Contains("off-camera", StringComparison.Ordinal) || allText.Contains("off camera", StringComparison.Ordinal);
        var canonProtected = constraints.Any(x => x.Contains("canon", StringComparison.OrdinalIgnoreCase) || x.Contains("remains", StringComparison.OrdinalIgnoreCase) || x.Contains("may be off-camera", StringComparison.OrdinalIgnoreCase));
        if (offCamera)
            Add(checks, shot, "world-crop", canonProtected ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
                "World canon vs. crop", canonProtected ? "The crop is explicitly separated from world truth." : "State that the unseen feature remains in world canon.",
                canonProtected ? "Off-camera feature protected by a locked rule" : "Off-camera language without a canon-preservation rule");

        var requestsMusic = allText.Contains("background music", StringComparison.Ordinal) && !allText.Contains("no background music", StringComparison.Ordinal);
        var mentionsMusic = allText.Contains("music", StringComparison.Ordinal);
        Add(checks, shot, "audio", requestsMusic ? ContinuityCheckState.Block : mentionsMusic ? ContinuityCheckState.Pass : ContinuityCheckState.Info,
            "Audio separation", requestsMusic ? "Background music cannot be requested inside a video generation." : "Video and still prompts remain free of generated background music; music belongs on the post track.",
            requestsMusic ? "Generation intent requests background music" : mentionsMusic ? "Explicit no-background-music constraint" : "No music instruction in the visual prompt");

        var previous = index > 0 ? shots[index - 1] : null;
        if (previous is not null)
        {
            var shared = ids.Intersect(Deserialize(previous.ReferenceIdsJson)).ToArray();
            Add(checks, shot, "adjacent-authority", shared.Length > 0 ? ContinuityCheckState.Pass : ContinuityCheckState.Info,
                "Adjacent authority bridge", shared.Length > 0 ? "Shared canon anchors connect this shot to the previous slot." : "No shared authority is required by metadata; inspect the cut visually.",
                shared.Length > 0 ? $"Shared with {previous.Code}: {string.Join(", ", shared)}" : $"Previous slot: {previous.Code}");
            var previousDirection = string.Join(" ", new[] { previous.Action }.Concat(Deserialize(previous.ConstraintsJson))).ToLowerInvariant();
            var directional = previousDirection.Contains("left to right", StringComparison.Ordinal) || previousDirection.Contains("right to left", StringComparison.Ordinal) || previousDirection.Contains("screen direction", StringComparison.Ordinal);
            if (directional)
            {
                var currentDirection = allText.Contains("screen direction", StringComparison.Ordinal) || allText.Contains("left to right", StringComparison.Ordinal) || allText.Contains("right to left", StringComparison.Ordinal);
                Add(checks, shot, "screen-direction", currentDirection ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
                    "Screen direction", currentDirection ? "The current shot carries an explicit directional instruction." : "Carry or intentionally break the previous shot's screen direction.",
                    $"Directional language detected in {previous.Code}");
            }
        }

        if (shot.VideoLastFrameCandidateId is Guid lastCandidateId)
        {
            var endpointCandidates = await db.CandidateVersions.AsNoTracking()
                .Where(candidate => candidate.Id == lastCandidateId || candidate.Id == shot.VideoFirstFrameCandidateId)
                .ToListAsync(cancellationToken);
            var endpointAssetIds = endpointCandidates.Where(candidate => candidate.AssetId is not null).Select(candidate => candidate.AssetId!.Value).Distinct().ToArray();
            var endpointAssets = await db.Assets.AsNoTracking().Where(asset => endpointAssetIds.Contains(asset.Id)).ToDictionaryAsync(asset => asset.Id, cancellationToken);
            var firstCandidate = endpointCandidates.SingleOrDefault(candidate => candidate.Id == shot.VideoFirstFrameCandidateId);
            var lastCandidate = endpointCandidates.SingleOrDefault(candidate => candidate.Id == lastCandidateId);
            var firstAsset = firstCandidate?.AssetId is Guid firstId && endpointAssets.TryGetValue(firstId, out var first) ? first : currentAsset;
            var lastAsset = lastCandidate?.AssetId is Guid lastId && endpointAssets.TryGetValue(lastId, out var last) ? last : null;
            var readable = firstAsset?.Width is > 0 && firstAsset.Height is > 0 && lastAsset?.Width is > 0 && lastAsset.Height is > 0;
            var ratiosAgree = readable && Math.Abs(firstAsset!.Width!.Value / (double)firstAsset.Height!.Value - lastAsset!.Width!.Value / (double)lastAsset.Height!.Value) <= .01;
            Add(checks, shot, "video-endpoints", !readable ? ContinuityCheckState.Block : ratiosAgree ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
                "First/last frame compatibility", !readable
                    ? "Both endpoint images need readable dimensions before H3 interpolation."
                    : ratiosAgree
                        ? "Both endpoint sources share a compatible canvas shape; still inspect pose, camera, lighting, and subject placement."
                        : "The endpoint sources have different canvas shapes. Framewright normalizes them, but inspect the crop before spending a production pass.",
                readable ? $"First {firstAsset!.Width}×{firstAsset.Height}; last {lastAsset!.Width}×{lastAsset.Height}" : "One or both endpoint assets are unavailable", true);
        }

        var visualState = currentAsset is null ? ContinuityCheckState.Info
            : visualAudit is null ? ContinuityCheckState.Review
            : visualAudit.GateState == "Clear" ? ContinuityCheckState.Pass
            : visualAudit.GateState == "Blocked" ? ContinuityCheckState.Block
            : ContinuityCheckState.Review;
        Add(checks, shot, "visual-review", visualState, "Pixel continuity review",
            currentAsset is null
                ? "No current image asset exists, so pixel-level continuity cannot be evaluated."
                : visualAudit is null
                    ? "Run the Codex visual check before production final. Metadata alone cannot verify identity, hands, props, counts, pose, or eyelines."
                    : visualAudit.GateState == "Clear"
                        ? "Codex inspected this exact image and contract; no material visible contradictions were found."
                        : $"The latest visual check is {visualAudit.GateState.ToLowerInvariant()}. Reconcile its findings before production final.",
            currentAsset is null ? "Intent-only candidate" : visualAudit is null ? "No audit for this image and contract hash" : $"Audit {visualAudit.Id:N} · {visualAudit.Summary}", true);

        if (shot.Stage == nameof(ShotStage.Final) && shot.Approval == nameof(ApprovalState.Ratified))
        {
            var endpointLanguage = allText.Contains("hold", StringComparison.Ordinal) || allText.Contains("settle", StringComparison.Ordinal) || allText.Contains("land on", StringComparison.Ordinal) || allText.Contains("first frame", StringComparison.Ordinal) || allText.Contains("last frame", StringComparison.Ordinal);
            Add(checks, shot, "video-anchor", endpointLanguage ? ContinuityCheckState.Pass : ContinuityCheckState.Review,
                "Video endpoint strategy", endpointLanguage ? "Action language provides a stable hold or settle for video design." : "Define the shot's stable opening or landing instead of blindly forcing two incompatible endpoints.",
                endpointLanguage ? "Hold/settle language detected" : "No stable endpoint language detected");
        }

        var gate = checks.Any(x => x.State == ContinuityCheckState.Block) ? "Blocked" : checks.Any(x => x.State == ContinuityCheckState.Review) ? "Review" : "Clear";
        return new(shot.Id, shot.Code, shot.Version, gate, checks, timeProvider.GetUtcNow(), "The metadata preflight never claims to have inspected pixels. The separate Codex visual check is bound to the exact image hash and shot-contract hash; neither silently stands in for the other.");
    }

    private static string[] Deserialize(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    private static void Add(List<ContinuityCheckSummary> checks, ShotRecord shot, string id, ContinuityCheckState state, string title, string detail, string evidence, bool visual = false)
        => checks.Add(new($"{shot.Id:N}-{id}", shot.Id, state, id, title, detail, evidence, visual));
}
