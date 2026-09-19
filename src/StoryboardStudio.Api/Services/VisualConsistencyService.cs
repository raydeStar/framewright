using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Pixel-aware review for the exact image and shot contract currently on screen.
/// This is deliberately separate from <see cref="ContinuityService"/>: metadata
/// checks must never masquerade as vision evidence.
/// </summary>
public sealed class VisualConsistencyService(
    StudioDbContext db,
    AssetStore assets,
    ICodexRuntime codex,
    StudioRepository repository,
    TimeProvider timeProvider,
    ILogger<VisualConsistencyService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, bool, int, Exception?> LogVisualAuditFailure = LoggerMessage.Define<bool, int>(
        LogLevel.Warning, new EventId(4201, nameof(VisualConsistencyService)),
        "Visual audit failed (started {Started}, exit {ExitCode}). The looking glass was foggy, not prophetic.");
    private const string ScopeNote = "Codex inspected the exact current image against the versioned shot contract. It proposes review decisions but never changes world, character, style, or reference authorities.";

    public async Task<ShotVisualAuditSummary?> GetCurrentAsync(Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot?.CurrentAssetId is not Guid assetId) return null;
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId, cancellationToken);
        if (asset is null) return null;
        var contractHash = VisualContractHash.Compute(shot);
        var records = await db.ShotVisualAudits.AsNoTracking()
            .Where(x => x.ShotId == shotId && x.ShotVersion == shot.Version && x.AssetId == asset.Id
                && x.AssetHash == asset.ContentHash && x.ContractHash == contractHash)
            .ToListAsync(cancellationToken);
        var record = records.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return record is null ? null : Map(record);
    }

    public async Task<RepositoryResult<ShotVisualAuditSummary>> RunAsync(Guid shotId, bool force, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        if (shot is null) return RepositoryResult<ShotVisualAuditSummary>.NotFound();
        if (shot.CurrentAssetId is not Guid assetId)
            return RepositoryResult<ShotVisualAuditSummary>.Conflict("Generate or import a current image before running the visual consistency check.");
        var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assetId && x.Kind == AssetKind.Image.ToString(), cancellationToken);
        if (asset is null) return RepositoryResult<ShotVisualAuditSummary>.Conflict("The current image is unavailable. Choose another revision before checking it.");

        var contractHash = VisualContractHash.Compute(shot);
        if (!force)
        {
            var existingRecords = await db.ShotVisualAudits.AsNoTracking()
                .Where(x => x.ShotId == shotId && x.ShotVersion == shot.Version && x.AssetId == asset.Id
                    && x.AssetHash == asset.ContentHash && x.ContractHash == contractHash && x.State == "Completed")
                .ToListAsync(cancellationToken);
            var existing = existingRecords.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (existing is not null) return RepositoryResult<ShotVisualAuditSummary>.Ok(Map(existing));
        }

        var runtime = codex.Inspect();
        if (!runtime.Installed || !runtime.Authenticated)
            return RepositoryResult<ShotVisualAuditSummary>.Unavailable(runtime.Detail);

        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == shot.ProjectId, cancellationToken);
        var ids = DeserializeStrings(shot.ReferenceIdsJson);
        var references = await db.References.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        var versions = await db.ReferenceVersions.AsNoTracking()
            .Where(x => ids.Contains(x.ReferenceId))
            .ToListAsync(cancellationToken);
        var authorityText = references.Select(reference =>
        {
            var version = versions.SingleOrDefault(item => item.ReferenceId == reference.Id && item.Version == reference.CurrentVersion);
            return $"- {reference.Id}: {reference.Name} ({reference.Category}) v{reference.CurrentVersion}. {version?.Description} LOCKED: {version?.LockedConstraint}";
        });
        var prompt = BuildPrompt(shot, project, ids, authorityText);
        var working = Path.Combine(Path.GetTempPath(), "Framewright", "visual-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        CodexProcessResult result;
        try
        {
            var arguments = new List<string>
            {
                "exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "-C", working,
                "--image", assets.ResolveContentPath(asset), "--color", "never", "-"
            };
            result = await codex.RunAsync(arguments, working, TimeSpan.FromMinutes(3), cancellationToken, prompt);
        }
        catch (OperationCanceledException) { throw; }
        finally { try { Directory.Delete(working, recursive: true); } catch { } }

        if (!result.Started || result.ExitCode != 0 || !TryParsePayload(result.StandardOutput, out var payload))
        {
            LogVisualAuditFailure(logger, result.Started, result.ExitCode, null);
            return RepositoryResult<ShotVisualAuditSummary>.Unavailable("Codex could not return a safe structured visual audit. The image and shot contract were not changed.");
        }

        var findings = NormalizeFindings(payload.Findings);
        var gate = findings.Any(x => x.Severity == VisualAuditSeverity.Block) ? "Blocked"
            : findings.Count > 0 ? "Review" : "Clear";
        var proposal = NormalizeProposal(payload.AdoptedShotProposal, shot, ids, references);
        var now = timeProvider.GetUtcNow();
        var record = new ShotVisualAuditRecord
        {
            Id = Guid.NewGuid(),
            ShotId = shot.Id,
            ShotVersion = shot.Version,
            AssetId = asset.Id,
            AssetHash = asset.ContentHash,
            ContractHash = contractHash,
            State = "Completed",
            GateState = gate,
            Summary = Bound(payload.Summary, 600, findings.Count == 0 ? "The visible frame matches the observable shot contract." : "The frame needs reconciliation before production final."),
            FindingsJson = JsonSerializer.Serialize(findings, Json),
            DecisionsJson = "[]",
            AdoptedShotProposalJson = proposal is null ? null : JsonSerializer.Serialize(proposal, Json),
            CreatedAt = now,
            CompletedAt = now
        };
        db.ShotVisualAudits.Add(record);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "VisualConsistencyAudited", TargetType = "Shot", TargetId = shot.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { auditId = record.Id, shot.Version, assetId = asset.Id, asset.ContentHash, contractHash, gate, findings = findings.Count }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<ShotVisualAuditSummary>.Ok(Map(record));
    }

    public async Task<RepositoryResult<VisualReconciliationPlan>> ReconcileAsync(
        Guid shotId, Guid auditId, ReconcileVisualAuditRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.Shots.SingleOrDefaultAsync(x => x.Id == shotId, cancellationToken);
        var audit = await db.ShotVisualAudits.SingleOrDefaultAsync(x => x.Id == auditId && x.ShotId == shotId, cancellationToken);
        if (shot is null || audit is null) return RepositoryResult<VisualReconciliationPlan>.NotFound();
        if (shot.CurrentAssetId != audit.AssetId || shot.Version != audit.ShotVersion || VisualContractHash.Compute(shot) != audit.ContractHash)
            return RepositoryResult<VisualReconciliationPlan>.Conflict("The image or shot contract changed after this check. Run the visual check again before reconciling it.");

        var findings = Deserialize<VisualAuditFindingSummary[]>(audit.FindingsJson) ?? [];
        var decisions = request.Decisions
            .GroupBy(x => x.FindingId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .Where(x => findings.Any(finding => finding.Id == x.FindingId))
            .Select(x => new VisualAuditDecisionSummary(x.FindingId, x.Action, Bound(x.MoreDetails, 1200, "")))
            .ToArray();
        if (decisions.Length != findings.Length)
            return RepositoryResult<VisualReconciliationPlan>.Invalid("Choose Fix image, Adopt image, or Decide later for every discrepancy.");

        audit.DecisionsJson = JsonSerializer.Serialize(decisions, Json);
        var fixes = decisions.Where(x => x.Action == VisualReconciliationAction.FixImage).ToArray();
        var adopts = decisions.Where(x => x.Action == VisualReconciliationAction.AdoptImage).ToArray();
        var later = decisions.Where(x => x.Action == VisualReconciliationAction.DecideLater).ToArray();
        VisualCorrectionRoute? route = fixes.Length == 0 ? null : fixes.Any(decision => findings.Single(x => x.Id == decision.FindingId).SuggestedRoute == VisualCorrectionRoute.RebuildFromSketch)
            ? VisualCorrectionRoute.RebuildFromSketch : VisualCorrectionRoute.RefineCurrent;
        var direction = string.Join("\n", fixes.Select(decision =>
        {
            var finding = findings.Single(x => x.Id == decision.FindingId);
            return $"- {finding.FixInstruction}{(decision.MoreDetails.Length > 0 ? $" Additional direction: {decision.MoreDetails}" : "")}";
        }));

        ShotSummary? updatedShot = null;
        var contractApplied = false;
        if (request.ApplyContract)
        {
            if (adopts.Length == 0)
                return RepositoryResult<VisualReconciliationPlan>.Conflict("Choose Accept what's shown for at least one discrepancy before applying a shot override.");
            var adoptedCategories = adopts
                .Select(decision => findings.Single(finding => finding.Id == decision.FindingId).Category)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preservedConstraints = DeserializeStrings(shot.ConstraintsJson)
                .Where(rule => !VisualOverrideContract.IsOverrideForAny(rule, adoptedCategories));
            var visualOverrides = adopts.Select(decision =>
            {
                var finding = findings.Single(item => item.Id == decision.FindingId);
                return VisualOverrideContract.Build(finding, decision.MoreDetails);
            });
            var result = await repository.UpdateShotAsync(shot.Id, new UpdateShotRequest(
                shot.UpdatedAt, shot.Title, shot.Description, shot.DurationFrames, shot.Camera, shot.Action,
                DeserializeStrings(shot.ReferenceIdsJson), preservedConstraints.Concat(visualOverrides).ToArray()), cancellationToken);
            if (result.Kind != RepositoryResultKind.Ok || result.Value is null)
                return new(default, result.Kind, result.Error);
            updatedShot = result.Value;
            contractApplied = true;
            audit.State = "SupersededByContract";
        }

        audit.GateState = contractApplied ? "Superseded" : later.Length > 0 || adopts.Length > 0 ? "Review" : fixes.Length > 0 ? "Blocked" : "Clear";
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "VisualConsistencyReconciled", TargetType = "ShotVisualAudit", TargetId = audit.Id.ToString(),
            PayloadJson = JsonSerializer.Serialize(new { fixes = fixes.Length, adopts = adopts.Length, later = later.Length, route, contractApplied }),
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        var detail = contractApplied
            ? fixes.Length > 0
                ? "The full scene intent was preserved, accepted differences became narrow shot-specific overrides, and the selected image corrections are ready for one combined generation."
                : "The full scene intent was preserved. Accepted differences are recorded as narrow shot-specific overrides for future iterations; global authorities were not changed. Run a fresh check before production final."
            : fixes.Length > 0
                ? route == VisualCorrectionRoute.RebuildFromSketch
                    ? "Structural mismatches will rebuild from the saved sketch and immutable authorities; the incorrect current image is not used as the composition source."
                    : "Cosmetic mismatches can refine the current frame while rebuilding the named anatomy or rendering defects."
                : "The unresolved decisions remain visible in Review. Nothing was changed.";
        return RepositoryResult<VisualReconciliationPlan>.Ok(new(Map(audit), contractApplied, fixes.Length > 0, route, direction, updatedShot, detail));
    }

    private static string BuildPrompt(ShotRecord shot, ProjectRecord project, string[] ids, IEnumerable<string> authorities)
    {
        var constraints = DeserializeStrings(shot.ConstraintsJson);
        var baseRules = constraints.Where(rule => !VisualOverrideContract.IsOverride(rule)).Select(rule => $"- {rule}");
        var visualOverrides = constraints.Where(VisualOverrideContract.IsOverride).Select(rule => $"- {rule}");
        return $$"""
        You are Framewright's strict visual continuity auditor. Inspect the ONE attached current frame. Compare visible pixels to the observable contract below. Return ONLY one JSON object, with no markdown.

        Required schema:
        {
          "summary": "short plain-language verdict",
          "findings": [{
            "category": "blocking|subject-count|pose|prop|location|camera|style|identity|wardrobe|anatomy|other",
            "severity": "Review|Block",
            "title": "short discrepancy",
            "contractExpectation": "what the shot explicitly requires",
            "observedImage": "what is visibly present instead",
            "confidence": 0.0,
            "suggestedRoute": "RefineCurrent|RebuildFromSketch",
            "fixInstruction": "specific image-generation instruction"
          }],
          "adoptedShotProposal": {
            "description": "complete revised scene description that preserves every unaffected detail from DESCRIPTION and changes only contradictions visible in the image",
            "action": "complete revised action that preserves every unaffected beat from ACTION and changes only contradictions visible in the image",
            "constraints": ["complete revised shot-local constraint list; retain every unaffected rule and change only contradicted observable rules"],
            "referenceIds": ["only IDs from ALLOWED REFERENCE IDS"],
            "rationale": "precisely which narrow visible differences would be accepted"
          }
        }

        Audit rules:
        - Report visible contradictions and significant omissions, not subjective taste.
        - Missing required people, wrong count, wrong pose/blocking, wrong prop scale/location, or a different environment is Block and RebuildFromSketch.
        - Local anatomy defects, minor wardrobe drift, or finish/style drift can be Review and RefineCurrent unless they are pervasive.
        - Explicitly inspect hands, limbs, faces, subject count, full-body crop, pose, prop placement/scale, environment, camera/framing, and rendering style.
        - Do not excuse a mismatch because the image is attractive.
        - Do not invent invisible facts. If uncertain, use Review and state the uncertainty.
        - If there are no material discrepancies, findings must be empty.
        - APPROVED SHOT-SPECIFIC VISUAL OVERRIDES take precedence only where they directly conflict with older scene wording or base shot rules. Never treat an override as permission to discard unrelated scene context.
        - The adoptedShotProposal is a conservative full-scene rewrite, never a caption. Preserve the setting, cast, blocking, camera, action beats, props, and constraints that the image does not contradict. It never edits a global authority, character identity, world rule, or style definition.

        SHOT {{shot.Code}} v{{shot.Version}}
        DESCRIPTION: {{shot.Description}}
        ACTION: {{shot.Action}}
        CAMERA: {{shot.Camera}}
        BASE SHOT RULES:
        {{string.Join("\n", baseRules)}}

        APPROVED SHOT-SPECIFIC VISUAL OVERRIDES (narrow precedence; preserve everything else):
        {{string.Join("\n", visualOverrides)}}

        PROJECT VISUAL STYLE: {{project.VisualStyle}}
        WORLD CANON: {{project.WorldCanon}}
        PROJECT PRESERVE RULES: {{project.PromptDirectives}}
        PROJECT NEGATIVE RULES: {{project.NegativeDirectives}}

        ALLOWED REFERENCE IDS: {{string.Join(", ", ids)}}
        IMMUTABLE AUTHORITIES:
        {{string.Join("\n", authorities)}}
        """;
    }

    private static List<VisualAuditFindingSummary> NormalizeFindings(List<VisualFindingPayload>? payload)
    {
        var result = new List<VisualAuditFindingSummary>();
        foreach (var item in payload ?? [])
        {
            var expectation = Bound(item.ContractExpectation, 800, "The visible frame should match the shot contract.");
            var observed = Bound(item.ObservedImage, 800, "The visible frame appears inconsistent.");
            if (expectation.Length == 0 || observed.Length == 0) continue;
            var route = Enum.TryParse<VisualCorrectionRoute>(item.SuggestedRoute, true, out var parsedRoute) ? parsedRoute : VisualCorrectionRoute.RefineCurrent;
            var severity = Enum.TryParse<VisualAuditSeverity>(item.Severity, true, out var parsedSeverity) ? parsedSeverity : VisualAuditSeverity.Review;
            var category = Bound(item.Category, 80, "other").ToLowerInvariant();
            result.Add(new($"visual-{result.Count + 1}-{category.Replace(' ', '-')}", category, severity,
                Bound(item.Title, 160, "Visible mismatch"), expectation, observed, Math.Clamp(item.Confidence, 0, 1), route,
                Bound(item.FixInstruction, 1000, $"Rebuild the frame so {expectation}")));
        }
        return result.Take(12).ToList();
    }

    private static ShotContractProposal? NormalizeProposal(ShotProposalPayload? payload, ShotRecord shot, string[] allowedIds, IReadOnlyCollection<ReferenceRecord> references)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Description)) return null;
        var allowed = allowedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A shot may intentionally drop a cast/pose/prop reference when the
        // artist adopts a changed image. Its selected Style authority is not a
        // depicted subject, though, and must never disappear as a side effect
        // of describing the pixels. The tiny covenant survives the crop.
        var protectedStyleIds = references.Where(x => string.Equals(x.Category, "Style", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id);
        var referenceIds = (payload.ReferenceIds ?? []).Where(allowed.Contains).Concat(protectedStyleIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(
            Bound(payload.Description, 2000, shot.Description),
            Bound(payload.Action, 1200, shot.Action),
            (payload.Constraints ?? []).Select(x => Bound(x, 500, "")).Where(x => x.Length > 0).Distinct().Take(20).ToArray(),
            referenceIds,
            Bound(payload.Rationale, 800, "Update only this shot's observable contract to match the chosen image."));
    }

    private static bool TryParsePayload(string output, out VisualAuditPayload payload)
    {
        payload = new();
        try
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            payload = JsonSerializer.Deserialize<VisualAuditPayload>(output[start..(end + 1)], Json) ?? new();
            return payload.Findings is not null;
        }
        catch (JsonException) { return false; }
    }

    private static ShotVisualAuditSummary Map(ShotVisualAuditRecord record) => new(
        record.Id, record.ShotId, record.ShotVersion, record.AssetId, record.AssetHash, record.ContractHash,
        record.State, record.GateState, record.Summary,
        Deserialize<VisualAuditFindingSummary[]>(record.FindingsJson) ?? [],
        Deserialize<VisualAuditDecisionSummary[]>(record.DecisionsJson) ?? [],
        Deserialize<ShotContractProposal>(record.AdoptedShotProposalJson),
        record.CreatedAt, record.CompletedAt, ScopeNote);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return default; }
    }

    private static string[] DeserializeStrings(string json) => Deserialize<string[]>(json) ?? [];
    private static string Bound(string? value, int max, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private sealed class VisualAuditPayload
    {
        public string Summary { get; set; } = "";
        public List<VisualFindingPayload>? Findings { get; set; } = [];
        public ShotProposalPayload? AdoptedShotProposal { get; set; }
    }

    private sealed class VisualFindingPayload
    {
        public string Category { get; set; } = "other";
        public string Severity { get; set; } = "Review";
        public string Title { get; set; } = "";
        public string ContractExpectation { get; set; } = "";
        public string ObservedImage { get; set; } = "";
        public double Confidence { get; set; }
        public string SuggestedRoute { get; set; } = "RefineCurrent";
        public string FixInstruction { get; set; } = "";
    }

    private sealed class ShotProposalPayload
    {
        public string Description { get; set; } = "";
        public string Action { get; set; } = "";
        public List<string>? Constraints { get; set; } = [];
        public List<string>? ReferenceIds { get; set; } = [];
        public string Rationale { get; set; } = "";
    }
}

