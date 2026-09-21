import { useEffect, useRef, useState } from 'react'
import { AmbientLight, Box3, Color, DirectionalLight, GridHelper, Mesh, MeshStandardMaterial, PerspectiveCamera, PMREMGenerator, Scene, SRGBColorSpace, Vector3, WebGLRenderer, type Material, type Object3D } from 'three'
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'

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
 * Resources are released explicitly when the model changes or the surface
 * closes. A WebGL context left holding geometry is how a long session turns into
 * an unexplained slowdown.
 */
export interface ModelViewerProps {
  contentUrl: string
  label: string
  /** Scene-space dimensions from the server, used to frame the opening view. */
  dimensions: [number, number, number]
  onError?: (message: string) => void
}

type ViewerState = 'loading' | 'ready' | 'failed' | 'unsupported'

const openingYaw = 0.9
const openingPitch = 0.42

export default function ModelViewer({ contentUrl, label, dimensions, onError }: ModelViewerProps) {
  const host = useRef<HTMLDivElement>(null)
  const [state, setState] = useState<ViewerState>('loading')
  const [detail, setDetail] = useState('Preparing the model surface')
  const [readout, setReadout] = useState({ yaw: openingYaw, pitch: openingPitch })
  const [painted, setPainted] = useState(true)
  const [wired, setWired] = useState(false)
  // The renderer reads these rather than React state, because it runs inside an
  // effect that must not be torn down and rebuilt to change how a surface looks.
  const display = useRef({ painted: true, wired: false })
  // One camera, one source of truth. The renderer reads this; the pointer and
  // keyboard paths both write to it, so they can never drift apart.
  const view = useRef({ yaw: openingYaw, pitch: openingPitch, distance: 1, target: new Vector3() })
  const surface = useRef<{ place: () => void; frame: () => void; show: () => void } | undefined>(undefined)
  const drag = useRef<{ pointerId: number; x: number; y: number; panning: boolean } | undefined>(undefined)
  const reportError = useRef(onError)
  reportError.current = onError

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
    const span = Math.max(dimensions[0], dimensions[1], dimensions[2], 0.001)
    let loaded: Object3D | undefined

    view.current = { yaw: openingYaw, pitch: openingPitch, distance: span * 2.6, target: new Vector3() }

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
    scene.environmentIntensity = 0.65
    room.dispose?.()

    scene.add(new AmbientLight(0xffffff, 0.75))
    const key = new DirectionalLight(0xffffff, 1.9)
    key.position.set(1, 2, 1.4)
    scene.add(key)
    // A single key from above leaves every downward face black, and the
    // underside of a prop is exactly where a generated mesh hides its worst
    // surface. This fill is dimmer than the key and comes from the opposite
    // side and below, so the shape still reads as lit from above while nothing
    // is left unreadable.
    const fill = new DirectionalLight(0xffffff, 1.1)
    fill.position.set(-1.1, -1.8, -0.9)
    scene.add(fill)
    const grid = new GridHelper(Math.max(4, span * 4), 16, new Color('#3c4a44'), new Color('#232c29'))
    scene.add(grid)

    const draw = () => { if (!disposed) renderer.render(scene, camera) }

    const place = () => {
      const { yaw, pitch, distance, target } = view.current
      camera.position.set(
        target.x + distance * Math.cos(pitch) * Math.sin(yaw),
        target.y + distance * Math.sin(pitch),
        target.z + distance * Math.cos(pitch) * Math.cos(yaw))
      camera.lookAt(target)
      setReadout({ yaw, pitch })
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

    const show = () => {
      for (const { mesh, own, plain } of dressed) {
        mesh.material = display.current.painted ? own : plain
        for (const material of (Array.isArray(mesh.material) ? mesh.material : [mesh.material])) {
          const surfaceMaterial = material as Material & { wireframe?: boolean }
          if ('wireframe' in surfaceMaterial) surfaceMaterial.wireframe = display.current.wired
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

    surface.current = { place, frame, show }

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
      view.current.distance = Math.min(Math.max(view.current.distance * (event.deltaY > 0 ? 1.12 : 0.89), span * 0.4), span * 40)
      place()
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
        dress(loaded)
        show()
        setState('ready')
        setDetail('Drag to orbit, right-drag or shift-drag to move up and down, scroll to zoom. The model is not moved.')
        resize()
        frame()
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
      surface.current = undefined
      for (const { plain } of dressed) plain.dispose()
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
  }, [contentUrl, dimensions])

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
  const panBy = (across: number, up: number) => {
    const { yaw, distance, target } = view.current
    target.x -= Math.cos(yaw) * across * distance
    target.z += Math.sin(yaw) * across * distance
    target.y += up * distance
    surface.current?.place()
  }

  const nudge = (event: React.KeyboardEvent<HTMLDivElement>) => {
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

  const pointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (state !== 'ready') return
    event.currentTarget.setPointerCapture(event.pointerId)
    // Right or middle button slides; so does shift, for a trackpad with one.
    drag.current = {
      pointerId: event.pointerId, x: event.clientX, y: event.clientY,
      panning: event.button === 1 || event.button === 2 || event.shiftKey,
    }
  }
  const pointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current || drag.current.pointerId !== event.pointerId) return
    const deltaX = (event.clientX - drag.current.x) * 0.008
    const deltaY = (event.clientY - drag.current.y) * 0.008
    const panning = drag.current.panning
    drag.current = { ...drag.current, x: event.clientX, y: event.clientY }
    if (panning) panBy(-deltaX * 0.35, deltaY * 0.35)
    else orbitBy(-deltaX, deltaY)
  }
  const pointerUp = (event: React.PointerEvent<HTMLDivElement>) => {
    if (drag.current?.pointerId === event.pointerId) drag.current = undefined
  }

  return <div className="model-viewer" data-testid="model-viewer" data-state={state}>
    <div
      ref={host}
      className="model-stage"
      data-testid="model-stage"
      role="img"
      aria-label={`${label}. Inspection view. Drag or use the arrow keys to orbit, right-drag or shift-drag to move up and down; the model itself is not moved.`}
      tabIndex={0}
      onKeyDown={nudge}
      onPointerDown={pointerDown}
      onPointerMove={pointerMove}
      onPointerUp={pointerUp}
      onPointerCancel={pointerUp}
      onContextMenu={event => event.preventDefault()}
    />
    <div className="model-viewer-bar">
      <span data-testid="model-viewer-state" role="status">{detail}</span>
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
        <button type="button" disabled={state !== 'ready'} onClick={() => surface.current?.frame()}>Frame</button>
        <button type="button" disabled={state !== 'ready'} onClick={() => { view.current.yaw = openingYaw; view.current.pitch = openingPitch; surface.current?.frame() }}>Reset view</button>
      </div>
    </div>
    <p className="model-viewer-orbit" data-testid="model-viewer-orbit">
      Inspection camera · yaw {readout.yaw.toFixed(2)} · pitch {readout.pitch.toFixed(2)}
    </p>
  </div>
}
