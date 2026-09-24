export type Workspace = 'board' | 'assets' | 'world' | 'authority' | 'scene' | 'shot' | 'review' | 'sequence'
export type ShotStage = 'Sketch' | 'Draft' | 'Final' | 'Video'
export type ApprovalState = 'Working' | 'NeedsWork' | 'Ratified'
export type GenerationRoute = 'FastDraft' | 'PrecisionDraft'
export type GenerationPurpose = 'Draft' | 'Final' | 'Video'
export type VideoQuality = 'Low' | 'Medium' | 'High' | 'Max'

export interface ProjectSummary { id: string; name: string; production: string; sequenceCode: string; sequenceName: string; framesPerSecond: number; aspectRatio: string; deliveryWidth: number; deliveryHeight: number; visualStyle: string; worldCanon: string; promptDirectives: string; negativeDirectives: string; updatedAt: string; colorSpace: 'Rec.709' | 'Display P3 D65' | 'Rec.2020'; audioSampleRate: 44100 | 48000 | 96000; isSample?: boolean }
/** One row in the project switcher. Counts come from the server, which reads them across the scope. */
export interface ProjectListItem { id: string; name: string; production: string; sequenceCode: string; sequenceName: string; shotCount: number; authorityCount: number; isActive: boolean; updatedAt: string; isSample?: boolean }
export interface ProjectDeletionSummary { name: string; shots: number; authorities: number; ratifiedVersions: number; assetRecords: number }
export interface PortableProjectImportSummary { projectId: string; name: string; shotCount: number; assetCount: number; sceneCount: number; sceneShotBindingCount: number; idsRemapped: boolean; detail: string }
export interface ProposedAuthority { name: string; category: string; description: string; lockedConstraint: string; accent: string }
/** A Codex proposal for a new project. Never applied automatically — it fills the editable form. */
export interface ProjectInterviewProposal { name: string; production: string; sequenceCode: string; sequenceName: string; framesPerSecond: number; aspectRatio: string; deliveryWidth: number; deliveryHeight: number; visualStyle: string; worldCanon: string; promptDirectives: string; negativeDirectives: string; starterAuthorities: ProposedAuthority[]; rationale: string; live: boolean; detail: string }
export interface ShotSummary { id: string; code: string; title: string; description: string; stage: ShotStage; approval: ApprovalState; version: number; durationFrames: number; sortOrder: number; visualVariant: number; openComments: number; continuityState: string; camera: string; action: string; referenceIds: string[]; constraints: string[]; updatedAt: string; currentAssetId?: string; currentAssetUrl?: string; videoFirstFrameCandidateId?: string; videoLastFrameCandidateId?: string; productionVideoJobId?: string; productionVideoAssetId?: string; productionVideoAssetUrl?: string }
export interface ReferenceSummary { id: string; name: string; category: string; description: string; version: number; status: string; accent: string; lockedConstraint: string; visualVariant: number; imageAssetId?: string; imageUrl?: string }
export interface ReferenceVersionSummary { id: string; referenceId: string; version: number; description: string; lockedConstraint: string; imageAssetId?: string; imageUrl?: string; contentHash: string; ratifiedAt: string }
/**
 * A library authority as the active project sees it. The import fields are what make
 * the row actionable: absent means importable, present and behind means pullable.
 */
