import { useEffect, useState } from 'react'
import { Boxes, LoaderCircle } from 'lucide-react'
import { studioApi } from '../api'
import BlenderInstallNote from './BlenderInstallNote'
import type { AssetSummary, ModelGenerationReadiness } from '../types'

/**
 * Turning this reference into a 3D model.
 *
 * The work belongs to the Reference Asset Compiler and takes minutes, so this
 * does not wait for it: it freezes which bytes of this reference were chosen,
 * queues the job, and hands the artist back to their work. The queue carries
 * it from there, and the model arrives in the library when it arrives.
 *
 * What cannot run says so before anything is queued, with the reason. A route
 * that could run but has not been commissioned is a different answer from a
 * compiler that is not installed, and the artist is told which.
 *
 * The one thing it insists on is size, because a generator normalises:
 * whatever it makes comes back about two metres tall, a lantern exactly as
 * much as a person. It asks in the only terms most people can answer — where
 * does this come up to on someone standing next to it — rather than in metres,
 * which almost nobody can judge for a prop.
 *
 * It also offers glass, and only offers it. Nothing in a mesh says which faces
 * are glass: a pane and the frame around it are the same surface. The paint is
 * what says so, so the artist names the colour their glass was painted, and a
 * model with none — which is most of them — simply says nothing and gets no
 * glazing step. Guessing here would turn an ordinary painted surface
 * see-through.
 *
 * Both lists come from the compiler, which owns the vocabularies and what each
 * word means.
 *
 * A hero is painted a second time up close, and how depends on what it is, so
 * the artist is asked rather than the name guessed at. A figure or an animal
 * has its head painted again on its own. A prop has no head, and the head pass
 * on one repaints its top as if it were a face and leaves a hard line across
 * it; a large prop is painted again in sections instead, which covers every
 * face. That costs one painter run a section, and the panel says so, with the
 * choice to leave it off, before anything is queued.
 */
