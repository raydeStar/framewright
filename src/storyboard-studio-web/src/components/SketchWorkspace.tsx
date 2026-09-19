import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ChangeEvent, type DragEvent as ReactDragEvent, type PointerEvent as ReactPointerEvent } from 'react'
import { BadgeCheck, Check, CircleAlert, Cloud, Cpu, Eye, EyeOff, FileCheck2, Gauge, Grid3X3, ImagePlus, LockKeyhole, MousePointer2, PenLine, Plus, Redo2, Sparkles, Trash2, Type, Undo2, Upload, UserRound, WandSparkles } from 'lucide-react'
import { studioApi } from '../api'
import Artwork from './Artwork'
import { BlockingInspector, BlockingLayer, BlockingPalette } from './BlockingKit'
import { createBlockingObject, drawBlockingObjects } from './blockingKitModel'
import type { AssetGenerationDraft, DraftWorkflowSummary, GenerationAdapterSummary, GenerationPurpose, GenerationRoute, PosePresetSummary, ReferenceSummary, ShotSummary, SketchContent, SketchObject, SketchObjectKind, SketchPoint, SketchStroke } from '../types'

type SketchTool = 'select' | 'pen' | 'text'
type DraftRoute = 'fast' | 'precision'
type Point = SketchPoint
type Stroke = SketchStroke
type SketchDocument = SketchContent
type SaveState = { phase: 'loading' | 'dirty' | 'saving' | 'saved' | 'error'; at?: number; error?: string }
type ReferenceDirection = { role: string; note: string }

const EMPTY_DOCUMENT: SketchDocument = { strokes: [], labels: [], objects: [] }
// The sketch surface is intentionally dark; production ink must read at a
// glance on a tablet in a bright room. These are drawing colors, not the
// paper-text token used elsewhere in the interface.
const COLORS = ['#f4e4c1', '#e67e5f', '#71c3d4']

const storageKey = (shotId: string) => `storyboard-sketch:${shotId}`

/** Browser storage is recovery-only. The server-side revisioned sketch is the
 * authority; this cache lets the first durable save migrate earlier concepts. */
function restoreDocument(shotId: string): SketchDocument {
  const key = storageKey(shotId)
  try {
    // Carry over anything left behind by the old sessionStorage build.
    const legacy = sessionStorage.getItem(key)
    if (legacy && localStorage.getItem(key) === null) localStorage.setItem(key, legacy)
    const raw = localStorage.getItem(key)
    if (!raw) return EMPTY_DOCUMENT
    const parsed = JSON.parse(raw) as Partial<SketchDocument>
    return Array.isArray(parsed.strokes) && Array.isArray(parsed.labels) ? { strokes: parsed.strokes, labels: parsed.labels, objects: Array.isArray(parsed.objects) ? parsed.objects : [] } : EMPTY_DOCUMENT
  } catch { return EMPTY_DOCUMENT }
}

function normalizeDocument(document: SketchContent): SketchDocument {
  return { strokes: document.strokes ?? [], labels: document.labels ?? [], objects: document.objects ?? [] }
}

function buildPrompt(shot: ShotSummary, references: ReferenceSummary[]) {
  const authorityNames = references.map(reference => `${reference.name} v${reference.version}`).join(', ')
  const authorityDirection = authorityNames ? ` Preserve the approved authority versions for ${authorityNames}.` : ''
  return `${shot.description} ${shot.action} Camera: ${shot.camera}. Treat the sketch as composition and blocking, not finished anatomy.${authorityDirection} Keep all locked shot constraints intact.`
}

