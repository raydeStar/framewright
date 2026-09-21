import { useCallback, useEffect, useState } from 'react'
import { Check, LoaderCircle, Scaling, TriangleAlert, X } from 'lucide-react'
import { studioApi } from '../api'
import type {
  AssetSummary,
  JobSummary,
  ModelGenerationReadiness,
  ModelPreparationEvidence,
  ModelProfileSummary,
} from '../types'

/**
 * Preparing this model for runtime, and deciding about what comes back.
 *
 * This is the opposite handling from generating one. A generated mesh has no
 * topology worth keeping, so it is rebuilt and repainted. A model already here
 * is the other way round: its UVs, its materials and its shell are what
 * somebody looked at and accepted, so the compiler adopts it unchanged and
 * collapses it with them riding along.
 *
 * What arrives is a revision beside the source, not instead of it. The source
 * stays the current one until a person has looked at both, because a mesh
 * nobody has judged should not quietly become the thing every scene reaches
 * for. Looking is what this panel is mostly for: the same four views in the
 * same two passes, of each, side by side, with the matcap pass where faceting
 * and flipped faces have nowhere to hide behind a texture.
 *
 * Accepting is the only thing that promotes it. Refusing keeps it, with the
 * reason, because the reason is how the next budget gets chosen.
 */