export default function ModelFromReference({ asset, onQueued }: {
  asset: AssetSummary
  onQueued: (message: string) => void
}) {
  const [readiness, setReadiness] = useState<ModelGenerationReadiness>()
  const [readinessError, setReadinessError] = useState<string>()
  const [readinessAttempt, setReadinessAttempt] = useState(0)
  const [size, setSize] = useState('')
  const [glass, setGlass] = useState('')
  // Set dressing unless asked: most generated things are seen from a distance,
  // and a hero costs several minutes more of GPU.
  const [detail, setDetail] = useState('set')
  // Asked, not assumed, once the camera is close: a figure, an animal or a
  // prop are painted a second time in different ways.
  const [headEnd, setHeadEnd] = useState('')
  // On unless switched off. Only ever sent for a hero prop.
  const [sections, setSections] = useState(true)
  // What makes the shape. Pixal3D where the compiler offers it, because the
  // owner chose it for props; Hunyuan3D for a figure or an animal.
  const [engine, setEngine] = useState('')
  const [up, setUp] = useState('shortest')
  const [seed, setSeed] = useState('')
  const [topology, setTopology] = useState('closed')
  const [axis, setAxis] = useState('landmark')
  const [metres, setMetres] = useState('')
  const [mounting, setMounting] = useState(false)
  const [rise, setRise] = useState('')
  const [landmarks, setLandmarks] = useState<number[][]>([])
  const pointNames = ['lower mounting foot', 'upper mounting foot', 'top of the lower post']
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    let live = true
    setReadiness(undefined)
    setReadinessError(undefined)
    studioApi.modelGenerationReadiness()
      .then(answer => { if (live) setReadiness(answer) })
      .catch(reason => {
        if (live) setReadinessError(reason instanceof Error ? reason.message : 'The compiler readiness check failed.')
      })
    return () => { live = false }
  }, [readinessAttempt])

  const engines = readiness?.engines ?? []
  const chosenEngine = engine !== '' ? engine
    : engines.some(choice => choice.engine === 'pixal3d') ? 'pixal3d' : 'hunyuan3d'
  const pixal = chosenEngine === 'pixal3d'
  const checks = pixal && readiness?.pixalGeometryChecks
  const measured = checks && axis !== 'landmark'
  const mountOffer = checks && readiness?.pixalMounts && topology === 'open' && axis === 'width'
  const mounted = mountOffer && mounting
  const validMounts = !mounted || (landmarks.length === 3 && rise !== '' && Number(rise) >= 0 && Number(rise) <= 50)
  const validSize = measured ? Number(metres) >= 0.01 && Number(metres) <= 50 : size !== ''
  const hero = detail === 'hero'
  const prop = hero && (pixal || headEnd === 'none')
  const sectionsOffer = readiness?.paintSections ?? null
  const sectionsAsked = prop && sectionsOffer !== null
  const headNote = headEnd === ''
    ? 'A figure or an animal has its head painted a second time on its own. A prop has no head pass.'
    : headEnd === 'none'
    ? 'No head pass: on a prop it repaints the top as if it were a face and leaves a hard line across it.'
    : 'The head is painted a second time on its own; this is where it is cut and cropped.'

  const generate = async () => {
    setBusy(true); setError(undefined)
    try {
      const job = await studioApi.generateModel(
        asset.id, asset.displayName, measured ? '' : size, glass === '' ? undefined : glass, detail,
        pixal ? 'none' : detail === 'hero' ? headEnd : undefined, sectionsAsked ? sections : undefined,
        engines.length > 1 || pixal ? chosenEngine : undefined,
        pixal && seed.trim() !== '' ? Number(seed) : undefined, pixal ? up : undefined,
        checks ? { ...(topology === 'solid' ? { topology: 'closed', gameExport: 'solid' } : { topology }),
          ...(measured ? { sizeM: Number(metres), sizeIs: axis } : {}),
          ...(mounted ? { mounts: { lowerFoot: landmarks[0], upperFoot: landmarks[1], postTop: landmarks[2], riseM: Number(rise) } } : {}) } : undefined)
      onQueued(`${job.shotCode} queued. It keeps going if you leave this screen.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That model could not be queued.')
    } finally { setBusy(false) }
  }

  return <section className="model-from-reference" data-testid="model-from-reference">
    <h3>Make a 3D model</h3>
    {readinessError
      ? <div role="alert">
          <p className="form-error">{readinessError}</p>
          <button type="button" className="secondary" onClick={() => setReadinessAttempt(attempt => attempt + 1)}>Retry readiness check</button>
        </div>
      : readiness === undefined
      ? <p className="model-note"><LoaderCircle className="spin" size={14} /> Asking the compiler what it can do…</p>
      : <>
          <p className="model-note" data-testid="model-generation-readiness" data-can-run={readiness.canRun ? 'true' : 'false'}>
            {readiness.detail}
          </p>
          <BlenderInstallNote install={readiness.blenderInstall} />
          {readiness.canRun && checks && <>
            <label className="model-size"><span>Measure by</span>
              <select value={axis} onChange={event => setAxis(event.target.value)}>
                <option value="landmark">Height beside a person</option><option value="width">Width</option>
                <option value="height">Height</option><option value="longest">Longest side</option>
              </select>
            </label>
            {measured && <label className="model-size"><span>{mounted ? 'Horizontal run between feet in metres' : 'Length in metres'}</span>
              <input type="number" min="0.01" max="50" step="any" value={metres}
                onChange={event => setMetres(event.target.value)} /></label>}
            <label className="model-size"><span>Shell construction</span>
              <select value={topology} onChange={event => setTopology(event.target.value)}>
                <option value="closed">Closed solid</option>
                {readiness.pixalSolidExport && <option value="solid">Rebuild a single closed volume</option>}
                <option value="open">Preserve intentional open edges</option>
              </select>
            </label>
            {mountOffer && <label><input type="checkbox" checked={mounting} onChange={e => setMounting(e.target.checked)} /> Align to mounting feet</label>}
            {mounted && <fieldset><legend>Mounting points</legend>
              <label>Rise between feet in metres <input type="number" step="any" min="0" max="50" value={rise} onChange={e => setRise(e.target.value)} /></label>
              <p>{landmarks.length < 3 ? 'Mark the ' + pointNames[landmarks.length] + ' on the picture.' : 'Three mounting points marked.'}</p>
              <div style={{ position: 'relative', lineHeight: 0 }}>
                <img alt="Reference for mounting points" src={asset.contentUrl} style={{ width: '100%', cursor: 'crosshair' }} onClick={e => {
                  if (landmarks.length >= 3) return
                  const box = e.currentTarget.getBoundingClientRect()
                  setLandmarks([...landmarks, [(e.clientX-box.left)/box.width, (e.clientY-box.top)/box.height]])
                }} />
                {landmarks.map(([x, y], i) => <b key={i} title={pointNames[i]} style={{ position: 'absolute', left: (x*100)+'%', top: (y*100)+'%', transform: 'translate(-50%, -50%)', border: '2px solid white', background: '#17282d', color: 'white', borderRadius: '50%', padding: '3px', lineHeight: 1, pointerEvents: 'none' }}>{i+1}</b>)}
              </div>
              <button className="secondary" type="button" disabled={!landmarks.length} onClick={() => setLandmarks(landmarks.slice(0,-1))}>Undo last point</button>
            </fieldset>}
          </>}
          {readiness.canRun && !measured && readiness.sizes && <label className="model-size">
            <span>Roughly how big is it?</span>
            <select value={size} onChange={event => setSize(event.target.value)}
              data-testid="model-size">
              <option value="">Pick a size…</option>
              {readiness.sizes.map(choice => <option key={choice.size} value={choice.size}>
                {choice.description}
              </option>)}
            </select>
            <span className="model-note">
              Standing next to it, where would it come up to?
            </span>
          </label>}
          {readiness.canRun && engines.length > 1 && <label className="model-size">
            <span>What makes the shape?</span>
            <select value={chosenEngine} onChange={event => setEngine(event.target.value)}
              data-testid="model-engine">
              {engines.map(choice => <option key={choice.engine} value={choice.engine}>
                {choice.description}
              </option>)}
            </select>
            <span className="model-note" data-testid="model-engine-cost">
              {engines.find(choice => choice.engine === chosenEngine)?.cost}
            </span>
          </label>}
          {readiness.canRun && pixal && <label className="model-size">
            <span>How does it stand?</span>
            <select value={up} onChange={event => setUp(event.target.value)} data-testid="model-up">
              <option value="shortest">Long and low: a boat, a bench, a crate</option>
              <option value="longest">Tall: a lantern, a ladder, a column</option>
              <option value="middle">A panel standing on its edge</option>
            </select>
            <span className="model-note">Pixal3D keeps the picture&apos;s camera tilt; this is how it is stood up.</span>
          </label>}
          {readiness.canRun && pixal && <label className="model-size">
            <span>Design (seed)</span>
            <input type="number" min={1} step={1} value={seed} placeholder="The compiler's default"
              onChange={event => setSeed(event.target.value)} data-testid="model-seed" />
            <span className="model-note">Another seed is another design from the same picture.</span>
          </label>}
          {readiness.canRun && readiness.details && readiness.details.length > 1 && <label className="model-size">
            <span>How close will the camera get?</span>
            <select value={detail} onChange={event => setDetail(event.target.value)}
              data-testid="model-detail">
              {readiness.details.map(choice => <option key={choice.detail} value={choice.detail}>
                {choice.description}
              </option>)}
            </select>
            <span className="model-note">
              {readiness.details.find(choice => choice.detail === detail)?.cost}
            </span>
          </label>}
          {readiness.canRun && hero && !pixal && <label className="model-size">
            <span>Is it a figure, an animal or a prop?</span>
            <select value={headEnd} onChange={event => setHeadEnd(event.target.value)}
              data-testid="model-head-end">
              <option value="">Pick one…</option>
              <option value="top">A standing figure: its head at the top</option>
              <option value="left">An animal seen from the side: its head at the left</option>
              <option value="right">An animal seen from the side: its head at the right</option>
              <option value="none">A prop: it has no head</option>
            </select>
            <span className="model-note" data-testid="model-head-note">{headNote}</span>
          </label>}
          {readiness.canRun && sectionsAsked && sectionsOffer && <label className="model-paint-sections">
            <input type="checkbox" checked={sections} data-testid="model-paint-sections"
              onChange={event => setSections(event.target.checked)} />
            <span>
              <strong>Paint a large prop again in sections.</strong>{' '}
              <span data-testid="model-paint-sections-cost">{sectionsOffer.cost}</span>
            </span>
          </label>}
          {readiness.canRun && readiness.colours && <label className="model-size">
            <span>Does it have glass?</span>
            <select value={glass} onChange={event => setGlass(event.target.value)}
              data-testid="model-glass">
              <option value="">No glass</option>
              {readiness.colours.map(choice => <option key={choice.colour} value={choice.colour}>
                Glass painted {choice.description}
              </option>)}
            </select>
            <span className="model-note">
              Only the paint says which faces are panes, so name their colour.
            </span>
          </label>}
          <button type="button" className="secondary" data-testid="model-generate"
            disabled={busy || !readiness.canRun || !validSize || !validMounts || (hero && !pixal && headEnd === '')}
            onClick={() => void generate()}>
            <Boxes size={15} />{busy ? 'Queuing…' : 'Generate from this reference'}
          </button>
        </>}
    {error && <p className="form-error" role="alert" data-testid="model-generate-error">{error}</p>}
    <p className="model-note">
      The compiler does this work, not this studio. It runs for minutes, the queue keeps it,
      and the model arrives in the library naming the reference it came from.
    </p>
  </section>
}
