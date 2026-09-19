using ModelContextProtocol.Server;
using StoryboardStudio.Core;
using System.ComponentModel;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Curated read model for Codex. Tools return product contracts, never database
/// rows, secrets, arbitrary files, or provider controls.
/// </summary>
[McpServerToolType]
public sealed class FramewrightMcpTools(
    StudioRepository repository,
    AssetStore assets,
    GenerationOrchestrator orchestrator,
    RuntimeReadinessService readiness)
{
    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("List Framewright shots with current stage, approval, camera, continuity, and open-note counts. Read-only.")]
    public async Task<IReadOnlyList<ShotSummary>> ListShots(CancellationToken cancellationToken)
        => (await repository.GetSnapshotAsync(cancellationToken)).Shots;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("Get one Framewright shot and its exact current references, rules, open notes, and recent jobs. Read-only.")]
    public async Task<object> GetShot(
        [Description("Shot UUID or shot code such as SH-030")] string shot,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetSnapshotAsync(cancellationToken);
        var selected = ResolveShot(snapshot, shot) ?? throw new InvalidOperationException($"Shot '{shot}' was not found.");
        return new
        {
            shot = selected,
            references = snapshot.References.Where(reference => selected.ReferenceIds.Contains(reference.Id, StringComparer.OrdinalIgnoreCase)),
            openNotes = snapshot.Comments.Where(note => note.ShotId == selected.Id && note.State == "Open"),
            recentJobs = snapshot.Jobs.Where(job => job.ShotId == selected.Id)
        };
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("Get the immutable candidate history and generation manifests for one Framewright shot. Read-only.")]
    public async Task<object> GetRevisionPacket(
        [Description("Shot UUID or shot code such as SH-030")] string shot,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetSnapshotAsync(cancellationToken);
        var selected = ResolveShot(snapshot, shot) ?? throw new InvalidOperationException($"Shot '{shot}' was not found.");
        return new
        {
            shot = selected,
            candidates = await repository.GetCandidatesAsync(selected.Id, cancellationToken),
            ratifiedVersions = await repository.GetVersionsAsync(selected.Id, cancellationToken),
            manifests = await repository.GetManifestsAsync(selected.Id, cancellationToken)
        };
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("List approved Framewright authority references and their locked identity or world constraints. Read-only.")]
    public async Task<IReadOnlyList<ReferenceSummary>> ListAuthorities(CancellationToken cancellationToken)
        => (await repository.GetSnapshotAsync(cancellationToken)).References;

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("Get one authority and its immutable version history. Read-only.")]
    public async Task<object> GetAuthority(
        [Description("Authority id or exact authority name")] string authority,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetSnapshotAsync(cancellationToken);
        var selected = snapshot.References.FirstOrDefault(item =>
            string.Equals(item.Id, authority, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Name, authority, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Authority '{authority}' was not found.");
        return new { authority = selected, versions = await repository.GetReferenceVersionsAsync(selected.Id, cancellationToken) };
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("List unresolved Framewright feedback notes, optionally filtered to one shot. Read-only.")]
    public async Task<IReadOnlyList<CommentSummary>> ListOpenNotes(
        [Description("Optional shot UUID or code")] string? shot = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await repository.GetSnapshotAsync(cancellationToken);
        var notes = snapshot.Comments.Where(note => note.State == "Open");
        if (!string.IsNullOrWhiteSpace(shot))
        {
            var selected = ResolveShot(snapshot, shot) ?? throw new InvalidOperationException($"Shot '{shot}' was not found.");
            notes = notes.Where(note => note.ShotId == selected.Id);
        }
        return notes.ToArray();
    }

    [McpServerTool(ReadOnly = true, Destructive = false, OpenWorld = false), Description("Get a frozen Framewright ImageGen packet, including local image paths that Codex can inspect and pass to built-in ImageGen. Read-only.")]
    public async Task<object> GetImageGenerationPacket(
        [Description("Prepared manifest UUID")] Guid manifestId,
        CancellationToken cancellationToken)
    {
        var admission = await readiness.AdmitGenerationAsync(cancellationToken);
        if (admission.Kind != RepositoryResultKind.Ok)
            throw new InvalidOperationException(admission.Error ?? "Generation prerequisites are not ready.");

        var snapshot = await repository.GetSnapshotAsync(cancellationToken);
        ShotSummary? owner = null;
        GenerationManifestSummary? selected = null;
        foreach (var candidate in snapshot.Shots)
        {
            selected = (await repository.GetManifestsAsync(candidate.Id, cancellationToken)).SingleOrDefault(item => item.Id == manifestId);
            if (selected is null) continue;
            owner = candidate;
            break;
        }
        if (owner is null || selected is null) throw new InvalidOperationException($"Prepared manifest '{manifestId}' was not found.");
        if (selected.State != ManifestState.Prepared) throw new InvalidOperationException("Only a prepared, undispatched manifest can be handed to Codex.");
        string? compositionPath = null;
        if (selected.CompositionAssetId is { } compositionId)
        {
            var composition = await assets.GetAsync(compositionId, cancellationToken);
            if (composition is not null) compositionPath = assets.ResolveContentPath(composition);
        }
        var referencePaths = new List<object>();
        foreach (var binding in selected.Authorities)
        {
            var version = (await repository.GetReferenceVersionsAsync(binding.Id, cancellationToken)).SingleOrDefault(item => item.Version == binding.Version);
            if (version?.ImageAssetId is not { } imageId) continue;
            var asset = await assets.GetAsync(imageId, cancellationToken);
            if (asset is not null) referencePaths.Add(new { binding.Name, binding.Category, binding.Version, path = assets.ResolveContentPath(asset) });
        }
        return new
        {
            manifest = selected,
            shot = owner,
            compositionPath,
            referencePaths,
            instruction = "Inspect each local image with view_image, call built-in ImageGen using the creative brief and locked constraints, then call import_codex_candidate with the resulting local PNG/JPEG path."
        };
    }

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Import a user-approved Codex ImageGen result as the next unapproved Framewright candidate. This writes product state and requires approval.")]
    public async Task<JobSummary> ImportCodexCandidate(
        [Description("Prepared manifest UUID")] Guid manifestId,
        [Description("Exact manifest hash returned by get_image_generation_packet")] string expectedManifestHash,
        [Description("Local PNG/JPEG output path under the Codex generated_images directory")] string generatedImagePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(generatedImagePath);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var allowedRoot = Path.GetFullPath(Path.Combine(codexHome, "generated_images")) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Candidate import is limited to Codex generated_images output.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The generated image does not exist.", fullPath);
        var completed = await orchestrator.CompleteCodexHandoffAsync(manifestId, expectedManifestHash, fullPath, cancellationToken);
        if (completed.Kind != RepositoryResultKind.Ok || completed.Value is null) throw new InvalidOperationException(completed.Error ?? "The Codex candidate could not be imported.");
        return completed.Value;
    }

    private static ShotSummary? ResolveShot(StudioSnapshot snapshot, string value)
        => Guid.TryParse(value, out var id)
            ? snapshot.Shots.FirstOrDefault(shot => shot.Id == id)
            : snapshot.Shots.FirstOrDefault(shot => string.Equals(shot.Code, value, StringComparison.OrdinalIgnoreCase));
}
