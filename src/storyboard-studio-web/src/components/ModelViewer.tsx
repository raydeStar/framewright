import { useEffect, useRef, useState } from 'react'
import { AmbientLight, AnimationMixer, Box3, Color, DirectionalLight, GridHelper, LoopRepeat, Mesh, MeshStandardMaterial, PerspectiveCamera, PMREMGenerator, Scene, SRGBColorSpace, Vector3, WebGLRenderer, type AnimationAction, type AnimationClip, type Material, type Object3D } from 'three'
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'
import { keepsCamera, pinchZoom, zoomed, zoomLimits, ZOOM_IN, ZOOM_OUT, type KeptCamera } from '../inspectionCamera'
import { lightRigs, type SurfaceLight } from '../inspectionLight'
import { channelMaterial, openingLook, surfaceChannels, type SurfaceChannel, type SurfaceLook } from '../surfaceChannels'
import { sharpenTextures } from './textureQuality'

/**
 * An isolated inspection surface for one model revision.
 *
 * The inspection camera orbits, pans and zooms; the model itself is never
 * touched. Every transform written here lands on the camera, so "I looked at it
 * from the other side" can never quietly become "I moved it". Panning moves the
 * point the camera looks at, which is the same promise: the model stays where
 * the file put it.
 *
 * Paint can be turned off and the wire turned on, because they answer different
 * questions. A textured surface hides its own topology, and a decimated mesh's
 * spikes and creases live in the topology. Neither toggle edits anything: the
 * file's own materials are kept and put back.
 *
 * A painted surface can also be read one channel at a time -- base colour,
 * normal map, roughness or metalness, each unlit -- because the lit view mixes
 * all four and a fault lives in one of them. Those are stand-ins beside the
 * file's materials, never edits to them.
 *
 * The lit surface can be seen under two lights. The even inspection light is
 * the default and leaves no face unreadable; the look-dev light is the room,
 * one key and a filmic tone map, for judging paint and metal the way an engine
 * will show them. Only the lights and the tone map change: the model and its
 * materials are the same objects under both.
 *
 * The camera comes as close as a person would stand -- a quarter of a metre
 * from the point it looks at -- by wheel, by pinch or by the plus and minus
 * keys, so a part can be judged from the distance it will be seen at.
 *
 * This surface is rebuilt for every revision, so the camera a reviewer leaves
 * is handed to the parent and picked up by the next revision of the same
 * stack: flipping between two revisions compares the same spot. Paint,
 * wireframe, channel and light are held by the parent too, so it compares the
 * same map under the same light.
 *
 * Resources are released explicitly when the model changes or the surface
 * closes. A WebGL context left holding geometry is how a long session turns into
 * an unexplained slowdown.
 */
export interface ModelViewerProps {
  contentUrl: string
  label: string
  /** Scene-space dimensions from the server, used to frame the opening view. */
  dimensions: [number, number, number]
  supportedClipNames?: string[]
  onError?: (message: string) => void
  /** Told where the inspection camera is whenever it moves, so a note can say what it was written from. */
  onOrbit?: (yaw: number, pitch: number) => void
  /** Puts the inspection camera back at a view; a new object each time it is asked. */
  goTo?: { yaw: number; pitch: number }
  /** The revision stack this model belongs to. Another revision of it opens with the camera where `kept` says it was left. */
  stack?: string
  /** Where the camera was left. Held by the parent, because this surface does not outlive its revision. */
  kept?: { current: KeptCamera | undefined }
  /** Paint, wireframe, channel and light. Held by the parent for the same reason, so the next revision opens looked at the same way. */
  look?: { current: SurfaceLook }
}

type ViewerState = 'loading' | 'ready' | 'failed' | 'unsupported'

const openingYaw = 0.9
const openingPitch = 0.42