export default function SketchWorkspace({ shot, references, purpose = 'Draft', launch, assetDraft, authorityTarget, onLaunchConsumed, onOpenFrame, onJobQueued, onAssetJobQueued }: { shot: ShotSummary; references: ReferenceSummary[]; purpose?: GenerationPurpose; launch?: { id: number; prompt?: string; route?: DraftRoute; underlayAssetId?: string }; assetDraft?: AssetGenerationDraft; authorityTarget?: { referenceId: string; expectedVersion: number; description: string; lockedConstraint: string }; onLaunchConsumed?: () => void; onOpenFrame: () => void; onJobQueued: () => void; onAssetJobQueued?: (job: import('../types').JobSummary) => void }) {
  const subjectId = assetDraft?.id ?? shot.id
  const subjectCode = assetDraft?.destination === 'authority' ? 'AUTHORITY' : assetDraft ? 'ASSET' : shot.code
  const [history, setHistory] = useState<SketchDocument[]>(() => [restoreDocument(subjectId)])
  const [historyIndex, setHistoryIndex] = useState(0)
  const [tool, setTool] = useState<SketchTool>('pen')
  const [blockingKitOpen, setBlockingKitOpen] = useState(false)
  const [selectedObjectId, setSelectedObjectId] = useState<string>()
  const [color, setColor] = useState(COLORS[0])
  const [width, setWidth] = useState(4)
  const [showGrid, setShowGrid] = useState(true)
  const [showUnderlay, setShowUnderlay] = useState(Boolean(assetDraft?.underlayAssetId || assetDraft?.revisionSource))
  const [importedUnderlay, setImportedUnderlay] = useState<string>()
  const [underlayAssetId, setUnderlayAssetId] = useState<string | undefined>(assetDraft?.underlayAssetId)
  const [sourceAspect, setSourceAspect] = useState<number>()
  const [sourceStageSize, setSourceStageSize] = useState<{ width: number; height: number }>()
  const [compositionAssetId, setCompositionAssetId] = useState<string>()
  const [route, setRoute] = useState<DraftRoute>(assetDraft?.route ?? (purpose === 'Final' ? 'precision' : 'fast'))
  const [prompt, setPrompt] = useState(() => assetDraft?.initialPrompt || buildPrompt(shot, references))
  const [assetName, setAssetName] = useState(assetDraft?.name ?? '')
  const [selectedReferenceIds, setSelectedReferenceIds] = useState<string[]>([])
  const [referenceDirections, setReferenceDirections] = useState<Record<string, ReferenceDirection>>({})
  const [precisionAdapterId, setPrecisionAdapterId] = useState('codex-imagegen')
  const [assetGenerating, setAssetGenerating] = useState(false)
  const [assetElapsed, setAssetElapsed] = useState(0)
  const [textDraft, setTextDraft] = useState<{ x: number; y: number; value: string }>()
  const [saveState, setSaveState] = useState<SaveState>({ phase: 'loading' })
  const [revision, setRevision] = useState(0)
  const [hydrated, setHydrated] = useState(false)
  const [preparing, setPreparing] = useState(false)
  const [routeError, setRouteError] = useState<string>()
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [draftWorkflow, setDraftWorkflow] = useState<DraftWorkflowSummary>()
  const [posePresets, setPosePresets] = useState<PosePresetSummary[]>([])
  const [dispatching, setDispatching] = useState(false)
  const persistedSignature = useRef<string | undefined>(undefined)
  const saveInFlight = useRef(false)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const stageShellRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const activeStroke = useRef<Stroke | undefined>(undefined)
  const sketchDoc = history[historyIndex]
  const usesComposition = Boolean(underlayAssetId || compositionAssetId || sketchDoc.strokes.length || sketchDoc.labels.length || sketchDoc.objects.length)
  const selectedObject = sketchDoc.objects.find(item => item.id === selectedObjectId)

  useEffect(() => {
    let cancelled = false
    void studioApi.posePresets().then(items => { if (!cancelled) setPosePresets(items) }).catch(() => { /* The built-in poses remain fully usable offline. */ })
    return () => { cancelled = true }
  }, [])

  const commit = useCallback((next: SketchDocument) => {
    setHistory(current => [...current.slice(0, historyIndex + 1), next])
    setHistoryIndex(index => index + 1)
    // Any visual edit invalidates the last rasterized composition. Prompt-only
    // changes do not, so repeated generations can reuse the durable image.
    setCompositionAssetId(undefined)
  }, [historyIndex])

  useEffect(() => {
    let cancelled = false
    setHydrated(false)
    setSaveState({ phase: 'loading' })
    if (assetDraft) {
      setHistory([restoreDocument(subjectId)])
      setHistoryIndex(0)
      setPrompt(assetDraft.initialPrompt)
      setUnderlayAssetId(assetDraft.underlayAssetId)
      setSourceAspect(undefined)
      setSourceStageSize(undefined)
      setImportedUnderlay(assetDraft.underlayAssetId ? `/api/assets/${assetDraft.underlayAssetId}/content` : undefined)
      setShowUnderlay(Boolean(assetDraft.underlayAssetId || assetDraft.revisionSource))
      setSaveState({ phase: 'saved', at: Date.now() })
      setHydrated(true)
      return () => { cancelled = true }
    }
    void studioApi.sketch(shot.id).then(remote => {
      if (cancelled) return
      if (remote) {
        setHistory([normalizeDocument(remote.content)])
        setHistoryIndex(0)
        setPrompt(remote.creativeBrief)
        setUnderlayAssetId(remote.underlayAssetId)
        setCompositionAssetId(remote.compositionAssetId)
        if (remote.underlayAssetId) setImportedUnderlay(`/api/assets/${remote.underlayAssetId}/content`)
        setRevision(remote.revision)
        persistedSignature.current = JSON.stringify({ creativeBrief: remote.creativeBrief, content: remote.content, underlayAssetId: remote.underlayAssetId, compositionAssetId: remote.compositionAssetId })
        setSaveState({ phase: 'saved', at: Date.parse(remote.updatedAt) })
      } else {
        setSaveState({ phase: 'dirty' })
      }
      setHydrated(true)
    }).catch(error => {
      if (cancelled) return
      setSaveState({ phase: 'error', error: error instanceof Error ? error.message : 'Could not load the sketch.' })
    })
    return () => { cancelled = true }
  }, [assetDraft, shot.id, subjectId])

  useLayoutEffect(() => {
    const shell = stageShellRef.current
    if (!shell || !assetDraft || !sourceAspect) { setSourceStageSize(undefined); return }
    const resize = () => {
      const rect = shell.getBoundingClientRect()
      const maxWidth = Math.min(rect.width, 1200)
      const maxHeight = rect.height
      if (maxWidth <= 0 || maxHeight <= 0) return
      if (maxWidth / maxHeight > sourceAspect) setSourceStageSize({ width: maxHeight * sourceAspect, height: maxHeight })
      else setSourceStageSize({ width: maxWidth, height: maxWidth / sourceAspect })
    }
    resize()
    const observer = new ResizeObserver(resize)
    observer.observe(shell)
    return () => observer.disconnect()
  }, [assetDraft, sourceAspect])

  useEffect(() => {
    if (!hydrated || !launch) return
    if (launch.prompt) setPrompt(launch.prompt)
    if (launch.route) setRoute(launch.route)
    if (launch.underlayAssetId) {
      setUnderlayAssetId(launch.underlayAssetId)
      setImportedUnderlay(`/api/assets/${launch.underlayAssetId}/content`)
      setShowUnderlay(true)
    }
    onLaunchConsumed?.()
  }, [hydrated, launch, onLaunchConsumed])

  useEffect(() => {
    void studioApi.generationAdapters().then(items => {
      setAdapters(items)
      const precision = items.filter(item => item.id !== 'local-proof' && item.routes.includes('PrecisionDraft') && item.purposes.includes(purpose))
      const preferred = precision.find(item => item.id === 'codex-imagegen' && item.canDispatch)
        ?? precision.find(item => item.id === 'openai-gpt-image' && item.canDispatch)
        ?? precision[0]
      if (preferred) setPrecisionAdapterId(preferred.id)
    }).catch(error => setRouteError(error instanceof Error ? error.message : 'Could not inspect generation adapters.'))
  }, [purpose])

  const revisesSelectedImage = Boolean(assetDraft?.revisionSource?.imageAssetId && underlayAssetId)

  useEffect(() => {
    void studioApi.draftWorkflow(shot.id, revisesSelectedImage ? false : usesComposition, revisesSelectedImage).then(setDraftWorkflow).catch(() => setDraftWorkflow(undefined))
  }, [shot.id, usesComposition, revisesSelectedImage])

  useEffect(() => {
    if (!assetGenerating) { setAssetElapsed(0); return }
    const started = Date.now()
    const timer = window.setInterval(() => setAssetElapsed(Math.floor((Date.now() - started) / 1000)), 1000)
    return () => window.clearInterval(timer)
  }, [assetGenerating])

  useEffect(() => {
    try { localStorage.setItem(storageKey(subjectId), JSON.stringify(sketchDoc)) } catch { /* Recovery cache is best effort. */ }
    if (!hydrated) return
    if (assetDraft) {
      setSaveState({ phase: 'saved', at: Date.now() })
      return
    }
    const signature = JSON.stringify({ creativeBrief: prompt, content: sketchDoc, underlayAssetId, compositionAssetId })
    if (signature === persistedSignature.current) return
    setSaveState({ phase: 'dirty' })
    const timer = window.setTimeout(() => {
      if (saveInFlight.current) return
      saveInFlight.current = true
      setSaveState({ phase: 'saving' })
      void studioApi.saveSketch(shot.id, { expectedRevision: revision, creativeBrief: prompt, content: sketchDoc, underlayAssetId, compositionAssetId }).then(saved => {
        persistedSignature.current = signature
        setRevision(saved.revision)
        setSaveState({ phase: 'saved', at: Date.parse(saved.updatedAt) })
      }).catch(error => setSaveState({ phase: 'error', error: error instanceof Error ? error.message : 'Sketch save failed.' }))
        .finally(() => { saveInFlight.current = false })
    }, 550)
    return () => window.clearTimeout(timer)
  }, [assetDraft, compositionAssetId, hydrated, prompt, revision, shot.id, sketchDoc, subjectId, underlayAssetId])

  // Escape for the route packet is handled by <Dialog>'s native `cancel` event.

  useEffect(() => {
    const shortcuts = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement
      if (target.closest('input, textarea')) return
      if (event.key.toLowerCase() === 'p') setTool('pen')
      if (event.key.toLowerCase() === 't') setTool('text')
      if (event.key.toLowerCase() === 'v') setTool('select')
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
        event.preventDefault()
        setCompositionAssetId(undefined)
        if (event.shiftKey) setHistoryIndex(index => Math.min(history.length - 1, index + 1))
        else setHistoryIndex(index => Math.max(0, index - 1))
      }
      if ((event.key === 'Delete' || event.key === 'Backspace') && selectedObjectId) {
        event.preventDefault()
        commit({ ...sketchDoc, objects: sketchDoc.objects.filter(item => item.id !== selectedObjectId) })
        setSelectedObjectId(undefined)
      }
    }
    window.addEventListener('keydown', shortcuts)
    return () => window.removeEventListener('keydown', shortcuts)
  }, [commit, history.length, selectedObjectId, sketchDoc])

  const render = useCallback(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const context = canvas.getContext('2d')
    if (!context) return
    const { width: cssWidth, height: cssHeight } = canvas.getBoundingClientRect()
    context.clearRect(0, 0, cssWidth, cssHeight)
    for (const stroke of sketchDoc.strokes) {
      if (stroke.points.length < 2) continue
      context.strokeStyle = stroke.color
      context.lineCap = 'round'
      context.lineJoin = 'round'
      for (let index = 1; index < stroke.points.length; index += 1) {
        const from = stroke.points[index - 1]
        const to = stroke.points[index]
        context.lineWidth = Math.max(1.25, stroke.width * (.55 + to.pressure * .75))
        context.beginPath()
        context.moveTo(from.x * cssWidth, from.y * cssHeight)
        context.lineTo(to.x * cssWidth, to.y * cssHeight)
        context.stroke()
      }
    }
  }, [sketchDoc])

  useLayoutEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const resize = () => {
      const rect = canvas.getBoundingClientRect()
      const scale = window.devicePixelRatio || 1
      canvas.width = Math.round(rect.width * scale)
      canvas.height = Math.round(rect.height * scale)
      canvas.getContext('2d')?.setTransform(scale, 0, 0, scale, 0, 0)
      render()
    }
    resize()
    const observer = new ResizeObserver(resize)
    observer.observe(canvas)
    return () => observer.disconnect()
  }, [render])

  const pointFromEvent = (event: { clientX: number; clientY: number; pressure?: number }): Point => {
    const rect = canvasRef.current!.getBoundingClientRect()
    return {
      x: Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)),
      y: Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height)),
      pressure: event.pressure && event.pressure > 0 ? event.pressure : .55,
    }
  }

  const pointerDown = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    if (tool !== 'pen') { setSelectedObjectId(undefined); return }
    event.currentTarget.setPointerCapture(event.pointerId)
    activeStroke.current = { id: crypto.randomUUID(), points: [pointFromEvent(event)], color, width }
  }

  const pointerMove = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    if (!activeStroke.current || tool !== 'pen') return
    const native = event.nativeEvent
    const events = typeof native.getCoalescedEvents === 'function' ? native.getCoalescedEvents() : [native]
    activeStroke.current.points.push(...events.map(pointFromEvent))
    const preview = { ...sketchDoc, strokes: [...sketchDoc.strokes, activeStroke.current] }
    const canvas = canvasRef.current
    if (!canvas) return
    const context = canvas.getContext('2d')
    const rect = canvas.getBoundingClientRect()
    if (!context) return
    context.clearRect(0, 0, rect.width, rect.height)
    for (const stroke of preview.strokes) {
      context.strokeStyle = stroke.color
      context.lineCap = 'round'
      context.lineJoin = 'round'
      for (let index = 1; index < stroke.points.length; index += 1) {
        const from = stroke.points[index - 1]
        const to = stroke.points[index]
        context.lineWidth = Math.max(1.25, stroke.width * (.55 + to.pressure * .75))
        context.beginPath()
        context.moveTo(from.x * rect.width, from.y * rect.height)
        context.lineTo(to.x * rect.width, to.y * rect.height)
        context.stroke()
      }
    }
  }

  const pointerUp = () => {
    const stroke = activeStroke.current
    if (!stroke) return
    activeStroke.current = undefined
    if (stroke.points.length === 1) stroke.points.push({ ...stroke.points[0], x: Math.min(1, stroke.points[0].x + .001) })
    commit({ ...sketchDoc, strokes: [...sketchDoc.strokes, stroke] })
  }

  const saveText = () => {
    if (!textDraft?.value.trim()) { setTextDraft(undefined); return }
    commit({ ...sketchDoc, labels: [...sketchDoc.labels, { id: crypto.randomUUID(), x: textDraft.x, y: textDraft.y, text: textDraft.value.trim() }] })
    setTextDraft(undefined)
  }

  const importImage = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (!file) return
    setSaveState({ phase: 'saving' })
    void studioApi.uploadImage(file, file.name).then(asset => {
      setImportedUnderlay(asset.contentUrl)
      setUnderlayAssetId(asset.id)
      setSourceAspect(undefined)
      setSourceStageSize(undefined)
      setCompositionAssetId(undefined)
      setShowUnderlay(true)
      setSaveState({ phase: 'dirty' })
    }).catch(error => setSaveState({ phase: 'error', error: error instanceof Error ? error.message : 'Underlay import failed.' }))
    event.target.value = ''
  }

  const exportComposition = () => new Promise<Blob>((resolve, reject) => {
    const source = canvasRef.current
    if (!source) { reject(new Error('The sketch canvas is unavailable.')); return }
    const width = source.width
    const height = source.height
    const output = document.createElement('canvas')
    output.width = width
    output.height = height
    const context = output.getContext('2d')
    if (!context) { reject(new Error('The composition could not be exported.')); return }
    context.fillStyle = '#ded7c7'
    context.fillRect(0, 0, width, height)
    const finish = () => {
      context.drawImage(source, 0, 0)
      drawBlockingObjects(context, sketchDoc.objects, width, height)
      const scale = window.devicePixelRatio || 1
      context.font = `${Math.round(15 * scale)}px system-ui, sans-serif`
      context.textBaseline = 'top'
      for (const label of sketchDoc.labels) {
        const x = label.x * width
        const y = label.y * height
        const metrics = context.measureText(label.text)
        context.fillStyle = '#f1ecdf'
        context.fillRect(x - 5 * scale, y - 3 * scale, metrics.width + 10 * scale, 22 * scale)
        context.fillStyle = '#4a433b'
        context.fillText(label.text, x, y)
      }
      output.toBlob(blob => blob ? resolve(blob) : reject(new Error('The composition could not be encoded.')), 'image/png')
    }
    if (showUnderlay && importedUnderlay) {
      const image = new Image()
      image.crossOrigin = 'anonymous'
      image.onload = () => {
        // Asset and authority revision work uses the selected image as source
        // material. Shot sketch underlays stay faint because they are tracing
        // guides rather than pixels approved as the generation base.
        context.globalAlpha = assetDraft ? 1 : .3
        const scale = Math.min(width / image.naturalWidth, height / image.naturalHeight)
        const drawWidth = image.naturalWidth * scale
        const drawHeight = image.naturalHeight * scale
        context.drawImage(image, (width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight)
        context.globalAlpha = 1
        finish()
      }
      image.onerror = () => reject(new Error('The imported underlay could not be rendered.'))
      image.src = importedUnderlay
      return
    }
    finish()
  })

  const prepareRoute = async () => {
    setPreparing(true)
    setRouteError(undefined)
    try {
      let nextCompositionAssetId = compositionAssetId
      const hasMarks = Boolean(sketchDoc.strokes.length || sketchDoc.labels.length || sketchDoc.objects.length)
      const hasComposition = Boolean(underlayAssetId || hasMarks)
      // With no drawn change, pass the selected image itself to the configured
      // ComfyUI image-edit workflow instead of inventing a blank composition.
      // or GPT Image Edit. Re-rasterizing it as a faint sketch underlay made the
      // provider lose detail and the user's exact selected-version intent.
      if (assetDraft && underlayAssetId && !hasMarks) nextCompositionAssetId = underlayAssetId
      if (!nextCompositionAssetId && (!assetDraft || hasComposition)) {
        const blob = await exportComposition()
        const asset = await studioApi.uploadImage(blob, `${subjectCode.toLowerCase()}-sketch-r${revision}.png`)
        nextCompositionAssetId = asset.id
      }
      if (assetDraft) {
        const adapterId = route === 'fast' ? 'comfyui-fast-draft' : precisionAdapterId
        const adapter = adapters.find(item => item.id === adapterId)
        if (!adapter?.canDispatch) throw new Error(adapter?.detail ?? `${route === 'fast' ? 'ComfyUI' : 'The precision image engine'} is not ready.`)
        const assignments = selectedReferenceIds.map(id => {
          const reference = assetDraft.references.find(item => item.assetId === id)
          const direction = referenceDirections[id] ?? { role: 'General visual reference', note: '' }
          return `- ${reference?.label ?? id}: ${direction.role}${direction.note.trim() ? `. ${direction.note.trim()}` : ''}`
        })
        const generationBrief = assignments.length
          ? `${prompt.trim()}\n\nREFERENCE ASSIGNMENTS:\n${assignments.join('\n')}\nUse only the assigned traits from each reference; do not blend their subjects, crops, poses, or backgrounds.`
          : prompt.trim()
        setCompositionAssetId(nextCompositionAssetId)
        setAssetGenerating(true)
        const job = await studioApi.generateAssetImage({
          name: assetName.trim(),
          creativeBrief: generationBrief,
          route: route === 'fast' ? 'FastDraft' : 'PrecisionDraft',
          adapterId,
          compositionAssetId: nextCompositionAssetId,
          referenceAssetIds: selectedReferenceIds,
          revisionFamilyId: assetDraft.revisionFamilyId,
          parentAssetId: assetDraft.parentAssetId,
          useCurrentFrame: revisesSelectedImage,
          authorityTarget,
        })
        onAssetJobQueued?.(job)
        return
      }
      const saved = await studioApi.saveSketch(shot.id, {
        expectedRevision: revision,
        creativeBrief: prompt,
        content: sketchDoc,
        underlayAssetId,
        compositionAssetId: nextCompositionAssetId,
      })
      setCompositionAssetId(nextCompositionAssetId)
      setRevision(saved.revision)
      persistedSignature.current = JSON.stringify({ creativeBrief: saved.creativeBrief, content: saved.content, underlayAssetId: saved.underlayAssetId, compositionAssetId: saved.compositionAssetId })
      setSaveState({ phase: 'saved', at: Date.parse(saved.updatedAt) })
      const prepared = await studioApi.prepareManifest(shot.id, {
        expectedShotVersion: shot.version,
        expectedSketchRevision: saved.revision,
        route: (route === 'fast' ? 'FastDraft' : 'PrecisionDraft') as GenerationRoute,
        purpose,
      })
      const adapterId = route === 'fast' ? 'comfyui-fast-draft' : precisionAdapterId
      const adapter = adapters.find(item => item.id === adapterId)
      if (!adapter?.canDispatch) throw new Error(adapter?.detail ?? `${route === 'fast' ? 'ComfyUI' : 'The precision image engine'} is not ready.`)
      setDispatching(true)
      await studioApi.dispatchManifest(prepared.id, prepared.manifestHash, adapterId)
      onJobQueued()
      onOpenFrame()
    } catch (error) {
      setRouteError(error instanceof Error ? error.message : 'Could not prepare the route manifest.')
    } finally {
      setPreparing(false)
      setDispatching(false)
      setAssetGenerating(false)
    }
  }

  const saved = saveState.phase === 'saved'
  const referenceLimit = route === 'fast' ? 3 : 8
  const precisionAdapters = adapters.filter(item => item.id !== 'local-proof' && item.routes.includes('PrecisionDraft') && item.purposes.includes(purpose))
  const selectedPrecisionAdapter = precisionAdapters.find(item => item.id === precisionAdapterId)
  const addBlockingObject = (kind: SketchObjectKind, placement?: { x: number; y: number }) => {
    const kindCount = sketchDoc.objects.filter(item => item.kind === kind).length
    const next = createBlockingObject(kind, kindCount + 1)
    const humanSlots = [.34, .66, .5, .18, .82]
    const offset = Math.min(.18, sketchDoc.objects.length * .035)
    next.x = placement?.x ?? (kind === 'Human' ? humanSlots[kindCount % humanSlots.length] : Math.min(.8, .42 + offset))
    next.y = placement?.y ?? (kind === 'Human' ? .52 : .58)
    commit({ ...sketchDoc, objects: [...sketchDoc.objects, next] })
    setSelectedObjectId(next.id)
    setTool('select')
    setBlockingKitOpen(false)
  }
  const dropBlockingObject = (event: ReactDragEvent<HTMLDivElement>) => {
    const kind = event.dataTransfer.getData('application/x-framewright-blocker') as SketchObjectKind
    if (!['Human', 'Chair', 'Table', 'Doorway', 'Arrow', 'Block'].includes(kind)) return
    event.preventDefault()
    const rect = event.currentTarget.getBoundingClientRect()
    addBlockingObject(kind, {
      x: Math.min(.96, Math.max(.04, (event.clientX - rect.left) / rect.width)),
      y: Math.min(.96, Math.max(.04, (event.clientY - rect.top) / rect.height)),
    })
  }
  const updateBlockingObject = (next: SketchObject) => commit({ ...sketchDoc, objects: sketchDoc.objects.map(item => item.id === next.id ? next : item) })
  const duplicateBlockingObject = (item: SketchObject) => {
    const copy = structuredClone(item)
    copy.id = crypto.randomUUID(); copy.label = `${item.label || item.kind} copy`; copy.x = item.x < .5 ? Math.min(.86, item.x + .32) : Math.max(.14, item.x - .32); copy.y = item.y
    commit({ ...sketchDoc, objects: [...sketchDoc.objects, copy] }); setSelectedObjectId(copy.id); setTool('select')
  }
  const deleteBlockingObject = (id: string) => { commit({ ...sketchDoc, objects: sketchDoc.objects.filter(item => item.id !== id) }); setSelectedObjectId(undefined) }
  const fastWorkflowLabel = assetDraft
    ? revisesSelectedImage
      ? draftWorkflow ? `${draftWorkflow.name} · ${draftWorkflow.mode}` : 'ComfyUI · loading current-frame edit'
      : usesComposition
      ? 'ComfyUI workflow · Sketch guided'
      : selectedReferenceIds.length > 0 || Boolean(assetDraft.revisionSource?.imageAssetId)
        ? 'ComfyUI workflow · Reference compose'
        : 'ComfyUI workflow · Text guided'
    : draftWorkflow
      ? `${draftWorkflow.name} · ${draftWorkflow.mode}`
      : 'ComfyUI · loading workflow'

  return <main className="workspace shot-workspace sketch-workspace" data-testid="sketch-workspace">
    <aside className="version-rail">
      <div className="rail-label">{assetDraft?.destination === 'authority' ? 'Authority revision' : assetDraft ? 'Asset workflow' : 'Shot stages'}</div>
      <button className="version-slot active available" aria-current="step"><span className="version-marker"><PenLine size={13} /></span><span><strong>{assetDraft?.revisionSource ? `Selected v${assetDraft.revisionSource.version}` : 'Sketch'}</strong><small>{assetDraft?.revisionSource ? 'Revision source is active' : revision > 0 ? `Studio revision ${revision}` : 'Unsaved concept'}</small></span></button>
      {assetDraft ? <button className="version-slot available" onClick={onOpenFrame}><span className="version-marker"><ImagePlus size={13} /></span><span><strong>Asset library</strong><small>Return without generating</small></span></button> : <button className="version-slot available" onClick={onOpenFrame}><span className="version-marker">v{shot.version}</span><span><strong>Current frame</strong><small>{shot.stage} · open v{shot.version}</small></span></button>}
      <div className="sketch-rail-note"><Sparkles size={15} /><p><strong>{assetDraft?.revisionSource ? 'Revise the selection' : 'Sketch is optional'}</strong>{assetDraft?.revisionSource ? 'The selected revision is the base. Draw only when you want to change its composition or placement.' : 'Use loose shapes only when you want tighter control over blocking and framing.'}</p></div>
    </aside>

    <section className="shot-center sketch-center">
      <div className="canvas-toolbar sketch-toolbar">
        <div className="segmented tools" role="group" aria-label="Sketch tools">
          <button aria-label="Select tool" aria-pressed={tool === 'select'} className={tool === 'select' ? 'active' : ''} onClick={() => setTool('select')}><MousePointer2 size={17} /></button>
          <button aria-label="Pen tool" aria-pressed={tool === 'pen'} className={tool === 'pen' ? 'active' : ''} onClick={() => setTool('pen')}><PenLine size={17} /></button>
          <button aria-label="Text tool" aria-pressed={tool === 'text'} className={tool === 'text' ? 'active' : ''} onClick={() => setTool('text')}><Type size={17} /></button>
          <button aria-label="Open blocking kit" aria-expanded={blockingKitOpen} className={blockingKitOpen ? 'active' : ''} onClick={() => setBlockingKitOpen(value => !value)}><Plus size={17} /></button>
        </div>
        <div className="canvas-title"><span>{subjectCode}</span><strong>{assetDraft ? assetName || 'New visual asset' : 'Composition sketch'}</strong><em>{assetDraft?.destination === 'authority' ? 'Selected authority' : assetDraft ? 'Library concept' : saved ? `Saved · r${revision}` : 'Working'}</em></div>
        <div className="toolbar-actions sketch-toolbar-actions">
          <span className={`save-state ${saved ? 'ok' : saveState.phase === 'error' ? 'failed' : ''}`} role="status" title={saveState.error}>
            {saved ? <><Check size={13} />{assetDraft ? 'Draft saved locally' : 'Saved to studio'} {saveState.at ? new Date(saveState.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''}</> : saveState.phase === 'error' ? <><CircleAlert size={13} />Save needs attention</> : <><Upload size={13} />{saveState.phase === 'loading' ? 'Loading sketch' : saveState.phase === 'saving' ? assetDraft ? 'Saving draft' : 'Saving to studio' : 'Unsaved changes'}</>}
          </span>
          <button aria-label="Undo sketch action" title="Undo" disabled={historyIndex === 0} onClick={() => { setCompositionAssetId(undefined); setHistoryIndex(index => index - 1) }}><Undo2 size={17} /></button>
          <button aria-label="Redo sketch action" title="Redo" disabled={historyIndex >= history.length - 1} onClick={() => { setCompositionAssetId(undefined); setHistoryIndex(index => index + 1) }}><Redo2 size={17} /></button>
          <button aria-label="Clear sketch" title="Clear" disabled={!sketchDoc.strokes.length && !sketchDoc.labels.length && !sketchDoc.objects.length} onClick={() => { commit(EMPTY_DOCUMENT); setSelectedObjectId(undefined) }}><Trash2 size={17} /></button>
        </div>
      </div>

      {blockingKitOpen && <BlockingPalette onAdd={addBlockingObject} />}

      <div className="sketch-stage-shell" ref={stageShellRef}>
        <div className={`sketch-stage ${assetDraft && sourceAspect ? 'source-aspect' : ''}`} style={sourceStageSize} data-testid="sketch-canvas" onDragOver={event => { if (event.dataTransfer.types.includes('application/x-framewright-blocker')) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' } }} onDrop={dropBlockingObject}>
          {showUnderlay && (importedUnderlay ? <img className={assetDraft ? 'revision-source-image' : ''} src={importedUnderlay} onLoad={event => { if (assetDraft && event.currentTarget.naturalWidth > 0 && event.currentTarget.naturalHeight > 0) setSourceAspect(event.currentTarget.naturalWidth / event.currentTarget.naturalHeight) }} alt={assetDraft?.revisionSource ? `${assetDraft.revisionSource.label} version ${assetDraft.revisionSource.version} selected as revision source` : 'Imported sketch underlay'} /> : assetDraft?.revisionSource ? <Artwork className="revision-source-image" variant={assetDraft.revisionSource.visualVariant} label={`${assetDraft.revisionSource.label} version ${assetDraft.revisionSource.version} text authority preview`} /> : <Artwork variant={shot.visualVariant} muted label="Current frame underlay" />)}
          <div className="sketch-paper" />
          {showGrid && <div className="sketch-grid" aria-hidden="true"><i /><i /><i /><i /></div>}
          <div className="sketch-safe-frame" aria-hidden="true" />
          <canvas ref={canvasRef} aria-label={tool === 'pen' ? 'Sketch canvas. Draw with mouse, touch, or pen.' : 'Sketch canvas. Click to place text.'} onClick={event => { if (tool !== 'text') return; const point = pointFromEvent(event); setTextDraft({ x: point.x, y: point.y, value: '' }) }} onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={pointerUp} onPointerCancel={pointerUp} />
          <BlockingLayer objects={sketchDoc.objects} selectedId={selectedObjectId} interactive={tool === 'select'} onSelect={setSelectedObjectId} onCommit={objects => commit({ ...sketchDoc, objects })} />
          {sketchDoc.labels.map(label => <span key={label.id} className="sketch-label" style={{ left: `${label.x * 100}%`, top: `${label.y * 100}%` }}>{label.text}</span>)}
          {textDraft && <form className="sketch-text-entry" style={{ left: `${textDraft.x * 100}%`, top: `${textDraft.y * 100}%` }} onSubmit={event => { event.preventDefault(); saveText() }}><input autoFocus aria-label="Sketch text" placeholder="Type a note" value={textDraft.value} onChange={event => setTextDraft({ ...textDraft, value: event.target.value })} onBlur={saveText} /></form>}
          {!sketchDoc.strokes.length && !sketchDoc.labels.length && !sketchDoc.objects.length && !assetDraft?.revisionSource && <div className="sketch-empty"><PenLine /><strong>{assetDraft ? 'Sketch optional' : 'Block the shot'}</strong><span>{assetDraft ? 'Leave this blank to generate from your brief and selected references. Draw only when you want to control placement or framing.' : <>Draw freely or open the <b>+</b> Blocking Kit for poseable people, props, and staging.</>}</span></div>}
          {!sketchDoc.strokes.length && !sketchDoc.labels.length && !sketchDoc.objects.length && assetDraft?.revisionSource && <div className="revision-source-caption"><BadgeCheck size={15} /><span><strong>{assetDraft.revisionSource.label} v{assetDraft.revisionSource.version} is the base</strong><small>{assetDraft.revisionSource.imageAssetId ? 'Generate now to revise this image, or draw over it to direct composition changes.' : 'This revision has text authority but no approved source image. Generate from its identity and constraints, or sketch optional composition.'}</small></span></div>}
          <div className="sketch-stage-meta"><span>{subjectCode} / SKETCH</span><span>{assetDraft?.destination === 'authority' ? 'New authority revision' : assetDraft ? 'Reusable library image' : shot.camera}</span></div>
        </div>
      </div>

      <div className="sketch-controls">
        {sketchDoc.objects.length > 0 && <div className="control-group blocking-count"><UserRound size={14} /><strong>{sketchDoc.objects.filter(item => item.kind === 'Human').length}</strong><span>people</span><strong>{sketchDoc.objects.filter(item => item.kind !== 'Human').length}</strong><span>objects</span></div>}
        <div className="control-group"><span>Ink</span>{COLORS.map(swatch => <button key={swatch} aria-label={`Use ${swatch} ink`} aria-pressed={color === swatch} className="ink-swatch" style={{ background: swatch }} onClick={() => setColor(swatch)} />)}</div>
        <div className="control-group line-width"><span>Stroke</span>{[2.5, 4, 7].map(size => <button key={size} aria-label={`${size} pixel stroke`} aria-pressed={width === size} className={width === size ? 'active' : ''} onClick={() => setWidth(size)}><i style={{ height: size }} /></button>)}</div>
        <div className="control-spacer" />
        <input ref={fileRef} className="sr-only" type="file" accept="image/*" aria-label="Choose sketch underlay image" onChange={importImage} />
        <button onClick={() => fileRef.current?.click()}><ImagePlus size={16} />Import underlay</button>
        <button aria-pressed={showUnderlay} onClick={() => setShowUnderlay(value => !value)}>{showUnderlay ? <Eye size={16} /> : <EyeOff size={16} />}Underlay</button>
        <button aria-pressed={showGrid} onClick={() => setShowGrid(value => !value)}><Grid3X3 size={16} />Grid</button>
      </div>
    </section>

    <aside className="shot-inspector sketch-inspector">
      <header><p className="eyebrow">Generate image</p><h2>{assetDraft?.destination === 'authority' ? 'Build an authority revision' : assetDraft ? 'Build a reusable visual asset' : 'Turn blocking into a frame'}</h2><span>{assetDraft?.destination === 'authority' ? 'Same canvas and image engines · protected revision history' : assetDraft ? 'Same canvas and image engines · saved to Assets' : 'Shot context is included automatically'}</span></header>
      <div className="inspector-scroll">
        {selectedObject && <BlockingInspector item={selectedObject} references={references} posePresets={posePresets} onChange={updateBlockingObject} onDuplicate={() => duplicateBlockingObject(selectedObject)} onDelete={() => deleteBlockingObject(selectedObject.id)} onSavePose={async (name, joints) => { const savedPose = await studioApi.createPosePreset(name, joints); setPosePresets(current => [...current, savedPose].sort((a, b) => a.name.localeCompare(b.name))) }} onDeletePose={async id => { await studioApi.deletePosePreset(id); setPosePresets(current => current.filter(item => item.id !== id)) }} />}
        {assetDraft && <label className="prompt-brief"><span>{assetDraft.destination === 'authority' ? 'Revision name' : 'Asset name'}</span><input aria-label={assetDraft.destination === 'authority' ? 'Revision name' : 'Asset name'} value={assetName} maxLength={120} onChange={event => setAssetName(event.target.value)} /></label>}
        <label className="prompt-brief"><span>Creative brief</span><textarea aria-label="Creative brief" value={prompt} onChange={event => setPrompt(event.target.value)} /></label>
        {assetDraft?.revisionSource && <section className="revision-source-card" aria-label="Selected revision source"><div>{assetDraft.revisionSource.imageUrl ? <img src={assetDraft.revisionSource.imageUrl} alt="" /> : <Artwork variant={assetDraft.revisionSource.visualVariant} muted />}</div><span><small>Selected authority source</small><strong>{assetDraft.revisionSource.label} · v{assetDraft.revisionSource.version}</strong><p>{assetDraft.revisionSource.imageAssetId ? 'Its approved image is sent directly to the selected image-edit provider.' : 'No approved image is attached; its exact identity and locked constraint drive text generation.'}</p></span><BadgeCheck size={17} /></section>}
        {!assetDraft && <button className="prompt-helper" onClick={() => setPrompt(buildPrompt(shot, references))}><Sparkles size={16} /><span><strong>Build from shot intent</strong><small>Camera, action, authorities, constraints</small></span></button>}
        {assetDraft && <section className="asset-reference-picker"><div className="route-heading"><span>Visual references</span><small>{selectedReferenceIds.length}/{referenceLimit} selected</small></div><p>{assetDraft.revisionSource?.imageAssetId ? 'The selected revision is already the visual base. Add only references that control a specific detail.' : assetDraft.revisionSource ? 'This authority has text context but no source image. References can establish its first visual identity.' : 'Choose exact people, places, wardrobe, props, or styles. Then tell Framewright what each image controls.'} {route === 'fast' ? selectedReferenceIds.length > 0 ? `Fast Draft sends ${selectedReferenceIds.length} assigned reference${selectedReferenceIds.length === 1 ? '' : 's'} to the active ComfyUI workflow.` : 'No references is fine for a text-led draft.' : 'Precision mode accepts up to eight assigned references.'}</p><div className="asset-reference-grid">{assetDraft.references.filter(reference => reference.assetId !== assetDraft.revisionSource?.imageAssetId).map(reference => { const selectedIndex = selectedReferenceIds.indexOf(reference.assetId); const selected = selectedIndex >= 0; return <button type="button" key={reference.id} aria-pressed={selected} disabled={!selected && selectedReferenceIds.length >= referenceLimit} className={selected ? 'selected' : ''} onClick={() => { setSelectedReferenceIds(current => current.includes(reference.assetId) ? current.filter(id => id !== reference.assetId) : [...current, reference.assetId]); if (!selected) setReferenceDirections(current => ({ ...current, [reference.assetId]: current[reference.assetId] ?? { role: 'General visual reference', note: '' } })) }}><img src={reference.imageUrl} alt="" /><span><strong>{reference.label}</strong><small>{reference.source} · {reference.detail}</small></span>{selected && <em className="reference-order" aria-label={`Reference image ${selectedIndex + 1}`}>{selectedIndex + 1}</em>}</button>})}</div>{selectedReferenceIds.length > 0 && <div className="reference-assignments"><div><strong>What each reference controls</strong><small>This prevents accidental face, pose, and background blending.</small></div>{selectedReferenceIds.map(id => { const reference = assetDraft.references.find(item => item.assetId === id); const direction = referenceDirections[id] ?? { role: 'General visual reference', note: '' }; return <div className="reference-assignment" key={id}><span>{reference?.label ?? 'Selected image'}</span><select aria-label={`${reference?.label ?? 'Reference'} controls`} value={direction.role} onChange={event => setReferenceDirections(current => ({ ...current, [id]: { ...direction, role: event.target.value } }))}><option>General visual reference</option><option>Identity / face</option><option>Wardrobe / costume</option><option>Shape / silhouette</option><option>Material / finish</option><option>Style / palette</option><option>Location / architecture</option><option>Composition only</option></select><input aria-label={`${reference?.label ?? 'Reference'} direction`} value={direction.note} placeholder="Optional detail, e.g. right-side sleeve only" onChange={event => setReferenceDirections(current => ({ ...current, [id]: { ...direction, note: event.target.value } }))} /></div>})}</div>}</section>}
        <section className="route-section"><div className="route-heading"><span>{purpose === 'Final' ? 'Production route' : 'Choose image route'}</span><small>One click to generate</small></div>
          <button className={`route-card ${route === 'fast' ? 'active' : ''}`} aria-pressed={route === 'fast'} disabled={purpose === 'Final'} title={purpose === 'Final' ? 'Production finals use the precision route.' : undefined} onClick={() => { setRoute('fast'); setSelectedReferenceIds(current => current.slice(0, 3)) }}>
            <i><Cpu size={18} /></i><span><strong>Fast Draft</strong><small>{fastWorkflowLabel}</small><em><Gauge size={13} />Fast composition loop</em></span>{route === 'fast' && <BadgeCheck size={18} />}
          </button>
          <button className={`route-card precision ${route === 'precision' ? 'active' : ''}`} aria-pressed={route === 'precision'} onClick={() => setRoute('precision')}>
            <i><Cloud size={18} /></i><span><strong>Precision image</strong><small>{usesComposition || selectedReferenceIds.length ? 'High-fidelity image-guided pass' : 'High-fidelity text-to-image pass'}</small><em><Sparkles size={13} />Codex or direct OpenAI API</em></span>{route === 'precision' && <BadgeCheck size={18} />}
          </button>
          {route === 'precision' && <div className="adapter-picker asset-precision-picker" role="radiogroup" aria-label="Precision image engine">{precisionAdapters.map(item => <button type="button" role="radio" aria-checked={precisionAdapterId === item.id} key={item.id} className={precisionAdapterId === item.id ? 'selected' : ''} onClick={() => { setPrecisionAdapterId(item.id); setRouteError(undefined) }}><i>{item.id === 'codex-imagegen' ? <Cpu size={17} /> : <Sparkles size={17} />}</i><span><strong>{item.name}{item.id === 'openai-gpt-image' && <em className="preview-tag">Preview</em>}</strong><small>{item.id === 'codex-imagegen' ? 'Uses your Codex ChatGPT login · no OpenAI API key' : 'Direct OpenAI API · requires a project API key'}</small></span><em className={item.canDispatch ? 'ready' : 'protected'}>{item.canDispatch ? 'Ready' : 'Setup'}</em></button>)}</div>}
        </section>
        <section className="packet-preview"><div><LockKeyhole size={15} /><span><strong>Context ready</strong><small>{assetDraft ? `${selectedReferenceIds.length} visual references selected` : `${references.length} references + ${shot.constraints.length} locked rules included`}</small></span></div><p>{assetDraft?.destination === 'authority' ? 'The result is saved to Assets and added as the new top authority revision.' : assetDraft ? 'The result returns to the asset library. No shot or approval slot is changed.' : 'Your sketch, shot intent, camera, references, and rules travel automatically.'}</p></section>
      </div>
      <footer className="sketch-submit">
        <p><FileCheck2 size={14} /><span><strong>Ready to generate</strong>{assetDraft?.destination === 'authority' ? 'The result is saved to Assets and added as a new protected authority revision.' : assetDraft ? 'The result is saved to Assets for future shots and references.' : 'Framewright includes the shot, references, rules, and selected image route automatically.'}</span></p>
        {assetGenerating && <div className="music-generation-progress" role="status"><WandSparkles /><span><strong>Generating {assetName}</strong><small>{assetElapsed}s · This may take a few minutes; you can leave the node graph closed.</small></span></div>}
        {routeError && <p className="route-error" role="alert"><CircleAlert size={14} /><span><strong>Generation couldn't start</strong>{routeError}</span></p>}
        <button className="primary" onClick={() => void prepareRoute()} disabled={!saved || !prompt.trim() || (assetDraft ? !assetName.trim() : false) || preparing || dispatching}><WandSparkles size={17} />{preparing || dispatching ? 'Starting generation…' : !saved ? 'Waiting for sketch save…' : assetDraft?.destination === 'authority' ? route === 'fast' ? 'Generate revision in ComfyUI' : `Generate revision with ${selectedPrecisionAdapter?.name ?? 'precision image'}` : assetDraft ? route === 'fast' ? 'Generate asset in ComfyUI' : `Generate asset with ${selectedPrecisionAdapter?.name ?? 'precision image'}` : route === 'fast' ? 'Generate draft in ComfyUI' : `Generate draft with ${selectedPrecisionAdapter?.name ?? 'precision image'}`}</button>
        <button className="secondary" onClick={onOpenFrame}>{assetDraft?.destination === 'authority' ? 'Return to authority' : assetDraft ? 'Return to asset library' : 'Return to current frame'}</button>
      </footer>
    </aside>
  </main>
}
