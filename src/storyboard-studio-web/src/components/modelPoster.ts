import {
  AmbientLight,
  Box3,
  DirectionalLight,
  PerspectiveCamera,
  PMREMGenerator,
  Scene,
  SRGBColorSpace,
  Vector3,
  WebGLRenderer,
  type Object3D,
} from 'three'
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'

/**
 * One small picture of a model, rendered once and then never again.
 *
 * A library of models with no thumbnails is a wall of identical boxes, and the
 * obvious fix — render every card — would pull a hundred and forty megabytes of
 * geometry every time the page opened. So this renders a model exactly once in
 * its whole life: the picture is stored by the model's own content hash, every
 * later visit is a cached image, and a model whose bytes change simply has no
 * picture again until somebody looks at it.
 *
 * The work is deliberately serialised. Three big meshes decoding at once on the
 * same context is how a library page becomes a stutter, and nobody is waiting
 * on these: a card that gets its picture a second later is fine, a page that
 * locks up is not.
 */

/** The one renderer these all share. Created on first use, never per card. */
let shared: { renderer: WebGLRenderer; environment: PMREMGenerator } | undefined

/** One at a time, in the order they were asked for. */
let queue: Promise<unknown> = Promise.resolve()

/**
 * What has already been asked for this session, by asset.
 *
 * A promise rather than a flag, because the library refreshes and remounts
 * its cards: with a flag, the first mount started the work, the remount was
 * turned away as a duplicate, and the first mount's answer had already been
 * discarded as stale — so the picture was rendered and then thrown away, and
 * every model kept its box for ever. A remount now waits on the same work.
 */
const started = new Map<string, Promise<string | undefined>>()

const SIZE = 320

/**
 * A model this big is not worth stalling a library page for. Nothing is broken
 * about it; it simply keeps its box until somebody opens it properly.
 */
const MAX_BYTES = 64 * 1024 * 1024

function context() {
  if (shared) return shared
  const renderer = new WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true })
  renderer.setSize(SIZE, SIZE, false)
  renderer.setPixelRatio(1)
  renderer.outputColorSpace = SRGBColorSpace
  const environment = new PMREMGenerator(renderer)
  shared = { renderer, environment }
  return shared
}

function dispose(root: Object3D) {
  root.traverse(node => {
    const mesh = node as { geometry?: { dispose(): void }; material?: unknown }
    mesh.geometry?.dispose()
    const material = mesh.material
    for (const entry of Array.isArray(material) ? material : [material]) {
      const disposable = entry as { dispose?: () => void; map?: { dispose(): void } } | undefined
      disposable?.map?.dispose()
      disposable?.dispose?.()
    }
  })
}

/**
 * Renders the model at `contentUrl` and PUTs the picture back, returning its
 * URL. Resolves to undefined whenever it decides not to, which is an ordinary
 * outcome rather than a failure: the card keeps its box.
 */
export async function renderModelPoster(
  assetId: string, contentUrl: string, bytes: number,
): Promise<string | undefined> {
  if (bytes > MAX_BYTES) return undefined
  const already = started.get(assetId)
  if (already) return already

  const run = queue.then(async () => {
    const { renderer, environment } = context()
    const scene = new Scene()
    let model: Object3D | undefined
    try {
      const gltf = await new GLTFLoader().loadAsync(contentUrl)
      model = gltf.scene

      // The same lighting the inspector uses, so a thumbnail is a small
      // version of what opening the model actually shows.
      const room = environment.fromScene(new RoomEnvironment(), 0.04)
      scene.environment = room.texture
      scene.environmentIntensity = 1.1
      scene.add(new AmbientLight(0xffffff, 0.55))
      const key = new DirectionalLight(0xffffff, 1.5)
      key.position.set(2.5, 4, 3)
      scene.add(key)
      const fill = new DirectionalLight(0xffffff, 0.45)
      fill.position.set(-2, -1.5, -2.5)
      scene.add(fill)
      scene.add(model)

      const bounds = new Box3().setFromObject(model)
      const size = bounds.getSize(new Vector3())
      const centre = bounds.getCenter(new Vector3())
      const extent = Math.max(size.x, size.y, size.z) || 1
      const camera = new PerspectiveCamera(35, 1, extent / 100, extent * 100)
      // Three-quarter and slightly above, which is the view that tells you
      // what a prop is at a glance.
      const distance = extent * 2.1
      camera.position.set(centre.x + distance * 0.6, centre.y + distance * 0.45, centre.z + distance * 0.75)
      camera.lookAt(centre)
      camera.updateProjectionMatrix()

      renderer.render(scene, camera)
      const blob = await new Promise<Blob | null>(resolve =>
        renderer.domElement.toBlob(resolve, 'image/png'))
      room.texture.dispose()
      if (!blob) return undefined

      const response = await fetch(`/api/assets/${assetId}/poster`, {
        method: 'PUT',
        headers: { 'Content-Type': 'image/png', 'X-Storyboard-Studio': '1' },
        body: blob,
      })
      if (!response.ok) return undefined
      return `/api/assets/${assetId}/poster?v=${Date.now()}`
    } catch (reason) {
      // A model the loader will not open is a model without a thumbnail, which
      // is not worth interrupting anybody over -- but swallowing it entirely
      // once hid every render after the first failing for the same reason, so
      // it is said out loud where a developer will see it.
      console.warn('No thumbnail for this model:', reason)
      return undefined
    } finally {
      if (model) { scene.remove(model); dispose(model) }
    }
  })

  // The queue carries on whatever one render did, so a single bad model cannot
  // stop every card behind it from getting its picture.
  queue = run.catch(() => undefined)
  started.set(assetId, run)
  return run
}