/// <summary>
/// Keeps artist-approved departures separate from the stable scene brief. An
/// override has narrow precedence: it changes one audited visual fact and is
/// never permission to collapse the rest of the shot into a caption.
/// </summary>
internal static class VisualOverrideContract
{
    private const string Prefix = "APPROVED VISUAL OVERRIDE";

    public static bool IsOverride(string value)
        => value.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsOverrideForAny(string value, IReadOnlySet<string> categories)
    {
        if (!IsOverride(value)) return false;
        return categories.Any(category => value.StartsWith($"{Prefix} [{category}]", StringComparison.OrdinalIgnoreCase));
    }

    public static string Build(VisualAuditFindingSummary finding, string moreDetails)
    {
        var direction = $"{Prefix} [{finding.Category}]: {finding.ObservedImage} This supersedes only the directly conflicting scene wording or base shot rule; preserve every other scene, action, camera, reference, and authority detail.";
        return string.IsNullOrWhiteSpace(moreDetails) ? direction : $"{direction} Artist direction: {moreDetails.Trim()}";
    }
}

internal static class VisualContractHash
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    public static string Compute(ShotRecord shot)
    {
        var json = JsonSerializer.Serialize(new
        {
            shot.Code, shot.Title, shot.Description, shot.DurationFrames, shot.Camera, shot.Action,
            referenceIds = JsonSerializer.Deserialize<string[]>(shot.ReferenceIdsJson) ?? [],
            constraints = JsonSerializer.Deserialize<string[]>(shot.ConstraintsJson) ?? []
        }, Canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
