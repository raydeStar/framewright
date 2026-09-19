using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Turns the frozen generation packet into an artist-readable dispatch proof.
/// This is deliberately deterministic: it reports what Framewright will send,
/// never a hopeful description of what a model might understand.
/// </summary>
public sealed class GenerationPreflightService(
    StudioDbContext db,
    GenerationOrchestrator orchestrator,
    WorkflowLibrary workflows,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<RepositoryResult<GenerationPreflightSummary>> InspectAsync(
        Guid manifestId, string adapterId, CancellationToken cancellationToken)
    {
        var manifest = await db.GenerationManifests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == manifestId, cancellationToken);
        if (manifest is null) return RepositoryResult<GenerationPreflightSummary>.NotFound();
        var adapter = orchestrator.ListAdapters().SingleOrDefault(item => item.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null) return RepositoryResult<GenerationPreflightSummary>.Invalid("The selected generation adapter is not installed.");

        var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == manifest.ProjectId, cancellationToken);
        var bindings = JsonSerializer.Deserialize<AuthorityBinding[]>(manifest.AuthoritiesJson, WebJson) ?? [];
        var referenceRows = new List<(AuthorityBinding Binding, bool HasImage)>();
        foreach (var binding in bindings)
        {
            var version = await db.ReferenceVersions.AsNoTracking().SingleOrDefaultAsync(
                item => item.ReferenceId == binding.Id && item.Version == binding.Version, cancellationToken);
            referenceRows.Add((binding, version?.ImageAssetId is not null));
        }

        var currentFrameEdit = GenerationReferenceDispatchPlanner.IsCurrentFrameManifest(manifest.ManifestJson, manifest.CreativeBrief);
        var workflowPath = ResolveWorkflowPath(manifest, adapterId, referenceRows.Any(item => item.HasImage), currentFrameEdit);
        var workflow = ResolveWorkflow(workflowPath);
        var validation = workflow is null ? null : workflows.Validate(workflow);
        var workflowCapacity = workflowPath is null
            ? 0
            : GenerationReferenceDispatchPlanner.CountComfyVisualSlots(await File.ReadAllTextAsync(workflowPath, cancellationToken));
        var maxReferences = GenerationReferenceDispatchPlanner.CapacityForAdapter(adapterId, workflowCapacity);
        var dispatchPlan = GenerationReferenceDispatchPlanner.Plan(
            referenceRows.Select(item => (item.Binding, item.HasImage)).ToArray(),
            maxReferences,
            currentFrameEdit);
        var references = dispatchPlan.Select(item => new GenerationPreflightReference(
            item.Binding.Id,
            item.Binding.Name,
            item.Binding.Category,
            item.Binding.Version,
            item.HasApprovedImage,
            item.WillBindVisually,
            item.Detail)).ToArray();

        var checks = new List<GenerationPreflightCheck>();
        AddCheck(checks, adapter.Routes.Contains(Enum.Parse<GenerationRoute>(manifest.Route)) && adapter.Purposes.Contains(Enum.Parse<GenerationPurpose>(manifest.Purpose)),
            "Adapter supports this packet", $"{adapter.Name} declares {manifest.Route} / {manifest.Purpose} support.");
        AddCheck(checks, adapter.CanDispatch, "Provider is ready", adapter.Detail);
        AddCheck(checks, manifest.State == ManifestState.Prepared.ToString(), "Packet has not been dispatched", $"Current manifest state: {manifest.State}.");
        if (validation is not null)
            AddCheck(checks, validation.Valid, "Workflow contract is valid", validation.Valid ? validation.Summary : string.Join("; ", validation.Problems));
        if (references.Count(item => item.HasApprovedImage) > maxReferences && maxReferences > 0)
            checks.Add(new("Review", "Visual reference capacity is bounded", $"{references.Count(item => item.HasApprovedImage)} approved images are available; {maxReferences} fit this workflow. All textual canon still travels."));
        checks.Add(new("Pass", "Project delivery contract attached", $"{project.DeliveryWidth} × {project.DeliveryHeight} at {project.FramesPerSecond} fps ({project.AspectRatio}), {project.ColorSpace}; audio {project.AudioSampleRate / 1000d:0.#} kHz."));
        checks.Add(new("Pass", "Locked rules attached", $"{JsonSerializer.Deserialize<string[]>(manifest.ConstraintsJson)?.Length ?? 0} shot constraints plus immutable authority canon."));

        var summary = new GenerationPreflightSummary(
            manifest.Id,
            manifest.ManifestHash,
            adapter.Id,
            adapter.Name,
            manifest.Route,
            manifest.Purpose,
            workflow?.Id ?? adapter.Id,
            workflow?.Name ?? adapter.Name,
            workflow?.Capabilities ?? [],
            maxReferences,
            manifest.CompositionAssetId is not null,
            references,
            JsonSerializer.Deserialize<string[]>(manifest.ConstraintsJson) ?? [],
            project.DeliveryWidth,
            project.DeliveryHeight,
            project.FramesPerSecond,
            project.ColorSpace,
            project.AudioSampleRate,
            checks,
            checks.All(check => check.State != "Block"));
        return RepositoryResult<GenerationPreflightSummary>.Ok(summary);
    }

    private string? ResolveWorkflowPath(GenerationManifestRecord manifest, string adapterId, bool hasVisualReferences, bool currentFrameEdit)
    {
        if (adapterId.Equals(ComfyUiGenerationAdapter.AdapterId, StringComparison.OrdinalIgnoreCase))
        {
            if (currentFrameEdit)
                return ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalCurrentFrameWorkflowPath"]
                    ?? configuration["Integrations:ComfyUi:ExternalWorkflowPath"]);
            if (manifest.CompositionAssetId is not null)
                return ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalWorkflowPath"]);
            if (hasVisualReferences
                && ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalReferenceWorkflowPath"]) is { } referencePath)
                return referencePath;
            return ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalTextWorkflowPath"]);
        }
        if (adapterId.Equals(ComfyUiVideoGenerationAdapter.AdapterId, StringComparison.OrdinalIgnoreCase))
            return ComfyUi.ResolveWorkflowPath(configuration["Integrations:ComfyUi:ExternalVideoWorkflowPath"]);
        return null;
    }

    private WorkflowEntry? ResolveWorkflow(string? workflowPath)
    {
        if (workflowPath is null) return null;
        return workflows.LoadIndex().FirstOrDefault(item =>
            string.Equals(
                Path.GetFullPath(Path.Combine(workflows.Root, item.File)),
                workflowPath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void AddCheck(List<GenerationPreflightCheck> checks, bool pass, string title, string detail)
        => checks.Add(new(pass ? "Pass" : "Block", title, detail));
}
