using StoryboardStudio.Core;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>ChatGPT-authenticated image generation in an isolated job directory.</summary>
public sealed class CodexImageGenerationAdapter(ICodexRuntime runtime, IConfiguration configuration) : IGenerationAdapter
{
    public const string AdapterId = "codex-imagegen";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public GenerationAdapterSummary Describe()
    {
        var status = runtime.Inspect();
        var connected = status.Installed && status.Authenticated;
        var unattended = configuration.GetValue("Integrations:Codex:NonInteractiveImageEnabled", false);
        return new(AdapterId, "Codex ImageGen", "ChatGPT login", connected ? "Connected" : "NeedsSetup",
            connected
                ? unattended
                    ? "Experimental unattended Codex ImageGen execution is enabled."
                    : "ChatGPT login is connected. Prepare a handoff, then Codex uses Framewright MCP and built-in ImageGen; no API key is required."
                : status.Detail,
            connected && unattended, [GenerationRoute.PrecisionDraft], [GenerationPurpose.Draft, GenerationPurpose.Final]);
    }

    public async Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
    {
        var descriptor = Describe();
        if (!descriptor.CanDispatch) throw new GenerationDispatchException(descriptor.Detail);
        var root = Path.Combine(Path.GetTempPath(), "Framewright", "codex-imagegen", context.JobId.ToString("N"));
        Directory.CreateDirectory(root);
        var providerStarted = false;
        try
        {
            await context.ReportAsync(18, "Preparing isolated Codex ImageGen workspace", cancellationToken);
            var images = new List<string>();
            var attachmentMap = new List<string>();
            var plannedReferences = GenerationReferenceDispatchPlanner.PlanImages(
                context.ReferenceImages,
                GenerationReferenceDispatchPlanner.CapacityForAdapter(AdapterId),
                context.IsCurrentFrameEdit);
            if (context.CompositionPath is not null && context.Composition is not null)
            {
                images.Add(await CopyInputAsync(context.CompositionPath, root, "01-current-frame", context.Composition.MimeType, cancellationToken));
                attachmentMap.Add("- IMAGE 1: CURRENT VISUAL BASE. Preserve its composition, cast, camera, and unmentioned content; its rendering style yields to the approved Style authority.");
            }
            var index = images.Count + 1;
            foreach (var reference in plannedReferences.Where(item => item.Decision.WillBindVisually))
            {
                var imageNumber = images.Count + 1;
                var stem = $"{index++:00}-{SafeStem(reference.Binding.Category)}-{SafeStem(reference.Binding.Name)}-v{reference.Binding.Version}";
                images.Add(await CopyInputAsync(reference.Path, root, stem, reference.Asset.MimeType, cancellationToken));
                attachmentMap.Add($"- IMAGE {imageNumber}: {reference.Binding.Name} ({reference.Binding.Category}) v{reference.Binding.Version}. {ReferenceRole(reference.Binding.Category)}");
            }
            var textOnlyMap = plannedReferences.Where(item => !item.Decision.WillBindVisually)
                .Select(item => $"- {item.Binding.Name} ({item.Binding.Category}) v{item.Binding.Version}: {item.Decision.Detail}")
                .ToArray();
            var outputPath = Path.Combine(root, "framewright-result.png");
            // --image accepts one-or-more values, so the immutable prompt travels
            // over stdin. This keeps every CLI option parseable and avoids turning
            // the first prompt word into a subcommand.
            // Docker is already the outer trust boundary and runs this process as
            // an unprivileged user. Its kernel forbids the nested user namespace
            // that Codex's workspace-write sandbox needs, so the container may
            // explicitly opt into danger-full-access *inside that container*.
            // Native workstation runs retain workspace-write by default.
            var sandbox = configuration["Integrations:Codex:Sandbox"] is "danger-full-access"
                ? "danger-full-access"
                : "workspace-write";
            var arguments = new List<string> { "exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", sandbox, "-C", root };
            foreach (var image in images) { arguments.Add("--image"); arguments.Add(image); }
            arguments.Add("--color"); arguments.Add("never"); arguments.Add("-");
            // Codex cannot reconnect to an interrupted CLI child. Persist the
            // submission boundary before starting it so restart recovery fails
            // closed and asks for an explicit retry instead of duplicating work.
            await context.ReportSubmittedAsync(30, "Codex ImageGen started — generating with ChatGPT login", context.JobId.ToString("N"), cancellationToken);
            providerStarted = true;
            var timeout = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Integrations:Codex:ImageTimeoutMinutes", 12), 2, 30));
            var result = await runtime.RunAsync(arguments, root, timeout, cancellationToken, BuildPrompt(context, outputPath, attachmentMap, textOnlyMap));
            if (!result.Started) throw new GenerationDispatchException($"Codex ImageGen could not start: {Bound(result.StandardError)}", false);
            if (result.ExitCode != 0) throw new GenerationDispatchException($"Codex ImageGen failed: {Bound(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput)}", true, context.JobId.ToString("N"));
            await context.ReportAsync(90, "Validating Codex ImageGen result", cancellationToken);
            var generated = FindGeneratedImage(root, images) ?? throw new GenerationDispatchException("Codex completed without writing a PNG or JPEG result in its isolated job directory.", true, context.JobId.ToString("N"));
            var info = new FileInfo(generated);
            if (info.Length is <= 0 or > AssetStore.MaxImageBytes) throw new GenerationDispatchException("Codex produced an empty image or one larger than Framewright's 25 MB safety limit.", true, context.JobId.ToString("N"));
            var mime = DetectMime(generated) ?? throw new GenerationDispatchException("Codex produced a file that is not a recognizable PNG or JPEG.", true, context.JobId.ToString("N"));
            if (RequiresVisualAcceptance(context))
            {
                await context.ReportAsync(94, "Checking the visible Last Frame change", cancellationToken);
                var auditArguments = new List<string>
                {
                    "exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "-C", root,
                    "--image", generated, "--color", "never", "-"
                };
                var audit = await runtime.RunAsync(auditArguments, root, TimeSpan.FromMinutes(3), cancellationToken, BuildAcceptancePrompt(context));
                if (!audit.Started || audit.ExitCode != 0 || !TryParseAcceptance(audit.StandardOutput, out var acceptance))
                    throw new GenerationDispatchException("Codex ImageGen returned a frame, but Framewright could not verify the visible Last Frame change. The result was not selected.", true, context.JobId.ToString("N"));
                var criteria = acceptance.Criteria!;
                var failedCriteria = criteria.Where(criterion => !criterion.Passes).ToArray();
                if (!acceptance.Passes || failedCriteria.Length > 0)
                {
                    var evidence = failedCriteria.Length > 0
                        ? string.Join("; ", failedCriteria.Select(criterion => $"{criterion.Criterion}: {criterion.Evidence}"))
                        : acceptance.Reason;
                    throw new GenerationDispatchException($"Last Frame visual acceptance failed: {Bound(evidence)} The result was not selected.", true, context.JobId.ToString("N"));
                }
                if (criteria.Count > 8)
                    throw new GenerationDispatchException("Last Frame visual acceptance returned too many criteria to verify safely. The result was not selected.", true, context.JobId.ToString("N"));
                foreach (var criterion in criteria)
                {
                    var criterionAudit = await runtime.RunAsync(auditArguments, root, TimeSpan.FromMinutes(2), cancellationToken, BuildCriterionPrompt(criterion.Criterion));
                    if (!criterionAudit.Started || criterionAudit.ExitCode != 0 || !TryParseCriterionVerdict(criterionAudit.StandardOutput, out var verdict))
                        throw new GenerationDispatchException($"Framewright could not independently verify '{Bound(criterion.Criterion)}'. The result was not selected.", true, context.JobId.ToString("N"));
                    if (!verdict.Passes)
                        throw new GenerationDispatchException($"Last Frame visual acceptance failed — {Bound(criterion.Criterion)}: {Bound(verdict.Evidence)} The result was not selected.", true, context.JobId.ToString("N"));
                }
            }
            var memory = new MemoryStream((int)info.Length);
            await using (var input = new FileStream(generated, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            return new(memory, $"{context.ShotCode.ToLowerInvariant()}-{context.Purpose.ToString().ToLowerInvariant()}-codex.{(mime == "image/jpeg" ? "jpg" : "png")}", mime, true, context.JobId.ToString("N"));
        }
        catch (OperationCanceledException) when (providerStarted) { throw new GenerationDispatchException("Codex ImageGen was stopped or exceeded its time limit.", true, context.JobId.ToString("N")); }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { } }
    }

    private static string BuildPrompt(GenerationExecutionContext context, string outputPath, List<string> attachmentMap, string[] textOnlyMap) => $"""
        $imagegen
        Create one production-ready storyboard frame for Framewright using the immutable generation packet below.
        {(context.CompositionPath is not null ? "The first attached image is the current visual base. Preserve unmentioned content and apply the labeled authorities exactly where the packet directs." : attachmentMap.Count > 0 ? "This is a new composition guided by labeled authority images; their original crops and backgrounds are not scene layout." : "This is text-to-image; construct the frame only from the supplied packet.")}
        Do not add text, labels, borders, watermarks, UI, or annotation marks unless the packet explicitly requests visible text in the scene.
        Return exactly one final raster image. Save it as PNG at this exact path: {outputPath}

        {BuildPriorityDirection(context)}

        REQUIRED VISUAL ACCEPTANCE CHECK
        - After the first generation, inspect the saved image at full-frame scale against the current visual base and every explicit requested change in the immutable packet.
        - Treat pose, gaze, facial expression, subject count, placement, props, and named style requirements as observable acceptance criteria, not suggestions.
        - If any explicit requested change is absent, too subtle to read at full-frame scale, or contradicted by the result, perform one corrective ImageGen edit pass with stronger localized direction and inspect the replacement again.
        - Do not report success merely because a file exists. Return the best inspected frame only after the requested change is visibly present; preserve all unmentioned content through the corrective pass.

        ATTACHED IMAGE MAP (ordered exactly like the --image arguments)
        {(attachmentMap.Count == 0 ? "- No images attached." : string.Join('\n', attachmentMap))}

        TEXT-ONLY AUTHORITY MAP
        {(textOnlyMap.Length == 0 ? "- None." : string.Join('\n', textOnlyMap))}

        IMMUTABLE FRAMEWRIGHT PACKET
        Manifest: {context.ManifestHash}
        Shot: {context.ShotCode}
        Purpose: {context.Purpose}
        {context.Prompt}
        """;

    private static string BuildPriorityDirection(GenerationExecutionContext context)
        => RequiresVisualAcceptance(context)
            ? $"""
              LAST FRAME END-STATE PRIORITY
              {ExtractEndStateDirection(context.Prompt)}
              This visible change is the purpose of the generation. Preserve continuity, but rebuild the affected pose, head, eyes, expression, hands, clothing, and nearby anatomy as needed so the end-state is unmistakable at full-frame scale.
              """
            : string.Empty;

    private static bool RequiresVisualAcceptance(GenerationExecutionContext context)
        => context.IsCurrentFrameEdit
            && context.Prompt.Contains("CREATE THE LAST FRAME FOR THIS SHOT", StringComparison.OrdinalIgnoreCase);

    private static string BuildAcceptancePrompt(GenerationExecutionContext context) => $$"""
        Inspect the ONE attached generated frame. This is an adversarial acceptance check, not an aesthetic review.

        REQUESTED END-STATE (authoritative; test every observable clause independently)
        {{ExtractEndStateDirection(context.Prompt)}}

        Return ONLY one JSON object, with no markdown:
        {"passes":true|false,"criteria":[{"criterion":"one atomic visible requirement","passes":true|false,"evidence":"what the pixels visibly show"}],"reason":"short overall verdict"}.

        Acceptance rules:
        - Split the REQUESTED END-STATE into atomic visible criteria. Include every requested pose, head direction, gaze, facial expression, subject count, placement, prop, and style change as its own criteria entry.
        - Every explicit end-state change must be plainly visible at full-frame scale. A merely compatible or ambiguous image fails.
        - Pose, head direction, gaze, facial expression, subject count, placement, props, and named style requirements are observable criteria, not suggestions.
        - A polished image fails if a requested change is absent, too subtle to read, or contradicted by the pixels.
        - Do not infer intent, emotion, or gaze from the prompt. Report only what the image actually shows.
        - Set passes=true only when every criteria entry passes. When uncertain, fail the affected criterion.

        IMMUTABLE LAST FRAME PACKET
        {{context.Prompt}}
        """;

    private static string ExtractEndStateDirection(string prompt)
    {
        const string marker = "END-STATE DIRECTION:";
        var start = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return prompt;
        start += marker.Length;
        var end = prompt.IndexOf("SHOT INTENT:", start, StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? prompt[start..] : prompt[start..end]).Trim();
    }

    private static string BuildCriterionPrompt(string criterion) => $$"""
        Inspect the ONE attached image for exactly ONE visible requirement:
        {{criterion}}

        Return ONLY one JSON object, with no markdown: {"passes":true|false,"evidence":"specific visible pixel evidence"}.
        This is a skeptical verification pass. Ignore presumed intent and visual polish. Pass only when the requirement is unmistakably visible at full-frame scale; ambiguity, partial compliance, or a merely compatible image fails.
        """;

    private static bool TryParseAcceptance(string output, out VisualAcceptance acceptance)
    {
        acceptance = new(false, [], "No structured verdict was returned.");
        try
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            var payload = JsonSerializer.Deserialize<VisualAcceptance>(output[start..(end + 1)], Json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Reason) || payload.Criteria is null || payload.Criteria.Count == 0) return false;
            if (payload.Criteria.Any(criterion => string.IsNullOrWhiteSpace(criterion.Criterion) || string.IsNullOrWhiteSpace(criterion.Evidence))) return false;
            acceptance = payload;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseCriterionVerdict(string output, out CriterionVerdict verdict)
    {
        verdict = new(false, "No structured evidence was returned.");
        try
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return false;
            var payload = JsonSerializer.Deserialize<CriterionVerdict>(output[start..(end + 1)], Json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Evidence)) return false;
            verdict = payload;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private sealed record VisualAcceptance(bool Passes, List<VisualCriterion>? Criteria, string Reason);
    private sealed record VisualCriterion(string Criterion, bool Passes, string Evidence);
    private sealed record CriterionVerdict(bool Passes, string Evidence);

    private static string ReferenceRole(string category) => category.ToLowerInvariant() switch
    {
        "style" => "Apply this exemplar's rendering language globally across the whole frame; do not depict its subject or copy its composition.",
        "character" or "identity" => "Use only for the exact person's identity, facial structure, hair, proportions, skin tone, and approved marks.",
        "face" => "Use only for facial identity and approved facial marks; do not inherit pose, wardrobe, crop, or background.",
        "role" => "Use for shared costume, equipment, insignia, and demeanor; generate a distinct natural face unless the packet names an exact character.",
        "wardrobe" or "outfit" => "Transfer the approved clothing and accessories to the assigned existing subject; do not create another person.",
        "location" or "architecture" => "Use for the continuous environment's architecture, materials, and spatial identity; do not paste it as a backdrop panel.",
        "prop" => "Use for the named prop's design and materials only, at the placement and scale specified by the packet.",
        "pose" => "Use only for body blocking and gesture; do not inherit identity, wardrobe, or background.",
        _ => "Use only for the explicitly assigned reference role in the immutable packet."
    };

    private static string SafeStem(string value)
    {
        var stem = string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'));
        while (stem.Contains("--", StringComparison.Ordinal)) stem = stem.Replace("--", "-", StringComparison.Ordinal);
        return stem.Trim('-');
    }

    private static async Task<string> CopyInputAsync(string source, string root, string stem, string mime, CancellationToken cancellationToken)
    {
        var target = Path.Combine(root, $"{stem}.{(mime == "image/jpeg" ? "jpg" : "png")}");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        return target;
    }

    private static string? FindGeneratedImage(string root, IReadOnlyCollection<string> inputs) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !inputs.Contains(path, StringComparer.OrdinalIgnoreCase)).Where(path => DetectMime(path) is not null)
        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    private static string? DetectMime(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[8]; using var stream = File.OpenRead(path);
            if (stream.Read(header) < 3) return null;
            if (header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
            return header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff ? "image/jpeg" : null;
        }
        catch { return null; }
    }

    private static string Bound(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "No diagnostic was returned." : value.Trim();
        return normalized.Length <= 1200 ? normalized : normalized[..1200] + "…";
    }
}
