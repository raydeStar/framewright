import { useEffect, useRef, useState } from 'react'
import { AmbientLight, Box3, Color, DirectionalLight, GridHelper, Mesh, PerspectiveCamera, Scene, SRGBColorSpace, Vector3, WebGLRenderer, type Object3D } from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'

/**
 * An isolated inspection surface for one model revision.
 *
 * The inspection camera orbits around the model; the model itself is never
 * touched. Every transform written here lands on the camera, so "I looked at it
 * from the other side" can never quietly become "I moved it".
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
  // One camera, one source of truth. The renderer reads this; the pointer and
  // keyboard paths both write to it, so they can never drift apart.
  const view = useRef({ yaw: openingYaw, pitch: openingPitch, distance: 1, target: new Vector3() })
  const surface = useRef<{ place: () => void; frame: () => void } | undefined>(undefined)
  const drag = useRef<{ pointerId: number; x: number; y: number } | undefined>(undefined)
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

    scene.add(new AmbientLight(0xffffff, 1.4))
    const key = new DirectionalLight(0xffffff, 2.2)
    key.position.set(1, 2, 1.4)
    scene.add(key)
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

    surface.current = { place, frame }

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
        setState('ready')
        setDetail('Drag or use the arrow keys to orbit. The model is not moved.')
        resize()
        frame()
        publishLoadedBounds()
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

  const nudge = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const step = event.shiftKey ? 0.25 : 0.08
    const moves: Record<string, [number, number]> = {
      ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, step], ArrowDown: [0, -step],
    }
    const move = moves[event.key]
    if (!move) return
    event.preventDefault()
    orbitBy(move[0], move[1])
  }

  const pointerDown = (event: React.PointerEvent<HTMLDivElement>) => {
    if (state !== 'ready') return
    event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY }
  }
  const pointerMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current || drag.current.pointerId !== event.pointerId) return
    const deltaX = (event.clientX - drag.current.x) * 0.008
    const deltaY = (event.clientY - drag.current.y) * 0.008
    drag.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY }
    orbitBy(-deltaX, deltaY)
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
      aria-label={`${label}. Inspection view. Drag or use the arrow keys to orbit; the model itself is not moved.`}
      tabIndex={0}
      onKeyDown={nudge}
      onPointerDown={pointerDown}
      onPointerMove={pointerMove}
      onPointerUp={pointerUp}
      onPointerCancel={pointerUp}
    />
    <div className="model-viewer-bar">
      <span data-testid="model-viewer-state" role="status">{detail}</span>
      <div>
        <button type="button" disabled={state !== 'ready'} onClick={() => surface.current?.frame()}>Frame</button>
        <button type="button" disabled={state !== 'ready'} onClick={() => { view.current.yaw = openingYaw; view.current.pitch = openingPitch; surface.current?.frame() }}>Reset view</button>
      </div>
    </div>
    <p className="model-viewer-orbit" data-testid="model-viewer-orbit">
      Inspection camera · yaw {readout.yaw.toFixed(2)} · pitch {readout.pitch.toFixed(2)}
    </p>
  </div>
}
