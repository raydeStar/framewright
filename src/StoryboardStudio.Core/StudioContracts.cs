using System.Text.Json.Serialization;

namespace StoryboardStudio.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShotStage
{
    Sketch,
    Draft,
    Final,
    Video
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalState
{
    Working,
    NeedsWork,
    Ratified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationState
{
    Connected,
    Ready,
    NeedsSetup,
    Offline,
    Protected
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GenerationRoute
{
    FastDraft,
    PrecisionDraft
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GenerationPurpose
{
    Draft,
    Final,
    Video
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoQuality
{
    Low,
    Medium,
    High,
    Max
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManifestState
{
    Prepared,
    Dispatched,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// A note on one scene object. It reports whether the revision it was anchored
/// to is still the one in the scene, so a note never silently describes
/// geometry that has changed underneath it.
/// </summary>
public sealed record SceneAnnotationSummary(
    Guid Id, Guid SceneId, Guid InstanceId, Guid AssetId, string InstanceName,
    double[] Anchor, SceneCameraSummary Camera, string Body, string State,
    bool Stale, bool Orphaned, DateTimeOffset CreatedAt);

public sealed record CreateSceneAnnotationRequest(Guid InstanceId, double[] Anchor, SceneCameraSummary Camera, string Body);

/// <summary>A staged change to exactly one instance. Applying it is the artist's move.</summary>
public sealed record SceneProposalSummary(
    Guid Id, Guid SceneId, Guid InstanceId, string InstanceName, int BaseSceneVersion,
    string Direction, string Rationale,
    double[]? Position, double[]? Rotation, double[]? Scale,
    string State, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, DateTimeOffset? AppliedAt);

public sealed record CreateSceneProposalRequest(
    Guid SceneId, Guid InstanceId, int ExpectedSceneVersion, string ObservedStateToken,
    string Direction, string Rationale,
    double[]? Position, double[]? Rotation, double[]? Scale, string IdempotencyKey);

/// <summary>Where the scene's inspection camera sits. Orbit values, not a matrix, so a saved view reopens exactly.</summary>
public sealed record SceneCameraSummary(double Yaw, double Pitch, double Distance, double[] Target, double FieldOfView);

/// <summary>The scene's basic lighting. Deliberately small: one key direction plus ambient fill.</summary>
public sealed record SceneEnvironmentSummary(double KeyIntensity, double KeyYaw, double KeyPitch, double AmbientIntensity);

/// <summary>
/// Simple geometry standing in for an object that has no library model yet. A
/// placeholder is a real object in the scene with its own identity and
/// transform, so replacing it later does not disturb anything around it.
/// </summary>
public sealed record ScenePlaceholderSummary(string Shape, double[] Size);

/// <summary>
/// One placed object. It is either pinned to an exact model revision or drawn
/// as a placeholder, never both, and it reports whether that revision can still
/// be loaded, so a scene opens honestly rather than silently dropping something
/// it cannot draw.
/// </summary>
public sealed record SceneInstanceSummary(
    Guid Id, Guid? AssetId, string Name,
    double[] Position, double[] Rotation, double[] Scale,
    string AssetName, int RevisionNumber, string? ContentUrl,
    bool Available, bool Archived, double[] Dimensions,
    ScenePlaceholderSummary? Placeholder = null, string? Role = null, Guid? PlanId = null,
    SceneClipBindingSummary? Clip = null, SceneRigidMotionSummary? Motion = null);

public sealed record SceneSummary(
    Guid Id, string Name, int Version,
    SceneCameraSummary Camera, SceneEnvironmentSummary Environment,
    SceneInstanceSummary[] Instances, DateTimeOffset UpdatedAt);

public sealed record SceneListItem(Guid Id, string Name, int Version, int InstanceCount, DateTimeOffset UpdatedAt);

public sealed record CreateSceneRequest(string Name);

public sealed record SaveSceneInstanceRequest(
    Guid Id, Guid? AssetId, string Name, double[] Position, double[] Rotation, double[] Scale,
    ScenePlaceholderSummary? Placeholder = null,
    SceneClipBindingSummary? Clip = null,
    SceneRigidMotionSummary? Motion = null);

public sealed record SaveSceneRequest(
    int ExpectedVersion, string Name,
    SceneCameraSummary Camera, SceneEnvironmentSummary Environment,
    SaveSceneInstanceRequest[] Instances);

/// <summary>
/// One object in a construction plan: what it is for, what would stand in for
/// it, roughly where it goes, and how sure the plan is about it. Confidence is
/// carried per object because a reference shows some things plainly and hides
/// others behind what is in front of them.
/// </summary>
public sealed record SceneBlockoutItemSummary(
    Guid Id, string Role, Guid? MatchAssetId, string? MatchAssetName,
    ScenePlaceholderSummary? Placeholder,
    double[] Position, double[] Rotation, double[] Scale,
    string MotionIntent, string Confidence, string Note, Guid? InstanceId);

/// <summary>
/// A reviewable construction plan read off one reference image. It builds
/// nothing by itself: until the artist approves it there is no scene, and
/// approving it generates no assets, only placeholders and library matches the
/// project already holds.
/// </summary>
public sealed record SceneBlockoutPlanSummary(
    Guid Id, Guid ReferenceAssetId, string ReferenceName, string ReferenceContentHash,
    string? ReferenceUrl, bool ReferenceChanged,
    string Title, string Summary, string State,
    SceneCameraSummary Camera, string[] Assumptions, string[] Uncertainties,
    SceneBlockoutItemSummary[] Items, Guid? SceneId, string? SceneName,
    DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt, DateTimeOffset? AppliedAt);

public sealed record ProposeSceneBlockoutItemRequest(
    string Role, Guid? MatchAssetId, string? Shape, double[]? Size,
    double[]? Position, double[]? Rotation, double[]? Scale,
    string? MotionIntent, string? Confidence, string? Note);

public sealed record ProposeSceneBlockoutRequest(
    Guid ReferenceAssetId, string ObservedReferenceHash, string Title, string Summary,
    SceneCameraSummary? Camera, string[]? Assumptions, string[]? Uncertainties,
    ProposeSceneBlockoutItemRequest[] Items, string IdempotencyKey);

public sealed record ApplySceneBlockoutRequest(string? SceneName);

/// <summary>One material as the model inspector reports it.</summary>
public sealed record ModelMaterialSummary(
    string Name, bool Textured, string AlphaMode, bool DoubleSided, double Transmission = 0);

/// <summary>The bounded GLB subset and the ceilings that refuse a model before it loads.</summary>
public sealed record ModelSupportLimits(
    long MaxBytes, int MaxVertices, int MaxTriangles, long MaxEmbeddedTextureBytes,
    int MaxNodes, int MaxMaterials, int MaxImages, string[] SupportedRequiredExtensions);

/// <summary>What the artist reads about a stored model before and while inspecting it.</summary>
/// <summary>One bone of a stored rig, with its rest pose as the file holds it.</summary>
public sealed record ModelBoneSummary(
    string Name, string? Parent, int Depth,
    double[] RestTranslation, double[] RestRotation, double[] RestScale, double[] RestWorldPosition);

/// <summary>
/// What is known about a stored model's rig, and what could not be confirmed.
///
/// AnimationReady is the only claim anything downstream may act on, and it is
/// granted solely when a documented body profile is matched and every check
/// passed. A skeleton with bones named something else is reported as an unknown
/// skeleton, never as an almost-humanoid that might work.
/// </summary>
public sealed record ModelRigSummary(
    bool HasSkeleton, string ProfileId, string ProfileName, bool ProfileMatched,
    string[] MissingBones, string[] UnexpectedBones,
    int SkinCount, int BoneCount, int SkinnedVertexCount, int MaxInfluencesPerVertex,
    ModelBoneSummary[] Bones,
    bool TransformsFinite, bool BindPoseValid, bool SkinWeightsValid, int SkinWeightsChecked,
    string[] Findings, bool AnimationReady,
    /// <summary>
    /// This exact skeleton's identity, agreed with the compiler that produced
    /// it. A clip authored against a matching fingerprint belongs to this rig;
    /// it is not a claim that anything retargets.
    /// </summary>
    string? Fingerprint = null);

/// <summary>A rotation in radians applied to one named bone, on top of its rest pose.</summary>
public sealed record RigBonePoseRequest(string Bone, double[] Rotation);

public sealed record RigPoseRequest(RigBonePoseRequest[] Pose);

public sealed record RigJointPlacementSummary(string Bone, string? Parent, double[] Position, double[] RestPosition);

/// <summary>
/// Where a rig's bones land under one pose. It is bound to the exact model
/// revision it was calculated from, and it stores and draws nothing.
/// </summary>
public sealed record RigPoseSummary(
    Guid AssetId, string ContentHash, string ProfileId, RigJointPlacementSummary[] Joints);

/// <summary>What the artist asks for: this reference image, made into a model.</summary>
/// <summary>
/// A generator normalises: whatever it makes arrives about two metres tall, a
/// lantern exactly as much as a person. Nothing downstream can fix that by
/// measuring, so the artist says how big the thing is -- not in metres, which
/// almost nobody can judge for a prop, but as a landmark on a person. A number
/// invented to get past a prompt would be worse than none, because every
/// measurement after it would be taken against a lie.
/// </summary>
public sealed record CreateModelGenerationRequest(
    Guid SourceAssetId, string Name, string? Size = null, double? SizeAdjust = null,
    string? GlassColour = null);

/// <summary>
/// Whether model generation can run on this workstation, and whether it is
/// allowed to. Capability and permission are separate answers: a route that
/// could run is still uncommissioned until the artist says otherwise.
/// </summary>
public sealed record ModelGenerationReadiness(
    bool Installed, bool Commissioned, bool CanRun,
    string? CompilerVersion, string? Checkout, string? Blender,
    string[] Missing, string Detail, ModelSizeChoice[]? Sizes = null,
    IReadOnlyDictionary<string, string>? Suffixes = null, ModelColourChoice[]? Colours = null);

/// <summary>
/// A colour a model's glass might have been painted, offered the way a person
/// would name it. Nothing in a mesh says which faces are glass -- a pane and
/// its frame are the same surface -- so the paint is what says so, and the
/// artist says which paint. The compiler owns this vocabulary.
/// </summary>
public sealed record ModelColourChoice(string Colour, string Description);

/// <summary>
/// How big a thing is, offered the way a person can judge it: where it comes
/// up to on someone standing next to it. The compiler owns this vocabulary and
/// the metres behind each landmark; this studio shows what it is told.
/// </summary>
public sealed record ModelSizeChoice(string Size, string Description, double Metres);

/// <summary>One reusable clip a model carries, as the file declares it.</summary>
public sealed record ModelClipSummary(
    string Name, double Duration, int ChannelCount,
    string[] TargetBones, string[] Paths, bool MovesRoot,
    bool Supported, string[] Findings);

/// <summary>
/// A clip bound to one scene object, with that object's own playback settings.
/// Two objects can share one clip and still be trimmed, looped, sped, and
/// scrubbed independently, because every one of these values lives here.
/// </summary>
public sealed record SceneClipBindingSummary(
    Guid ClipAssetId, string ClipName, string? ClipAssetName,
    double Start, double End, double Speed, double Time, bool Loop, string RootMotion);

/// <summary>
/// A rigid part turning about a declared pivot, with no skeleton involved. The
/// pivot is a point in the object's own space, and it is the one point the
/// motion leaves exactly where it is.
/// </summary>
public sealed record SceneRigidMotionSummary(
    double[] Pivot, string Axis, double FromRadians, double ToRadians, double Seconds, bool PingPong);

/// <summary>Where one object sits at one exact time. Calculated, never stored.</summary>
public sealed record SceneMotionSampleSummary(
    Guid SceneId, Guid InstanceId, string InstanceName, double Time, string Kind,
    RigJointPlacementSummary[] Joints, double[] RootOffset, string RootMotion,
    double[] Position, double[] Rotation, double Angle);

public sealed record ModelProfileSummary(
    Guid AssetId, string DisplayName, string ContentHash, long Bytes, string ContentUrl,
    string Container, string SpecificationVersion, string Generator,
    int NodeCount, int MeshCount, int PrimitiveCount, int VertexCount, int TriangleCount,
    int ImageCount, long EmbeddedTextureBytes, long BinaryChunkBytes,
    string[] DeclaredExtensions, string[] RequiredExtensions,
    ModelMaterialSummary[] Materials,
    double[] BoundsMin, double[] BoundsMax, double[] Dimensions,
    ModelSupportLimits Limits,
    ModelRigSummary? Rig = null,
    ModelClipSummary[]? Clips = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetKind
{
    Image,
    Video,
    Audio,
    Model
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContinuityCheckState
{
    Pass,
    Info,
    Review,
    Block
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualAuditSeverity
{
    Review,
    Block
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualCorrectionRoute
{
    RefineCurrent,
    RebuildFromSketch
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualReconciliationAction
{
    FixImage,
    AdoptImage,
    DecideLater
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineTrackKind
{
    Dialogue,
    Voice,
    Music
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoiceProfileKind
{
    ProviderPreset,
    ConsentedClone
}

public static class StudioDefaults
{
    public static readonly Guid ProjectId = Guid.Parse("2e883b13-6cc6-46e2-b9ed-f9c3aac6c5ec");

    public const string VisualStyle = "Prestige stylized animation with painterly cel shading, hand-painted textures, graphic shape language, controlled linework, expressive faces, and cinematic lighting. Aim for the visual qualities of premium adult animated drama without imitating a specific copyrighted character or frame.";
    public const string WorldCanon = "Skychasers is an arcane civic-fantasy world of monumental stone architecture, ceremonial technology, suspended structures, sculpted haze, oxidized metals, and restrained magical light. World features remain canon when they are outside the shot crop.";
    public const string PromptDirectives = "Preserve approved character identity, wardrobe, props, architecture, and shot blocking. Treat face and wardrobe authorities as separate compatible layers. Preserve distinctive marks and asymmetric features on the subject's specified anatomical side. Render characters as designed animation models with intentional silhouettes and simplified, expressive anatomy. Keep materials painterly and shapes readable.";
    public const string NegativeDirectives = "No photoreal live-action people, glossy game-engine rendering, waxy skin, generic cosplay photography, neon sci-fi bloom, literal stick figures, interface chrome, watermarks, or unintended text.";
}

public sealed record ProjectSummary(
    Guid Id,
    string Name,
    string Production,
    string SequenceCode,
    string SequenceName,
    int FramesPerSecond,
    string AspectRatio,
    int DeliveryWidth,
    int DeliveryHeight,
    string VisualStyle,
    string WorldCanon,
    string PromptDirectives,
    string NegativeDirectives,
    DateTimeOffset UpdatedAt = default,
    string ColorSpace = "Rec.709",
    int AudioSampleRate = 48000);

public sealed record UpdateProjectRequest(
    DateTimeOffset ExpectedUpdatedAt,
    string Name,
    string Production,
    string SequenceCode,
    string SequenceName,
    int FramesPerSecond,
    string AspectRatio,
    int DeliveryWidth,
    int DeliveryHeight,
    string VisualStyle,
    string WorldCanon,
    string PromptDirectives,
    string NegativeDirectives,
    string ColorSpace = "Rec.709",
    int AudioSampleRate = 48000);

/// <summary>
/// One row in the project switcher. Carries the counts the artist needs to tell
/// two productions apart at a glance without opening either.
/// </summary>
public sealed record ProjectListItem(
    Guid Id,
    string Name,
    string Production,
    string SequenceCode,
    string SequenceName,
    int ShotCount,
    int AuthorityCount,
    bool IsActive,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Creates a project. World settings are deliberately optional: a new production
/// starts from the studio defaults and the artist (or the Codex interview) refines
/// them afterwards, rather than being made to write four essays up front.
/// </summary>
public sealed record CreateProjectRequest(
    string Name,
    string Production,
    string SequenceCode,
    string SequenceName,
    int FramesPerSecond,
    string AspectRatio,
    int DeliveryWidth,
    int DeliveryHeight,
    string? VisualStyle = null,
    string? WorldCanon = null,
    string? PromptDirectives = null,
    string? NegativeDirectives = null,
    string ColorSpace = "Rec.709",
    int AudioSampleRate = 48000);

/// <summary>
/// The artist's answers to the project interview. Four plain-language questions
/// rather than a form: what this is, what it looks like, who is in it, and what is
/// already locked.
/// </summary>
public sealed record ProjectInterviewRequest(
    string Kind,
    string Look,
    string Cast,
    string Locked);

/// <summary>
/// A proposed starter authority. Deliberately shaped like the fields
/// <c>POST /api/references</c> accepts, so ratifying it is an ordinary create and
/// not a special path that bypasses review.
/// </summary>
public sealed record ProposedAuthority(
    string Name,
    string Category,
    string Description,
    string LockedConstraint,
    string Accent);

/// <summary>
/// What Codex proposes for a new project.
///
/// A proposal, never an application: nothing here is written until the artist
/// ratifies it. Every other Codex integration in this codebase advises while the
/// human ratifies, and a wizard that silently wrote world canon would be the one
/// place that boundary broke.
/// </summary>
public sealed record ProjectInterviewProposal(
    string Name,
    string Production,
    string SequenceCode,
    string SequenceName,
    int FramesPerSecond,
    string AspectRatio,
    int DeliveryWidth,
    int DeliveryHeight,
    string VisualStyle,
    string WorldCanon,
    string PromptDirectives,
    string NegativeDirectives,
    IReadOnlyList<ProposedAuthority> StarterAuthorities,
    string Rationale,
    bool Live,
    string Detail);

/// <summary>
/// A library authority as the active project sees it.
///
/// The import fields describe this project's relationship to it, which is what makes
/// the list actionable: not yet imported, imported at the current version, or
/// imported at an older one and therefore pullable.
/// </summary>
public sealed record LibraryAuthoritySummary(
    Guid Id,
    string Slug,
    string Name,
    string Category,
    int Version,
    string Description,
    string LockedConstraint,
    string Accent,
    int VisualVariant,
    string? ImageUrl,
    DateTimeOffset UpdatedAt,
    string? ImportedAsReferenceId,
    int? ImportedVersion,
    bool UpdateAvailable);

public sealed record LibraryAuthorityVersionSummary(
    Guid Id,
    Guid LibraryAuthorityId,
    int Version,
    string Description,
    string LockedConstraint,
    string? ImageUrl,
    string ContentHash,
    DateTimeOffset RatifiedAt,
    Guid? OriginProjectId);

/// <summary>
/// An authority in the active project, plus where it came from. Provenance is what
/// turns "port quickly" into an explicit act rather than a silent divergence.
/// </summary>
public sealed record ImportedAuthoritySummary(
    ReferenceSummary Reference,
    Guid? OriginLibraryId,
    int? OriginVersion,
    int? LibraryVersion,
    bool UpdateAvailable);

public sealed record ReorderShotsRequest(IReadOnlyList<Guid> ShotIds);

public sealed record ShotSummary(
    Guid Id,
    string Code,
    string Title,
    string Description,
    ShotStage Stage,
    ApprovalState Approval,
    int Version,
    int DurationFrames,
    int SortOrder,
    int VisualVariant,
    int OpenComments,
    string ContinuityState,
    string Camera,
    string Action,
    IReadOnlyList<string> ReferenceIds,
    IReadOnlyList<string> Constraints,
    DateTimeOffset UpdatedAt,
    Guid? CurrentAssetId = null,
    string? CurrentAssetUrl = null,
    Guid? VideoFirstFrameCandidateId = null,
    Guid? VideoLastFrameCandidateId = null,
    Guid? ProductionVideoJobId = null,
    Guid? ProductionVideoAssetId = null,
    string? ProductionVideoAssetUrl = null);

public sealed record UpdateVideoEndpointsRequest(
    DateTimeOffset ExpectedUpdatedAt,
    Guid? FirstFrameCandidateId = null,
    Guid? LastFrameCandidateId = null);

public sealed record ReferenceSummary(
    string Id,
    string Name,
    string Category,
    string Description,
    int Version,
    string Status,
    string Accent,
    string LockedConstraint,
    int VisualVariant,
    Guid? ImageAssetId = null,
    string? ImageUrl = null);

public sealed record CreateReferenceRequest(
    string Name,
    string Category,
    string Description,
    string LockedConstraint,
    string Accent,
    Guid? ImageAssetId = null);

public sealed record CreateReferenceVersionRequest(
    int ExpectedVersion,
    string Description,
    string LockedConstraint,
    Guid? ImageAssetId = null);

public sealed record ReferenceVersionSummary(
    Guid Id,
    string ReferenceId,
    int Version,
    string Description,
    string LockedConstraint,
    Guid? ImageAssetId,
    string? ImageUrl,
    string ContentHash,
    DateTimeOffset RatifiedAt);

public sealed record CreateShotRequest(
    string Code,
    string Title,
    string Description,
    int DurationFrames,
    string Camera,
    string Action,
    IReadOnlyList<string> ReferenceIds,
    IReadOnlyList<string> Constraints,
    Guid? InitialImageAssetId = null);

public sealed record SuggestShotIntentRequest(string Description);

public sealed record ShotIntentSuggestion(
    string Title,
    string Description,
    int DurationFrames,
    string Camera,
    string Action,
    IReadOnlyList<string> ReferenceIds,
    IReadOnlyList<string> Constraints,
    bool Live,
    string Detail);

public sealed record UpdateShotRequest(
    DateTimeOffset ExpectedUpdatedAt,
    string Title,
    string Description,
    int DurationFrames,
    string Camera,
    string Action,
    IReadOnlyList<string> ReferenceIds,
    IReadOnlyList<string> Constraints);

public sealed record CommentSummary(
    Guid Id,
    Guid ShotId,
    int Version,
    double X,
    double Y,
    string Body,
    string State,
    DateTimeOffset CreatedAt,
    string? ReferenceId = null,
    int? ReferenceVersion = null);

/// <summary>
/// A spatial review instruction bound to one immutable image asset. The same
/// note is visible from an authority revision and the standalone image studio.
/// </summary>
public sealed record AssetReviewNoteSummary(
    Guid Id,
    Guid AssetId,
    double X,
    double Y,
    string Body,
    string State,
    DateTimeOffset CreatedAt);

public sealed record CreateAssetReviewNoteRequest(double X, double Y, string Body);

public sealed record MoveCommentRequest(double X, double Y);

public sealed record PromoteCandidateRequest(int ExpectedCurrentVersion);

public sealed record ShotVersionSummary(
    Guid Id,
    Guid ShotId,
    int Version,
    ShotStage Stage,
    ApprovalState Approval,
    int VisualVariant,
    string ManifestHash,
    DateTimeOffset RatifiedAt,
    Guid? AssetId = null,
    string? AssetUrl = null,
    Guid? ProductionVideoJobId = null,
    Guid? ProductionVideoAssetId = null,
    string? ProductionVideoAssetUrl = null,
    string Code = "",
    string Title = "",
    string Description = "",
    int DurationFrames = 1,
    string Camera = "",
    string Action = "",
    Guid? VideoFirstFrameCandidateId = null,
    Guid? VideoLastFrameCandidateId = null);

public sealed record CandidateVersionSummary(
    Guid Id,
    Guid ShotId,
    int Version,
    ShotStage Stage,
    ApprovalState Approval,
    bool IsCurrent,
    Guid? AssetId,
    string? AssetUrl,
    Guid? SourceManifestId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SupersededAt,
    int? Width = null,
    int? Height = null);

public sealed record ContinuityCheckSummary(
    string Id,
    Guid ShotId,
    ContinuityCheckState State,
    string Category,
    string Title,
    string Detail,
    string Evidence,
    bool RequiresVisualReview = false);

public sealed record ShotContinuityReport(
    Guid ShotId,
    string ShotCode,
    int Version,
    string GateState,
    IReadOnlyList<ContinuityCheckSummary> Checks,
    DateTimeOffset EvaluatedAt,
    string ScopeNote);

public sealed record VisualAuditFindingSummary(
    string Id,
    string Category,
    VisualAuditSeverity Severity,
    string Title,
    string ContractExpectation,
    string ObservedImage,
    double Confidence,
    VisualCorrectionRoute SuggestedRoute,
    string FixInstruction);

public sealed record ShotContractProposal(
    string Description,
    string Action,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> ReferenceIds,
    string Rationale);

public sealed record VisualAuditDecisionSummary(
    string FindingId,
    VisualReconciliationAction Action,
    string MoreDetails);

public sealed record ShotVisualAuditSummary(
    Guid Id,
    Guid ShotId,
    int ShotVersion,
    Guid AssetId,
    string AssetHash,
    string ContractHash,
    string State,
    string GateState,
    string Summary,
    IReadOnlyList<VisualAuditFindingSummary> Findings,
    IReadOnlyList<VisualAuditDecisionSummary> Decisions,
    ShotContractProposal? AdoptedShotProposal,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string ScopeNote);

public sealed record RunVisualAuditRequest(bool Force = false);

public sealed record VisualAuditDecisionRequest(
    string FindingId,
    VisualReconciliationAction Action,
    string MoreDetails = "");

public sealed record ReconcileVisualAuditRequest(
    IReadOnlyList<VisualAuditDecisionRequest> Decisions,
    bool ApplyContract = false);

public sealed record VisualReconciliationPlan(
    ShotVisualAuditSummary Audit,
    bool ContractApplied,
    bool RequiresImageGeneration,
    VisualCorrectionRoute? ImageRoute,
    string GenerationDirection,
    ShotSummary? UpdatedShot,
    string Detail);

public sealed record TimelineClipSummary(
    Guid Id,
    TimelineTrackKind Track,
    string Label,
    int StartFrame,
    int DurationFrames,
    double TrimStartSeconds,
    double Volume,
    Guid? AssetId,
    string? AssetUrl,
    Guid? VoiceProfileId,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveTimelineClipRequest(
    TimelineTrackKind Track,
    string Label,
    int StartFrame,
    int DurationFrames,
    double TrimStartSeconds,
    double Volume,
    Guid? AssetId,
    Guid? VoiceProfileId,
    string Text,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record VoiceProfileSummary(
    Guid Id,
    string Name,
    VoiceProfileKind Kind,
    string Provider,
    string ProviderVoiceId,
    string CharacterName,
    string ConsentAttestation,
    DateTimeOffset? ConsentedAt,
    DateTimeOffset CreatedAt,
    string? CharacterReferenceId = null,
    Guid? SampleAssetId = null,
    string? SampleAssetUrl = null);

public sealed record CreateVoiceProfileRequest(
    string Name,
    VoiceProfileKind Kind,
    string Provider,
    string ProviderVoiceId,
    string CharacterName,
    bool ConsentConfirmed,
    string ConsentAttestation,
    string? CharacterReferenceId = null,
    Guid? SampleAssetId = null);

public sealed record VoiceSynthesisStatus(
    bool Enabled,
    bool CredentialConfigured,
    bool CanSynthesize,
    string Model,
    string Detail,
    bool LocalCanSynthesize = false,
    string? LocalModel = null,
    string? LocalDetail = null);

public sealed record DesignVoiceAuditionsRequest(
    string Direction,
    string CalibrationText,
    int Count = 3);

public sealed record VoiceAuditionSummary(
    Guid AssetId,
    string AssetUrl,
    string ProviderVoiceId,
    int Seed,
    string CalibrationText,
    string Direction,
    string Model);

public sealed record SynthesizeVoiceRequest(DateTimeOffset ExpectedUpdatedAt);

public sealed record VoiceSynthesisResult(
    TimelineClipSummary Clip,
    string Model,
    string? ProviderRequestId,
    bool ProviderCallMade);

public sealed record AudioMasteringStatus(
    bool ToolAvailable,
    int ClipsWithMedia,
    int GuideClips,
    bool CanMix,
    string Detail);

public sealed record PairingClaimRequest(string Code);

public sealed record PairingStatusSummary(
    bool LanEnabled,
    bool IsLoopback,
    bool IsPaired,
    string Workstation,
    string? PairingCode,
    DateTimeOffset? CodeExpiresAt,
    string SecurityNote);

public sealed record JobSummary(
    Guid Id,
    Guid ShotId,
    string ShotCode,
    string Kind,
    JobState State,
    int Progress,
    string Phase,
    string Backend,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    Guid? ManifestId = null,
    string? AdapterId = null,
    Guid? OutputAssetId = null,
    string? OutputAssetUrl = null,
    string? ProviderRequestId = null,
    int Attempt = 1,
    Guid? RetryOfJobId = null,
    DateTimeOffset? LastHeartbeatAt = null,
    string WorkType = "Shot");

public sealed record GenerationAdapterSummary(
    string Id,
    string Name,
    string Kind,
    string State,
    string Detail,
    bool CanDispatch,
    IReadOnlyList<GenerationRoute> Routes,
    IReadOnlyList<GenerationPurpose> Purposes);

/// <summary>The concrete graph selected behind the artist-facing draft action.</summary>
public sealed record DraftWorkflowSummary(
    string Id,
    string Name,
    string Mode,
    string Summary,
    bool UsesComposition,
    IReadOnlyList<string>? Capabilities = null,
    int MaxReferenceImages = 0);

public sealed record GenerationPreflightReference(
    string Id,
    string Name,
    string Category,
    int Version,
    bool HasApprovedImage,
    bool WillBindVisually,
    string Detail);

public sealed record GenerationPreflightCheck(string State, string Title, string Detail);

public sealed record GenerationPreflightSummary(
    Guid ManifestId,
    string ManifestHash,
    string AdapterId,
    string AdapterName,
    string Route,
    string Purpose,
    string WorkflowId,
    string WorkflowName,
    IReadOnlyList<string> WorkflowCapabilities,
    int MaxReferenceImages,
    bool HasComposition,
    IReadOnlyList<GenerationPreflightReference> References,
    IReadOnlyList<string> Constraints,
    int DeliveryWidth,
    int DeliveryHeight,
    int FramesPerSecond,
    string ColorSpace,
    int AudioSampleRate,
    IReadOnlyList<GenerationPreflightCheck> Checks,
    bool Ready);

public sealed record DispatchManifestRequest(
    string ExpectedManifestHash,
    string AdapterId);

public sealed record IntegrationSummary(
    string Id,
    string Name,
    IntegrationState State,
    string Headline,
    string Detail,
    string? Endpoint,
    bool CanInspect,
    bool CanSubmit,
    DateTimeOffset CheckedAt);

public sealed record StudioSnapshot(
    ProjectSummary Project,
    IReadOnlyList<ShotSummary> Shots,
    IReadOnlyList<ReferenceSummary> References,
    IReadOnlyList<CommentSummary> Comments,
    IReadOnlyList<JobSummary> Jobs,
    DateTimeOffset ServerTime,
    bool DemoMode);

public sealed record SketchPoint(double X, double Y, double Pressure);

public sealed record SketchStroke(
    string Id,
    IReadOnlyList<SketchPoint> Points,
    string Color,
    double Width);

public sealed record SketchLabel(
    string Id,
    double X,
    double Y,
    string Text);

public sealed record SketchJoint(
    string Name,
    double X,
    double Y);

public sealed record PosePresetSummary(
    Guid Id,
    string Name,
    IReadOnlyList<SketchJoint> Joints,
    DateTimeOffset CreatedAt);

public sealed record CreatePosePresetRequest(
    string Name,
    IReadOnlyList<SketchJoint> Joints);

public sealed record SketchObject(
    string Id,
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation,
    string Color,
    string Label,
    string Pose,
    string Facing,
    string Build,
    string IdentityMode,
    bool FullBody,
    string? CharacterReferenceId,
    string? WardrobeReferenceId,
    IReadOnlyList<SketchJoint> Joints);

public sealed record SketchContent(
    IReadOnlyList<SketchStroke> Strokes,
    IReadOnlyList<SketchLabel> Labels,
    IReadOnlyList<SketchObject>? Objects = null);

public sealed record SaveSketchRequest(
    int ExpectedRevision,
    string CreativeBrief,
    SketchContent Content,
    Guid? UnderlayAssetId = null,
    Guid? CompositionAssetId = null);

public sealed record SketchDocumentSummary(
    Guid Id,
    Guid ShotId,
    int Revision,
    string CreativeBrief,
    SketchContent Content,
    Guid? UnderlayAssetId,
    Guid? CompositionAssetId,
    string ContentHash,
    DateTimeOffset UpdatedAt);

public sealed record SaveFrameMarkupRequest(
    int ExpectedRevision,
    IReadOnlyList<SketchStroke> Strokes);

public sealed record FrameMarkupSummary(
    Guid Id,
    Guid ShotId,
    int Version,
    int Revision,
    IReadOnlyList<SketchStroke> Strokes,
    string ContentHash,
    DateTimeOffset UpdatedAt);

public sealed record AuthorityBinding(
    string Id,
    string Name,
    string Category,
    int Version,
    bool IsPinned = false,
    IReadOnlyList<AuthorityPlacementBinding>? Placements = null,
    int SourceOrder = 0);

/// <summary>
/// A frozen semantic placement for one authority. This travels separately from
/// the prose prompt so provider routing never has to guess whether a reference
/// was pinned by searching for a particular sentence fragment.
/// </summary>
public sealed record AuthorityPlacementBinding(
    double X,
    double Y,
    string Body,
    string Instruction);

public sealed record PrepareGenerationManifestRequest(
    int ExpectedShotVersion,
    int ExpectedSketchRevision,
    GenerationRoute Route,
    GenerationPurpose Purpose = GenerationPurpose.Draft,
    Guid? CompositionAssetId = null,
    string? CreativeBriefOverride = null,
    int? MarkupRevision = null,
    bool AllowSketchCompositionFallback = true,
    string? VideoEndpointRole = null,
    Guid? VideoEndpointSourceCandidateId = null);

/// <summary>
/// Starts the ordinary draft path from the shot card. The service resolves the
/// current authority packet and automatically chooses text-to-image when no
/// composition exists, or sketch-to-image when one does.
/// </summary>
public sealed record GenerateDraftRequest(
    int ExpectedShotVersion,
    string? AdapterId = null,
    Guid? CompositionAssetId = null,
    string? CreativeBriefOverride = null,
    int? MarkupRevision = null,
    bool AllowSketchCompositionFallback = true);

public sealed record PrepareVideoManifestRequest(
    int ExpectedShotVersion,
    string MotionBrief,
    bool ConfirmEndpointCompatibility = false,
    Guid? FirstFrameCandidateId = null,
    Guid? LastFrameCandidateId = null,
    VideoQuality Quality = VideoQuality.Low,
    string? TakeId = null);

public sealed record PromoteVideoTakeRequest(
    int ExpectedShotVersion,
    string AdapterId = "comfyui-h3-video");

public sealed record GenerationManifestSummary(
    Guid Id,
    Guid ShotId,
    string ShotCode,
    int ShotVersion,
    Guid SketchId,
    int SketchRevision,
    GenerationRoute Route,
    GenerationPurpose Purpose,
    ManifestState State,
    string CreativeBrief,
    IReadOnlyList<AuthorityBinding> Authorities,
    IReadOnlyList<string> Constraints,
    string ManifestHash,
    bool ProviderCallMade,
    Guid? CompositionAssetId,
    string? CompositionAssetHash,
    DateTimeOffset CreatedAt,
    Guid? LastFrameAssetId = null,
    string? LastFrameAssetHash = null,
    VideoQuality? VideoQuality = null,
    long? VideoSeed = null,
    string? VideoTakeId = null,
    Guid? PromotedFromJobId = null);

public sealed record AssetSummary(
    Guid Id,
    Guid ProjectId,
    AssetKind Kind,
    string OriginalFileName,
    string MimeType,
    long Bytes,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string ContentHash,
    string ContentUrl,
    DateTimeOffset CreatedAt,
    string DisplayName = "",
    Guid? CollectionId = null,
    IReadOnlyList<string>? Tags = null,
    string Notes = "",
    string Source = "Imported",
    bool IsArchived = false,
    DateTimeOffset UpdatedAt = default,
    Guid? RevisionFamilyId = null,
    int? RevisionNumber = null,
    bool IsCurrentRevision = false,
    Guid? ParentAssetId = null,
    string RevisionPrompt = "",
    string RevisionEngine = "");

public sealed record AssetCollectionSummary(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Color,
    int SortOrder,
    int AssetCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAssetCollectionRequest(string Name, string Color = "#73b7cf");
public sealed record UpdateAssetCollectionRequest(string Name, string Color);
public sealed record UpdateAssetRequest(string DisplayName, Guid? CollectionId, IReadOnlyList<string> Tags, string Notes);

/// <summary>
/// Generates a library image without manufacturing a fake shot. The same
/// adapters and composition contract serve both subjects; only the promotion
/// target differs (asset library instead of a shot candidate stack).
/// </summary>
public sealed record GenerateAssetImageRequest(
    string Name,
    string CreativeBrief,
    GenerationRoute Route,
    string AdapterId,
    Guid? CompositionAssetId = null,
    IReadOnlyList<Guid>? ReferenceAssetIds = null,
    Guid? RevisionFamilyId = null,
    Guid? ParentAssetId = null,
    bool UseCurrentFrame = false,
    AuthorityGenerationTarget? AuthorityTarget = null,
    IReadOnlyList<AssetGenerationReference>? ReferenceBindings = null);

/// <summary>
/// A deliberately selected asset reference and the visual role it controls.
/// Legacy callers may continue to send <c>ReferenceAssetIds</c>; those become
/// explicit General references rather than silently disappearing during edits.
/// </summary>
public sealed record AssetGenerationReference(
    Guid AssetId,
    string Role = "General");

/// <summary>
/// Optional durable promotion target for an asset generation. Keeping this in
/// the frozen job packet means an authority render can finish after the artist
/// navigates away without relying on a browser callback to append the version.
/// </summary>
public sealed record AuthorityGenerationTarget(
    string ReferenceId,
    int ExpectedVersion,
    string Description,
    string LockedConstraint);
public sealed record AddAssetRevisionRequest(Guid AssetId, string Prompt = "Imported revision", string Engine = "Imported");
public sealed record CreateAssetPlacementRequest(Guid ShotId, string Role);
public sealed record AssetPlacementSummary(Guid Id, Guid AssetId, Guid ShotId, string ShotCode, string ShotTitle, string Role, DateTimeOffset CreatedAt);

public sealed record CreateCommentRequest(double X, double Y, string Body, string? ReferenceId = null);

public sealed record RatifyRequest(int ExpectedVersion, string Reason);

public sealed record CodexAssistRequest(string Mode, string Message, Guid? ShotId);

public sealed record CodexAssistResponse(
    string Mode,
    string Headline,
    string Message,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> SuggestedActions,
    bool Live,
    DateTimeOffset CompletedAt);

public sealed record ImproveGenerationDirectionRequest(
    string Direction,
    string Purpose);

public sealed record ImprovedGenerationDirection(
    string OriginalText,
    string ImprovedText,
    string Summary,
    bool Live);

/// <summary>A one-line "do this differently" note that re-renders the shot.</summary>
public sealed record RedirectShotRequest(
    string Direction,
    string? AdapterId = null,
    Guid? GuidanceAssetId = null,
    int? MarkupRevision = null);

/// <summary>What deleting a shot slot destroyed, so the UI can say it plainly.</summary>
public sealed record ShotDeletionSummary(string Code, int Candidates, int RatifiedVersions, int Comments, int Jobs);

/// <summary>
/// What deleting a project destroyed. AssetRecords counts rows, not stored files:
/// the asset store is content-addressed and shared, so the bytes stay put.
/// </summary>
public sealed record ProjectDeletionSummary(string Name, int Shots, int Authorities, int RatifiedVersions, int AssetRecords);

/// <summary>Identity metadata only. Versioned content still needs a new version.</summary>
public sealed record UpdateReferenceRequest(string Name, string Category, string Accent);
