import { useEffect, useRef, useState } from 'react'
import { AmbientLight, AnimationMixer, Box3, BoxGeometry, Color, CylinderGeometry, DirectionalLight, GridHelper, LoopOnce, LoopRepeat, MathUtils, Matrix4, Mesh, MeshStandardMaterial, Object3D, PerspectiveCamera, Raycaster, Scene, SkinnedMesh, SphereGeometry, SRGBColorSpace, Vector2, Vector3, WebGLRenderer, type AnimationAction, type AnimationClip, type BufferGeometry } from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'
import { TransformControls } from 'three/examples/jsm/controls/TransformControls.js'
import { settleTransform, type HandleMode, type SceneInstanceTransform } from '../sceneTransforms'
import { buildMannequin } from './sceneMannequin'
import { frameGuide, framedFieldOfView } from '../sceneFraming'
import { ACESFilmicToneMapping, NoToneMapping, PCFShadowMap, PointLight } from 'three'
import { sharpenTextures } from './textureQuality'
// A skinned mesh cannot be cloned with Object3D.clone: the copies would share
// one skeleton and pose identically, which is the opposite of two instances.
import { clone as cloneSkinned } from 'three/examples/jsm/utils/SkeletonUtils.js'
import type { SceneCameraSummary, SceneEnvironmentSummary, SceneInstanceSummary, ScenePlaceholderSummary } from '../types'

/**
 * Draws a scene's instances and lets the artist orbit and click to select.
 *
 * The camera is the only thing this component moves. Instance transforms come
 * from the scene state and go back through explicit controls, so orbiting can
 * never be mistaken for editing, and what is drawn is always what will be
 * saved.
 *
 * An instance whose model is unavailable is drawn as a labelled placeholder box
 * at its real transform rather than skipped, so a scene never silently loses an
 * object it cannot load.
 *
 * A blockout placeholder is different: it is not a failure but the object
 * itself, simple geometry at a stated size. It is drawn solid and tinted by
 * name so two stand-ins are never mistaken for one another, and never with the
 * wireframe that means "this model would not load".
 */
export interface SceneViewportProps {
  instances: SceneInstanceSummary[]
  camera: SceneCameraSummary
  environment: SceneEnvironmentSummary
  selectedId?: string
  /** When placing a note, a click reports the point in the object's own local space. */
  noteMode?: boolean
  /** Wall-clock seconds into playback. Every object reads its own clip at its own settings from this. */
  playhead?: number
  /** Whether playback is running. Scrubbing works either way. */
  playing?: boolean
  onSelect: (instanceId: string | undefined) => void
  onCameraChange: (camera: SceneCameraSummary) => void
  onPlaceNote?: (instanceId: string, localAnchor: [number, number, number]) => void
  /**
   * A handle drag, drop to floor, or other direct edit of one object's saved
   * transform. Reported into working state only; Save is still the only write.
   */
  onTransform?: (instanceId: string, transform: SceneInstanceTransform) => void
  /**
   * Looking through the shot camera: the stage shows the delivery-shaped frame
   * a render would take, and `camera` is the shot camera. Absent, the stage is
   * the inspection view.
   */
  framing?: { aspect: number; label: string }
  /** Supplies the real renderer's exact-canvas still capture while it is mounted. */
  onCaptureReady?: (capture: ((request: { camera: SceneCameraSummary; time: number; width: number; height: number }) => Promise<Blob>) | undefined) => void
}

type ViewportState = 'loading' | 'ready' | 'unsupported'

/** Identifies one stand-in's geometry, so a resized placeholder is rebuilt. */
const shapeKey = (shape: ScenePlaceholderSummary) => `${shape.shape}:${shape.size.join(',')}:${shape.pose ?? ''}`

