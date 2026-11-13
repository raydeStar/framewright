using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Sound review: the sounds a MOSS list asks for, the takes MOSS has written
/// for them, and a person's decision about each sound.
///
/// The list and the takes are files the sound tooling owns; this only reads
/// them. A take is known by the SHA-256 its run manifest records, so the same
/// file found under two folders is one take, and a decision names exactly the
/// bytes that were heard. Decisions live in the project's database, like asset
/// review decisions, with the same three values.
///
/// A rejected sound that gets a newer take reads as Pending again, answering
/// the rejection, the way a new model revision answers a send-back.
/// </summary>
public sealed partial class SfxReviewService(
    StudioDbContext db, IConfiguration configuration, IWebHostEnvironment environment, TimeProvider timeProvider)
{
    public const string ManifestName = "sfx-manifest.json";
    public const string ExportManifestName = "approved-sfx.json";
    public const int MaxNoteLength = 1_000;
    private const int MaxTakeRoots = 12;
    private const int MaxManifestDepth = 6;
    private const string RejectedWithoutNote = "(rejected without a note)";

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim SettingsLock = new(1, 1);

    private string SettingsPath => Path.Combine(StudioPaths.ResolveDataRoot(configuration, environment), "sfx-review-sources.json");

    public async Task<SfxReviewBoard> BoardAsync(CancellationToken cancellationToken)
    {
        var sources = await SourcesAsync(cancellationToken);
        var (listed, listError) = ReadList(sources.ListPath);
        var (takes, manifestErrors) = ScanTakes(sources.TakeRoots);
        var rows = await db.SfxReviews.ToListAsync(cancellationToken);
        var sounds = Compose(listed, takes, rows);
        return new SfxReviewBoard(sources, listError, manifestErrors, Count(sounds), sounds);
    }

    public async Task<RepositoryResult<SfxReviewBoard>> SetSourcesAsync(SetSfxReviewSourcesRequest request, CancellationToken cancellationToken)
    {
        var listPath = (request.ListPath ?? "").Trim().Trim('"');
        if (listPath.Length > 0)
        {
            if (!Path.IsPathFullyQualified(listPath) || !listPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return RepositoryResult<SfxReviewBoard>.Invalid("The sound list is a full path to a MOSS .json list.");
            if (!File.Exists(listPath))
                return RepositoryResult<SfxReviewBoard>.Invalid($"There is no sound list at {listPath}.");
            listPath = Path.GetFullPath(listPath);
        }

        var roots = new List<string>();
        foreach (var raw in request.TakeRoots ?? [])
        {
            var root = (raw ?? "").Trim().Trim('"');
            if (root.Length == 0) continue;
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                return RepositoryResult<SfxReviewBoard>.Invalid($"There is no folder at {root}.");
            root = Path.GetFullPath(root);
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
        }
        if (roots.Count > MaxTakeRoots)
            return RepositoryResult<SfxReviewBoard>.Invalid($"Keep it to {MaxTakeRoots} take folders or fewer.");

        var exportPath = (request.ExportPath ?? "").Trim().Trim('"');
        if (exportPath.Length > 0)
        {
            if (!Path.IsPathFullyQualified(exportPath))
                return RepositoryResult<SfxReviewBoard>.Invalid("The export folder is a full path.");
            exportPath = Path.GetFullPath(exportPath);
            if (InsideUnrealProject(exportPath) is { } project)
                return RepositoryResult<SfxReviewBoard>.Invalid(
                    $"That folder is inside the Unreal project at {project}. Export somewhere the game imports from, not into the project itself.");
        }

        await SettingsLock.WaitAsync(cancellationToken);
        try
        {
            var all = ReadSettings();
            all[db.ActiveProjectId.ToString()] = new SfxReviewSources(listPath, [.. roots], exportPath);
            var temporary = SettingsPath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all, SettingsJson), cancellationToken);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { SettingsLock.Release(); }

        return RepositoryResult<SfxReviewBoard>.Ok(await BoardAsync(cancellationToken));
    }

    public async Task<RepositoryResult<SfxSoundSummary>> DecideAsync(
        string name, SetSfxReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Decision))
            return RepositoryResult<SfxSoundSummary>.Invalid("A decision is Pending, Approved or ChangesRequested.");
        var note = (request.Note ?? "").Trim();
        if (note.Length > MaxNoteLength)
            return RepositoryResult<SfxSoundSummary>.Invalid($"Keep the note to {MaxNoteLength:N0} characters or fewer.");

        var sources = await SourcesAsync(cancellationToken);
        var (listed, _) = ReadList(sources.ListPath);
        var (takes, _) = ScanTakes(sources.TakeRoots);
        var rows = await db.SfxReviews.ToListAsync(cancellationToken);
        var sound = Compose(listed, takes, rows).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (sound is null) return RepositoryResult<SfxSoundSummary>.NotFound();

        string? accepted = null;
        if (request.Decision == AssetReviewDecision.Approved)
        {
            if (sound.Takes.Length == 0)
                return RepositoryResult<SfxSoundSummary>.Invalid($"Nothing has been generated for {sound.Name} yet, so there is no take to accept.");
            var sha = (request.TakeSha256 ?? "").Trim().ToLowerInvariant();
            if (sha.Length == 0 && sound.Takes.Length == 1) sha = sound.Takes[0].Sha256;
            if (sha.Length == 0)
                return RepositoryResult<SfxSoundSummary>.Invalid("Say which take is accepted.");
            if (sound.Takes.All(take => take.Sha256 != sha))
                return RepositoryResult<SfxSoundSummary>.Invalid($"That take is not one of {sound.Name}'s.");
            accepted = sha;
        }

        var now = timeProvider.GetUtcNow();
        var row = rows.FirstOrDefault(item => string.Equals(item.SoundName, sound.Name, StringComparison.OrdinalIgnoreCase));
        var previous = row?.Decision ?? nameof(AssetReviewDecision.Pending);
        if (row is null)
        {
            row = new SfxReviewRecord { Id = Guid.NewGuid(), ProjectId = db.ActiveProjectId, SoundName = sound.Name, Decision = "" };
            db.SfxReviews.Add(row);
            rows.Add(row);
        }
        row.Decision = request.Decision.ToString();
        row.Note = request.Decision == AssetReviewDecision.Pending ? "" : note;
        row.DecidedAt = request.Decision == AssetReviewDecision.Pending ? null : now;
        row.AcceptedTakeSha256 = accepted;
        row.UpdatedAt = now;
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SfxReviewDecided", TargetType = "Sound", TargetId = sound.Name,
            PayloadJson = JsonSerializer.Serialize(new { decision = row.Decision, previous, note = row.Note, acceptedTake = accepted }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        return RepositoryResult<SfxSoundSummary>.Ok(
            Compose(listed, takes, rows).First(item => string.Equals(item.Name, sound.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The file a take's audio is in, if a manifest under the configured folders lists it.</summary>
    public async Task<string?> TakeFileAsync(string sha256, CancellationToken cancellationToken)
    {
        var sources = await SourcesAsync(cancellationToken);
        var wanted = (sha256 ?? "").Trim().ToLowerInvariant();
        return ScanTakes(sources.TakeRoots).Takes.FirstOrDefault(take => take.Sha256 == wanted)?.FullPath;
    }

    /// <summary>
    /// Copies every accepted take into the export folder under its game cue
    /// name, replacing an earlier export of the same cue, and writes a small
    /// manifest beside them saying which take each file is.
    /// </summary>
    public async Task<RepositoryResult<SfxExportResult>> ExportAsync(CancellationToken cancellationToken)
    {
        var sources = await SourcesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sources.ExportPath))
            return RepositoryResult<SfxExportResult>.Invalid("Choose an export folder first.");
        if (InsideUnrealProject(sources.ExportPath) is { } project)
            return RepositoryResult<SfxExportResult>.Invalid($"The export folder is inside the Unreal project at {project}.");

        var (listed, _) = ReadList(sources.ListPath);
        var (takes, _) = ScanTakes(sources.TakeRoots);
        var rows = await db.SfxReviews.ToListAsync(cancellationToken);
        var sounds = Compose(listed, takes, rows);
        var byHash = takes.GroupBy(take => take.Sha256).ToDictionary(group => group.Key, group => group.First());

        Directory.CreateDirectory(sources.ExportPath);
        var written = new List<SfxExportedFile>();
        var missing = new List<string>();
        var entries = new List<object>();
        foreach (var sound in sounds.Where(item => item.StoredDecision == AssetReviewDecision.Approved && item.Decision == AssetReviewDecision.Approved))
        {
            if (sound.AcceptedTakeSha256 is not { } sha || !byHash.TryGetValue(sha, out var take) || !File.Exists(take.FullPath))
            {
                missing.Add(sound.Name);
                continue;
            }
            var target = Path.Combine(sources.ExportPath, sound.CueFileName);
            var temporary = target + ".tmp";
            File.Copy(take.FullPath, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);
            written.Add(new SfxExportedFile(sound.Name, sound.CueFileName, sha, target));
            entries.Add(new
            {
                sound = sound.Name, category = sound.Category, cueFile = sound.CueFileName, takeSha256 = sha,
                source = take.FullPath, run = take.RunId, take = take.TakeNumber, loop = take.Loop,
                durationSeconds = take.Duration, note = sound.Note, decidedAt = sound.DecidedAt,
            });
        }

        var manifestPath = Path.Combine(sources.ExportPath, ExportManifestName);
        var now = timeProvider.GetUtcNow();
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            schema = "framewright.sfx-approved.v1",
            exportedAt = now,
            list = sources.ListPath,
            files = entries,
            missing,
        }, SettingsJson), cancellationToken);
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.NewGuid(), Type = "SfxReviewExported", TargetType = "Sound", TargetId = sources.ExportPath,
            PayloadJson = JsonSerializer.Serialize(new { written = written.Count, missing }), CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<SfxExportResult>.Ok(new SfxExportResult(sources.ExportPath, [.. written], [.. missing], manifestPath));
    }

    // ── Sources ────────────────────────────────────────────────────────────

    private async Task<SfxReviewSources> SourcesAsync(CancellationToken cancellationToken)
    {
        await SettingsLock.WaitAsync(cancellationToken);
        try
        {
            if (ReadSettings().TryGetValue(db.ActiveProjectId.ToString(), out var saved))
                return WithDefaultRoots(saved);
        }
        finally { SettingsLock.Release(); }

        var section = configuration.GetSection("Studio:SfxReview");
        var roots = section.GetSection("TakeRoots").Get<string[]>() ?? [];
        return WithDefaultRoots(new SfxReviewSources(section["ListPath"] ?? "", roots, section["ExportPath"] ?? ""));
    }

    /// <summary>With no take folder named, the folder the list is in is where its takes are looked for.</summary>
    private static SfxReviewSources WithDefaultRoots(SfxReviewSources sources)
    {
        if (sources.TakeRoots.Length > 0 || string.IsNullOrWhiteSpace(sources.ListPath)) return sources;
        var folder = Path.GetDirectoryName(sources.ListPath);
        return folder is null ? sources : sources with { TakeRoots = [folder] };
    }

    private Dictionary<string, SfxReviewSources> ReadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, SfxReviewSources>>(File.ReadAllText(SettingsPath), SettingsJson) ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>The Unreal project a folder is inside, if any: a parent folder holding a .uproject.</summary>
    internal static string? InsideUnrealProject(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            try
            {
                if (current.Exists && current.EnumerateFiles("*.uproject").Any()) return current.FullName;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }

    // ── The list ───────────────────────────────────────────────────────────

    internal sealed record ListedSound(string Name, string Category, string Prompt, double? Seconds, bool Loop, int? Variations);

    internal static (ListedSound[] Sounds, string? Error) ReadList(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ([], "No sound list is chosen yet.");
        if (!File.Exists(path)) return ([], $"The sound list is not at {path} any more.");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("sounds", out var sounds) || sounds.ValueKind != JsonValueKind.Array)
                return ([], "The sound list has no \"sounds\" array.");
            var result = new List<ListedSound>();
            foreach (var sound in sounds.EnumerateArray())
            {
                var name = Text(sound, "name");
                if (string.IsNullOrWhiteSpace(name) || result.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new ListedSound(name, Text(sound, "category") ?? "", Text(sound, "prompt") ?? "",
                    Number(sound, "seconds"), Bool(sound, "loop"), (int?)Number(sound, "variations")));
            }
            return ([.. result], null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"The sound list could not be read: {exception.Message}");
        }
    }

    // ── The takes ──────────────────────────────────────────────────────────

    internal sealed record FoundTake(
        string Name, string Category, string Sha256, string FullPath, string RunId, string FileName,
        int TakeNumber, long? Seed, double Duration, bool Loop, string Prompt, DateTimeOffset? CreatedAt);

    [GeneratedRegex(@"_(\d+)\.wav$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeNumberPattern();

    internal static (List<FoundTake> Takes, string[] Errors) ScanTakes(IEnumerable<string> roots)
    {
        var takes = new List<FoundTake>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MaxManifestDepth, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(root, ManifestName, options).ToList(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{root}: {exception.Message}");
                continue;
            }
            foreach (var manifest in manifests)
            {
                if (!seen.Add(Path.GetFullPath(manifest))) continue;
                try { ReadManifest(manifest, takes); }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{manifest}: {exception.Message}");
                }
            }
        }
        // The same bytes under two folders are one take.
        return ([.. takes.GroupBy(take => take.Sha256).Select(group => group.First())], [.. errors]);
    }

    private static void ReadManifest(string manifestPath, List<FoundTake> takes)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return;
        var runCreated = Date(root, "created_at");
        foreach (var file in files.EnumerateArray())
        {
            var name = Text(file, "name");
            var relative = Text(file, "file");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relative)) continue;
            var full = Path.GetFullPath(Path.Combine(folder, relative));
            // A manifest only speaks for files inside its own run.
            if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
            var sha = (Text(file, "sha256") ?? "").Trim().ToLowerInvariant();
            if (sha.Length != 64) sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
            var match = TakeNumberPattern().Match(full);
            takes.Add(new FoundTake(
                name, Text(file, "category") ?? "", sha, full, Path.GetFileName(folder), Path.GetFileName(full),
                match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1,
                (long?)Number(file, "seed"),
                Number(file, "final_duration") ?? Number(file, "seconds_effective") ?? 0,
                Bool(file, "loop"),
                Text(file, "prompt") ?? "",
                Date(file, "timestamp_utc") ?? runCreated ?? new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero)));
        }
    }

    // ── Putting it together ────────────────────────────────────────────────

    internal static SfxSoundSummary[] Compose(ListedSound[] listed, List<FoundTake> takes, List<SfxReviewRecord> rows)
    {
        var byName = takes.GroupBy(take => take.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(take => take.CreatedAt ?? DateTimeOffset.MinValue).ThenBy(take => take.RunId, StringComparer.Ordinal).ThenBy(take => take.TakeNumber)
                .ToList(), StringComparer.OrdinalIgnoreCase);
        var names = listed.Select(sound => (sound.Name, Listed: (ListedSound?)sound)).ToList();
        foreach (var unlisted in byName.Keys
                     .Where(name => listed.All(sound => !string.Equals(sound.Name, name, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(name => byName[name][0].Category, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
            names.Add((byName[unlisted][0].Name, null));

        return [.. names.Select(entry =>
        {
            var found = byName.TryGetValue(entry.Name, out var list) ? list : [];
            var row = rows.FirstOrDefault(item => string.Equals(item.SoundName, entry.Name, StringComparison.OrdinalIgnoreCase));
            var stored = row is not null && Enum.TryParse<AssetReviewDecision>(row.Decision, out var parsed) ? parsed : AssetReviewDecision.Pending;
            bool After(FoundTake take) => stored == AssetReviewDecision.ChangesRequested
                && row?.DecidedAt is { } decided && take.CreatedAt is { } made && made > decided;
            var answered = found.Any(After);
            var decision = answered ? AssetReviewDecision.Pending : stored;
            var category = entry.Listed?.Category is { Length: > 0 } listedCategory ? listedCategory : found.LastOrDefault()?.Category ?? "";
            var prefix = string.Equals(category, "Music", StringComparison.OrdinalIgnoreCase) ? "MUS" : "SFX";
            return new SfxSoundSummary(
                entry.Name,
                category,
                entry.Listed?.Prompt is { Length: > 0 } prompt ? prompt : found.LastOrDefault()?.Prompt ?? "",
                entry.Listed?.Seconds,
                (entry.Listed?.Loop ?? false) || found.Any(take => take.Loop),
                entry.Listed?.Variations,
                entry.Listed is not null,
                found.Count > 0,
                decision,
                stored,
                row?.Note ?? "",
                row?.DecidedAt,
                row?.AcceptedTakeSha256,
                answered ? (row!.Note.Length > 0 ? row.Note : RejectedWithoutNote) : "",
                $"{prefix}_{entry.Name}.wav",
                [.. found.Select(take => new SfxTakeSummary(
                    take.Sha256, take.RunId, take.FileName, take.TakeNumber, take.Seed, take.Duration, take.Loop,
                    take.Prompt, take.CreatedAt, $"/api/sfx-review/takes/{take.Sha256}/audio", After(take)))]);
        })];
    }

    internal static SfxReviewCounts Count(SfxSoundSummary[] sounds) => new(
        sounds.Length,
        sounds.Count(sound => sound.Decision == AssetReviewDecision.Pending && sound.Generated),
        sounds.Count(sound => sound.Decision == AssetReviewDecision.Approved),
        sounds.Count(sound => sound.Decision == AssetReviewDecision.ChangesRequested),
        sounds.Count(sound => sound.Decision == AssetReviewDecision.Pending && !sound.Generated));

    // ── JSON reading ───────────────────────────────────────────────────────

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        Text(element, property) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