export interface LibraryAuthoritySummary { id: string; slug: string; name: string; category: string; version: number; description: string; lockedConstraint: string; accent: string; visualVariant: number; imageUrl?: string; updatedAt: string; importedAsReferenceId?: string; importedVersion?: number; updateAvailable: boolean }
export interface LibraryAuthorityVersionSummary { id: string; libraryAuthorityId: string; version: number; description: string; lockedConstraint: string; imageUrl?: string; contentHash: string; ratifiedAt: string; originProjectId?: string }
export interface CommentSummary { id: string; shotId: string; version: number; x: number; y: number; body: string; state: string; createdAt: string; referenceId?: string; referenceVersion?: number }
export interface AssetReviewNoteSummary { id: string; assetId: string; x: number; y: number; body: string; state: string; createdAt: string }
export interface JobSummary { id: string; shotId: string; shotCode: string; kind: string; state: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'; progress: number; phase: string; backend: string; createdAt: string; completedAt?: string; error?: string; manifestId?: string; adapterId?: string; outputAssetId?: string; outputAssetUrl?: string; providerRequestId?: string; attempt: number; retryOfJobId?: string; lastHeartbeatAt?: string; workType: 'Shot' | 'Asset' | 'Voice' | 'Music' | 'VoiceDesign' | 'SceneRender'; acknowledgedAt?: string }
export interface StudioSnapshot { project: ProjectSummary; shots: ShotSummary[]; references: ReferenceSummary[]; comments: CommentSummary[]; jobs: JobSummary[]; serverTime: string; demoMode: boolean }
export interface IntegrationSummary { id: string; name: string; state: 'Connected' | 'Ready' | 'NeedsSetup' | 'Offline' | 'Protected'; headline: string; detail: string; endpoint?: string; canInspect: boolean; canSubmit: boolean; checkedAt: string }
export interface CodexAssistResponse { mode: string; headline: string; message: string; findings: string[]; suggestedActions: string[]; live: boolean; completedAt: string }
export interface ImprovedGenerationDirection { originalText: string; improvedText: string; summary: string; live: boolean }
export interface ShotIntentSuggestion { title: string; description: string; durationFrames: number; camera: string; action: string; referenceIds: string[]; constraints: string[]; live: boolean; detail: string }
export interface SketchPoint { x: number; y: number; pressure: number }
export interface SketchStroke { id: string; points: SketchPoint[]; color: string; width: number }
export interface SketchLabel { id: string; x: number; y: number; text: string }
export type SketchObjectKind = 'Human' | 'Chair' | 'Table' | 'Doorway' | 'Arrow' | 'Block'
export interface SketchJoint { name: string; x: number; y: number }
export interface PosePresetSummary { id: string; name: string; joints: SketchJoint[]; createdAt: string }
export interface SketchObject {
  id: string
  kind: SketchObjectKind
  x: number
  y: number
  width: number
  height: number
  rotation: number
  color: string
  label: string
  pose: string
  facing: string
  build: string
  identityMode: string
  fullBody: boolean
  characterReferenceId?: string
  wardrobeReferenceId?: string
  joints: SketchJoint[]
}
export interface SketchContent { strokes: SketchStroke[]; labels: SketchLabel[]; objects: SketchObject[] }
export interface SketchDocumentSummary { id: string; shotId: string; revision: number; creativeBrief: string; content: SketchContent; underlayAssetId?: string; compositionAssetId?: string; contentHash: string; updatedAt: string }
export interface FrameMarkupSummary { id: string; shotId: string; version: number; revision: number; strokes: SketchStroke[]; contentHash: string; updatedAt: string }
export type ContinuityCheckState = 'Pass' | 'Info' | 'Review' | 'Block'
export interface ContinuityCheckSummary { id: string; shotId: string; state: ContinuityCheckState; category: string; title: string; detail: string; evidence: string; requiresVisualReview: boolean }
export interface ShotContinuityReport { shotId: string; shotCode: string; version: number; gateState: string; checks: ContinuityCheckSummary[]; evaluatedAt: string; scopeNote: string }
export type VisualAuditSeverity = 'Review' | 'Block'
export type VisualCorrectionRoute = 'RefineCurrent' | 'RebuildFromSketch'
export type VisualReconciliationAction = 'FixImage' | 'AdoptImage' | 'DecideLater'
export interface VisualAuditFindingSummary { id: string; category: string; severity: VisualAuditSeverity; title: string; contractExpectation: string; observedImage: string; confidence: number; suggestedRoute: VisualCorrectionRoute; fixInstruction: string }
export interface ShotContractProposal { description: string; action: string; constraints: string[]; referenceIds: string[]; rationale: string }
export interface VisualAuditDecisionSummary { findingId: string; action: VisualReconciliationAction; moreDetails: string }
export interface ShotVisualAuditSummary { id: string; shotId: string; shotVersion: number; assetId: string; assetHash: string; contractHash: string; state: string; gateState: string; summary: string; findings: VisualAuditFindingSummary[]; decisions: VisualAuditDecisionSummary[]; adoptedShotProposal?: ShotContractProposal; createdAt: string; completedAt?: string; scopeNote: string }
export interface VisualReconciliationPlan { audit: ShotVisualAuditSummary; contractApplied: boolean; requiresImageGeneration: boolean; imageRoute?: VisualCorrectionRoute; generationDirection: string; updatedShot?: ShotSummary; detail: string }
export type TimelineTrackKind = 'Dialogue' | 'Voice' | 'Music'
export interface TimelineClipSummary { id: string; track: TimelineTrackKind; label: string; startFrame: number; durationFrames: number; trimStartSeconds: number; volume: number; assetId?: string; assetUrl?: string; voiceProfileId?: string; text: string; createdAt: string; updatedAt: string }
export type VoiceProfileKind = 'ProviderPreset' | 'ConsentedClone'
export interface VoiceProfileSummary { id: string; name: string; kind: VoiceProfileKind; provider: string; providerVoiceId: string; characterName: string; consentAttestation: string; consentedAt?: string; createdAt: string; characterReferenceId?: string; sampleAssetId?: string; sampleAssetUrl?: string }
export interface VoiceAuditionSummary { assetId: string; assetUrl: string; providerVoiceId: string; seed: number; calibrationText: string; direction: string; model: string }
export interface VoiceSynthesisStatus { enabled: boolean; credentialConfigured: boolean; canSynthesize: boolean; model: string; detail: string; localCanSynthesize: boolean; localModel?: string; localDetail?: string }
export interface VoiceSynthesisResult { clip: TimelineClipSummary; model: string; providerRequestId?: string; providerCallMade: boolean }
export interface MusicGenerationStatus { enabled: boolean; endpointAllowed: boolean; serviceReachable: boolean; canCompose: boolean; canRender: boolean; detail: string; model: string; device: string }
export interface MusicSection { id: string; type: string; bars: number; lyrics: string; chords: string[]; melody: string }
export interface MusicCompositionDocument { title: string; description: string; style: string; tempo: number; meter: string; key: string; sections: MusicSection[]; performancePrompt: string }
export interface MusicRenderSummary { id: string; jobId: string; assetId: string; assetUrl: string; renderer: string; settingsJson: string; createdAt: string }
export interface MusicCompositionRevisionSummary { id: string; revisionNumber: number; parentRevisionId?: string; editSummary: string; composition: MusicCompositionDocument; abcNotation: string; contentHash: string; planArtifactManifestJson: string; createdAt: string; renders: MusicRenderSummary[] }
export interface MusicCompositionSummary { id: string; title: string; description: string; currentRevisionId: string; currentRevisionNumber: number; createdAt: string; updatedAt: string; revisions: MusicCompositionRevisionSummary[] }
export interface CredentialStatus { isConfigured: boolean; source: string; canManageHere: boolean; detail: string }
/**
 * The generation switches an artist can set from Production setup. `locked`
 * names each setting whose effective value comes from outside the app (an
 * environment variable or command line), so the switch is shown but not live.
 */
export interface GenerationSetup {
  canManageHere: boolean
  comfyUi: { endpoint: string; imagesEnabled: boolean; videoEnabled: boolean; locked: string[] }
  codex: { oneClickImages: boolean; locked: string[] }
}
export interface GenerationSetupChange { comfyUiEndpoint: string; comfyUiImagesEnabled: boolean; comfyUiVideoEnabled: boolean; codexOneClickImages: boolean }
export interface ComfyUiConnectionTest { reachable: boolean; detail: string }
export interface AudioMasteringStatus { toolAvailable: boolean; clipsWithMedia: number; guideClips: number; canMix: boolean; detail: string }
export interface PairingStatusSummary { lanEnabled: boolean; isLoopback: boolean; isPaired: boolean; workstation: string; pairingCode?: string; codeExpiresAt?: string; securityNote: string }
export interface BackupStatus { databaseIntegrity: string; assetCount: number; assetBytes: number; policy: string; scheduled: boolean; retainedBackups: number; latestBackupAt?: string; lastAttemptAt?: string; lastSuccessAt?: string; lastResult: string; lastError?: string; backupRootWritable: boolean; backupOverdue: boolean; backupHealth: 'Disabled' | 'Failed' | 'Overdue' | 'Running' | 'Healthy' }
export interface GenerationQueueStatus { queued: number; running: number; failed: number; oldestQueuedAt?: string; checkedAt: string }
export interface RuntimeReadinessCheck { id: string; state: 'Ready' | 'Failed' | 'Degraded' | 'Disabled'; detail: string; required: boolean }
export interface BuildIdentity { version: string; commit: string; builtAtUtc: string; channel: string }
export interface RuntimeReadinessSummary { status: 'Ready' | 'Degraded' | 'NotReady'; checks: RuntimeReadinessCheck[]; queue: GenerationQueueStatus; checkedAt: string; build: BuildIdentity }
export interface ProductionExportReadiness { canExportProduction: boolean; blockers: string[]; warnings: string[]; shotCount: number; ratifiedShotCount: number; openNoteCount: number; activeJobCount: number; examinedAt: string }
export interface AuthorityBinding { id: string; name: string; category: string; version: number }
export interface GenerationManifestSummary { id: string; shotId: string; shotCode: string; shotVersion: number; sketchId: string; sketchRevision: number; route: GenerationRoute; purpose: GenerationPurpose; state: 'Prepared' | 'Dispatched' | 'Completed' | 'Failed' | 'Cancelled'; creativeBrief: string; authorities: AuthorityBinding[]; constraints: string[]; manifestHash: string; providerCallMade: boolean; compositionAssetId?: string; compositionAssetHash?: string; createdAt: string; lastFrameAssetId?: string; lastFrameAssetHash?: string; videoQuality?: VideoQuality; videoSeed?: number; videoTakeId?: string; promotedFromJobId?: string }
export interface AssetSummary { id: string; projectId: string; kind: 'Image' | 'Video' | 'Audio' | 'Model'; originalFileName: string; mimeType: string; bytes: number; width?: number; height?: number; durationSeconds?: number; contentHash: string; contentUrl: string; createdAt: string; displayName: string; collectionId?: string; tags: string[]; notes: string; source: string; isArchived: boolean; updatedAt: string; revisionFamilyId?: string; revisionNumber?: number; isCurrentRevision: boolean; parentAssetId?: string; revisionPrompt: string; revisionEngine: string; preparationAcceptedAt?: string; preparationAcceptedBy: string; preparationAcceptanceNote: string; preparationTopologyChanged: boolean }
export interface AssetCollectionSummary { id: string; projectId: string; name: string; color: string; sortOrder: number; assetCount: number; createdAt: string; updatedAt: string }
/** Where one asset is actually used, across shots, scenes and clip bindings. */
export interface AssetUsageSummary { assetId: string; shots: number; scenes: number; clips: number; where: string[] }
export interface AssetPlacementSummary { id: string; assetId: string; shotId: string; shotCode: string; shotTitle: string; role: string; createdAt: string }
export interface AssetGenerationReference { id: string; assetId: string; label: string; detail: string; imageUrl: string; source: 'Shot' | 'Asset' | 'Authority' }
export interface RevisionGenerationSource { label: string; version: number; description: string; lockedConstraint: string; imageAssetId?: string; imageUrl?: string; visualVariant: number }
export interface AssetGenerationDraft { id: string; name: string; initialPrompt: string; route: 'fast' | 'precision'; underlayAssetId?: string; references: AssetGenerationReference[]; destination?: 'asset' | 'authority'; revisionSource?: RevisionGenerationSource; revisionFamilyId?: string; parentAssetId?: string }
export interface GenerationAdapterSummary { id: string; name: string; kind: string; state: string; detail: string; canDispatch: boolean; routes: GenerationRoute[]; purposes: GenerationPurpose[] }
export interface DraftWorkflowSummary { id: string; name: string; mode: string; summary: string; usesComposition: boolean; capabilities: string[]; maxReferenceImages: number }
export interface GenerationPreflightReference { id: string; name: string; category: string; version: number; hasApprovedImage: boolean; willBindVisually: boolean; detail: string }
export interface GenerationPreflightCheck { state: 'Pass' | 'Review' | 'Block'; title: string; detail: string }
export interface GenerationPreflightSummary { manifestId: string; manifestHash: string; adapterId: string; adapterName: string; route: string; purpose: string; workflowId: string; workflowName: string; workflowCapabilities: string[]; maxReferenceImages: number; hasComposition: boolean; references: GenerationPreflightReference[]; constraints: string[]; deliveryWidth: number; deliveryHeight: number; framesPerSecond: number; colorSpace: string; audioSampleRate: number; checks: GenerationPreflightCheck[]; ready: boolean }
export interface CandidateVersionSummary { id: string; shotId: string; version: number; stage: ShotStage; approval: ApprovalState; isCurrent: boolean; assetId?: string; assetUrl?: string; sourceManifestId?: string; createdAt: string; supersededAt?: string; width?: number; height?: number }

export interface WebMcpEnvelope<T = unknown> { ok: boolean; status: string; code: string; message: string; retryable: boolean; data?: T }
export interface ShotRevisionProposalSummary { id: string; shotId: string; baseVersion: number; creativeDirection: string; rationale: string; desiredMediaType: 'Image' | 'Video'; authorityIds: string[]; noteIds: string[]; preservedConstraints: string[]; state: 'Pending' | 'Accepted' | 'Rejected' | 'Applied'; createdAt: string; updatedAt: string; decidedAt?: string; appliedAt?: string }
/** What applying an accepted proposal hands to the ordinary revision surface. Never a provider call. */
export interface ShotRevisionInstructions { proposal: ShotRevisionProposalSummary; instructions: { shotId: string; code: string; baseVersion: number; direction: string; rationale: string; desiredMediaType: 'Image' | 'Video'; preservedConstraints: string[]; targetedNotes: { id: string; x: number; y: number; body: string; authorityId?: string; authorityVersion?: number }[]; generationAuthorized: boolean; note: string } }
/** What the artist currently has on screen in the Shot workspace. */
export interface DirectorShotView { kind: 'shot'; shotId: string; displayedVersion: number; archived: boolean; directorMode?: boolean; tool?: string }
/** What the artist has open in the Scene workspace: which object is selected, which reference is being read, and whether the view has a saved service-side counterpart. */
export interface DirectorSceneView { kind: 'scene'; sceneId: string; instanceId?: string; referenceAssetId?: string; directorMode?: boolean; dirty: boolean; time: number }
/** The exact library revision on screen. Comparison views cannot yield a single-frame observation. */
export interface DirectorAssetView { kind: 'asset'; assetId: string; contentHash: string; directorMode: boolean; comparing: boolean }
export type DirectorViewQuery = DirectorShotView | DirectorSceneView | DirectorAssetView
/** Where a scene's inspection camera sits. Orbit values, so a saved view reopens exactly. */
export interface SceneCameraSummary { yaw: number; pitch: number; distance: number; target: number[]; fieldOfView: number }
/** A scene's basic lighting: one key direction plus ambient fill. */
export interface ScenePointLightSummary { id: string; name: string; position: [number, number, number]; color: string; intensity: number; distance: number; castShadow: boolean }
export interface SceneEnvironmentSummary {
  keyIntensity: number; keyYaw: number; keyPitch: number; ambientIntensity: number
  keyColor?: string; ambientColor?: string; backgroundColor?: string; exposure?: number
  cinematic?: boolean; showGrid?: boolean; pointLights?: ScenePointLightSummary[] | null
}
/** Simple geometry standing in for an object that has no library model yet. */
export interface ScenePlaceholderSummary { shape: 'Box' | 'Cylinder' | 'Sphere' | 'Plane'; size: number[] }
/** One placed object: either pinned to an exact model revision or drawn as a placeholder, never both. */
export interface SceneInstanceSummary {
  id: string; assetId: string | null; name: string
  position: number[]; rotation: number[]; scale: number[]
  assetName: string; revisionNumber: number; contentUrl: string | null
  available: boolean; archived: boolean; dimensions: number[]
  placeholder?: ScenePlaceholderSummary | null
  /** What this object is for, and the plan that put it here. Both server-owned. */
  role?: string | null; planId?: string | null
  /** This object's own clip and playback settings, or its own rigid motion. Never both. */
  clip?: SceneClipBindingSummary | null
  motion?: SceneRigidMotionSummary | null
}
/** One object in a construction plan read off a reference. */
export interface SceneBlockoutItemSummary {
  id: string; role: string; matchAssetId: string | null; matchAssetName: string | null
  placeholder: ScenePlaceholderSummary | null
  position: number[]; rotation: number[]; scale: number[]
  motionIntent: string; confidence: 'Certain' | 'Approximate' | 'Occluded'; note: string
  instanceId: string | null
}
/** A reviewable construction plan. It builds nothing until the artist approves it. */
export interface SceneBlockoutPlanSummary {
  id: string; referenceAssetId: string; referenceName: string; referenceContentHash: string
  referenceUrl: string | null; referenceChanged: boolean
  title: string; summary: string; state: 'Pending' | 'Rejected' | 'Applied'
  camera: SceneCameraSummary; assumptions: string[]; uncertainties: string[]
  items: SceneBlockoutItemSummary[]; sceneId: string | null; sceneName: string | null
  createdAt: string; decidedAt?: string; appliedAt?: string
}
export interface SceneSummary { id: string; name: string; version: number; camera: SceneCameraSummary; environment: SceneEnvironmentSummary; instances: SceneInstanceSummary[]; updatedAt: string }
export interface SceneListItem { id: string; name: string; version: number; instanceCount: number; updatedAt: string }
export interface SceneShotBindingSummary {
  id: string; sceneId: string; sceneName: string; sceneVersion: number
  shotId: string; shotCode: string; shotVersion: number; camera: SceneCameraSummary
  startTime: number; endTime: number; stillTime: number
  deliveryWidth: number; deliveryHeight: number; framesPerSecond: number; colorSpace: string
  snapshotHash: string; stillAssetId: string; stillAssetUrl: string; createdAt: string
}
/** A resumable exact-frame take being assembled from an accepted scene still. */
export interface SceneRenderSummary {
  jobId: string; manifestId: string; bindingId: string
  sceneId: string; shotId: string; shotCode: string; sourceShotVersion: number
  state: 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'
  progress: number; phase: string; error?: string
  width: number; height: number; framesPerSecond: number; frameCount: number
  startTime: number; endTime: number
  startCamera: SceneCameraSummary; endCamera: SceneCameraSummary
  missingFrames: number[]; encoder: string; manifestHash: string
  attempt: number; retryOfJobId?: string
  outputAssetId?: string; outputAssetUrl?: string; promoted: boolean
}
export interface SceneRenderFrameReceipt {
  jobId: string; frameIndex: number; contentHash: string; alreadyPresent: boolean
  uploadedFrames: number; frameCount: number
}
/** A note on one scene object, bound to the revision it was measured against. */
export interface SceneAnnotationSummary {
  id: string; sceneId: string; instanceId: string; assetId: string; instanceName: string
  anchor: number[]; camera: SceneCameraSummary; body: string; state: string
  stale: boolean; orphaned: boolean; createdAt: string
}
/** A staged change to exactly one instance. */
export interface SceneProposalSummary {
  id: string; sceneId: string; instanceId: string; instanceName: string; baseSceneVersion: number
  direction: string; rationale: string
  position: number[] | null; rotation: number[] | null; scale: number[] | null
  state: 'Pending' | 'Accepted' | 'Rejected' | 'Applied'
  createdAt: string; decidedAt?: string; appliedAt?: string
}
/** One material as the model inspector reports it. */
export interface ModelMaterialSummary {
  name: string; textured: boolean; alphaMode: string; doubleSided: boolean
  /** 0 is solid. Above 0 the material transmits, which glTF records as an extension rather than an alpha mode. */
  transmission: number
}
/** The supported GLB subset and the ceilings that refuse a model before it loads. */
export interface ModelSupportLimits { maxBytes: number; maxVertices: number; maxTriangles: number; maxEmbeddedTextureBytes: number; maxNodes: number; maxMaterials: number; maxImages: number; supportedRequiredExtensions: string[] }
/** One bone of a stored rig, with its rest pose as the file holds it. */
export interface ModelBoneSummary {
  name: string; parent: string | null; depth: number
  restTranslation: number[]; restRotation: number[]; restScale: number[]; restWorldPosition: number[]
}
/**
 * What is known about a stored model's rig. `animationReady` is the only claim
 * anything may act on, and it is true solely when a documented body profile is
 * matched and every check passed.
 */
export interface ModelRigSummary {
  hasSkeleton: boolean; profileId: string; profileName: string; profileMatched: boolean
  missingBones: string[]; unexpectedBones: string[]
  skinCount: number; boneCount: number; skinnedVertexCount: number; maxInfluencesPerVertex: number
  bones: ModelBoneSummary[]
  transformsFinite: boolean; bindPoseValid: boolean; skinWeightsValid: boolean; skinWeightsChecked: number
  findings: string[]; animationReady: boolean
}
/** Where a rig's bones land under one pose, bound to the exact model revision. */
export interface RigPoseSummary {
  assetId: string; contentHash: string; profileId: string
  joints: { bone: string; parent: string | null; position: number[]; restPosition: number[] }[]
}
/**
 * Whether model generation can run here, and whether it is allowed to.
 * Capability and permission are separate answers.
 */
export interface ModelGenerationReadiness {
  installed: boolean; commissioned: boolean; canRun: boolean
  compilerVersion: string | null; checkout: string | null; blender: string | null
  missing: string[]; detail: string; sizes: ModelSizeChoice[] | null
  colours: ModelColourChoice[] | null
  /** How close the camera will get; hero is offered only where the compiler can paint a head on its own. */
  details: ModelDetailChoice[] | null
}
export interface ModelDetailChoice { detail: string; description: string; cost: string }
/** The fixed views one preparation rendered, of the source and of what it made. */
export interface ModelPreparationEvidence {
  jobId: string; sourceAssetId: string | null; derivativeAssetId: string | null
  source: ModelPreparationViews | null; derivative: ModelPreparationViews | null
  /** A rig's pose suite and landmark overlays, when the job rigged a humanoid. */
  deformation?: ModelPreparationViews | null
}
export interface ModelPreparationViews { step: string; sourceSha256: string; views: ModelPreparationView[] }
export interface ModelPreparationView { view: string; pass: string; url: string; sha256: string }
/** A colour a model's glass might have been painted, named the way a person would. */
export interface ModelColourChoice { colour: string; description: string }
/** How big a thing is, said as where it comes up to on a person. */
export interface ModelSizeChoice { size: string; description: string; metres: number }
/** One reusable clip a model carries, as its file declares it. */
export interface ModelClipSummary {
  name: string; duration: number; channelCount: number
  targetBones: string[]; paths: string[]; movesRoot: boolean
  supported: boolean; findings: string[]
}
/** A clip bound to one object, with that object's own playback settings. */
export interface SceneClipBindingSummary {
  clipAssetId: string; clipName: string; clipAssetName: string | null
  start: number; end: number; speed: number; time: number; loop: boolean
  rootMotion: 'Hold' | 'Offset'
}
/** A rigid part turning about a pivot declared in its own space. No skeleton. */
export interface SceneRigidMotionSummary {
  pivot: number[]; axis: 'X' | 'Y' | 'Z'
  fromRadians: number; toRadians: number; seconds: number; pingPong: boolean
}
/** Where one object sits at one exact time, calculated by the service. */
export interface SceneMotionSampleSummary {
  sceneId: string; instanceId: string; instanceName: string; time: number
  kind: 'Character' | 'RigidPart' | 'Static'
  joints: { bone: string; parent: string | null; position: number[]; restPosition: number[] }[]
  rootOffset: number[]; rootMotion: 'Hold' | 'Offset'
  position: number[]; rotation: number[]; angle: number
}
/** What the artist reads about a stored model, measured from the stored bytes. */
export interface ModelProfileSummary {
  assetId: string; displayName: string; contentHash: string; bytes: number; contentUrl: string
  container: string; specificationVersion: string; generator: string
  nodeCount: number; meshCount: number; primitiveCount: number; vertexCount: number; triangleCount: number
  imageCount: number; embeddedTextureBytes: number; binaryChunkBytes: number
  declaredExtensions: string[]; requiredExtensions: string[]
  materials: ModelMaterialSummary[]
  boundsMin: number[]; boundsMax: number[]; dimensions: number[]
  limits: ModelSupportLimits
  rig?: ModelRigSummary | null
  clips?: ModelClipSummary[] | null
  /** Which TEXCOORD_n channels the geometry carries, and how many primitives carry none. */
  uvChannels?: number[] | null
  primitivesWithoutUvs: number
}
export interface AgentActivityEntry { id: number; tool: string; state: 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'; message: string; at: string }
