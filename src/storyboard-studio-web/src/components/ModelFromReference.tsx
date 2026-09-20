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
 */
export default function ModelFromReference({ asset, onQueued }: {
  asset: AssetSummary
  onQueued: (message: string) => void
}) {
  const [readiness, setReadiness] = useState<ModelGenerationReadiness>()
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
      const job = await studioApi.generateModel(asset.id, asset.displayName)
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
          <button type="button" className="secondary" data-testid="model-generate"
            disabled={busy || !readiness.canRun} onClick={() => void generate()}>
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
