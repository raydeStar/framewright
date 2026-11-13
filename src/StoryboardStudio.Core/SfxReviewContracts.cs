using System.Text.Json.Serialization;

namespace StoryboardStudio.Core;

/// <summary>
/// Where sound review reads from and writes to: one MOSS sound list (the
/// sounds that should exist, in priority order), the folders MOSS writes its
/// runs into (each run has an <c>sfx-manifest.json</c>), and the folder the
/// accepted takes are exported to under the game's cue names.
/// </summary>
public sealed record SfxReviewSources(string ListPath, string[] TakeRoots, string ExportPath);

public sealed record SetSfxReviewSourcesRequest(string? ListPath, string[]? TakeRoots, string? ExportPath);

/// <summary>One generated take of a sound, as its MOSS run manifest describes it.</summary>
public sealed record SfxTakeSummary(
    string Sha256,
    string RunId,
    string FileName,
    int TakeNumber,
    long? Seed,
    double DurationSeconds,
    bool Loop,
    string Prompt,
    DateTimeOffset? CreatedAt,
    string AudioUrl,
    // Made after the sound was rejected: it answers that rejection.
    bool AfterRejection);

/// <summary>
/// A sound on the list (or only in a MOSS run), its takes and the decision on
/// it. <see cref="Decision"/> is what to act on: a rejected sound that has a
/// newer take is Pending again, with <see cref="AnswersNote"/> saying what that
/// take answers. <see cref="StoredDecision"/> is what was last decided.
/// </summary>
public sealed record SfxSoundSummary(
    string Name,
    string Category,
    string Prompt,
    double? Seconds,
    bool Loop,
    int? Variations,
    bool OnList,
    bool Generated,
    AssetReviewDecision Decision,
    AssetReviewDecision StoredDecision,
    string Note,
    DateTimeOffset? DecidedAt,
    string? AcceptedTakeSha256,
    string AnswersNote,
    // The file the accepted take is exported as: SFX_<Name>.wav, or MUS_<Name>.wav for music.
    string CueFileName,
    SfxTakeSummary[] Takes);

public sealed record SfxReviewCounts(int Total, int Pending, int Approved, int ChangesRequested, int NotGenerated);

public sealed record SfxReviewBoard(
    SfxReviewSources Sources,
    string? ListError,
    string[] ManifestErrors,
    SfxReviewCounts Counts,
    SfxSoundSummary[] Sounds);

/// <summary>
/// Accept one take of a sound, reject the sound with an optional note, or put
/// it back to Pending. Accepting names the take; the note is kept either way.
/// </summary>
public sealed record SetSfxReviewDecisionRequest(AssetReviewDecision Decision, string? Note, string? TakeSha256);

public sealed record SfxExportedFile(string Sound, string CueFileName, string TakeSha256, string Path);

public sealed record SfxExportResult(
    string ExportPath,
    SfxExportedFile[] Written,
    // Accepted sounds whose accepted take could not be found on disk, by name.
    string[] Missing,
    string ManifestPath);