export default function ModelPreparation({ asset, profile, onQueued, onDecided }: {
  asset: AssetSummary
  profile: ModelProfileSummary
  onQueued: (message: string) => void
  onDecided: (message: string) => void
}) {
  const [readiness, setReadiness] = useState<ModelGenerationReadiness>()
  const [budget, setBudget] = useState(() => suggested(profile.triangleCount))
  const [job, setJob] = useState<JobSummary>()
  const [evidence, setEvidence] = useState<ModelPreparationEvidence>()
  const [pass, setPass] = useState<'beauty' | 'matcap'>('beauty')
  // The source's own numbers, so a loss is a comparison rather than a
  // figure the artist has to remember from the other revision.
  const [origin, setOrigin] = useState<ModelProfileSummary>()
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    let live = true
    studioApi.modelPreparationReadiness()
      .then(answer => { if (live) setReadiness(answer) })
      .catch(() => { if (live) setReadiness(undefined) })
    return () => { live = false }
  }, [])

  useEffect(() => { setBudget(suggested(profile.triangleCount)) }, [profile.triangleCount])

  // A derivative carries its own evidence, so opening one tomorrow shows the
  // same comparison it was delivered with. Without this the pictures the
  // decision rests on lived only in the session that started the work, which
  // is no kind of review gate.
  useEffect(() => {
    let live = true
    setEvidence(undefined); setJob(undefined); setNote('')
    setOrigin(undefined)
    studioApi.assetPreparationEvidence(asset.id)
      .then(async found => {
        if (!live) return
        setEvidence(found)
        // Measured from the source's own stored bytes, not from what the
        // compiler said it did, so the comparison is of two real files.
        if (found.sourceAssetId && found.sourceAssetId !== asset.id) {
          try { const before = await studioApi.modelProfile(found.sourceAssetId); if (live) setOrigin(before) }
          catch { if (live) setOrigin(undefined) }
        }
      })
      .catch(() => { if (live) setEvidence(undefined) })
    return () => { live = false }
  }, [asset.id])

  // A preparation runs for minutes on hardware this screen does not own, so
  // this follows the job rather than waiting on it. Leaving the screen does
  // not stop it; the queue carries it.
  const follow = useCallback(async (id: string) => {
    const seen = await studioApi.job(id)
    setJob(seen)
    if (seen.state === 'Completed') {
      try { setEvidence(await studioApi.preparationEvidence(id)) } catch { setEvidence(undefined) }
    }
    return seen
  }, [])

  useEffect(() => {
    if (!job || job.state === 'Completed' || job.state === 'Failed' || job.state === 'Cancelled') return
    const timer = window.setInterval(() => { void follow(job.id) }, 2000)
    return () => window.clearInterval(timer)
  }, [job, follow])

  const prepare = async () => {
    setBusy(true); setError(undefined); setEvidence(undefined)
    try {
      const queued = await studioApi.prepareModel(
        asset.id, `${asset.displayName} (runtime)`, budget)
      setJob(queued)
      onQueued(`${queued.shotCode} queued. It keeps going if you leave this screen.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That derivative could not be queued.')
    } finally { setBusy(false) }
  }

  const decide = async (accepted: boolean) => {
    const derivative = evidence?.derivativeAssetId
    if (!derivative) return
    setBusy(true); setError(undefined)
    try {
      const decided = await studioApi.setPreparationAcceptance(derivative, accepted, note)
      onDecided(accepted
        ? `Accepted. Revision ${decided.revisionNumber ?? 2} is now the current model.`
        : 'Refused, and the reason kept. The reviewed model is still the current one.')
      setNote('')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That decision could not be saved.')
    } finally { setBusy(false) }
  }

  const running = job !== undefined && job.state !== 'Completed' && job.state !== 'Failed'
  // Fewer triangles is what was asked for. Fewer maps, materials or
  // textures is a loss nobody asked for, and is called one.
  const lostUvs = origin !== undefined
    && ((origin.uvChannels?.length ?? 0) > (profile.uvChannels?.length ?? 0)
      || profile.primitivesWithoutUvs > origin.primitivesWithoutUvs)
  const lostMaterials = origin !== undefined && profile.materials.length < origin.materials.length
  const lostTextures = origin !== undefined && profile.imageCount < origin.imageCount
  const views = (of: 'source' | 'derivative') =>
    (of === 'source' ? evidence?.source : evidence?.derivative)?.views
      .filter(view => view.pass === pass) ?? []

  return <section className="model-preparation" data-testid="model-preparation">
    <h2><Scaling size={15} />Runtime derivative</h2>

    {readiness === undefined
      ? <p className="model-note"><LoaderCircle className="spin" size={14} /> Asking the compiler what it can do…</p>
      : <>
          <p className="model-note" data-testid="model-preparation-readiness"
            data-can-run={readiness.canRun ? 'true' : 'false'}>{readiness.detail}</p>

          {readiness.canRun && <label className="model-size">
            <span>Runtime triangle budget</span>
            <input type="number" min={1000} max={Math.max(1000, profile.triangleCount - 1)} step={500}
              value={budget} data-testid="model-preparation-budget"
              onChange={event => setBudget(Number(event.target.value))} />
            <span className="model-note">
              This model has {profile.triangleCount.toLocaleString()}. A derivative has to be smaller.
            </span>
          </label>}

          <button type="button" className="secondary" data-testid="model-prepare"
            disabled={busy || running || !readiness.canRun || budget >= profile.triangleCount}
            onClick={() => void prepare()}>
            <Scaling size={15} />{running ? 'Preparing…' : 'Prepare for runtime'}
          </button>
        </>}

    {job && <p className="model-note" data-testid="model-preparation-progress">
      {job.state === 'Failed'
        ? <span className="form-error" role="alert">{job.error}</span>
        : <>{job.phase} · {job.progress}%</>}
    </p>}

    {error && <p className="form-error" role="alert" data-testid="model-preparation-error">{error}</p>}

    {evidence?.source && evidence.derivative && <div className="model-comparison" data-testid="model-comparison">
      <div className="model-comparison-passes" role="group" aria-label="Which pass to compare">
        {(['beauty', 'matcap'] as const).map(choice => <button key={choice} type="button"
          className={choice === pass ? 'compact active' : 'compact'} aria-pressed={choice === pass}
          data-testid={`model-comparison-${choice}`} onClick={() => setPass(choice)}>
          {choice === 'beauty' ? 'Textured' : 'Clay'}
        </button>)}
      </div>
      <p className="model-note">
        The clay pass is flat under a normals matcap, where faceting and flipped faces have
        nowhere to hide behind a texture. A good front view cannot conceal a broken side, which
        is why all four are here.
      </p>
      {(['source', 'derivative'] as const).map(of => <figure key={of} className="model-comparison-row">
        {/* No budget in the caption: reopening a derivative tomorrow loads the
            evidence it was delivered with, and the number in the input box is
            whatever is being asked for next, not what made these pictures. */}
        <figcaption>{of === 'source' ? 'Reviewed model' : 'Derivative'}</figcaption>
        <div className="model-comparison-views">
          {views(of).map(view => <img key={view.url} src={view.url} alt={`${of} ${view.view}`} loading="lazy" />)}
        </div>
      </figure>)}

      {origin && <table className="model-integrity" data-testid="model-integrity">
        <caption>What survived the reduction</caption>
        <thead><tr><th scope="col"></th><th scope="col">Reviewed</th><th scope="col">Derivative</th></tr></thead>
        <tbody>
          <tr><th scope="row">Triangles</th>
            <td>{origin.triangleCount.toLocaleString()}</td>
            <td>{profile.triangleCount.toLocaleString()}</td></tr>
          <tr><th scope="row">Vertices</th>
            <td>{origin.vertexCount.toLocaleString()}</td>
            <td>{profile.vertexCount.toLocaleString()}</td></tr>
          <tr data-lost={lostUvs ? 'true' : 'false'}><th scope="row">UV maps</th>
            <td>{describeUvs(origin)}</td>
            <td>{describeUvs(profile)}</td></tr>
          <tr data-lost={lostMaterials ? 'true' : 'false'}><th scope="row">Materials</th>
            <td>{origin.materials.length}</td>
            <td>{profile.materials.length}</td></tr>
          <tr data-lost={lostTextures ? 'true' : 'false'}><th scope="row">Textures</th>
            <td>{origin.imageCount}</td>
            <td>{profile.imageCount}</td></tr>
        </tbody>
      </table>}
      {(lostUvs || lostMaterials || lostTextures) && <p className="model-note" data-testid="model-integrity-loss">
        <TriangleAlert size={14} /> The derivative carries less than its source did. Fewer triangles
        is the point; fewer maps, materials or textures is a loss, and the views above are where
        you can see what it cost.
      </p>}

      <label className="model-size">
        <span>What you saw</span>
        <textarea rows={2} value={note} data-testid="model-preparation-note"
          onChange={event => setNote(event.target.value)}
          placeholder="Silhouette holds; the rim reads the same at this budget." />
        <span className="model-note">Refusing needs one. It is how the next budget gets chosen.</span>
      </label>
      <div className="model-revision-actions">
        <button type="button" className="primary compact" disabled={busy}
          data-testid="model-preparation-accept" onClick={() => void decide(true)}>
          <Check size={15} />Accept, and make it current
        </button>
        <button type="button" className="compact" disabled={busy || note.trim() === ''}
          data-testid="model-preparation-refuse" onClick={() => void decide(false)}>
          <X size={15} />Refuse
        </button>
      </div>
    </div>}

    {asset.preparationTopologyChanged && <p className="model-note" data-testid="model-topology-changed">
      <TriangleAlert size={14} /> This revision's topology differs from the one it was prepared
      from, so nothing agreed against that surface — a rig, an anchor — carries over to it.
    </p>}
    {asset.preparationAcceptedAt && <p className="model-note" data-testid="model-accepted">
      <Check size={14} /> Accepted by {asset.preparationAcceptedBy} on{' '}
      {new Date(asset.preparationAcceptedAt).toLocaleDateString()}.
      {asset.preparationAcceptanceNote && ` “${asset.preparationAcceptanceNote}”`}
    </p>}
    {!asset.preparationAcceptedAt && asset.preparationAcceptanceNote && <p className="model-note" data-testid="model-refused">
      <X size={14} /> Refused: “{asset.preparationAcceptanceNote}”
    </p>}

    <p className="model-note">
      The compiler does this work, not this studio. What comes back is a revision beside this
      one, never instead of it, and it stays unapproved until somebody says otherwise.
    </p>
  </section>
}

/** Half, rounded to something a person would have typed, and never below the floor. */
function suggested(triangles: number) {
  return Math.max(1000, Math.round(triangles / 2 / 500) * 500)
}

/** UV maps in the terms a loss is read in: how many channels, or none at all. */
function describeUvs(profile: ModelProfileSummary) {
  const channels = profile.uvChannels?.length ?? 0
  if (channels === 0) return 'None'
  const gaps = profile.primitivesWithoutUvs
  return gaps > 0 ? `${channels} · ${gaps} without` : String(channels)
}
