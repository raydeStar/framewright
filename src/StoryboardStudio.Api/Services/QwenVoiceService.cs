using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

public sealed partial class QwenVoiceService(
    StudioDbContext db,
    AssetStore assets,
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<QwenVoiceService> logger)
{
    public const string ProviderName = "Qwen3-TTS local";
    public const string DesignModel = "Qwen3-TTS-12Hz-1.7B-VoiceDesign";
    public const string CloneModel = "Qwen3-TTS-12Hz-1.7B-Base";
    private static readonly JsonSerializerOptions WorkerJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequiredModelKeys = ["design", "base"];

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Error,
        Message = "Local Qwen voice design failed for {ReferenceId}")]
    private static partial void LogVoiceDesignFailure(ILogger logger, Exception exception, string referenceId);

    public (bool Ready, string Detail) Status()
    {
        if (!configuration.GetValue("QwenTts:Enabled", true)) return (false, "Local Qwen voice generation is disabled in configuration.");
        if (ResolveWorkerEndpoint() is Uri endpoint)
        {
            if (string.IsNullOrWhiteSpace(configuration["QwenTts:WorkerToken"])) return (false, "The local Qwen voice worker token is not configured.");
            try
            {
                using var request = CreateWorkerRequest(HttpMethod.Get, new Uri(endpoint, "/health"));
                using var response = clients.CreateClient("qwen-voice-worker-health").Send(request);
                return response.IsSuccessStatusCode
                    ? (true, "Local Qwen voice worker is connected. Voice design and approved character delivery stay on this workstation.")
                    : (false, $"The local Qwen voice worker answered with HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (false, $"The local Qwen voice worker is offline at {endpoint}. Start it with scripts/start-voice-worker.ps1.");
            }
        }
        var paths = ResolvePaths();
        if (!File.Exists(paths.Python)) return (false, $"Qwen Python runtime was not found at {paths.Python}.");
        if (!Directory.Exists(paths.Packages)) return (false, $"Qwen packages were not found at {paths.Packages}.");
        if (!File.Exists(paths.Script)) return (false, $"Framewright's Qwen voice bridge was not found at {paths.Script}.");
        if (!File.Exists(paths.Lock)) return (false, $"Framewright's pinned Qwen model lock was not found at {paths.Lock}.");
        var manifest = ValidateRuntimeManifest(paths.Manifest, paths.Lock, paths.Python, paths.Packages);
        return manifest.Ready
            ? (true, "Local Qwen voice design and reusable character delivery passed their pinned offline canary. No API key or cloud voice service is used.")
            : (false, manifest.Detail);
    }

    public async Task<RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>> DesignAuditionsAsync(
        string referenceId,
        DesignVoiceAuditionsRequest request,
        CancellationToken cancellationToken)
    {
        var character = await db.References.AsNoTracking().SingleOrDefaultAsync(x => x.Id == referenceId && x.Category == "Character", cancellationToken);
        if (character is null) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.NotFound();
        var direction = request.Direction?.Trim() ?? "";
        var text = request.CalibrationText?.Trim() ?? "";
        if (direction.Length is < 20 or > 1_000) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("Voice direction must contain 20 to 1,000 characters.");
        if (text.Length is < 20 or > 150) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("Audition text must contain 20 to 150 characters so the approved identity remains portable.");
        if (request.Count is < 1 or > 3) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("Generate between one and three auditions at a time.");
        var status = Status();
        if (!status.Ready) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid(status.Detail);

        if (ResolveWorkerEndpoint() is not null)
            return await DesignAuditionsWithWorkerAsync(character, direction, text, request.Count, cancellationToken);

        var temp = Directory.CreateTempSubdirectory("framewright-qwen-");
        try
        {
            var seedBase = Random.Shared.Next(100_000, 900_000);
            var paths = ResolvePaths();
            var args = new[] { "--packages", paths.Packages, "--manifest", paths.Manifest, "design", "--direction", direction, "--text", text, "--count", request.Count.ToString(CultureInfo.InvariantCulture), "--seed-base", seedBase.ToString(CultureInfo.InvariantCulture), "--output-dir", temp.FullName };
            var outcome = await RunAsync(paths.Python, paths.Script, args, cancellationToken);
            if (outcome.ExitCode != 0) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid($"Qwen voice design failed: {Bound(outcome.Error)}");

            var providerVoiceId = EncodeVoiceIdentity(text);
            var results = new List<VoiceAuditionSummary>();
            for (var index = 0; index < request.Count; index++)
            {
                var path = Path.Combine(temp.FullName, $"audition-{index + 1}.wav");
                if (!File.Exists(path)) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("Qwen completed without returning every requested audition.");
                await using var stream = File.OpenRead(path);
                var imported = await assets.ImportGeneratedMediaAsync(stream, $"{character.Name}-qwen-audition-{index + 1}.wav", "audio/wav", stream.Length, AssetKind.Audio, cancellationToken, "Qwen voice audition");
                if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null) return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid(imported.Error ?? "An audition could not be imported.");
                results.Add(new(imported.Value.Id, imported.Value.ContentUrl, providerVoiceId, seedBase + index, text, direction, DesignModel));
            }
            db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "VoiceAuditionsDesigned", TargetType = "Reference", TargetId = referenceId, PayloadJson = JsonSerializer.Serialize(new { model = DesignModel, count = results.Count, assetIds = results.Select(x => x.AssetId), providerCallMade = false }), CreatedAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Ok(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogVoiceDesignFailure(logger, ex, referenceId);
            return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid($"Local Qwen voice design failed: {Bound(ex.Message)}");
        }
        finally { try { temp.Delete(true); } catch (IOException) { } }
    }

    public async Task<RepositoryResult<VoiceSynthesisResult>> SynthesizeAsync(TimelineClipRecord clip, VoiceProfileRecord profile, CancellationToken cancellationToken)
    {
        var status = Status();
        if (!status.Ready) return RepositoryResult<VoiceSynthesisResult>.Invalid(status.Detail);
        if (profile.SampleAssetId is null) return RepositoryResult<VoiceSynthesisResult>.Invalid("The Qwen voice profile has no approved reference sample.");
        var sample = await db.Assets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profile.SampleAssetId && x.Kind == AssetKind.Audio.ToString() && !x.IsArchived, cancellationToken);
        if (sample is null) return RepositoryResult<VoiceSynthesisResult>.Invalid("The approved Qwen voice sample is unavailable.");
        var referenceText = DecodeVoiceIdentity(profile.ProviderVoiceId) ?? LegacyCalibration(profile.CharacterName);
        if (referenceText is null) return RepositoryResult<VoiceSynthesisResult>.Invalid("This older Qwen profile does not contain the calibration transcript required for consistent speech. Recast it once from the character card.");

        if (ResolveWorkerEndpoint() is not null)
            return await SynthesizeWithWorkerAsync(clip, profile, sample, referenceText, cancellationToken);

        var temp = Directory.CreateTempSubdirectory("framewright-qwen-clone-");
        try
        {
            var output = Path.Combine(temp.FullName, "dialogue.wav");
            var paths = ResolvePaths();
            var args = new[] { "--packages", paths.Packages, "--manifest", paths.Manifest, "clone", "--reference", assets.ResolveContentPath(sample), "--reference-text", referenceText, "--text", clip.Text, "--output", output };
            var outcome = await RunAsync(paths.Python, paths.Script, args, cancellationToken);
            if (outcome.ExitCode != 0 || !File.Exists(output)) return RepositoryResult<VoiceSynthesisResult>.Invalid($"Qwen speech failed: {Bound(outcome.Error)}");
            await using var stream = File.OpenRead(output);
            var imported = await assets.ImportGeneratedMediaAsync(stream, $"{clip.Label}-qwen.wav", "audio/wav", stream.Length, AssetKind.Audio, cancellationToken, "Qwen character dialogue");
            if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null) return RepositoryResult<VoiceSynthesisResult>.Invalid(imported.Error ?? "Qwen dialogue could not be imported.");
            clip.AssetId = imported.Value.Id;
            clip.UpdatedAt = timeProvider.GetUtcNow();
            db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "VoiceSpeechSynthesized", TargetType = "TimelineClip", TargetId = clip.Id.ToString(), PayloadJson = JsonSerializer.Serialize(new { model = CloneModel, profile.Id, assetId = imported.Value.Id, providerCallMade = false }), CreatedAt = clip.UpdatedAt });
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<VoiceSynthesisResult>.Ok(new(Map(clip), CloneModel, null, false));
        }
        finally { try { temp.Delete(true); } catch (IOException) { } }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string python, string script, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The Qwen Python process could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled host must not leave an orphaned GPU process producing a
            // second take behind the durable queue's back.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private async Task<RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>> DesignAuditionsWithWorkerAsync(
        ReferenceRecord character, string direction, string text, int count, CancellationToken cancellationToken)
    {
        var seedBase = Random.Shared.Next(100_000, 900_000);
        var response = await SendWorkerAsync(new Uri(ResolveWorkerEndpoint()!, "/v1/design"), new WorkerDesignRequest(direction, text, count, seedBase), cancellationToken);
        if (response.Kind != RepositoryResultKind.Ok || response.Value is null)
            return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid(response.Error ?? "The local voice worker did not return auditions.");
        if (response.Value.Auditions.Count != count)
            return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("The local voice worker did not return every requested audition.");

        var providerVoiceId = EncodeVoiceIdentity(text);
        var results = new List<VoiceAuditionSummary>();
        foreach (var audition in response.Value.Auditions)
        {
            byte[] audio;
            try { audio = Convert.FromBase64String(audition.AudioBase64); }
            catch (FormatException) { return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid("The local voice worker returned invalid audio data."); }
            await using var stream = new MemoryStream(audio, writable: false);
            var imported = await assets.ImportGeneratedMediaAsync(stream, $"{character.Name}-qwen-audition-{results.Count + 1}.wav", "audio/wav", stream.Length, AssetKind.Audio, cancellationToken, "Qwen voice audition");
            if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
                return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Invalid(imported.Error ?? "An audition could not be imported.");
            results.Add(new(imported.Value.Id, imported.Value.ContentUrl, providerVoiceId, audition.Seed, text, direction, DesignModel));
        }
        db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "VoiceAuditionsDesigned", TargetType = "Reference", TargetId = character.Id, PayloadJson = JsonSerializer.Serialize(new { model = DesignModel, worker = true, count = results.Count, assetIds = results.Select(x => x.AssetId), providerCallMade = false }), CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<IReadOnlyList<VoiceAuditionSummary>>.Ok(results);
    }

    private async Task<RepositoryResult<VoiceSynthesisResult>> SynthesizeWithWorkerAsync(
        TimelineClipRecord clip, VoiceProfileRecord profile, AssetRecord sample, string referenceText, CancellationToken cancellationToken)
    {
        var sampleBytes = await File.ReadAllBytesAsync(assets.ResolveContentPath(sample), cancellationToken);
        var request = new WorkerCloneRequest(Convert.ToBase64String(sampleBytes), referenceText, clip.Text);
        var response = await SendWorkerAsync(new Uri(ResolveWorkerEndpoint()!, "/v1/clone"), request, cancellationToken);
        if (response.Kind != RepositoryResultKind.Ok || response.Value is null)
            return RepositoryResult<VoiceSynthesisResult>.Invalid(response.Error ?? "The local voice worker did not return dialogue audio.");
        byte[] audio;
        try { audio = Convert.FromBase64String(response.Value.AudioBase64 ?? ""); }
        catch (FormatException) { return RepositoryResult<VoiceSynthesisResult>.Invalid("The local voice worker returned invalid dialogue audio."); }
        await using var stream = new MemoryStream(audio, writable: false);
        var imported = await assets.ImportGeneratedMediaAsync(stream, $"{clip.Label}-qwen.wav", "audio/wav", stream.Length, AssetKind.Audio, cancellationToken, "Qwen character dialogue");
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
            return RepositoryResult<VoiceSynthesisResult>.Invalid(imported.Error ?? "Qwen dialogue could not be imported.");
        clip.AssetId = imported.Value.Id;
        clip.UpdatedAt = timeProvider.GetUtcNow();
        db.AuditEvents.Add(new AuditEventRecord { Id = Guid.NewGuid(), Type = "VoiceSpeechSynthesized", TargetType = "TimelineClip", TargetId = clip.Id.ToString(), PayloadJson = JsonSerializer.Serialize(new { model = CloneModel, worker = true, profile.Id, assetId = imported.Value.Id, providerCallMade = false }), CreatedAt = clip.UpdatedAt });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<VoiceSynthesisResult>.Ok(new(Map(clip), CloneModel, null, false));
    }

    private async Task<RepositoryResult<WorkerResponse>> SendWorkerAsync<T>(Uri endpoint, T payload, CancellationToken cancellationToken)
    {
        using var request = CreateWorkerRequest(HttpMethod.Post, endpoint);
        // The local worker intentionally rejects unbounded request bodies. A
        // streaming JsonContent may use HTTP chunked transfer, which Python's
        // small stdlib server cannot infer from Content-Length. Buffer this
        // already-bounded contract so the workstation boundary receives an
        // explicit length instead of a mysteriously empty request.
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, WorkerJson));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await clients.CreateClient("qwen-voice-worker").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return RepositoryResult<WorkerResponse>.Invalid($"Local Qwen worker failed with HTTP {(int)response.StatusCode}: {Bound(error)}");
            }
            var body = await response.Content.ReadFromJsonAsync<WorkerResponse>(cancellationToken);
            return body is null ? RepositoryResult<WorkerResponse>.Invalid("The local Qwen worker returned an empty response.") : RepositoryResult<WorkerResponse>.Ok(body);
        }
        catch (HttpRequestException ex) { return RepositoryResult<WorkerResponse>.Invalid($"The local Qwen worker is offline: {Bound(ex.Message)}"); }
    }

    private HttpRequestMessage CreateWorkerRequest(HttpMethod method, Uri endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Add("X-Framewright-Voice-Token", configuration["QwenTts:WorkerToken"]);
        return request;
    }

    private Uri? ResolveWorkerEndpoint()
    {
        var raw = configuration["QwenTts:WorkerEndpoint"];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host is not ("127.0.0.1" or "localhost" or "host.docker.internal"))
            throw new InvalidOperationException("QwenTts:WorkerEndpoint must be an HTTP endpoint on localhost or host.docker.internal.");
        return endpoint;
    }

    private (string Python, string Packages, string Script, string Manifest, string Lock) ResolvePaths()
    {
        var python = configuration["QwenTts:PythonPath"] ?? @"C:\Comfy\python_embeded\python.exe";
        var packages = FindProjectPath(configuration["QwenTts:PackagePath"] ?? @"%LOCALAPPDATA%\FramewrightVoice\packages");
        var script = FindProjectPath(configuration["QwenTts:ScriptPath"] ?? "tools/voice/qwen_voice_tool.py");
        var manifest = FindProjectPath(configuration["QwenTts:ManifestPath"] ?? @"%LOCALAPPDATA%\FramewrightVoice\runtime-manifest.json");
        var modelLock = FindProjectPath(configuration["QwenTts:ModelLockPath"] ?? "tools/voice/models.lock.json");
        return (Path.GetFullPath(Environment.ExpandEnvironmentVariables(python)), packages, script, manifest, modelLock);
    }

    private static string FindProjectPath(string configured)
    {
        configured = Environment.ExpandEnvironmentVariables(configured);
        if (Path.IsPathRooted(configured)) return Path.GetFullPath(configured);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var cursor = new DirectoryInfo(start);
            while (cursor is not null)
            {
                var candidate = Path.GetFullPath(Path.Combine(cursor.FullName, configured));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                cursor = cursor.Parent;
            }
        }
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }

    private static (bool Ready, string Detail) ValidateRuntimeManifest(string manifestPath, string lockPath, string pythonPath, string packagesPath)
    {
        if (!File.Exists(manifestPath))
            return (false, $"The verified Qwen runtime manifest is missing at {manifestPath}. Run scripts/setup-voice-worker.ps1 once.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            using var lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1 ||
                !root.TryGetProperty("canary", out var canary) || !canary.TryGetProperty("passed", out var passed) || !passed.GetBoolean())
                return (false, "The local Qwen runtime has not passed its pinned synthesis canary. Run scripts/setup-voice-worker.ps1.");
            var lockRoot = lockDocument.RootElement;
            if (!lockRoot.TryGetProperty("schemaVersion", out var lockSchema) || lockSchema.GetInt32() != 1 ||
                !lockRoot.TryGetProperty("models", out var lockedModels) || lockedModels.ValueKind != JsonValueKind.Array)
                return (false, "Framewright's checked-in Qwen model lock is invalid.");
            if (!root.TryGetProperty("lockSha256", out var lockHash) ||
                !Sha256(lockPath).Equals(lockHash.GetString(), StringComparison.OrdinalIgnoreCase))
                return (false, "The local Qwen runtime does not match Framewright's checked-in model lock. Run scripts/setup-voice-worker.ps1.");
            var expected = lockedModels.EnumerateArray().ToDictionary(
                item => item.GetProperty("key").GetString() ?? "",
                item => (
                    Repository: item.GetProperty("repository").GetString() ?? "",
                    Revision: item.GetProperty("revision").GetString() ?? "",
                    Directory: item.GetProperty("directory").GetString() ?? ""),
                StringComparer.Ordinal);
            if (expected.Count != 2 || !expected.ContainsKey("design") || !expected.ContainsKey("base"))
                return (false, "Framewright's Qwen model lock must contain exactly the design and base models.");
            var runtimeRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var runtimePrefix = Path.TrimEndingDirectorySeparator(runtimeRoot) + Path.DirectorySeparatorChar;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in root.GetProperty("models").EnumerateArray())
            {
                var key = model.GetProperty("key").GetString() ?? "";
                if (!keys.Add(key) || !expected.TryGetValue(key, out var identity) ||
                    model.GetProperty("repository").GetString() != identity.Repository ||
                    model.GetProperty("revision").GetString() != identity.Revision ||
                    model.GetProperty("localPath").GetString() != identity.Directory)
                    return (false, $"The pinned Qwen {key} model identity does not match Framewright's checked-in lock.");
                var inventoried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in model.GetProperty("files").EnumerateArray())
                {
                    var path = Path.GetFullPath(Path.Combine(runtimeRoot, entry.GetProperty("path").GetString() ?? ""));
                    if (!path.StartsWith(runtimePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
                        !inventoried.Add(path) || new FileInfo(path).Length != entry.GetProperty("bytes").GetInt64() ||
                        !Sha256(path).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                        return (false, $"A pinned Qwen model file is missing or changed: {path}. Run scripts/setup-voice-worker.ps1.");
                }
                var modelRoot = Path.GetFullPath(Path.Combine(runtimeRoot, model.GetProperty("localPath").GetString() ?? ""));
                if (!modelRoot.StartsWith(runtimePrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(modelRoot))
                    return (false, $"The pinned Qwen {key} model directory is unavailable.");
                var actual = Directory.EnumerateFiles(modelRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains(".cache", StringComparer.Ordinal))
                    .Select(Path.GetFullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!actual.SetEquals(inventoried))
                    return (false, $"The pinned Qwen {key} model inventory contains added or missing files.");
            }
            if (!keys.SetEquals(RequiredModelKeys))
                return (false, "The local Qwen runtime does not contain both pinned voice models.");
            var canaryPath = Path.GetFullPath(Path.Combine(runtimeRoot, canary.GetProperty("path").GetString() ?? ""));
            if (!canaryPath.StartsWith(runtimePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(canaryPath) ||
                new FileInfo(canaryPath).Length != canary.GetProperty("bytes").GetInt64() ||
                !Sha256(canaryPath).Equals(canary.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return (false, "The local Qwen synthesis canary is missing or changed. Run scripts/setup-voice-worker.ps1.");
            if (!root.TryGetProperty("environment", out var environment) ||
                !environment.TryGetProperty("python", out var python) ||
                !environment.TryGetProperty("packagesRoot", out var packageRoot) ||
                !environment.TryGetProperty("distributions", out var distributions) ||
                !environment.TryGetProperty("torch", out var torch) ||
                !environment.TryGetProperty("files", out var runtimeFiles))
                return (false, "The local Qwen runtime has no verified interpreter/package/CUDA inventory. Run scripts/setup-voice-worker.ps1.");
            var configuredPython = Path.GetFullPath(pythonPath);
            var verifiedPython = Path.GetFullPath(python.GetProperty("path").GetString() ?? "");
            if (!configuredPython.Equals(verifiedPython, StringComparison.OrdinalIgnoreCase) || !File.Exists(configuredPython) ||
                new FileInfo(configuredPython).Length != python.GetProperty("bytes").GetInt64() ||
                !Sha256(configuredPython).Equals(python.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return (false, "The configured Qwen Python interpreter differs from the canary-verified runtime. Run scripts/setup-voice-worker.ps1.");
            if (!Path.GetFullPath(packagesPath).Equals(Path.GetFullPath(packageRoot.GetString() ?? ""), StringComparison.OrdinalIgnoreCase))
                return (false, "The configured Qwen package root differs from the canary-verified runtime.");
            foreach (var requiredDistribution in new[] { "qwen-tts", "torch", "soundfile" })
                if (!distributions.TryGetProperty(requiredDistribution, out var version) || string.IsNullOrWhiteSpace(version.GetString()))
                    return (false, $"The verified Qwen environment is missing {requiredDistribution}.");
            if (!torch.TryGetProperty("cudaAvailable", out var cudaAvailable) || !cudaAvailable.GetBoolean())
                return (false, "The canary-verified Qwen environment did not have CUDA available.");
            var runtimeInventoryCount = 0;
            foreach (var entry in runtimeFiles.EnumerateArray())
            {
                runtimeInventoryCount++;
                var path = Path.GetFullPath(entry.GetProperty("path").GetString() ?? "");
                if (!File.Exists(path) || new FileInfo(path).Length != entry.GetProperty("bytes").GetInt64() ||
                    !Sha256(path).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    return (false, $"A canary-verified Qwen runtime file is missing or changed: {path}.");
            }
            if (runtimeInventoryCount == 0) return (false, "The verified Qwen runtime file inventory is empty.");
            return (true, "Pinned local Qwen runtime is ready.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return (false, $"The local Qwen runtime manifest is invalid: {Bound(ex.Message)}");
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string EncodeVoiceIdentity(string text) => "qwen3tts:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string? DecodeVoiceIdentity(string value)
    {
        const string prefix = "qwen3tts:v1:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var encoded = value[prefix.Length..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); } catch (FormatException) { return null; }
    }
    private static string? LegacyCalibration(string characterName) => characterName.ToLowerInvariant() switch
    {
        "remora" => "I learned long ago that silence is not surrender. Watch the horizon, count every shadow, and speak only when the truth can no longer hide.",
        "magistrate" => "Authority is not the absence of doubt. It is the discipline to weigh every consequence, and still give the order when the hour demands it.",
        _ => null
    };
    private static TimelineClipSummary Map(TimelineClipRecord x) => new(x.Id, Enum.Parse<TimelineTrackKind>(x.Track), x.Label, x.StartFrame, x.DurationFrames, x.TrimStartSeconds, x.Volume, x.AssetId, x.AssetId is null ? null : $"/api/assets/{x.AssetId}/content", x.VoiceProfileId, x.Text, x.CreatedAt, x.UpdatedAt);
    private static string Bound(string value) => value.Length <= 800 ? value : value[..800] + "...";

    private sealed record WorkerDesignRequest(string Direction, string Text, int Count, int SeedBase);
    private sealed record WorkerCloneRequest(string ReferenceAudioBase64, string ReferenceText, string Text);
    private sealed record WorkerAudition(int Seed, string AudioBase64);
    private sealed record WorkerResponse(string? Model, IReadOnlyList<WorkerAudition> Auditions, string? AudioBase64, string? Error);
}
