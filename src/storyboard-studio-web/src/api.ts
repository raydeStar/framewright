import type { AssetCollectionSummary, AssetPlacementSummary, AssetSummary, AudioMasteringStatus, BackupStatus, CandidateVersionSummary, CodexAssistResponse, CommentSummary, CredentialStatus, DraftWorkflowSummary, FrameMarkupSummary, GenerationAdapterSummary, GenerationManifestSummary, GenerationPreflightSummary, GenerationPurpose, GenerationRoute, ImprovedGenerationDirection, IntegrationSummary, JobSummary, LibraryAuthoritySummary, LibraryAuthorityVersionSummary, MusicCompositionDocument, MusicCompositionSummary, MusicGenerationStatus, MusicSection, PairingStatusSummary, PosePresetSummary, ProductionExportReadiness, ProjectDeletionSummary, ProjectInterviewProposal, ProjectListItem, ProjectSummary, ReferenceSummary, ReferenceVersionSummary, RuntimeReadinessSummary, ShotContinuityReport, ShotIntentSuggestion, ShotRevisionProposalSummary, ShotSummary, ShotVisualAuditSummary, SketchContent, SketchDocumentSummary, SketchJoint, SketchStroke, StudioSnapshot, TimelineClipSummary, TimelineTrackKind, VisualReconciliationAction, VisualReconciliationPlan, VoiceAuditionSummary, VoiceProfileKind, VoiceProfileSummary, VoiceSynthesisStatus, WebMcpEnvelope } from './types'
import type { AssetReviewNoteSummary } from './types'

export class ApiError extends Error { constructor(message: string, public status: number) { super(message); this.name = 'ApiError' } }

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) {
    const text = await response.text()
    let problem: { error?: string; title?: string } | undefined
    try { problem = JSON.parse(text) as { error?: string; title?: string } } catch { /* Non-JSON errors are still useful to the operator. */ }
    throw new ApiError(problem?.error || problem?.title || text || `${response.status} ${response.statusText}`, response.status)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

async function optionalRequest<T>(url: string): Promise<T | null> {
  const response = await fetch(url, { headers: { 'Content-Type': 'application/json' } })
  if (response.status === 204 || response.status === 404) return null
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `${response.status} ${response.statusText}`)
  }
  const text = await response.text()
  return text ? JSON.parse(text) as T : null
}