export default function SceneViewport({ instances, camera, environment, selectedId, noteMode, playhead, playing, onSelect, onCameraChange, onPlaceNote, onCaptureReady, onTransform, framing }: SceneViewportProps) {
  const host = useRef<HTMLDivElement>(null)
  const [state, setState] = useState<ViewportState>('loading')
  const [handleMode, setHandleMode] = useState<HandleMode>('move')
  const [snap, setSnap] = useState(false)
  const surface = useRef<{
    place: () => void
    sync: (instances: SceneInstanceSummary[], selectedId?: string) => void
    animate: (instances: SceneInstanceSummary[], playhead: number) => void
    light: (environment: SceneEnvironmentSummary) => void
    pick: (clientX: number, clientY: number) => void
    frame: (instanceId?: string) => void
    handles: (mode: HandleMode, snap: boolean, enabled: boolean) => void
    handleDragging: () => boolean
    floorDrop: (instanceId: string) => SceneInstanceTransform | undefined
  } | undefined>(undefined)
  const transformed = useRef(onTransform)
  transformed.current = onTransform
  const framingAspect = useRef(framing?.aspect)
  framingAspect.current = framing?.aspect
  const [stageSize, setStageSize] = useState({ width: 0, height: 0 })
  const view = useRef({ ...camera })
  const drag = useRef<{ pointerId: number; x: number; y: number; moved: boolean } | undefined>(undefined)
  const report = useRef(onCameraChange)
  report.current = onCameraChange
  const select = useRef(onSelect)
  select.current = onSelect
  const placeNote = useRef(onPlaceNote)
  placeNote.current = onPlaceNote
  const placing = useRef(noteMode)
  placing.current = noteMode

  useEffect(() => {
    const container = host.current
    if (!container) return

    let renderer: WebGLRenderer
    try { renderer = new WebGLRenderer({ antialias: true, alpha: false, preserveDrawingBuffer: true }) }
    catch { setState('unsupported'); return }

    let disposed = false
    const scene = new Scene()
    const perspective = new PerspectiveCamera(camera.fieldOfView, 1, 0.01, 2000)
    const placed = new Map<string, Object3D>()
    const loaded = new Map<string, Object3D>()
    const loadingModels = new Set<string>()
    let currentInstances = instances
    let currentSelection = selectedId
    // One load per clip file, and one mixer per object, so two objects playing
    // the same clip keep their own time, speed, and loop.
    const clipFiles = new Map<string, AnimationClip[]>()
    const clipLoads = new Map<string, Promise<void>>()
    const players = new Map<string, { mixer: AnimationMixer; action: AnimationAction; key: string }>()

    renderer.outputColorSpace = SRGBColorSpace
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    renderer.domElement.setAttribute('aria-hidden', 'true')
    container.appendChild(renderer.domElement)

    const ambient = new AmbientLight(0xffffff, environment.ambientIntensity)
    const key = new DirectionalLight(0xffffff, environment.keyIntensity)
    scene.add(ambient, key)
    const practicalLights = new Map<string, PointLight>()
    const grid = new GridHelper(40, 40, new Color('#3c4a44'), new Color('#232c29'))
    scene.add(grid)

    const draw = () => {
      if (disposed) return
      renderer.render(scene, perspective)
      // Where the selected object's handle sits on screen, in stage pixels, so
      // a journey can grab it the way an artist would.
      if (handleSelection && handles.object) {
        const at = proxy.getWorldPosition(new Vector3()).project(perspective)
        container.dataset.handle = JSON.stringify([
          Math.round((at.x + 1) / 2 * container.clientWidth),
          Math.round((1 - at.y) / 2 * container.clientHeight)])
      } else delete container.dataset.handle
    }

    // Handles move an invisible proxy that sits at the selected object's saved
    // transform, never the drawn object itself. A clip or a pivot swing may be
    // moving what is drawn; the handle always edits what will be saved.
    const proxy = new Object3D()
    scene.add(proxy)
    const handles = new TransformControls(perspective, renderer.domElement)
    const handleHelper = handles.getHelper()
    scene.add(handleHelper)
    let handleSelection: string | undefined
    let handlesEnabled = false
    handles.addEventListener('change', () => draw())
    // Three's centre handle scales by the ratio of the pointer's distance from
    // the object's centre, which starts near zero when the artist grabs the
    // centre, so a short drag jumped an object to fifty times its size. The
    // centre scales from screen travel instead: twice as big per 150 pixels
    // right or up. Pointer positions are read in the capture phase so they are
    // current before the controls handle the same event.
    const pointer = { x: 0, y: 0 }
    const uniformStart = { x: 0, y: 0, scale: new Vector3(1, 1, 1) }
    const notePointer = (event: PointerEvent) => { pointer.x = event.clientX; pointer.y = event.clientY }
    container.addEventListener('pointerdown', notePointer, { capture: true })
    container.addEventListener('pointermove', notePointer, { capture: true })
    handles.addEventListener('mouseDown', () => {
      uniformStart.x = pointer.x; uniformStart.y = pointer.y
      uniformStart.scale.copy(proxy.scale)
    })
    handles.addEventListener('objectChange', () => {
      if (!handleSelection) return
      if (handles.mode === 'scale' && handles.axis === 'XYZ') {
        const travel = (pointer.x - uniformStart.x) - (pointer.y - uniformStart.y)
        proxy.scale.copy(uniformStart.scale).multiplyScalar(Math.pow(2, travel / 150))
        proxy.updateMatrixWorld(true)
      }
      transformed.current?.(handleSelection, settleTransform(
        proxy.position.toArray(), [proxy.rotation.x, proxy.rotation.y, proxy.rotation.z], proxy.scale.toArray()))
    })
    const attachHandles = () => {
      const instance = currentInstances.find(candidate => candidate.id === currentSelection)
      if (!handlesEnabled || !instance) {
        handleSelection = undefined
        if (handles.object) handles.detach()
        return
      }
      // Mid-drag the controls own the proxy; writing the reported value back
      // would only fight them.
      if (!handles.dragging || handleSelection !== instance.id) {
        proxy.position.fromArray(instance.position)
        proxy.rotation.set(instance.rotation[0], instance.rotation[1], instance.rotation[2])
        proxy.scale.fromArray(instance.scale)
        proxy.updateMatrixWorld(true)
      }
      handleSelection = instance.id
      if (handles.object !== proxy) handles.attach(proxy)
    }

    // A render sets its own aspect and must take the shot camera's field of
    // view exactly, so the stage's widened framing never reaches a still.
    let capturing = false
    const place = () => {
      const { yaw, pitch, distance, target } = view.current
      perspective.fov = capturing || !framingAspect.current
        ? view.current.fieldOfView
        : framedFieldOfView(view.current.fieldOfView, framingAspect.current, perspective.aspect)
      perspective.updateProjectionMatrix()
      perspective.position.set(
        target[0] + distance * Math.cos(pitch) * Math.sin(yaw),
        target[1] + distance * Math.sin(pitch),
        target[2] + distance * Math.cos(pitch) * Math.cos(yaw))
      perspective.lookAt(target[0], target[1], target[2])
      draw()
    }

    const light = (next: SceneEnvironmentSummary) => {
      ambient.intensity = next.ambientIntensity
      ambient.color.set(next.ambientColor ?? '#ffffff')
      key.intensity = next.keyIntensity
      key.color.set(next.keyColor ?? '#ffffff')
      scene.background = new Color(next.backgroundColor ?? '#171b19')
      grid.visible = next.showGrid ?? true
      renderer.toneMapping = next.cinematic ? ACESFilmicToneMapping : NoToneMapping
      renderer.toneMappingExposure = next.exposure ?? 1
      renderer.shadowMap.enabled = (next.pointLights ?? []).some(source => source.castShadow)
      renderer.shadowMap.type = PCFShadowMap
      const ids = new Set((next.pointLights ?? []).map(source => source.id))
      for (const [id, practical] of practicalLights) {
        if (ids.has(id)) continue
        scene.remove(practical)
        practical.dispose()
        practicalLights.delete(id)
      }
      for (const source of next.pointLights ?? []) {
        let practical = practicalLights.get(source.id)
        if (!practical) {
          practical = new PointLight()
          practical.shadow.mapSize.set(1024, 1024)
          practical.shadow.bias = -0.0005
          practical.shadow.normalBias = 0.025
          practicalLights.set(source.id, practical)
          scene.add(practical)
        }
        practical.name = source.name
        practical.position.fromArray(source.position)
        practical.color.set(source.color)
        practical.intensity = source.intensity
        practical.distance = source.distance
        practical.decay = 2
        practical.castShadow = source.castShadow
        practical.shadow.camera.near = 0.04
        practical.shadow.camera.far = source.distance
        practical.shadow.camera.updateProjectionMatrix()
      }
      key.position.set(
        6 * Math.cos(next.keyPitch) * Math.sin(next.keyYaw),
        6 * Math.sin(next.keyPitch),
        6 * Math.cos(next.keyPitch) * Math.cos(next.keyYaw))
      draw()
    }

    const placeholder = () => {
      const mesh = new Mesh(
        new BoxGeometry(1, 1, 1),
        new MeshStandardMaterial({ color: new Color('#6c5a47'), wireframe: true }))
      mesh.userData.placeholder = true
      return mesh
    }

    // Distinct stand-ins, so a blockout reads as separate objects rather than a
    // pile of identical boxes. The hue comes from the object's own name.
    // FNV-1a spreads names that differ only in their last character ("Box 1",
    // "Box 2") across the wheel; a plain running sum put them a degree apart.
    const hue = (name: string) => {
      let hash = 0x811c9dc5
      for (let index = 0; index < name.length; index += 1) hash = Math.imul(hash ^ name.charCodeAt(index), 0x01000193)
      // A final mix, so the last character reaches the high bits the hue reads.
      hash ^= hash >>> 16; hash = Math.imul(hash, 0x85ebca6b); hash ^= hash >>> 13; hash = Math.imul(hash, 0xc2b2ae35); hash ^= hash >>> 16
      return Math.floor(((hash >>> 0) / 0x100000000) * 360)
    }

    const blockout = (shape: ScenePlaceholderSummary, name: string): Object3D => {
      const [width, height, depth] = shape.size
      // A person is a posed figure rather than one solid, at the stand-in's height.
      if (shape.shape === 'Person') {
        const figure = buildMannequin(height, shape.pose, new MeshStandardMaterial({
          color: new Color(`hsl(${hue(name)}, 42%, 52%)`), roughness: 0.85, transparent: true, opacity: 0.92,
        }))
        figure.userData.placeholder = true
        figure.userData.blockout = shapeKey(shape)
        return figure
      }
      let geometry: BufferGeometry
      if (shape.shape === 'Cylinder') geometry = new CylinderGeometry(width / 2, width / 2, height, 24)
      else if (shape.shape === 'Sphere') geometry = new SphereGeometry(Math.max(width, height, depth) / 2, 24, 16)
      // A ground plane is drawn as a thin slab, so it can still be picked
      // and lit from both sides like every other object.
      else if (shape.shape === 'Plane') geometry = new BoxGeometry(width, Math.max(height, 0.01), depth)
      else geometry = new BoxGeometry(width, height, depth)
      const mesh = new Mesh(geometry, new MeshStandardMaterial({
        color: new Color(`hsl(${hue(name)}, 42%, 52%)`), roughness: 0.85, transparent: true, opacity: 0.86,
      }))
      mesh.userData.placeholder = true
      mesh.userData.blockout = shapeKey(shape)
      return mesh
    }

    const applyTransform = (object: Object3D, instance: SceneInstanceSummary) => {
      object.position.set(instance.position[0], instance.position[1], instance.position[2])
      object.rotation.set(instance.rotation[0], instance.rotation[1], instance.rotation[2])
      object.scale.set(instance.scale[0], instance.scale[1], instance.scale[2])
    }

    const release = (root: Object3D) => {
      root.traverse(node => {
        if (!(node instanceof Mesh)) return
        node.geometry?.dispose?.()
        for (const material of Array.isArray(node.material) ? node.material : [node.material]) material?.dispose?.()
      })
    }

    const releaseInstance = (root: Object3D) => {
      if (root.userData.placeholder) { release(root); return }
      root.traverse(node => {
        if (!(node instanceof Mesh)) return
        for (const material of Array.isArray(node.material) ? node.material : [node.material]) material.dispose()
      })
    }

    const sync = (next: SceneInstanceSummary[], selected?: string) => {
      currentInstances = next
      currentSelection = selected
      attachHandles()
      for (const [id, object] of placed) {
        if (next.some(instance => instance.id === id)) continue
        scene.remove(object)
        releaseInstance(object)
        placed.delete(id)
      }

      for (const instance of next) {
        const existing = placed.get(instance.id)
        const wantsPlaceholder = !instance.available
        const isPlaceholder = existing?.userData.placeholder === true
        const wantedBlockout = instance.placeholder ? shapeKey(instance.placeholder) : undefined
        if (existing && (wantsPlaceholder === isPlaceholder)
          && existing.userData.assetId === instance.assetId
          && existing.userData.blockout === wantedBlockout) {
          applyTransform(existing, instance)
          continue
        }
        if (existing) { scene.remove(existing); releaseInstance(existing); placed.delete(instance.id) }

        // Stand-in geometry the plan asked for, drawn as itself.
        if (instance.placeholder) {
          const marker = blockout(instance.placeholder, instance.name)
          marker.userData.instanceId = instance.id
          marker.userData.assetId = instance.assetId
          applyTransform(marker, instance)
          scene.add(marker)
          placed.set(instance.id, marker)
          continue
        }

        if (wantsPlaceholder || !instance.contentUrl) {
          const marker = placeholder()
          marker.userData.instanceId = instance.id
          marker.userData.assetId = instance.assetId
          applyTransform(marker, instance)
          scene.add(marker)
          placed.set(instance.id, marker)
          continue
        }

        const template = loaded.get(instance.assetId ?? '')
        if (template) {
          const skinned = (() => { let found = false; template.traverse(node => { if (node instanceof SkinnedMesh) found = true }); return found })()
          const copy = skinned ? cloneSkinned(template) : template.clone(true)
          // Selection belongs to one instance, even when its geometry is shared.
          copy.traverse(node => {
            if (!(node instanceof Mesh)) return
            node.castShadow = true
            node.receiveShadow = true
            node.material = Array.isArray(node.material) ? node.material.map(material => material.clone()) : node.material.clone()
          })
          copy.userData.instanceId = instance.id
          copy.userData.assetId = instance.assetId
          applyTransform(copy, instance)
          scene.add(copy)
          placed.set(instance.id, copy)
          continue
        }

        // One load per model revision; every instance of it is a clone.
        const assetKey = instance.assetId ?? ''
        if (loadingModels.has(assetKey)) continue
        loadingModels.add(assetKey)
        new GLTFLoader().load(instance.contentUrl, gltf => {
          loadingModels.delete(assetKey)
          if (disposed) { release(gltf.scene); return }
          sharpenTextures(gltf.scene, renderer)
          loaded.set(instance.assetId ?? '', gltf.scene)
          // A late delivery must not summon an object the artist already removed.
          sync(currentInstances, currentSelection)
          // The scene graph arrives before its textures finish decoding, and
          // this viewport draws on demand, so without waiting for the upload a
          // textured model would stay untextured until something else moved.
          void renderer.compileAsync(scene, perspective).then(() => { if (!disposed) draw() })
        }, undefined, () => {
          loadingModels.delete(assetKey)
          if (disposed) return
          const current = currentInstances.find(candidate => candidate.id === instance.id && candidate.assetId === instance.assetId)
          if (!current) return
          const marker = placeholder()
          marker.userData.instanceId = instance.id
          marker.userData.assetId = instance.assetId
          applyTransform(marker, current)
          scene.add(marker)
          placed.set(instance.id, marker)
          draw()
        })
      }

      for (const [id, object] of placed) {
        const highlighted = id === selected
        object.traverse(node => {
          if (!(node instanceof Mesh)) return
          for (const material of Array.isArray(node.material) ? node.material : [node.material]) {
            if (!material || !('emissive' in material)) continue
            const surfaceMaterial = material as MeshStandardMaterial
            material.userData.originalEmissive ??= surfaceMaterial.emissive.clone()
            surfaceMaterial.emissive.copy(highlighted ? new Color('#3d5c50') : material.userData.originalEmissive as Color)
          }
        })
      }
      // Two skinned objects must hold two skeletons. Sharing one would pose them
      // identically however independent their settings are, so the count is
      // published rather than assumed.
      const skeletons = new Set<object>()
      let skinnedObjects = 0
      for (const [, object] of placed) {
        let found = false
        object.traverse(node => {
          if (!(node instanceof SkinnedMesh)) return
          found = true
          skeletons.add(node.skeleton)
        })
        if (found) skinnedObjects += 1
      }
      container.dataset.skinned = String(skinnedObjects)
      container.dataset.skeletons = String(skeletons.size)

      loadClips(next)
      for (const [id, player] of players) {
        if (next.some(instance => instance.id === id && (instance.clip || instance.motion))) continue
        player.mixer.stopAllAction()
        players.delete(id)
      }
      container.dataset.objects = String(placed.size)
      container.dataset.blockouts = String([...placed.values()].filter(object => object.userData.blockout).length)
      setState('ready')
      draw()
    }

    // The same arithmetic the service uses, so what the artist sees at a given
    // playhead is what the service says is true at that time.
    const clipTime = (instance: SceneInstanceSummary, playhead: number) => {
      const binding = instance.clip!
      const span = binding.end > binding.start ? binding.end - binding.start : 0
      if (span <= 0) return binding.start
      const elapsed = playhead * (binding.speed > 0 ? binding.speed : 1)
      return binding.start + (binding.loop ? elapsed % span : Math.min(elapsed, span))
    }

    const rigidTransform = (instance: SceneInstanceSummary, playhead: number) => {
      const motion = instance.motion!
      const ratio = motion.seconds > 0 ? Math.min(playhead, motion.seconds) / motion.seconds : 0
      const eased = motion.pingPong ? (ratio <= 0.5 ? ratio * 2 : (1 - ratio) * 2) : ratio
      const angle = motion.fromRadians + (motion.toRadians - motion.fromRadians) * eased
      const axis = motion.axis === 'X' ? new Vector3(1, 0, 0) : motion.axis === 'Y' ? new Vector3(0, 1, 0) : new Vector3(0, 0, 1)
      const turn = new Matrix4().makeRotationAxis(axis, angle)
      const pivot = new Vector3(motion.pivot[0], motion.pivot[1], motion.pivot[2])
      // Turning about a pivot is a turn about the origin plus the offset that
      // puts the pivot back where it was.
      const correction = pivot.clone().sub(pivot.clone().applyMatrix4(turn))
      return { angle, correction }
    }

    /** The far end of what a clip moves: the bone a test can watch. */
    const watched = (root: Object3D, bone: string) => {
      let start: Object3D | undefined
      root.traverse(node => { if (!start && node.name === bone) start = node })
      if (!start) return undefined
      let deepest: Object3D = start
      let depth = -1
      start.traverse(node => {
        let steps = 0
        let walk: Object3D | null = node
        while (walk && walk !== start) { steps++; walk = walk.parent }
        if (steps > depth) { depth = steps; deepest = node }
      })
      return deepest
    }

    const animate = (next: SceneInstanceSummary[], playhead: number) => {
      const watchable: Record<string, number[]> = {}
      for (const instance of next) {
        const object = placed.get(instance.id)
        if (!object) continue

        if (instance.motion) {
          const { angle, correction } = rigidTransform(instance, playhead)
          object.position.set(
            instance.position[0] + correction.x,
            instance.position[1] + correction.y,
            instance.position[2] + correction.z)
          object.rotation.set(
            instance.rotation[0] + (instance.motion.axis === 'X' ? angle : 0),
            instance.rotation[1] + (instance.motion.axis === 'Y' ? angle : 0),
            instance.rotation[2] + (instance.motion.axis === 'Z' ? angle : 0))
          watchable[instance.id] = [object.position.x, object.position.y, object.position.z].map(value => Number(value.toFixed(4)))
          continue
        }

        if (!instance.clip) continue
        const clips = clipFiles.get(instance.clip.clipAssetId)
        if (!clips) continue
        const clip = clips.find(candidate => candidate.name === instance.clip!.clipName)
        if (!clip) continue

        const key = `${instance.clip.clipAssetId}:${instance.clip.clipName}`
        let player = players.get(instance.id)
        if (!player || player.key !== key) {
          player?.mixer.stopAllAction()
          const mixer = new AnimationMixer(object)
          const action = mixer.clipAction(clip)
          action.play()
          action.paused = true
          player = { mixer, action, key }
          players.set(instance.id, player)
        }
        player.action.setLoop(instance.clip.loop ? LoopRepeat : LoopOnce, Infinity)
        player.action.clampWhenFinished = true
        // Time is set outright rather than advanced, so scrubbing to a time is
        // the same as playing to it.
        player.action.time = clipTime(instance, playhead)
        player.mixer.update(0)

        const bone = watched(object, instance.clip.clipName && clip.tracks.length > 0
          ? clip.tracks[0].name.split('.')[0]
          : '')
        if (bone) {
          const position = bone.getWorldPosition(new Vector3())
          watchable[instance.id] = [position.x, position.y, position.z].map(value => Number(value.toFixed(4)))
        }
      }
      // What the view actually has on screen, so a journey can hold it against
      // what the service says is true at the same time.
      container.dataset.posed = JSON.stringify(watchable)
      draw()
    }

    const capture = async (request: { camera: SceneCameraSummary; time: number; width: number; height: number }) => {
      if (disposed || currentInstances.length === 0 || placed.size !== currentInstances.length)
        throw new Error('The scene is still loading. Wait for every object, then render again.')
      if (!Number.isInteger(request.width) || !Number.isInteger(request.height) || request.width < 64 || request.height < 64)
        throw new Error('The project delivery canvas is invalid.')
      const maxTexture = renderer.capabilities.maxTextureSize
      if (request.width > maxTexture || request.height > maxTexture)
        throw new Error(`This graphics device supports stills up to ${maxTexture} pixels on either side.`)
      await clipsReady(currentInstances)
      if (disposed) throw new Error('The scene closed before it could be rendered.')

      const originalView = { ...view.current, target: [...view.current.target] }
      const originalRatio = renderer.getPixelRatio()
      const originalSelection = currentSelection
      const restoreWidth = Math.max(1, container.clientWidth)
      const restoreHeight = Math.max(1, container.clientHeight)
      try {
        capturing = true
        grid.visible = false
        handleHelper.visible = false
        renderer.setPixelRatio(1)
        renderer.setSize(request.width, request.height, false)
        perspective.aspect = request.width / request.height
        perspective.updateProjectionMatrix()
        view.current = { ...request.camera, target: [...request.camera.target] }
        sync(currentInstances, undefined)
        place()
        animate(currentInstances, request.time)
        draw()
        return await new Promise<Blob>((resolve, reject) => renderer.domElement.toBlob(
          blob => blob ? resolve(blob) : reject(new Error('The graphics device could not encode the scene still.')),
          'image/png'))
      } finally {
        capturing = false
        grid.visible = true
        handleHelper.visible = true
        renderer.setPixelRatio(originalRatio)
        renderer.setSize(restoreWidth, restoreHeight, false)
        perspective.aspect = restoreWidth / restoreHeight
        perspective.updateProjectionMatrix()
        view.current = originalView
        sync(currentInstances, originalSelection)
        place()
        animate(currentInstances, latestPlayhead.current)
      }
    }

    const loadClips = (next: SceneInstanceSummary[]) => {
      for (const instance of next) {
        const binding = instance.clip
        if (!binding || clipFiles.has(binding.clipAssetId)) continue
        clipFiles.set(binding.clipAssetId, [])
        const loading = new Promise<void>((resolve, reject) => new GLTFLoader().load(`/api/assets/${binding.clipAssetId}/content`, gltf => {
          if (disposed) { resolve(); return }
          clipFiles.set(binding.clipAssetId, gltf.animations)
          release(gltf.scene)
          animate(currentInstances, latestPlayhead.current)
          resolve()
        }, undefined, error => {
          if (!disposed) { clipFiles.delete(binding.clipAssetId); clipLoads.delete(binding.clipAssetId) }
          reject(error)
        }))
        loading.catch(() => undefined)
        clipLoads.set(binding.clipAssetId, loading)
      }
    }

    // A still or a take frame is only true to the scene once every clip it
    // plays has arrived; until then the rig would be drawn in its rest pose.
    const clipsReady = async (next: SceneInstanceSummary[]) => {
      loadClips(next)
      const bound = next.filter(instance => instance.clip)
      try { await Promise.all(bound.map(instance => clipLoads.get(instance.clip!.clipAssetId))) }
      catch { throw new Error('A clip this scene plays could not be loaded, so the render would show the wrong pose. Render again.') }
      const missing = bound.find(instance => !clipFiles.get(instance.clip!.clipAssetId)?.some(clip => clip.name === instance.clip!.clipName))
      if (missing) throw new Error(`${missing.name}'s clip ${missing.clip!.clipName} is not in its file, so the render would show the wrong pose.`)
    }

    // Pull every placed object into view. Without this an object parked away
    // from the origin is simply off screen with no way back to it.
    const frame = (instanceId?: string) => {
      const framed = instanceId ? [placed.get(instanceId)].filter((object): object is Object3D => !!object) : [...placed.values()]
      if (framed.length === 0) return
      const bounds = new Box3()
      for (const object of framed) bounds.expandByObject(object)
      if (bounds.isEmpty()) return
      const centre = bounds.getCenter(new Vector3())
      const size = bounds.getSize(new Vector3())
      view.current = {
        ...view.current,
        target: [centre.x, centre.y, centre.z],
        distance: Math.min(400, Math.max(1.5, Math.max(size.x, size.y, size.z) * 1.9)),
      }
      place()
      report.current({ ...view.current })
    }

    const resize = () => {
      const width = Math.max(1, container.clientWidth)
      const height = Math.max(1, container.clientHeight)
      renderer.setSize(width, height, false)
      perspective.aspect = width / height
      setStageSize(current => current.width === width && current.height === height ? current : { width, height })
      // The shot frame's fit depends on the stage's shape, so placing again
      // keeps it right as the stage resizes.
      place()
    }
    const observer = new ResizeObserver(resize)
    observer.observe(container)

    const wheel = (event: WheelEvent) => {
      event.preventDefault()
      view.current.distance = Math.min(Math.max(view.current.distance * (event.deltaY > 0 ? 1.12 : 0.89), 0.4), 400)
      place()
      report.current({ ...view.current })
    }
    renderer.domElement.addEventListener('wheel', wheel, { passive: false })

    // Clicking picks the object under the pointer. Dragging orbits instead, so
    // a camera move never changes the selection.
    const pick = (clientX: number, clientY: number) => {
      const rect = renderer.domElement.getBoundingClientRect()
      const pointer = new Vector2(
        ((clientX - rect.left) / rect.width) * 2 - 1,
        -((clientY - rect.top) / rect.height) * 2 + 1)
      const raycaster = new Raycaster()
      raycaster.setFromCamera(pointer, perspective)
      const hits = raycaster.intersectObjects([...placed.values()], true)
      for (const hit of hits) {
        let node: Object3D | null = hit.object
        while (node && !node.userData.instanceId) node = node.parent
        if (!node?.userData.instanceId) continue
        const instanceId = node.userData.instanceId as string
        if (placing.current && placeNote.current) {
          // The anchor is stored in the object's own space, so it keeps meaning
          // the same spot on the model when the object is moved or rescaled.
          const local = node.worldToLocal(hit.point.clone())
          placeNote.current(instanceId, [local.x, local.y, local.z])
          return
        }
        select.current(instanceId)
        return
      }
      if (!placing.current) select.current(undefined)
    }

    // Rest an object's lowest point on the floor, measured at its saved
    // transform rather than wherever playback has it right now.
    const floorDrop = (instanceId: string) => {
      const object = placed.get(instanceId)
      const instance = currentInstances.find(candidate => candidate.id === instanceId)
      if (!object || !instance) return undefined
      applyTransform(object, instance)
      object.updateMatrixWorld(true)
      const bounds = new Box3().setFromObject(object)
      animate(currentInstances, latestPlayhead.current)
      if (bounds.isEmpty() || !Number.isFinite(bounds.min.y)) return undefined
      return settleTransform(
        [instance.position[0], instance.position[1] - bounds.min.y, instance.position[2]],
        instance.rotation, instance.scale)
    }

    const setHandles = (mode: HandleMode, snapping: boolean, enabled: boolean) => {
      handles.setMode(mode === 'move' ? 'translate' : mode)
      // Moving reads naturally against the floor; turning and stretching read
      // against the object's own axes.
      handles.setSpace(mode === 'move' ? 'world' : 'local')
      handles.setTranslationSnap(snapping ? 0.25 : null)
      handles.setRotationSnap(snapping ? MathUtils.degToRad(15) : null)
      handles.setScaleSnap(snapping ? 0.1 : null)
      handlesEnabled = enabled
      attachHandles()
      draw()
    }

    surface.current = { place, sync, light, pick, frame, animate, handles: setHandles, handleDragging: () => handles.dragging, floorDrop }
    onCaptureReady?.(capture)
    resize()
    place()
    light(environment)

    return () => {
      disposed = true
      onCaptureReady?.(undefined)
      surface.current = undefined
      observer.disconnect()
      renderer.domElement.removeEventListener('wheel', wheel)
      container.removeEventListener('pointerdown', notePointer, { capture: true })
      container.removeEventListener('pointermove', notePointer, { capture: true })
      handles.detach()
      handles.dispose()
      scene.remove(handleHelper, proxy)
      for (const [, player] of players) player.mixer.stopAllAction()
      players.clear()
      clipFiles.clear()
      for (const [, object] of placed) { scene.remove(object); releaseInstance(object) }
      placed.clear()
      for (const [, template] of loaded) release(template)
      loaded.clear()
      for (const practical of practicalLights.values()) practical.dispose()
      practicalLights.clear()
      renderer.domElement.remove()
      renderer.dispose()
      renderer.forceContextLoss()
    }
    // The viewport is built once; scene data flows in through the effects below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const latestPlayhead = useRef(playhead ?? 0)
  latestPlayhead.current = playhead ?? 0

  useEffect(() => { surface.current?.sync(instances, selectedId) }, [instances, selectedId])
  // Handles exist only for a selected object and never while a note is being
  // placed, when a click means "this spot", not "grab this".
  const handlesOn = !!selectedId && !noteMode && !!onTransform && state === 'ready'
  useEffect(() => { surface.current?.handles(handleMode, snap, handlesOn) }, [handleMode, snap, handlesOn])
  // Scrubbing and playing are the same thing: a playhead, read by every object
  // through its own settings.
  useEffect(() => { surface.current?.animate(instances, playhead ?? 0) }, [instances, playhead])
  useEffect(() => { surface.current?.light(environment) }, [environment])
  useEffect(() => { view.current = { ...camera }; surface.current?.place() }, [camera, framing?.aspect])

  // A running clock is the artist's play button; nothing here advances on its
  // own, so a paused scene draws nothing it was not asked to.
  useEffect(() => {
    if (!playing) return
    let frame = 0
    const step = () => { surface.current?.animate(instances, latestPlayhead.current); frame = requestAnimationFrame(step) }
    frame = requestAnimationFrame(step)
    return () => cancelAnimationFrame(frame)
  }, [playing, instances])

  const orbit = (yaw: number, pitch: number) => {
    view.current = {
      ...view.current,
      yaw: view.current.yaw + yaw,
      pitch: Math.min(1.5, Math.max(-1.5, view.current.pitch + pitch)),
    }
    surface.current?.place()
    report.current({ ...view.current })
  }

  const pointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    // The handles listen on the canvas itself, which hears the press first. A
    // press that grabbed a handle is theirs: orbiting too would move the
    // camera under the object being dragged, and capturing here would steal
    // the rest of the drag from them.
    if (surface.current?.handleDragging()) return
    event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, moved: false }
  }
  const pointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current || drag.current.pointerId !== event.pointerId) return
    const deltaX = (event.clientX - drag.current.x) * 0.008
    const deltaY = (event.clientY - drag.current.y) * 0.008
    if (Math.abs(deltaX) + Math.abs(deltaY) > 0.004) drag.current.moved = true
    drag.current.x = event.clientX; drag.current.y = event.clientY
    orbit(-deltaX, deltaY)
  }
  // A click selects what is under the pointer; a drag orbits instead, so moving
  // the camera never changes the selection.
  const pointerUp = (event: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.pointerId !== event.pointerId) return
    const moved = drag.current.moved
    drag.current = undefined
    if (!moved) surface.current?.pick(event.clientX, event.clientY)
  }

  const nudge = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const step = event.shiftKey ? 0.25 : 0.08
    const moves: Record<string, [number, number]> = {
      ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, step], ArrowDown: [0, -step],
    }
    const move = moves[event.key]
    if (!move) return
    event.preventDefault()
    orbit(move[0], move[1])
  }

  const guide = framing ? frameGuide(stageSize.width, stageSize.height, framing.aspect) : undefined
  return <div className="scene-viewport" data-testid="scene-viewport" data-state={state}>
    <div className="scene-stage-frame">
      <div
        ref={host}
        className={`scene-stage${framing ? ' through-shot' : ''}`}
        data-testid="scene-stage"
        data-view={framing ? 'shot' : 'inspection'}
        role="img"
        aria-label={noteMode
          ? 'Scene view. Placing a note: click the exact spot on an object.'
          : framing
            ? `Looking through the shot camera, ${framing.label}. Drag empty space or use the arrow keys to orbit the shot camera; the inspection camera stays where it was.`
            : "Scene view. Drag empty space or use the arrow keys to orbit the inspection camera. Drag a selected object's handles to move, rotate, or scale it; the placement fields do the same from the keyboard."}
        tabIndex={0}
        onKeyDown={nudge}
        onPointerDown={pointerDown}
        onPointerMove={pointerMove}
        onPointerUp={pointerUp}
        onPointerCancel={() => { drag.current = undefined }}
      />
      {/* What a render would take: everything outside the frame is dimmed. */}
      {guide && guide.width > 0 && <div className="scene-shot-guide" data-testid="scene-shot-guide" aria-hidden="true"
        style={{ left: guide.left, top: guide.top, width: guide.width, height: guide.height }}>
        <span>{framing!.label}</span>
      </div>}
    </div>
    <div className="scene-viewport-bar">
      <span data-testid="scene-object-count">{instances.length} {instances.length === 1 ? 'object' : 'objects'}</span>
      {onTransform && <div className="scene-handle-tools" role="group" aria-label="Object handles">
        {(['move', 'rotate', 'scale'] as const).map(mode => <button type="button" key={mode} aria-pressed={handleMode === mode}
          className={handleMode === mode ? 'active' : ''} disabled={!handlesOn} onClick={() => setHandleMode(mode)}>
          {mode === 'move' ? 'Move' : mode === 'rotate' ? 'Rotate' : 'Scale'}
        </button>)}
        <label className="scene-handle-snap"><input type="checkbox" checked={snap} disabled={!handlesOn}
          onChange={event => setSnap(event.target.checked)} />Snap</label>
        <button type="button" disabled={!handlesOn} onClick={() => {
          if (!selectedId) return
          const dropped = surface.current?.floorDrop(selectedId)
          if (dropped) onTransform(selectedId, dropped)
        }}>Drop to floor</button>
        <button type="button" disabled={!selectedId || state !== 'ready'} onClick={() => surface.current?.frame(selectedId)}>Focus</button>
      </div>}
      <button type="button" disabled={state !== 'ready' || instances.length === 0} onClick={() => surface.current?.frame()}>Frame all</button>
    </div>
    {state === 'unsupported' && <p className="scene-fallback" role="status">
      This browser cannot draw the 3D scene. The object list and placement controls still work, and the scene saves normally.
    </p>}
  </div>
}
