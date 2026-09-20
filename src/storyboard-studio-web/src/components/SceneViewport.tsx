import { useEffect, useRef, useState } from 'react'
import { AmbientLight, AnimationMixer, Box3, BoxGeometry, Color, CylinderGeometry, DirectionalLight, GridHelper, LoopOnce, LoopRepeat, Matrix4, Mesh, MeshStandardMaterial, PerspectiveCamera, Raycaster, Scene, SkinnedMesh, SphereGeometry, SRGBColorSpace, Vector2, Vector3, WebGLRenderer, type AnimationAction, type AnimationClip, type BufferGeometry, type Object3D } from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'
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
}

type ViewportState = 'loading' | 'ready' | 'unsupported'

/** Identifies one stand-in's geometry, so a resized placeholder is rebuilt. */
const shapeKey = (shape: ScenePlaceholderSummary) => `${shape.shape}:${shape.size.join(',')}`

export default function SceneViewport({ instances, camera, environment, selectedId, noteMode, playhead, playing, onSelect, onCameraChange, onPlaceNote }: SceneViewportProps) {
  const host = useRef<HTMLDivElement>(null)
  const [state, setState] = useState<ViewportState>('loading')
  const surface = useRef<{
    place: () => void
    sync: (instances: SceneInstanceSummary[], selectedId?: string) => void
    animate: (instances: SceneInstanceSummary[], playhead: number) => void
    light: (environment: SceneEnvironmentSummary) => void
    pick: (clientX: number, clientY: number) => void
    frame: () => void
  } | undefined>(undefined)
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
    try { renderer = new WebGLRenderer({ antialias: true, alpha: true }) }
    catch { setState('unsupported'); return }

    let disposed = false
    const scene = new Scene()
    const perspective = new PerspectiveCamera(camera.fieldOfView, 1, 0.01, 2000)
    const placed = new Map<string, Object3D>()
    const loaded = new Map<string, Object3D>()
    // One load per clip file, and one mixer per object, so two objects playing
    // the same clip keep their own time, speed, and loop.
    const clipFiles = new Map<string, AnimationClip[]>()
    const players = new Map<string, { mixer: AnimationMixer; action: AnimationAction; key: string }>()

    renderer.outputColorSpace = SRGBColorSpace
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    renderer.domElement.setAttribute('aria-hidden', 'true')
    container.appendChild(renderer.domElement)

    const ambient = new AmbientLight(0xffffff, environment.ambientIntensity)
    const key = new DirectionalLight(0xffffff, environment.keyIntensity)
    scene.add(ambient, key)
    scene.add(new GridHelper(40, 40, new Color('#3c4a44'), new Color('#232c29')))

    const draw = () => { if (!disposed) renderer.render(scene, perspective) }

    const place = () => {
      const { yaw, pitch, distance, target } = view.current
      perspective.fov = view.current.fieldOfView
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
      key.intensity = next.keyIntensity
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
    const hue = (name: string) => {
      let total = 0
      for (let index = 0; index < name.length; index += 1) total = (total * 31 + name.charCodeAt(index)) % 360
      return total
    }

    const blockout = (shape: ScenePlaceholderSummary, name: string) => {
      const [width, height, depth] = shape.size
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

    const sync = (next: SceneInstanceSummary[], selected?: string) => {
      for (const [id, object] of placed) {
        if (next.some(instance => instance.id === id)) continue
        scene.remove(object)
        if (object.userData.placeholder) release(object)
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
        if (existing) { scene.remove(existing); if (isPlaceholder) release(existing); placed.delete(instance.id) }

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
          copy.userData.instanceId = instance.id
          copy.userData.assetId = instance.assetId
          applyTransform(copy, instance)
          scene.add(copy)
          placed.set(instance.id, copy)
          continue
        }

        // One load per model revision; every instance of it is a clone.
        new GLTFLoader().load(instance.contentUrl, gltf => {
          if (disposed) { release(gltf.scene); return }
          loaded.set(instance.assetId ?? '', gltf.scene)
          sync(next, selected)
        }, undefined, () => {
          if (disposed) return
          const marker = placeholder()
          marker.userData.instanceId = instance.id
          marker.userData.assetId = instance.assetId
          applyTransform(marker, instance)
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
            ;(material as MeshStandardMaterial).emissive = new Color(highlighted ? '#3d5c50' : '#000000')
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

    const loadClips = (next: SceneInstanceSummary[]) => {
      for (const instance of next) {
        const binding = instance.clip
        if (!binding || clipFiles.has(binding.clipAssetId)) continue
        clipFiles.set(binding.clipAssetId, [])
        new GLTFLoader().load(`/api/assets/${binding.clipAssetId}/content`, gltf => {
          if (disposed) return
          clipFiles.set(binding.clipAssetId, gltf.animations)
          release(gltf.scene)
          animate(next, latestPlayhead.current)
        }, undefined, () => { if (!disposed) clipFiles.delete(binding.clipAssetId) })
      }
    }

    // Pull every placed object into view. Without this an object parked away
    // from the origin is simply off screen with no way back to it.
    const frame = () => {
      if (placed.size === 0) return
      const bounds = new Box3()
      for (const [, object] of placed) bounds.expandByObject(object)
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
      perspective.updateProjectionMatrix()
      draw()
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

    surface.current = { place, sync, light, pick, frame, animate }
    resize()
    place()
    light(environment)

    return () => {
      disposed = true
      surface.current = undefined
      observer.disconnect()
      renderer.domElement.removeEventListener('wheel', wheel)
      for (const [, player] of players) player.mixer.stopAllAction()
      players.clear()
      clipFiles.clear()
      for (const [, object] of placed) { scene.remove(object); if (object.userData.placeholder) release(object) }
      placed.clear()
      for (const [, template] of loaded) release(template)
      loaded.clear()
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
  // Scrubbing and playing are the same thing: a playhead, read by every object
  // through its own settings.
  useEffect(() => { surface.current?.animate(instances, playhead ?? 0) }, [instances, playhead])
  useEffect(() => { surface.current?.light(environment) }, [environment])
  useEffect(() => { view.current = { ...camera }; surface.current?.place() }, [camera])

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

  return <div className="scene-viewport" data-testid="scene-viewport" data-state={state}>
    <div
      ref={host}
      className="scene-stage"
      data-testid="scene-stage"
      role="img"
      aria-label={noteMode
        ? 'Scene view. Placing a note: click the exact spot on an object.'
        : 'Scene view. Drag or use the arrow keys to orbit the inspection camera; objects are moved with the placement controls.'}
      tabIndex={0}
      onKeyDown={nudge}
      onPointerDown={pointerDown}
      onPointerMove={pointerMove}
      onPointerUp={pointerUp}
      onPointerCancel={() => { drag.current = undefined }}
    />
    <div className="scene-viewport-bar">
      <span data-testid="scene-object-count">{instances.length} {instances.length === 1 ? 'object' : 'objects'}</span>
      <button type="button" disabled={state !== 'ready' || instances.length === 0} onClick={() => surface.current?.frame()}>Frame all</button>
    </div>
    {state === 'unsupported' && <p className="scene-fallback" role="status">
      This browser cannot draw the 3D scene. The object list and placement controls still work, and the scene saves normally.
    </p>}
  </div>
}
