import { useEffect, useState } from 'react'
import { Boxes, LoaderCircle } from 'lucide-react'
import { studioApi } from '../api'
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
  const [headEnd, setHeadEnd] = useState('top')
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

  const generate = async () => {
    setBusy(true); setError(undefined)
    try {
      const job = await studioApi.generateModel(
        asset.id, asset.displayName, size, glass === '' ? undefined : glass, detail,
        detail === 'hero' ? headEnd : undefined)
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
          {readiness.canRun && readiness.sizes && <label className="model-size">
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
          {readiness.canRun && detail === 'hero' && <label className="model-size">
            <span>Where is the head in the picture?</span>
            <select value={headEnd} onChange={event => setHeadEnd(event.target.value)}
              data-testid="model-head-end">
              <option value="top">At the top: a standing figure</option>
              <option value="left">At the left: an animal seen from the side</option>
              <option value="right">At the right: an animal seen from the side</option>
            </select>
            <span className="model-note">
              The head is painted a second time on its own; this is where it is cut and cropped.
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
            disabled={busy || !readiness.canRun || size === ''} onClick={() => void generate()}>
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
