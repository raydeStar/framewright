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
 * The one thing it asks for is size, because a generator normalises: whatever
 * it makes comes back about two metres tall, a lantern exactly as much as a
 * person. It asks in the only terms most people can answer — where does this
 * come up to on someone standing next to it — rather than in metres, which
 * almost nobody can judge for a prop. The list comes from the compiler, which
 * owns both the vocabulary and the height behind each word.
 */
export default function ModelFromReference({ asset, onQueued }: {
  asset: AssetSummary
  onQueued: (message: string) => void
}) {
  const [readiness, setReadiness] = useState<ModelGenerationReadiness>()
  const [size, setSize] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    let live = true
    studioApi.modelGenerationReadiness()
      .then(answer => { if (live) setReadiness(answer) })
      .catch(() => { if (live) setReadiness(undefined) })
    return () => { live = false }
  }, [])

  const generate = async () => {
    setBusy(true); setError(undefined)
    try {
      const job = await studioApi.generateModel(asset.id, asset.displayName, size)
      onQueued(`${job.shotCode} queued. It keeps going if you leave this screen.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That model could not be queued.')
    } finally { setBusy(false) }
  }

  return <section className="model-from-reference" data-testid="model-from-reference">
    <h3>Make a 3D model</h3>
    {readiness === undefined
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