export const studioApi = {
  webMcpContext: (selectedShotId?: string, signal?: AbortSignal) => request<WebMcpEnvelope<Record<string, unknown>>>(`/api/webmcp/context${selectedShotId ? `?selectedShotId=${encodeURIComponent(selectedShotId)}` : ''}`, { signal }),
  webMcpShots: (offset = 0, limit = 10, signal?: AbortSignal) => request<WebMcpEnvelope<Record<string, unknown>>>(`/api/webmcp/shots?offset=${offset}&limit=${limit}`, { signal }),
  webMcpShot: (shotId: string, signal?: AbortSignal) => request<WebMcpEnvelope<Record<string, unknown>>>(`/api/webmcp/shots/${shotId}`, { signal }),
  webMcpContinuity: (shotId: string, signal?: AbortSignal) => request<WebMcpEnvelope<ShotContinuityReport>>(`/api/webmcp/shots/${shotId}/continuity`, { signal }),
  webMcpProposals: (shotId?: string, signal?: AbortSignal) => request<WebMcpEnvelope<ShotRevisionProposalSummary[]>>(`/api/webmcp/proposals${shotId ? `?shotId=${encodeURIComponent(shotId)}` : ''}`, { signal }),
  webMcpPropose: (body: { shotId: string; expectedVersion: number; creativeDirection: string; rationale: string; desiredMediaType: 'image' | 'video'; authorityIds: string[]; noteIds: string[]; idempotencyKey: string }, signal?: AbortSignal) => request<WebMcpEnvelope<ShotRevisionProposalSummary>>('/api/webmcp/proposals', { method: 'POST', body: JSON.stringify(body), signal }),
  webMcpEditProposal: (id: string, body: { creativeDirection: string; rationale: string; desiredMediaType: 'Image' | 'Video' }, signal?: AbortSignal) => request<WebMcpEnvelope<ShotRevisionProposalSummary>>(`/api/webmcp/proposals/${id}`, { method: 'PUT', body: JSON.stringify(body), signal }),
  webMcpAcceptProposal: (id: string, signal?: AbortSignal) => request<WebMcpEnvelope<ShotRevisionProposalSummary>>(`/api/webmcp/proposals/${id}/accept`, { method: 'POST', signal }),
  webMcpRejectProposal: (id: string, signal?: AbortSignal) => request<WebMcpEnvelope<ShotRevisionProposalSummary>>(`/api/webmcp/proposals/${id}/reject`, { method: 'POST', signal }),
  webMcpJob: (id: string, signal?: AbortSignal) => request<WebMcpEnvelope<Record<string, unknown>>>(`/api/webmcp/jobs/${id}`, { signal }),
  snapshot: () => request<StudioSnapshot>('/api/studio'),
  pairingStatus: () => request<PairingStatusSummary>('/api/pairing/status'),
  rotatePairing: () => request<PairingStatusSummary>('/api/pairing/start', { method: 'POST' }),
  revokePairing: () => request<PairingStatusSummary>('/api/pairing/revoke', { method: 'POST' }),
  claimPairing: (code: string) => request<PairingStatusSummary>('/api/pairing/claim', { method: 'POST', body: JSON.stringify({ code }) }),
  integrations: () => request<IntegrationSummary[]>('/api/integrations'),
  openAiCredentialStatus: () => request<CredentialStatus>('/api/credentials/openai'),
  saveOpenAiCredential: (apiKey: string) => request<CredentialStatus>('/api/credentials/openai', { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: JSON.stringify({ apiKey }) }),
  deleteOpenAiCredential: () => request<CredentialStatus>('/api/credentials/openai', { method: 'DELETE', headers: { 'X-Storyboard-Studio': '1' } }),
  updateProject: (project: ProjectSummary) => request<ProjectSummary>('/api/project', { method: 'PUT', body: JSON.stringify({ expectedUpdatedAt: project.updatedAt, name: project.name, production: project.production, sequenceCode: project.sequenceCode, sequenceName: project.sequenceName, framesPerSecond: project.framesPerSecond, aspectRatio: project.aspectRatio, deliveryWidth: project.deliveryWidth, deliveryHeight: project.deliveryHeight, colorSpace: project.colorSpace, audioSampleRate: project.audioSampleRate, visualStyle: project.visualStyle, worldCanon: project.worldCanon, promptDirectives: project.promptDirectives, negativeDirectives: project.negativeDirectives }) }),
  projects: () => request<ProjectListItem[]>('/api/projects'),
  projectInterview: (body: { kind: string; look: string; cast: string; locked: string }) => request<ProjectInterviewProposal>('/api/codex/project-interview', { method: 'POST', body: JSON.stringify(body) }),
  createProject: (body: { name: string; production: string; sequenceCode: string; sequenceName: string; framesPerSecond: number; aspectRatio: string; deliveryWidth: number; deliveryHeight: number; colorSpace?: ProjectSummary['colorSpace']; audioSampleRate?: ProjectSummary['audioSampleRate']; visualStyle?: string; worldCanon?: string; promptDirectives?: string; negativeDirectives?: string }) => request<ProjectSummary>('/api/projects', { method: 'POST', body: JSON.stringify(body) }),
  activateProject: (projectId: string) => request<ProjectSummary>(`/api/projects/${projectId}/activate`, { method: 'POST' }),
  deleteProject: (projectId: string, acceptRatifiedLoss = false) => request<ProjectDeletionSummary>(`/api/projects/${projectId}?acceptRatifiedLoss=${acceptRatifiedLoss}`, { method: 'DELETE' }),
  assetReviewNotes: (assetId: string) => request<AssetReviewNoteSummary[]>(`/api/assets/${assetId}/review-notes`),
  addAssetReviewNote: (assetId: string, body: { x: number; y: number; body: string }) => request<AssetReviewNoteSummary>(`/api/assets/${assetId}/review-notes`, { method: 'POST', body: JSON.stringify(body) }),
  moveAssetReviewNote: (noteId: string, x: number, y: number) => request<AssetReviewNoteSummary>(`/api/asset-review-notes/${noteId}/position`, { method: 'PUT', body: JSON.stringify({ x, y }) }),
  resolveAssetReviewNote: (noteId: string) => request<AssetReviewNoteSummary>(`/api/asset-review-notes/${noteId}/resolve`, { method: 'POST' }),
  reorderShots: (shotIds: string[]) => request<ShotSummary[]>('/api/shots/order', { method: 'PUT', body: JSON.stringify({ shotIds }) }),
  comment: (shotId: string, body: { x: number; y: number; body: string; referenceId?: string }) => request<CommentSummary>(`/api/shots/${shotId}/comments`, { method: 'POST', body: JSON.stringify(body) }),
  moveComment: (commentId: string, x: number, y: number) => request<CommentSummary>(`/api/comments/${commentId}/position`, { method: 'PUT', body: JSON.stringify({ x, y }) }),
  redirectShot: (shotId: string, direction: string, adapterId?: string, guidanceAssetId?: string, markupRevision?: number) => request<JobSummary>(`/api/shots/${shotId}/redirect`, { method: 'POST', body: JSON.stringify({ direction, adapterId, guidanceAssetId, markupRevision }) }),
  deleteCandidate: (shotId: string, candidateId: string) => request<void>(`/api/shots/${shotId}/candidates/${candidateId}`, { method: 'DELETE' }),
  copyCandidateForward: (shotId: string, candidateId: string, expectedCurrentVersion: number) => request<ShotSummary>(`/api/shots/${shotId}/candidates/${candidateId}/copy-forward`, { method: 'POST', body: JSON.stringify({ expectedCurrentVersion }) }),
  resolveComment: (commentId: string) => request(`/api/comments/${commentId}/resolve`, { method: 'POST' }),
  ratify: (shotId: string, version: number) => request(`/api/shots/${shotId}/ratify`, { method: 'POST', body: JSON.stringify({ expectedVersion: version, reason: 'Approved in Framewright review' }) }),
  codex: (mode: string, message: string, shotId?: string) => request<CodexAssistResponse>('/api/codex/assist', { method: 'POST', body: JSON.stringify({ mode, message, shotId }) }),
  suggestShotIntent: (description: string) => request<ShotIntentSuggestion>('/api/codex/shot-intent', { method: 'POST', body: JSON.stringify({ description }) }),
  improveGenerationDirection: (shotId: string, direction: string, purpose: string) => request<ImprovedGenerationDirection>(`/api/shots/${shotId}/codex/improve-generation-direction`, { method: 'POST', body: JSON.stringify({ direction, purpose }) }),
  sketch: (shotId: string) => optionalRequest<SketchDocumentSummary>(`/api/shots/${shotId}/sketch`),
  saveSketch: (shotId: string, body: { expectedRevision: number; creativeBrief: string; content: SketchContent; underlayAssetId?: string; compositionAssetId?: string }) => request<SketchDocumentSummary>(`/api/shots/${shotId}/sketch`, { method: 'PUT', body: JSON.stringify(body) }),
  posePresets: () => request<PosePresetSummary[]>('/api/pose-presets'),
  createPosePreset: (name: string, joints: SketchJoint[]) => request<PosePresetSummary>('/api/pose-presets', { method: 'POST', body: JSON.stringify({ name, joints }) }),
  deletePosePreset: (id: string) => request<boolean>(`/api/pose-presets/${id}`, { method: 'DELETE' }),
  frameMarkup: (shotId: string, version: number) => optionalRequest<FrameMarkupSummary>(`/api/shots/${shotId}/versions/${version}/markup`),
  saveFrameMarkup: (shotId: string, version: number, expectedRevision: number, strokes: SketchStroke[]) => request<FrameMarkupSummary>(`/api/shots/${shotId}/versions/${version}/markup`, { method: 'PUT', body: JSON.stringify({ expectedRevision, strokes }) }),
  continuity: (shotId: string) => request<ShotContinuityReport>(`/api/shots/${shotId}/continuity`),
  visualAudit: (shotId: string) => optionalRequest<ShotVisualAuditSummary>(`/api/shots/${shotId}/visual-audit`),
  runVisualAudit: (shotId: string, force = false) => request<ShotVisualAuditSummary>(`/api/shots/${shotId}/visual-audit`, { method: 'POST', body: JSON.stringify({ force }) }),
  reconcileVisualAudit: (shotId: string, auditId: string, decisions: { findingId: string; action: VisualReconciliationAction; moreDetails: string }[], applyContract = false) => request<VisualReconciliationPlan>(`/api/shots/${shotId}/visual-audits/${auditId}/reconcile`, { method: 'POST', body: JSON.stringify({ decisions, applyContract }) }),
  timelineClips: () => request<TimelineClipSummary[]>('/api/timeline/clips'),
  saveTimelineClip: (body: { track: TimelineTrackKind; label: string; startFrame: number; durationFrames: number; trimStartSeconds: number; volume: number; assetId?: string; voiceProfileId?: string; text: string; expectedUpdatedAt?: string }, id?: string) => request<TimelineClipSummary>(id ? `/api/timeline/clips/${id}` : '/api/timeline/clips', { method: id ? 'PUT' : 'POST', body: JSON.stringify(body) }),
  deleteTimelineClip: (id: string) => request<void>(`/api/timeline/clips/${id}`, { method: 'DELETE' }),
  voiceProfiles: () => request<VoiceProfileSummary[]>('/api/voice-profiles'),
  createVoiceProfile: (body: { name: string; kind: VoiceProfileKind; provider: string; providerVoiceId: string; characterName: string; characterReferenceId: string; sampleAssetId: string; consentConfirmed: boolean; consentAttestation: string }) => request<VoiceProfileSummary>('/api/voice-profiles', { method: 'POST', body: JSON.stringify(body) }),
  designVoiceAuditions: (referenceId: string, body: { direction: string; calibrationText: string; count: number }) => request<JobSummary>(`/api/references/${referenceId}/voice-auditions`, { method: 'POST', body: JSON.stringify(body) }),
  voiceAuditionResults: (jobId: string) => request<VoiceAuditionSummary[]>(`/api/jobs/${jobId}/voice-auditions`),
  voiceSynthesisStatus: async () => {
    const status = await request<VoiceSynthesisStatus>('/api/voice/synthesis')
    return status.localCanSynthesize ? { ...status, canSynthesize: true, model: status.localModel ?? status.model, detail: status.localDetail ?? status.detail } : status
  },
  synthesizeVoice: (clipId: string, expectedUpdatedAt: string) => request<JobSummary>(`/api/timeline/clips/${clipId}/synthesize`, { method: 'POST', body: JSON.stringify({ expectedUpdatedAt }) }),
  musicStatus: () => request<MusicGenerationStatus>('/api/music/status'),
  musicCompositions: () => request<MusicCompositionSummary[]>('/api/music/compositions'),
  musicComposition: (id: string) => request<MusicCompositionSummary>(`/api/music/compositions/${id}`),
  createMusicComposition: (body: { title: string; description: string; style: string; lyrics?: string; tempo: number; meter: string; key: string; sections?: MusicSection[]; performancePrompt?: string; abcNotation?: string }) => request<MusicCompositionSummary>('/api/music/compositions', { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: JSON.stringify(body) }),
  reviseMusicComposition: (id: string, body: { expectedRevisionId: string; editSummary: string; composition: MusicCompositionDocument; abcNotation: string }) => request<MusicCompositionSummary>(`/api/music/compositions/${id}/revisions`, { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: JSON.stringify(body) }),
  editMusicComposition: (id: string, body: { expectedRevisionId: string; instruction: string }) => request<MusicCompositionSummary>(`/api/music/compositions/${id}/edit`, { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: JSON.stringify(body) }),
  renderMusicRevision: (revisionId: string) => request<JobSummary>(`/api/music/revisions/${revisionId}/render`, { method: 'POST', headers: { 'X-Storyboard-Studio': '1' } }),
  audioMasteringStatus: () => request<AudioMasteringStatus>('/api/export/audio/status'),
  uploadMedia: async (file: File, kind: 'Audio' | 'Video') => {
    const form = new FormData(); form.append('file', file)
    const response = await fetch(`/api/assets/media?kind=${kind}`, { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: form })
    if (!response.ok) { const text = await response.text(); let problem: { error?: string; title?: string } | undefined; try { problem = JSON.parse(text) } catch { /* keep text */ } throw new Error(problem?.error || problem?.title || text || `${response.status} ${response.statusText}`) }
    return response.json() as Promise<AssetSummary>
  },
  prepareManifest: (shotId: string, body: { expectedShotVersion: number; expectedSketchRevision: number; route: GenerationRoute; purpose: GenerationPurpose; compositionAssetId?: string; creativeBriefOverride?: string; markupRevision?: number; allowSketchCompositionFallback?: boolean; videoEndpointRole?: 'LastFrame'; videoEndpointSourceCandidateId?: string }) => request<GenerationManifestSummary>(`/api/shots/${shotId}/manifests/prepare`, { method: 'POST', body: JSON.stringify(body) }),
  generateDraft: (shotId: string, expectedShotVersion: number, adapterId = 'comfyui-fast-draft', options?: { compositionAssetId?: string; creativeBriefOverride?: string; markupRevision?: number; allowSketchCompositionFallback?: boolean }) => request<JobSummary>(`/api/shots/${shotId}/generate-draft`, { method: 'POST', body: JSON.stringify({ expectedShotVersion, adapterId, ...options }) }),
  prepareVideoManifest: (shotId: string, body: { expectedShotVersion: number; motionBrief: string; firstFrameCandidateId?: string; lastFrameCandidateId?: string; confirmEndpointCompatibility: boolean; quality: 'Low' | 'Medium' | 'High'; takeId: string }) => request<GenerationManifestSummary>(`/api/shots/${shotId}/video-manifests/prepare`, { method: 'POST', body: JSON.stringify(body) }),
  promoteVideoTake: (shotId: string, jobId: string, expectedShotVersion: number, adapterId = 'comfyui-h3-video') => request<JobSummary>(`/api/shots/${shotId}/video-jobs/${jobId}/promote`, { method: 'POST', body: JSON.stringify({ expectedShotVersion, adapterId }) }),
  manifests: (shotId: string) => request<GenerationManifestSummary[]>(`/api/shots/${shotId}/manifests`),
  uploadImage: async (file: Blob, fileName: string) => {
    // Materialize browser-generated canvas blobs before FormData. WebKit can
    // otherwise serialize a valid lazy Blob as an empty multipart file after
    // prior pointer/canvas activity on the same origin.
    const bytes = await file.arrayBuffer()
    if (bytes.byteLength === 0) throw new Error('The browser produced an empty image. Try exporting the guide again.')
    const body = new FormData()
    body.append('file', new Blob([bytes], { type: file.type || 'image/png' }), fileName)
    const response = await fetch('/api/assets/images', { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body })
    if (!response.ok) {
      const text = await response.text()
      let problem: { error?: string; title?: string } | undefined
      try { problem = JSON.parse(text) as { error?: string; title?: string } } catch { /* Preserve plain provider error text. */ }
      throw new Error(problem?.error || problem?.title || text || `${response.status} ${response.statusText}`)
    }
    return response.json() as Promise<AssetSummary>
  },
  assets: (includeArchived = false) => request<AssetSummary[]>(`/api/assets${includeArchived ? '?includeArchived=true' : ''}`),
  generateAssetImage: (body: { name: string; creativeBrief: string; route: GenerationRoute; adapterId: string; compositionAssetId?: string; referenceAssetIds: string[]; revisionFamilyId?: string; parentAssetId?: string; useCurrentFrame?: boolean; authorityTarget?: { referenceId: string; expectedVersion: number; description: string; lockedConstraint: string } }) => request<JobSummary>('/api/assets/generate-image', { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: JSON.stringify(body) }),
  assetRevisions: (assetId: string) => request<AssetSummary[]>(`/api/assets/${assetId}/revisions`),
  addAssetRevision: (parentAssetId: string, assetId: string, prompt = 'Imported revision', engine = 'Imported') => request<AssetSummary>(`/api/assets/${parentAssetId}/revisions`, { method: 'POST', body: JSON.stringify({ assetId, prompt, engine }) }),
  makeAssetRevisionCurrent: (assetId: string) => request<AssetSummary>(`/api/assets/${assetId}/make-current`, { method: 'POST' }),
  updateAsset: (assetId: string, body: { displayName: string; collectionId?: string; tags: string[]; notes: string }) => request<AssetSummary>(`/api/assets/${assetId}`, { method: 'PUT', body: JSON.stringify(body) }),
  archiveAsset: (assetId: string) => request<AssetSummary>(`/api/assets/${assetId}/archive`, { method: 'POST' }),
  restoreAsset: (assetId: string) => request<AssetSummary>(`/api/assets/${assetId}/restore`, { method: 'POST' }),
  assetCollections: () => request<AssetCollectionSummary[]>('/api/asset-collections'),
  createAssetCollection: (body: { name: string; color: string }) => request<AssetCollectionSummary>('/api/asset-collections', { method: 'POST', body: JSON.stringify(body) }),
  updateAssetCollection: (id: string, body: { name: string; color: string }) => request<AssetCollectionSummary>(`/api/asset-collections/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteAssetCollection: (id: string) => request<void>(`/api/asset-collections/${id}`, { method: 'DELETE' }),
  assetPlacements: (filters?: { assetId?: string; shotId?: string }) => { const params = new URLSearchParams(); if (filters?.assetId) params.set('assetId', filters.assetId); if (filters?.shotId) params.set('shotId', filters.shotId); return request<AssetPlacementSummary[]>(`/api/asset-placements${params.size ? `?${params}` : ''}`) },
  placeAsset: (assetId: string, shotId: string, role: string) => request<AssetPlacementSummary>(`/api/assets/${assetId}/placements`, { method: 'POST', body: JSON.stringify({ shotId, role }) }),
  removeAssetPlacement: (id: string) => request<void>(`/api/asset-placements/${id}`, { method: 'DELETE' }),
  library: () => request<LibraryAuthoritySummary[]>('/api/library'),
  libraryVersions: (libraryId: string) => request<LibraryAuthorityVersionSummary[]>(`/api/library/${libraryId}/versions`),
  importLibraryAuthority: (libraryId: string) => request<ReferenceSummary>(`/api/library/${libraryId}/import`, { method: 'POST' }),
  deleteLibraryAuthority: (libraryId: string) => request<void>(`/api/library/${libraryId}`, { method: 'DELETE' }),
  promoteToLibrary: (referenceId: string) => request<LibraryAuthoritySummary>(`/api/references/${referenceId}/promote-to-library`, { method: 'POST' }),
  pullLibraryUpdate: (referenceId: string) => request<ReferenceSummary>(`/api/references/${referenceId}/pull-library-update`, { method: 'POST' }),
  createReference: (body: { name: string; category: string; description: string; lockedConstraint: string; accent: string; imageAssetId?: string }) => request<ReferenceSummary>('/api/references', { method: 'POST', body: JSON.stringify(body) }),
  referenceVersions: (referenceId: string) => request<ReferenceVersionSummary[]>(`/api/references/${referenceId}/versions`),
  createReferenceVersion: (referenceId: string, body: { expectedVersion: number; description: string; lockedConstraint: string; imageAssetId?: string }) => request<ReferenceSummary>(`/api/references/${referenceId}/versions`, { method: 'POST', body: JSON.stringify(body) }),
  updateReference: (referenceId: string, body: { name: string; category: string; accent: string }) => request<ReferenceSummary>(`/api/references/${referenceId}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteReference: (referenceId: string) => request<void>(`/api/references/${referenceId}`, { method: 'DELETE' }),
  promoteReferenceVersion: (referenceId: string, version: number) => request<ReferenceSummary>(`/api/references/${referenceId}/versions/${version}/promote`, { method: 'POST' }),
  deleteReferenceVersion: (referenceId: string, version: number) => request<ReferenceSummary>(`/api/references/${referenceId}/versions/${version}`, { method: 'DELETE' }),
  createShot: (body: { code: string; title: string; description: string; durationFrames: number; camera: string; action: string; referenceIds: string[]; constraints: string[]; initialImageAssetId?: string }) => request<ShotSummary>('/api/shots', { method: 'POST', body: JSON.stringify(body) }),
  deleteShot: (shotId: string, acceptRatifiedLoss = false) => request<{ code: string; candidates: number; ratifiedVersions: number; comments: number; jobs: number }>(`/api/shots/${shotId}?acceptRatifiedLoss=${acceptRatifiedLoss}`, { method: 'DELETE' }),
  duplicateShot: (shotId: string) => request<ShotSummary>(`/api/shots/${shotId}/duplicate`, { method: 'POST' }),
  updateShot: (shotId: string, body: { expectedUpdatedAt: string; title: string; description: string; durationFrames: number; camera: string; action: string; referenceIds: string[]; constraints: string[] }) => request<ShotSummary>(`/api/shots/${shotId}`, { method: 'PUT', body: JSON.stringify(body) }),
  generationAdapters: () => request<GenerationAdapterSummary[]>('/api/generation/adapters'),
  retryJob: (jobId: string) => request<JobSummary>(`/api/jobs/${jobId}/retry`, { method: 'POST' }),
  backupStatus: () => request<BackupStatus>('/api/maintenance/status'),
  runtimeStatus: () => request<RuntimeReadinessSummary>('/api/runtime/status'),
  productionExportStatus: () => request<ProductionExportReadiness>('/api/export/production/status'),
  draftWorkflow: (shotId: string, useSketch?: boolean, useCurrentFrame?: boolean) => {
    const query = new URLSearchParams()
    if (useSketch !== undefined) query.set('useSketch', String(useSketch))
    if (useCurrentFrame !== undefined) query.set('useCurrentFrame', String(useCurrentFrame))
    const queryString = query.toString()
    return request<DraftWorkflowSummary>(`/api/shots/${shotId}/draft-workflow${queryString ? `?${queryString}` : ''}`)
  },
  dispatchManifest: (manifestId: string, expectedManifestHash: string, adapterId: string) => request<JobSummary>(`/api/manifests/${manifestId}/dispatch`, { method: 'POST', body: JSON.stringify({ expectedManifestHash, adapterId }) }),
  generationPreflight: (manifestId: string, adapterId: string) => request<GenerationPreflightSummary>(`/api/manifests/${manifestId}/preflight?adapterId=${encodeURIComponent(adapterId)}`),
  candidates: (shotId: string) => request<CandidateVersionSummary[]>(`/api/shots/${shotId}/candidates`),
  updateVideoEndpoints: (shotId: string, body: { expectedUpdatedAt: string; firstFrameCandidateId?: string; lastFrameCandidateId?: string }) => request<ShotSummary>(`/api/shots/${shotId}/video-endpoints`, { method: 'PUT', body: JSON.stringify(body) }),
}