export default function ModelViewer({ contentUrl, label, dimensions, supportedClipNames, onError, onOrbit, goTo, stack, kept, look }: ModelViewerProps) {
  const host = useRef<HTMLDivElement>(null)
  const [state, setState] = useState<ViewerState>('loading')
  const [detail, setDetail] = useState('Preparing the model surface')
  const [readout, setReadout] = useState({ yaw: openingYaw, pitch: openingPitch, distance: 1 })
  // The renderer reads these rather than React state, because it runs inside an
  // effect that must not be torn down and rebuilt to change how a surface looks.
  // A parent that holds the look across revisions lends its own object, which
  // is then changed in place; otherwise the surface keeps one for itself.
  const display = useRef<SurfaceLook>(look?.current ?? openingLook())
  const [painted, setPainted] = useState(display.current.painted)
  const [wired, setWired] = useState(display.current.wired)
  const [channel, setChannel] = useState<SurfaceChannel>(display.current.channel)
  const [light, setLight] = useState<SurfaceLight>(display.current.light)
  const [clipNames, setClipNames] = useState<string[]>([])
  const [selectedClip, setSelectedClip] = useState<string | null>(null)
  const [playing, setPlaying] = useState(false)
  const [clipTime, setClipTime] = useState(0)
  const [clipDuration, setClipDuration] = useState(0)
  const allowedClips = useRef(supportedClipNames)
  allowedClips.current = supportedClipNames
  // One camera, one source of truth. The renderer reads this; the pointer and
  // keyboard paths both write to it, so they can never drift apart.
  const view = useRef({ yaw: openingYaw, pitch: openingPitch, distance: 1, target: new Vector3() })
  const surface = useRef<{ place: () => void; frame: () => void; show: () => void; relight: () => void; zoom: (factor: number) => void;
    selectClip: (name: string | null) => void; play: (value: boolean) => void; seek: (seconds: number) => void } | undefined>(undefined)
  const drag = useRef<{ pointerId: number; x: number; y: number; panning: boolean } | undefined>(undefined)
  // Every pointer on the stage, so a second finger can turn a drag into a pinch.
  const touches = useRef(new Map<number, { x: number; y: number }>())
  const pinch = useRef<{ separation: number; x: number; y: number } | undefined>(undefined)
  const reportError = useRef(onError)
  reportError.current = onError
  // Read through a ref, like the error callback: a parent that re-renders
  // must never rebuild the surface just to keep listening.
  const reportOrbit = useRef(onOrbit)
  reportOrbit.current = onOrbit
  // And the same for the kept camera. A model gains its revision family when
  // a second revision is added, which can happen while it is on screen, and
  // a new name for the stack is no reason to load the model again.
  const resume = useRef({ stack, kept })
  resume.current = { stack, kept }
  // The three numbers, not the array. A parent that rebuilds the array on
  // every render -- and the inspection workspace does, every 700 ms while a
  // job is running -- would otherwise tear this whole surface down and load
  // the model again each time: the model blinked in and out for as long as
  // anything was in the queue.
  const [width, height, depth] = dimensions

  useEffect(() => {
    const container = host.current
    if (!container) return

    let renderer: WebGLRenderer
    try {
      renderer = new WebGLRenderer({ antialias: true, alpha: true })
    } catch {
      // An honest fallback beats a blank rectangle: the rest of Framewright,
      // including this model's measurements, stays usable without WebGL.
      setState('unsupported')
      setDetail('This browser cannot open a 3D view. The model and its measurements are still listed here.')
      return
    }

    let disposed = false
    const scene = new Scene()
    const camera = new PerspectiveCamera(38, 1, 0.01, 1000)
    const span = Math.max(width, height, depth, 0.001)
    const limits = zoomLimits(span, camera.near)
    let loaded: Object3D | undefined
    let mixer: AnimationMixer | undefined
    let clips: AnimationClip[] = []
    let action: AnimationAction | undefined
    let currentClip: AnimationClip | undefined
    let motionFrame = 0
    let previousFrame = 0
    let shownSize = 0

    // A camera left on another revision of this stack is picked up before the
    // model arrives, so the view does not start somewhere else and then jump.
    const opening = resume.current.stack
    const left = resume.current.kept?.current
    const resumed = keepsCamera(left, opening) ? left : undefined
    view.current = resumed
      ? { yaw: resumed.yaw, pitch: resumed.pitch, distance: resumed.distance, target: new Vector3(...resumed.target) }
      : { yaw: openingYaw, pitch: openingPitch, distance: span * 2.6, target: new Vector3() }

    renderer.outputColorSpace = SRGBColorSpace
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
    renderer.domElement.setAttribute('aria-hidden', 'true')
    container.appendChild(renderer.domElement)

    // Metal is mostly what it reflects. With lights but no surroundings a fully
    // metallic surface renders nearly black, which reads as dull paint rather
    // than as brass -- the same reason a painted character's armour came out
    // black when its metalness was wrong. A neutral room gives it something to
    // be, without putting a backdrop behind the model: the background stays
    // dark and only the surfaces pick it up.
    const environment = new PMREMGenerator(renderer)
    const room = new RoomEnvironment()
    scene.environment = environment.fromScene(room, 0.04).texture
    room.dispose?.()

    // The lights are made once and only their strengths change with the rig.
    // A light at zero adds nothing, and keeping all three in the scene means
    // the inspection light is the same scene it always was, not a rebuilt one.
    const ambient = new AmbientLight(0xffffff)
    scene.add(ambient)
    const key = new DirectionalLight(0xffffff)
    key.position.set(1, 2, 1.4)
    scene.add(key)
    // A single key from above leaves every downward face black, and the
    // underside of a prop is exactly where a generated mesh hides its worst
    // surface. This fill is dimmer than the key and comes from the opposite
    // side and below, so the shape still reads as lit from above while nothing
    // is left unreadable.
    const fill = new DirectionalLight(0xffffff)
    fill.position.set(-1.1, -1.8, -0.9)
    scene.add(fill)
    const grid = new GridHelper(Math.max(4, span * 4), 16, new Color('#3c4a44'), new Color('#232c29'))
    // The floor is a ruler, not a surface: it keeps its colours under either light.
    for (const material of Array.isArray(grid.material) ? grid.material : [grid.material]) material.toneMapped = false
    scene.add(grid)

    const draw = () => { if (!disposed) renderer.render(scene, camera) }

    // The tone map is part of each lit material's program, which the renderer
    // swaps when it changes. The single-channel stand-ins opt out of it and
    // use no lights, so they draw the same numbers under either rig.
    const relight = () => {
      const rig = lightRigs[display.current.light]
      renderer.toneMapping = rig.toneMapping
      renderer.toneMappingExposure = rig.exposure
      scene.environmentIntensity = rig.environment
      ambient.intensity = rig.ambient
      key.intensity = rig.key
      fill.intensity = rig.fill
      draw()
    }

    const place = () => {
      const { yaw, pitch, distance, target } = view.current
      camera.position.set(
        target.x + distance * Math.cos(pitch) * Math.sin(yaw),
        target.y + distance * Math.sin(pitch),
        target.z + distance * Math.cos(pitch) * Math.cos(yaw))
      camera.lookAt(target)
      setReadout({ yaw, pitch, distance })
      reportOrbit.current?.(yaw, pitch)
      draw()
    }

    const frame = () => {
      if (!loaded) return
      const box = new Box3().setFromObject(loaded)
      box.getCenter(view.current.target)
      const size = box.getSize(new Vector3())
      view.current.distance = Math.max(size.x, size.y, size.z, 0.001) * 2.6
      place()
    }

    // What the renderer actually loaded, published beside what the service
    // measured. If an axis were swapped or flipped between the stored file and
    // the view, these two would stop agreeing.
    const publishLoadedBounds = () => {
      if (!loaded) return
      const box = new Box3().setFromObject(loaded)
      const round = (value: number) => Number(value.toFixed(3))
      container.dataset.loadedMin = [box.min.x, box.min.y, box.min.z].map(round).join(',')
      container.dataset.loadedMax = [box.max.x, box.max.y, box.max.z].map(round).join(',')
    }

    // The file's own materials are kept, never edited, so turning paint back on
    // restores exactly what was delivered rather than an approximation of it.
    const dressed: { mesh: Mesh; own: Material | Material[]; plain: Material }[] = []
    // One unlit stand-in per file material and channel, made the first time
    // it is asked for and kept, so changing channel is a swap, not a rebuild.
    const stands = new Map<string, Material>()
    const standIn = (own: Material, channel: Exclude<SurfaceChannel, 'lit'>) => {
      const key = `${channel}:${own.uuid}`
      let stand = stands.get(key)
      if (!stand) stands.set(key, stand = channelMaterial(own, channel))
      return stand
    }

    const show = () => {
      const { painted, wired, channel } = display.current
      for (const { mesh, own, plain } of dressed) {
        // A channel is one part of the paint, so clay replaces it like the rest.
        mesh.material = !painted ? plain : channel === 'lit' ? own
          : Array.isArray(own) ? own.map(material => standIn(material, channel)) : standIn(own, channel)
        for (const material of (Array.isArray(mesh.material) ? mesh.material : [mesh.material])) {
          const surfaceMaterial = material as Material & { wireframe?: boolean }
          if ('wireframe' in surfaceMaterial) surfaceMaterial.wireframe = wired
        }
      }
      draw()
    }

    const dress = (root: Object3D) => {
      root.traverse(node => {
        if (!(node instanceof Mesh) || !node.material) return
        const own = node.material as Material | Material[]
        const first = (Array.isArray(own) ? own[0] : own) as MeshStandardMaterial
        dressed.push({
          mesh: node,
          own,
          // Unpainted clay, so the eye reads form and topology rather than the
          // texture drawn over them.
          plain: new MeshStandardMaterial({
            color: 0xc9c5bd,
            roughness: 0.82,
            metalness: 0,
            side: first?.side,
          }),
        })
      })
    }

    const tick = (now: number) => {
      if (disposed || !mixer || !action || !currentClip) return
      const seconds = Math.min((now - previousFrame) / 1000, 0.1)
      previousFrame = now
      mixer.update(seconds)
      setClipTime(action.time % currentClip.duration)
      draw()
      motionFrame = requestAnimationFrame(tick)
    }
    const play = (value: boolean) => {
      cancelAnimationFrame(motionFrame)
      setPlaying(value && Boolean(action))
      if (value && action) {
        previousFrame = performance.now()
        motionFrame = requestAnimationFrame(tick)
      }
    }
    const selectClip = (name: string | null) => {
      play(false)
      mixer?.stopAllAction()
      action = undefined
      currentClip = clips.find(clip => clip.name === name)
      if (mixer && currentClip) {
        action = mixer.clipAction(currentClip)
        action.reset().setLoop(LoopRepeat, Infinity).play()
        mixer.update(0)
      }
      setSelectedClip(currentClip?.name ?? null)
      setClipDuration(currentClip?.duration ?? 0)
      setClipTime(0)
      draw()
    }
    const seek = (seconds: number) => {
      if (!mixer || !action || !currentClip) return
      action.time = Math.min(Math.max(seconds, 0), currentClip.duration)
      mixer.update(0)
      setClipTime(action.time)
      draw()
    }
    const zoom = (factor: number) => {
      view.current.distance = zoomed(view.current.distance, factor, limits)
      place()
    }
    surface.current = { place, frame, show, relight, zoom, selectClip, play, seek }

    const resize = () => {
      const width = Math.max(1, container.clientWidth)
      const height = Math.max(1, container.clientHeight)
      renderer.setSize(width, height, false)
      camera.aspect = width / height
      camera.updateProjectionMatrix()
      draw()
    }
    const observer = new ResizeObserver(resize)
    observer.observe(container)

    const wheel = (event: WheelEvent) => {
      event.preventDefault()
      zoom(event.deltaY > 0 ? ZOOM_OUT : ZOOM_IN)
    }
    renderer.domElement.addEventListener('wheel', wheel, { passive: false })

    setState('loading')
    setDetail('Loading the model')
    new GLTFLoader().load(
      contentUrl,
      gltf => {
        if (disposed) { release(gltf.scene); return }
        loaded = gltf.scene
        scene.add(loaded)
        clips = gltf.animations.filter(clip =>
          clip.duration > 0 && (!allowedClips.current || allowedClips.current.includes(clip.name)))
        if (clips.length > 0) mixer = new AnimationMixer(loaded)
        setClipNames(clips.map(clip => clip.name))
        sharpenTextures(loaded, renderer)
        dress(loaded)
        show()
        setState('ready')
        // No longer than the hint it replaces: a third line would make the
        // bar taller and the stage shorter than they have always been.
        setDetail('Drag to orbit, shift-drag to slide, scroll or pinch to zoom. The model is not moved.')
        resize()
        const bounds = new Box3().setFromObject(loaded).getSize(new Vector3())
        shownSize = Math.max(bounds.x, bounds.y, bounds.z)
        // The kept camera stays only while it still suits what it looks at. A
        // revision of a very different size is framed like a newly opened model.
        if (keepsCamera(resumed, opening, shownSize)) place()
        else {
          if (resumed) { view.current.yaw = openingYaw; view.current.pitch = openingPitch }
          frame()
        }
        publishLoadedBounds()
        // The loader reports the scene as soon as its graph is built, while the
        // textures are still decoding. This viewer draws on demand rather than
        // every frame, so that first draw was also the last one, and a textured
        // model sat there white for ever. Wait for the upload, then draw again.
        void renderer.compileAsync(scene, camera).then(() => { if (!disposed) draw() })
      },
      undefined,
      () => {
        if (disposed) return
        setState('failed')
        const message = 'This model could not be opened in the 3D view. Its stored file and measurements are unchanged.'
        setDetail(message)
        reportError.current?.(message)
      })

    relight()
    resize()
    place()

    function release(root: Object3D) {
      root.traverse(node => {
        if (!(node instanceof Mesh)) return
        node.geometry?.dispose?.()
        const materials = Array.isArray(node.material) ? node.material : [node.material]
        for (const material of materials) {
          if (!material) continue
          for (const value of Object.values(material)) {
            const texture = value as { isTexture?: boolean; dispose?: () => void } | null
            if (texture && typeof texture === 'object' && texture.isTexture && texture.dispose) texture.dispose()
          }
          material.dispose?.()
        }
      })
    }

    return () => {
      disposed = true
      cancelAnimationFrame(motionFrame)
      mixer?.stopAllAction()
      if (loaded) mixer?.uncacheRoot(loaded)
      surface.current = undefined
      // Leave the camera for the next revision of this stack, under the name
      // the stack has now. A model that never loaded has nothing to hand on.
      const { stack: leaving, kept: keeper } = resume.current
      if (loaded && leaving && keeper) {
        const { yaw, pitch, distance, target } = view.current
        keeper.current = { stack: leaving, size: shownSize, yaw, pitch, distance, target: target.toArray() }
      }
      // Restore the owned materials before releasing: clay must not hide the silverware.
      for (const { mesh, own, plain } of dressed) { mesh.material = own; plain.dispose() }
      // The stand-ins share the file's textures, which are released with it below.
      for (const stand of stands.values()) stand.dispose()
      scene.environment?.dispose()
      scene.environment = null
      environment.dispose()
      observer.disconnect()
      renderer.domElement.removeEventListener('wheel', wheel)
      if (loaded) { scene.remove(loaded); release(loaded) }
      grid.geometry.dispose()
      for (const material of Array.isArray(grid.material) ? grid.material : [grid.material]) material.dispose()
      renderer.domElement.remove()
      // Releases the GPU context itself, not only the objects drawn in it.
      renderer.dispose()
      renderer.forceContextLoss()
    }
  }, [contentUrl, width, height, depth])

  // A note written from a view takes the camera back there. Only the angle
  // moves: distance and target stay where the artist left them.
  useEffect(() => {
    if (!goTo) return
    view.current.yaw = goTo.yaw
    view.current.pitch = Math.min(1.5, Math.max(-1.5, goTo.pitch))
    surface.current?.place()
  }, [goTo])

  const orbitBy = (yaw: number, pitch: number) => {
    view.current.yaw += yaw
    view.current.pitch = Math.min(1.5, Math.max(-1.5, view.current.pitch + pitch))
    surface.current?.place()
  }

  /**
   * Slides what the camera looks at, in the plane it is looking through, so
   * dragging up moves the model down the screen whatever angle it is seen from.
   * Scaled by distance: the same gesture should cross the same fraction of the
   * screen whether the camera is close in or far out.
   */
  const slide = (across: number, up: number) => {
    const { yaw, distance, target } = view.current
    target.x -= Math.cos(yaw) * across * distance
    target.z += Math.sin(yaw) * across * distance
    target.y += up * distance
  }
  const panBy = (across: number, up: number) => {
    slide(across, up)
    surface.current?.place()
  }

  const nudge = (event: React.KeyboardEvent<HTMLDivElement>) => {
    // Plus and minus are the wheel for a keyboard, so close inspection needs
    // no mouse. With a modifier they are the browser's own zoom and are left to it.
    if (!event.ctrlKey && !event.metaKey && !event.altKey) {
      const step = event.key === '+' || event.key === '=' ? ZOOM_IN : event.key === '-' || event.key === '_' ? ZOOM_OUT : undefined
      if (step) {
        event.preventDefault()
        surface.current?.zoom(step)
        return
      }
    }
    const moves: Record<string, [number, number]> = {
      ArrowLeft: [-0.08, 0], ArrowRight: [0.08, 0], ArrowUp: [0, 0.08], ArrowDown: [0, -0.08],
    }
    const move = moves[event.key]
    if (!move) return
    event.preventDefault()
    // Shift slides rather than turning, so the keyboard reaches everywhere the
    // pointer does and a model taller than the frame can still be read.
    if (event.shiftKey) panBy(-move[0] * 0.35, -move[1] * 0.35)
    else orbitBy(move[0], move[1])
  }

  /** Where the first two fingers are: how far apart, and the point between them. */
  const fingers = () => {
    const [first, second] = [...touches.current.values()]
    return { separation: Math.hypot(first.x - second.x, first.y - second.y), x: (first.x + second.x) / 2, y: (first.y + second.y) / 2 }
  }

  const pointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (state !== 'ready') return
    // Capture keeps a drag alive when the pointer leaves the stage. A pointer
    // that has already gone cannot be held, and still drags while it is inside.
    try { event.currentTarget.setPointerCapture(event.pointerId) } catch { /* Nothing to hold. */ }
    // The first pointer of a gesture starts it afresh, so one whose release
    // was never heard cannot turn the next touch into half a pinch.
    if (event.isPrimary) touches.current.clear()
    touches.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (touches.current.size > 2) return
    if (touches.current.size === 2) {
      // A second finger makes it a pinch: the two zoom and slide together,
      // which is the only way a touch screen reaches either.
      drag.current = undefined
      pinch.current = fingers()
      return
    }
    // Right or middle button slides; so does shift, for a trackpad with one.
    drag.current = {
      pointerId: event.pointerId, x: event.clientX, y: event.clientY,
      panning: event.button === 1 || event.button === 2 || event.shiftKey,
    }
  }
  const pointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (touches.current.has(event.pointerId)) touches.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (pinch.current && touches.current.size >= 2) {
      const before = pinch.current
      const now = pinch.current = fingers()
      slide(-(now.x - before.x) * 0.008 * 0.35, (now.y - before.y) * 0.008 * 0.35)
      surface.current?.zoom(pinchZoom(before.separation, now.separation))
      return
    }
    if (!drag.current || drag.current.pointerId !== event.pointerId) return
    const deltaX = (event.clientX - drag.current.x) * 0.008
    const deltaY = (event.clientY - drag.current.y) * 0.008
    const panning = drag.current.panning
    drag.current = { ...drag.current, x: event.clientX, y: event.clientY }
    if (panning) panBy(-deltaX * 0.35, deltaY * 0.35)
    else orbitBy(-deltaX, deltaY)
  }
  const pointerUp = (event: React.PointerEvent<HTMLDivElement>) => {
    touches.current.delete(event.pointerId)
    if (drag.current?.pointerId === event.pointerId) drag.current = undefined
    if (!pinch.current) return
    if (touches.current.size >= 2) { pinch.current = fingers(); return }
    pinch.current = undefined
    // The finger still down carries on as an orbit from where it is now,
    // rather than from where it was when the pinch began.
    for (const [pointerId, last] of touches.current) drag.current = { pointerId, x: last.x, y: last.y, panning: false }
  }

  const cycleClip = (direction: number) => {
    if (clipNames.length === 0) return
    const index = clipNames.indexOf(selectedClip ?? '')
    const next = index < 0 ? (direction < 0 ? clipNames.length - 1 : 0)
      : (index + direction + clipNames.length) % clipNames.length
    surface.current?.selectClip(clipNames[next])
  }

  // A single channel is unlit, so there is no light to choose while one is shown.
  const lightApplies = !painted || channel === 'lit'

  return <div className="model-viewer" data-testid="model-viewer" data-state={state} data-light={light}>
    <div
      ref={host}
      className="model-stage"
      data-testid="model-stage"
      role="img"
      aria-label={`${label}. Inspection view. Drag or use the arrow keys to orbit, right-drag or shift-drag to move up and down, scroll, pinch or use plus and minus to zoom; the model itself is not moved.`}
      tabIndex={0}
      onKeyDown={nudge}
      onPointerDown={pointerDown}
      onPointerMove={pointerMove}
      onPointerUp={pointerUp}
      onPointerCancel={pointerUp}
      onContextMenu={event => event.preventDefault()}
    />
    <div className="model-viewer-bar">
      <span data-testid="model-viewer-state" role="status"
        title={state === 'ready' ? 'Right-drag and a two-finger drag slide the view too; the plus and minus keys zoom' : undefined}>{detail}</span>
      <div>
        <button type="button" className={painted ? 'is-on' : undefined}
          data-testid="model-paint" aria-pressed={painted} disabled={state !== 'ready'}
          onClick={() => { display.current.painted = !painted; setPainted(!painted); surface.current?.show() }}>
          {painted ? 'Paint on' : 'Paint off'}
        </button>
        <button type="button" className={wired ? 'is-on' : undefined}
          data-testid="model-wireframe" aria-pressed={wired} disabled={state !== 'ready'}
          onClick={() => { display.current.wired = !wired; setWired(!wired); surface.current?.show() }}>
          {wired ? 'Wire on' : 'Wire off'}
        </button>
        <div className="model-channels" role="group" aria-label="Surface channel" data-testid="model-channel">
          {surfaceChannels.map(item => <button type="button" key={item.id} className={channel === item.id ? 'is-on' : undefined}
            data-testid={`model-channel-${item.id}`} aria-pressed={channel === item.id}
            title={painted ? item.hint : 'Turn paint on to look at one channel of it'}
            disabled={state !== 'ready' || !painted}
            onClick={() => { display.current.channel = item.id; setChannel(item.id); surface.current?.show() }}>
            {item.label}
          </button>)}
        </div>
        <button type="button" className={light === 'lookdev' ? 'is-on' : undefined}
          data-testid="model-light" aria-pressed={light === 'lookdev'} disabled={state !== 'ready' || !lightApplies}
          title={!lightApplies ? 'A single channel is shown unlit. Choose Lit to change the light'
            : light === 'lookdev' ? 'Lit by its surroundings and one key through a filmic tone map, as an engine shows it. Press for the even inspection light'
              : 'The even inspection light, which leaves no face unreadable. Press to judge paint and metal as an engine shows them'}
          onClick={() => {
            const next = light === 'lookdev' ? 'inspection' : 'lookdev'
            display.current.light = next
            setLight(next)
            surface.current?.relight()
          }}>
          {light === 'lookdev' ? 'Look-dev on' : 'Look-dev off'}
        </button>
        <button type="button" disabled={state !== 'ready'} onClick={() => surface.current?.frame()}>Frame</button>
        <button type="button" disabled={state !== 'ready'} onClick={() => { view.current.yaw = openingYaw; view.current.pitch = openingPitch; surface.current?.frame() }}>Reset view</button>
      </div>
    </div>
    {clipNames.length > 0 && <div className="model-animation-bar" data-testid="model-animation-bar">
      <strong>Animation</strong>
      <button type="button" aria-label="Previous animation" onClick={() => cycleClip(-1)}>‹</button>
      <select aria-label="Preview animation" data-testid="model-animation-select" value={selectedClip ?? ''}
        onChange={event => surface.current?.selectClip(event.target.value || null)}>
        <option value="">Rest pose</option>
        {clipNames.map(name => <option key={name} value={name}>{name.replaceAll('_', ' ')}</option>)}
      </select>
      <button type="button" aria-label="Next animation" onClick={() => cycleClip(1)}>›</button>
      <button type="button" data-testid="model-animation-play" disabled={!selectedClip}
        onClick={() => surface.current?.play(!playing)}>{playing ? 'Pause' : 'Play'}</button>
      <input type="range" aria-label="Animation time" data-testid="model-animation-time"
        min={0} max={clipDuration} step={0.01} value={clipTime} disabled={!selectedClip}
        onChange={event => surface.current?.seek(Number(event.target.value))} />
      <span>{clipTime.toFixed(1)} / {clipDuration.toFixed(1)}s</span>
    </div>}
    <p className="model-viewer-orbit" data-testid="model-viewer-orbit">
      Inspection camera · yaw {readout.yaw.toFixed(2)} · pitch {readout.pitch.toFixed(2)} · distance {readout.distance.toFixed(2)} m
    </p>
  </div>
}
