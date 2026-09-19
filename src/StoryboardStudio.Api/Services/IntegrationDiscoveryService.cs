using StoryboardStudio.Core;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StoryboardStudio.Api.Services;

public sealed partial class IntegrationDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IProviderCredentialStore credentials,
    ICodexRuntime codexRuntime,
    TimeProvider timeProvider,
    ILogger<IntegrationDiscoveryService> logger)
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "ComfyUI discovery did not find a reachable endpoint at {Endpoint}")]
    private static partial void LogComfyUiUnavailable(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Codex generation-direction refinement failed safely. Started={Started} ExitCode={ExitCode}")]
    private static partial void LogDirectionRefinementFailure(ILogger logger, bool started, int exitCode);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Codex project interview failed safely. Started={Started} ExitCode={ExitCode}")]
    private static partial void LogProjectInterviewFailure(ILogger logger, bool started, int exitCode);

    public async Task<IReadOnlyList<IntegrationSummary>> InspectAsync(CancellationToken cancellationToken)
    {
        var tasks = new[] { InspectComfyAsync(cancellationToken), InspectCodexAsync(cancellationToken), InspectOpenAiAsync() };
        return await Task.WhenAll(tasks);
    }

    private async Task<IntegrationSummary> InspectComfyAsync(CancellationToken cancellationToken)
    {
        var endpoint = configuration["Integrations:ComfyUi:Endpoint"] ?? "http://127.0.0.1:8188";
        var submissionEnabled = configuration.GetValue("Integrations:ComfyUi:SubmissionEnabled", false);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return new IntegrationSummary("comfyui", "ComfyUI", IntegrationState.NeedsSetup, "Endpoint is invalid",
                "Configure an absolute HTTP endpoint. No request was made.", endpoint, true, false, timeProvider.GetUtcNow());
        }
        var allowRemote = configuration.GetValue("Integrations:ComfyUi:AllowRemote", false);
        if (!allowRemote && !IsLoopback(endpointUri))
        {
            return new IntegrationSummary("comfyui", "ComfyUI", IntegrationState.Protected, "Remote probe blocked",
                "Remote ComfyUI discovery requires an explicit AllowRemote opt-in. No request was made.", endpoint, true, false, timeProvider.GetUtcNow());
        }
        try
        {
            var client = httpClientFactory.CreateClient("integration-probe");
            using var response = await client.GetAsync($"{endpoint.TrimEnd('/')}/queue", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var running = ReadCount(json.RootElement, "queue_running");
            var pending = ReadCount(json.RootElement, "queue_pending");
            var idle = running == 0 && pending == 0;
            var headline = idle ? "Online · queue is idle" : $"Online · {running} running · {pending} pending";
            return new IntegrationSummary("comfyui", "ComfyUI", idle ? IntegrationState.Ready : IntegrationState.Protected,
                headline,
                submissionEnabled
                    ? "External adapter configured. Studio will recheck ownership and queue state before dispatch."
                    : "Read-only discovery is active. Real submission is locked until an external workflow is configured.",
                endpoint, true, submissionEnabled && idle, timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            LogComfyUiUnavailable(logger, ex, endpoint);
            return new IntegrationSummary("comfyui", "ComfyUI", IntegrationState.Offline, "Not reachable",
                "Set the external endpoint when the workstation service is available. No ComfyUI code is bundled.", endpoint, true, false, timeProvider.GetUtcNow());
        }
    }

    private async Task<IntegrationSummary> InspectCodexAsync(CancellationToken cancellationToken)
    {
        var command = ResolveCodexCommand();
        var result = await RunProcessAsync(command.Executable, [.. command.PrefixArguments, "login", "status"], TimeSpan.FromSeconds(8), cancellationToken);
        if (!result.Started)
        {
            return new IntegrationSummary("codex", "Codex", IntegrationState.NeedsSetup, "CLI not found",
                "Install Codex CLI, then connect through its official ChatGPT browser sign-in.", null, true, false, timeProvider.GetUtcNow());
        }

        var output = $"{result.StandardOutput}\n{result.StandardError}";
        var loggedIn = result.ExitCode == 0 && output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
        return new IntegrationSummary("codex", "Codex", loggedIn ? IntegrationState.Connected : IntegrationState.NeedsSetup,
            loggedIn ? "Connected through ChatGPT" : "Sign-in required",
            loggedIn ? "Ready for directing assistance, MCP collaboration, and explicit ImageGen jobs through the official Codex skill. No Framewright API key is used." : "Run the official Codex login flow on this workstation.",
            null, true, loggedIn, timeProvider.GetUtcNow());
    }

    private Task<IntegrationSummary> InspectOpenAiAsync()
    {
        var credential = credentials.GetOpenAiStatus();
        var present = credential.IsConfigured;
        // An explicit Generate click is the image-call authorization. Preserve
        // the speech toggle independently, but do not add a second image gate.
        var speechEnabled = configuration.GetValue("Integrations:OpenAI:SpeechSubmissionEnabled", false);
        var state = present ? IntegrationState.Ready : IntegrationState.NeedsSetup;
        return Task.FromResult(new IntegrationSummary("openai", "OpenAI API", state,
            present ? "Project key detected · image dispatch ready" : "API project not connected",
            present
                ? $"GPT Image is available after an explicit Generate click{(speechEnabled ? "; preset speech is also enabled" : "; preset speech remains disabled")}. The key value is never exposed to the browser."
                : "Add a dedicated project API key to the Windows credential store or workstation service environment. The OpenAI API uses server-side bearer credentials; this is not the Codex browser sign-in.",
            "https://platform.openai.com/api-keys", true, present, timeProvider.GetUtcNow()));
    }

    public async Task<CodexAssistResponse> AskCodexAsync(CodexAssistRequest request, StudioSnapshot snapshot, CancellationToken cancellationToken)
    {
        var selected = request.ShotId is null ? null : snapshot.Shots.SingleOrDefault(x => x.Id == request.ShotId);
        var selectedAuthorities = selected is null
            ? []
            : snapshot.References.Where(reference => selected.ReferenceIds.Contains(reference.Id)).ToArray();
        var context = selected is null
            ? $"Project: {snapshot.Project.Name}. {snapshot.Shots.Count} shots."
            : $"Shot {selected.Code}: {selected.Description}\nCamera: {selected.Camera}\nAction: {selected.Action}\nShot constraints: {string.Join("; ", selected.Constraints)}\nImmutable authority canon:\n{string.Join("\n", selectedAuthorities.Select(reference => $"- {reference.Name} ({reference.Category}) v{reference.Version}: {reference.Description} LOCKED: {reference.LockedConstraint}"))}";
        if (request.Mode.Contains("Integration", StringComparison.OrdinalIgnoreCase))
        {
            var integrations = await InspectAsync(cancellationToken);
            context += "\nLive read-only integration report:\n" + string.Join("\n", integrations.Select(item => $"- {item.Name}: {item.Headline}. {item.Detail}"));
        }
        var prompt = $"""
            You are the embedded directing and production setup specialist for Framewright.
            Work read-only. Do not edit files, submit renders, interrupt queues, or access production assets.
            Mode: {request.Mode}
            Context: {context}
            User request: {request.Message}
            Treat the supplied context as the authoritative Studio inspection result. Do not claim the UI or status is unavailable.
            Immutable authority canon has higher priority than the user request, older shot constraints, and your own directing ideas. Never relocate an anatomical-side feature or paraphrase a locked feature into a different body location. If the request conflicts with canon, identify the conflict instead of recommending it.
            Reply concisely with: one headline, a short recommendation, up to 3 findings, and up to 3 suggested actions.
            """;
        var command = ResolveCodexCommand();
        var result = await RunProcessAsync(command.Executable, BuildCodexExecArguments(command.PrefixArguments, prompt), TimeSpan.FromSeconds(45), cancellationToken);
        if (result.Started && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var text = result.StandardOutput.Trim();
            return new CodexAssistResponse(request.Mode, "Codex production note", text, [], ["Review this note before applying any production change."], true, timeProvider.GetUtcNow());
        }

        return new CodexAssistResponse(request.Mode, "Local guidance",
            selected is null ? "Select a shot for a context-specific directing pass." : $"Keep {selected.Code} grounded in its locked constraints, then review camera motion separately from subject motion.",
            selected?.Constraints.Take(3).ToArray() ?? ["No shot selected"],
            ["Review selected authorities", "Resolve open feedback", "Freeze the next manifest only after approval"],
            false, timeProvider.GetUtcNow());
    }

    public async Task<ShotIntentSuggestion> SuggestShotIntentAsync(SuggestShotIntentRequest request, StudioSnapshot snapshot, CancellationToken cancellationToken)
    {
        var description = request.Description.Trim();
        var authorities = snapshot.References.Select(item => new { item.Id, item.Name, item.Category, item.Description, item.LockedConstraint }).ToArray();
        var prompt = $"""
            You are the shot-intake assistant for Framewright. Turn the artist's plain-language idea into editable storyboard metadata.
            Return ONLY one JSON object with these exact camelCase keys:
            title (short string), description (what is visible), durationFrames (integer 1-2400), camera (concrete 35mm-equivalent lens, framing, and movement), action (the beat or motion), referenceIds (array using only IDs listed below), constraints (array of concrete visual continuity rules).
            Do not invent canon. Select a reference only when the request clearly names or implies it. Keep constraints literal and observable.

            Artist description: {description}

            Available approved authorities:
            {JsonSerializer.Serialize(authorities)}
            """;
        var command = ResolveCodexCommand();
        var result = await RunProcessAsync(command.Executable, BuildCodexExecArguments(command.PrefixArguments, prompt), TimeSpan.FromSeconds(45), cancellationToken);
        if (result.Started && result.ExitCode == 0 && TryParseShotIntent(result.StandardOutput, out var parsed))
        {
            var validIds = authorities.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ShotIntentSuggestion(
                Bound(parsed.Title, 120, "Untitled shot"),
                Bound(parsed.Description, 1200, description),
                Math.Clamp(parsed.DurationFrames, 1, 2400),
                Bound(parsed.Camera, 240, "35mm equivalent · medium shot · locked"),
                Bound(parsed.Action, 1200, description),
                (parsed.ReferenceIds ?? []).Where(validIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                (parsed.Constraints ?? []).Select(value => Bound(value, 500, "")).Where(value => value.Length > 0).Distinct().Take(12).ToArray(),
                true,
                "Codex filled the editable fields. Review them before adding the card.");
        }

        var matched = snapshot.References.Where(item => description.Contains(item.Name, StringComparison.OrdinalIgnoreCase)).Select(item => item.Id).ToArray();
        var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var title = string.Join(' ', words.Take(7)).TrimEnd('.', ',', ';', ':');
        return new ShotIntentSuggestion(
            string.IsNullOrWhiteSpace(title) ? "Untitled shot" : title,
            description,
            72,
            "35mm equivalent · medium shot · locked",
            description,
            matched,
            [],
            false,
            "Codex was unavailable, so Framewright preserved your description and applied conservative editable defaults.");
    }

    public async Task<ImprovedGenerationDirection> ImproveGenerationDirectionAsync(
        Guid shotId,
        ImproveGenerationDirectionRequest request,
        StudioSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var original = Bound(request.Direction, 2_500, "");
        var shot = snapshot.Shots.SingleOrDefault(item => item.Id == shotId);
        if (shot is null || original.Length == 0)
            return new(original, original, "A shot and a direction are required before Codex can refine the generation text.", false);

        var authorities = snapshot.References.Where(reference => shot.ReferenceIds.Contains(reference.Id)).ToArray();
        var prompt = $"""
            You are Framewright's generation-direction editor. Improve the artist's plain-language instruction for a {request.Purpose} image pass.
            Return ONLY one JSON object with these exact camelCase keys: improvedText (string) and summary (short string).

            Preserve the artist's requested story beat and every explicit change. Add useful visual specificity, temporal clarity, subject motion, staging, and continuity language only when supported by the shot context. Do not invent new canon, characters, props, dialogue, camera moves, or costume changes. Do not override locked rules. Do not turn the result into a shot list, multiple panels, a before-and-after image, or a long technical prompt. The improvedText must remain an editable direction under 2,500 characters.

            ARTIST DIRECTION:
            {original}

            SHOT: {shot.Code} — {shot.Description}
            ACTION: {shot.Action}
            CAMERA: {shot.Camera}
            LOCKED SHOT RULES:
            {string.Join("\n", shot.Constraints.Select(rule => $"- {rule}"))}
            IMMUTABLE AUTHORITIES:
            {string.Join("\n", authorities.Select(reference => $"- {reference.Name} ({reference.Category}) v{reference.Version}: {reference.Description} LOCKED: {reference.LockedConstraint}"))}
            PROJECT VISUAL STYLE: {snapshot.Project.VisualStyle}
            WORLD CANON: {snapshot.Project.WorldCanon}
            """;
        var status = codexRuntime.Inspect();
        if (!status.Installed || !status.Authenticated)
            return new(original, original, status.Detail, false);

        var workingDirectory = Path.Combine(Path.GetTempPath(), "Framewright", "codex-text");
        Directory.CreateDirectory(workingDirectory);
        var result = await codexRuntime.RunAsync(
            ["exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never", prompt],
            workingDirectory,
            TimeSpan.FromSeconds(45),
            cancellationToken);
        if (result.Started && result.ExitCode == 0 && TryParseImprovedDirection(result.StandardOutput, out var improved, out var summary))
            return new(original, Bound(improved, 2_500, original), Bound(summary, 240, "Codex clarified the generation direction."), true);

        LogDirectionRefinementFailure(logger, result.Started, result.ExitCode);
        return new(original, original, "Codex could not return a safe structured refinement. Your original text is unchanged.", false);
    }

    /// <summary>
    /// Turns four plain-language answers into a proposed project contract.
    ///
    /// Strictly a proposal. The endpoint writes nothing; the artist edits and
    /// ratifies it, which then goes through the ordinary create-project and
    /// create-authority paths. That keeps the advise/ratify boundary that every
    /// other Codex integration here observes — a wizard that wrote world canon on
    /// its own would be the single place it broke.
    /// </summary>
    public async Task<ProjectInterviewProposal> ProposeProjectSetupAsync(ProjectInterviewRequest request, CancellationToken cancellationToken)
    {
        var kind = Bound(request.Kind, 600, "");
        var look = Bound(request.Look, 600, "");
        var cast = Bound(request.Cast, 600, "");
        var locked = Bound(request.Locked, 600, "");
        var prompt = $"""
            You are the project-setup assistant for Framewright, a storyboard and previs studio.
            Turn the artist's answers into an editable project contract. Return ONLY one JSON object with these exact camelCase keys:
            name (short project name), production (the production or client line), sequenceCode (like SQ-01), sequenceName (short),
            framesPerSecond (integer 1-120), aspectRatio (width:height such as 2.39:1 or 16:9),
            deliveryWidth (integer, multiple of 16), deliveryHeight (integer, multiple of 16),
            visualStyle (how it should look, 1-3 sentences), worldCanon (what is true in this world regardless of the crop),
            promptDirectives (what generation must preserve), negativeDirectives (what generation must never produce),
            starterAuthorities (array of at most 5 objects with name, category, description, lockedConstraint, accent),
            rationale (one or two sentences on why this format was chosen).

            Rules:
            - deliveryWidth divided by deliveryHeight must match aspectRatio within 1%, and both must be multiples of 16.
            - category must be one of Character, Role, Wardrobe, Pose, Location, Architecture, Prop, Style, World.
            - accent must be six-digit hex like #8ea6cc.
            - lockedConstraint must be literal and observable, not a mood.
            - Do not invent named people, brands, or copyrighted characters. Describe only what the artist said.

            What is this piece: {kind}
            What it looks like: {look}
            Who or what is in it: {cast}
            Already locked or non-negotiable: {locked}
            """;
        var command = ResolveCodexCommand();
        var result = await RunProcessAsync(command.Executable, BuildCodexExecArguments(command.PrefixArguments, prompt), TimeSpan.FromSeconds(60), cancellationToken);
        if (result.Started && result.ExitCode == 0 && TryParseJson<ProjectInterviewPayload>(result.StandardOutput, out var parsed) && !string.IsNullOrWhiteSpace(parsed.Name))
        {
            var (width, height, ratio) = NormalizeDeliveryFormat(parsed.AspectRatio, parsed.DeliveryWidth, parsed.DeliveryHeight);
            return new ProjectInterviewProposal(
                Bound(parsed.Name, 160, "Untitled project"),
                Bound(parsed.Production, 160, "Local production"),
                Bound(parsed.SequenceCode, 40, "SQ-01").ToUpperInvariant(),
                Bound(parsed.SequenceName, 160, "Opening sequence"),
                parsed.FramesPerSecond is >= 1 and <= 120 ? parsed.FramesPerSecond : 24,
                ratio, width, height,
                Bound(parsed.VisualStyle, 4_000, StudioDefaults.VisualStyle),
                Bound(parsed.WorldCanon, 4_000, StudioDefaults.WorldCanon),
                Bound(parsed.PromptDirectives, 4_000, StudioDefaults.PromptDirectives),
                Bound(parsed.NegativeDirectives, 4_000, StudioDefaults.NegativeDirectives),
                NormalizeProposedAuthorities(parsed.StarterAuthorities),
                Bound(parsed.Rationale, 400, "Codex proposed this format from your answers."),
                true,
                "Codex filled every field as an editable proposal. Nothing has been saved; review it and choose what to create.");
        }

        LogProjectInterviewFailure(logger, result.Started, result.ExitCode);
        // The fallback keeps the artist's own words rather than inventing canon,
        // and leaves the studio defaults in place for anything they did not answer.
        var described = new[] { kind, look, cast, locked }.Where(value => value.Length > 0).ToArray();
        return new ProjectInterviewProposal(
            Bound(FirstWords(kind, 6), 160, "Untitled project"),
            "Local production",
            "SQ-01",
            Bound(FirstWords(kind, 4), 160, "Opening sequence"),
            24, "2.39:1", 2304, 960,
            look.Length > 0 ? look : StudioDefaults.VisualStyle,
            described.Length > 0 ? string.Join(" ", described) : StudioDefaults.WorldCanon,
            StudioDefaults.PromptDirectives,
            StudioDefaults.NegativeDirectives,
            [],
            "Codex was unavailable, so your answers were preserved verbatim over the studio defaults.",
            false,
            "Codex could not be reached. These are conservative editable defaults carrying your own wording; nothing has been saved.");
    }

    /// <summary>
    /// Forces a proposed canvas to satisfy the same contract the project endpoints
    /// enforce, so a proposal can never be one the artist is then unable to save.
    /// </summary>
    private static (int Width, int Height, string AspectRatio) NormalizeDeliveryFormat(string? aspectRatio, int width, int height)
    {
        var ratio = (aspectRatio ?? "").Trim();
        if (!Regex.IsMatch(ratio, @"^\d+(?:\.\d+)?:\d+(?:\.\d+)?$")) return (2304, 960, "2.39:1");
        var parts = ratio.Split(':');
        var named = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
        if (!double.IsFinite(named) || named <= 0) return (2304, 960, "2.39:1");

        // Accept a proposal that already satisfies the contract, untouched. Deriving
        // a "tidier" width from the ratio would silently contradict the rationale
        // Codex wrote about its own numbers — it proposed 2048x864 and the form
        // showed 2064, which reads as a bug to anyone comparing the two.
        static bool Satisfies(int w, int h, double named)
            => w is >= 320 and <= 7680 && h is >= 192 and <= 4320 && w % 16 == 0 && h % 16 == 0
               && Math.Abs(named - (double)w / h) / named <= .01;
        if (Satisfies(width, height, named)) return (width, height, ratio);

        // Otherwise snap to multiples of 16 and derive the partner dimension from
        // the named ratio, because rounding both independently is what drifts
        // outside the 1% tolerance the project contract checks.
        var snappedHeight = Math.Clamp((int)Math.Round(height / 16d) * 16, 192, 4320);
        if (height is < 180 or > 4320) snappedHeight = 960;
        var derivedWidth = Math.Clamp((int)Math.Round(snappedHeight * named / 16d) * 16, 320, 7680);
        if (Math.Abs(named - (double)derivedWidth / snappedHeight) / named <= .01) return (derivedWidth, snappedHeight, ratio);

        var snappedWidth = Math.Clamp((int)Math.Round(width / 16d) * 16, 320, 7680);
        var derivedHeight = Math.Clamp((int)Math.Round(snappedWidth / named / 16d) * 16, 192, 4320);
        return Math.Abs(named - (double)snappedWidth / derivedHeight) / named <= .01
            ? (snappedWidth, derivedHeight, ratio)
            : (2304, 960, "2.39:1");
    }

    private static ProposedAuthority[] NormalizeProposedAuthorities(ProposedAuthorityPayload[]? proposed)
    {
        HashSet<string> categories = ["Character", "Role", "Wardrobe", "Pose", "Location", "Architecture", "Prop", "Style", "World"];
        return (proposed ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new ProposedAuthority(
                Bound(item.Name, 100, "Authority"),
                categories.FirstOrDefault(known => known.Equals(item.Category?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Style",
                Bound(item.Description, 2_000, "Described during project setup."),
                Bound(item.LockedConstraint, 1_000, "Confirm this rule before generation."),
                Regex.IsMatch(item.Accent ?? "", "^#[0-9a-fA-F]{6}$") ? item.Accent!.ToLowerInvariant() : "#8ea6cc"))
            .Take(5)
            .ToArray();
    }

    private static string FirstWords(string value, int count)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(count)).TrimEnd('.', ',', ';', ':');

    private static bool TryParseJson<T>(string output, out T payload) where T : new()
    {
        payload = new T();
        if (string.IsNullOrWhiteSpace(output)) return false;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            payload = JsonSerializer.Deserialize<T>(output[start..(end + 1)], CaseInsensitiveJson) ?? new T();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private sealed class ProjectInterviewPayload
    {
        public string? Name { get; set; }
        public string? Production { get; set; }
        public string? SequenceCode { get; set; }
        public string? SequenceName { get; set; }
        public int FramesPerSecond { get; set; }
        public string? AspectRatio { get; set; }
        public int DeliveryWidth { get; set; }
        public int DeliveryHeight { get; set; }
        public string? VisualStyle { get; set; }
        public string? WorldCanon { get; set; }
        public string? PromptDirectives { get; set; }
        public string? NegativeDirectives { get; set; }
        public ProposedAuthorityPayload[]? StarterAuthorities { get; set; }
        public string? Rationale { get; set; }
    }

    private sealed class ProposedAuthorityPayload
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Description { get; set; }
        public string? LockedConstraint { get; set; }
        public string? Accent { get; set; }
    }

    private static string Bound(string? value, int limit, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) normalized = fallback;
        return normalized[..Math.Min(normalized.Length, limit)];
    }

    private static bool TryParseShotIntent(string output, out ShotIntentPayload payload)
    {
        payload = new ShotIntentPayload();
        if (string.IsNullOrWhiteSpace(output)) return false;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            payload = JsonSerializer.Deserialize<ShotIntentPayload>(output[start..(end + 1)], CaseInsensitiveJson) ?? new ShotIntentPayload();
            return !string.IsNullOrWhiteSpace(payload.Title) && !string.IsNullOrWhiteSpace(payload.Description) && !string.IsNullOrWhiteSpace(payload.Action);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryParseImprovedDirection(string output, out string improvedText, out string summary)
    {
        improvedText = "";
        summary = "";
        if (string.IsNullOrWhiteSpace(output)) return false;
        for (var start = output.IndexOf('{'); start >= 0; start = output.IndexOf('{', start + 1))
        {
            for (var end = output.IndexOf('}', start + 1); end > start; end = output.IndexOf('}', end + 1))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<ImprovedDirectionPayload>(output[start..(end + 1)], CaseInsensitiveJson);
                    if (payload is null || string.IsNullOrWhiteSpace(payload.ImprovedText)) continue;
                    improvedText = payload.ImprovedText;
                    summary = payload.Summary;
                    return true;
                }
                catch (JsonException) { /* Codex may print progress around the final JSON. Keep scanning. */ }
            }
        }
        return false;
    }

    private static int ReadCount(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private CodexCommand ResolveCodexCommand()
    {
        var configured = configuration["Integrations:Codex:Executable"];
        if (!string.IsNullOrWhiteSpace(configured)) return new CodexCommand(configured, []);

        if (OperatingSystem.IsWindows())
        {
            var npmCli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
            if (File.Exists(npmCli)) return new CodexCommand("node", [npmCli]);

            var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nativeExecutable = pathDirectories.Select(directory => Path.Combine(directory, "codex.exe")).FirstOrDefault(File.Exists);
            if (nativeExecutable is not null) return new CodexCommand(nativeExecutable, []);
        }

        return new CodexCommand("codex", []);
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Without this the child inherits the service's own stdin, which
                // under a host that never closes it means an interactive-capable
                // CLI can block forever waiting to read. Every Codex call was
                // reaching its timeout and falling back for this reason, so the
                // whole advisory layer silently reported "Codex was unavailable"
                // while `codex login status` reported Connected. Redirecting and
                // then closing the pipe hands the child an immediate EOF — the
                // same reason ffmpeg is invoked with -nostdin elsewhere here.
                RedirectStandardInput = true,
                // The CLI emits UTF-8, but a redirected stream is decoded with the
                // console codepage by default on Windows — which turned "1920×800"
                // in Codex prose into "1920Ã—800". Anything non-ASCII was corrupted:
                // dashes, quotes, accented names.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SanitizeChildEnvironment(startInfo.Environment);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) return new ProcessResult(false, -1, "", "");
            process.StandardInput.Close();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            return new ProcessResult(true, process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch
        {
            return new ProcessResult(false, -1, "", "");
        }
    }

    public static void SanitizeChildEnvironment(IDictionary<string, string?> environment)
    {
        // Codex and provider credentials are distinct trust domains. A helper
        // process may use its own official login store, but never inherits the
        // image-provider project key held by Framewright.
        environment.Remove("OPENAI_API_KEY");
    }

    internal static IReadOnlyList<string> BuildCodexExecArguments(IReadOnlyList<string> prefixArguments, string prompt)
        => [.. prefixArguments, "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never", prompt];

    private static bool IsLoopback(Uri endpoint)
        => endpoint.IsLoopback || string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    private sealed record ProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError);
    private sealed record CodexCommand(string Executable, IReadOnlyList<string> PrefixArguments);
    private sealed record ShotIntentPayload(
        string Title = "",
        string Description = "",
        int DurationFrames = 72,
        string Camera = "",
        string Action = "",
        string[]? ReferenceIds = null,
        string[]? Constraints = null);
    private sealed record ImprovedDirectionPayload(string ImprovedText = "", string Summary = "");
}
