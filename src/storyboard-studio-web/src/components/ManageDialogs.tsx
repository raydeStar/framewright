import { useEffect, useMemo, useState, type DragEvent, type FormEvent } from 'react'
import { BadgeCheck, Clapperboard, ImagePlus, LoaderCircle, LockKeyhole, Plus, Sparkles, Upload, X } from 'lucide-react'
import { studioApi } from '../api'
import type { ReferenceSummary, ReferenceVersionSummary, ShotSummary } from '../types'
import Dialog from './Dialog'

const categories = ['Character', 'Role', 'Wardrobe', 'Pose', 'Location', 'Architecture', 'Prop', 'Style', 'World']

/** A title the artist did not write: the description's opening words, trimmed at a word boundary. */
function titleFromDescription(description: string) {
  const firstSentence = (description.trim().split(/(?<=[.!?;])\s/)[0] ?? '').replace(/[.!?;,:]+$/, '')
  if (firstSentence.length <= 60) return firstSentence
  let title = ''
  for (const word of firstSentence.split(/\s+/)) {
    if ((title + ' ' + word).trim().length > 48) break
    title = (title + ' ' + word).trim()
  }
  return `${title || firstSentence.slice(0, 48)}…`
}

/**
 * Creating a shot starts from one sentence. Everything a generator or reviewer
 * can use beyond that (camera, action, references, locked details, the shot
 * code) is kept, but folded away until the artist wants it. Duration is asked
 * in seconds and stored in frames at the project's rate.
 */
export function ShotEditorDialog({ shot, initialImage, references, suggestedCode, framesPerSecond, onClose, onSaved }: {
  shot?: ShotSummary
  initialImage?: File
  references: ReferenceSummary[]
  suggestedCode?: string
  framesPerSecond: number
  onClose: () => void
  onSaved: (shot: ShotSummary) => void
}) {
  const fps = framesPerSecond > 0 ? framesPerSecond : 24
  const [code, setCode] = useState(shot?.code ?? suggestedCode ?? '')
  const [title, setTitle] = useState(shot?.title ?? '')
  const [description, setDescription] = useState(shot?.description ?? '')
  const [seconds, setSeconds] = useState(() => Math.round(((shot?.durationFrames ?? 3 * fps) / fps) * 10) / 10)
  const [camera, setCamera] = useState(shot?.camera ?? '')
  const [action, setAction] = useState(shot?.action ?? '')
  const [referenceIds, setReferenceIds] = useState<string[]>(shot?.referenceIds ?? [])
  const [constraintsText, setConstraintsText] = useState((shot?.constraints ?? []).join('\n'))
  const [detailsOpen, setDetailsOpen] = useState(Boolean(shot))
  const [image, setImage] = useState<File | undefined>(initialImage)
  const [preview, setPreview] = useState<string>()
  const [suggesting, setSuggesting] = useState(false)
  const [suggestionDetail, setSuggestionDetail] = useState<string>()
  const [imageDragging, setImageDragging] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    if (!image) { setPreview(undefined); return }
    const url = URL.createObjectURL(image)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [image])

  const chooseImage = (file?: File) => {
    if (!file) return
    if (!['image/png', 'image/jpeg'].includes(file.type)) { setError('Choose a PNG or JPEG image.'); return }
    setError(undefined); setImage(file)
  }
  const dropImage = (event: DragEvent<HTMLLabelElement>) => {
    event.preventDefault(); setImageDragging(false); chooseImage(event.dataTransfer.files[0])
  }
  // Codex structures the artist's sentence into the folded-away fields. The
  // sentence itself stays exactly as the artist wrote it.
  const suggestDetails = async () => {
    if (!description.trim()) { setError('Describe the shot in a sentence or two first.'); return }
    setSuggesting(true); setError(undefined); setSuggestionDetail(undefined)
    try {
      const suggestion = await studioApi.suggestShotIntent(description.trim())
      setTitle(suggestion.title); setSeconds(Math.round((suggestion.durationFrames / fps) * 10) / 10)
      setCamera(suggestion.camera); setAction(suggestion.action); setReferenceIds(suggestion.referenceIds)
      setConstraintsText(suggestion.constraints.join('\n')); setSuggestionDetail(suggestion.detail)
      setDetailsOpen(true)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Codex could not suggest details for this shot.') }
    finally { setSuggesting(false) }
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!description.trim()) { setError('Describe the shot to add it.'); document.getElementById('shot-description')?.focus(); return }
    if (!(seconds > 0)) { setError('Give the shot a length of more than zero seconds.'); return }
    if (!code.trim()) { setDetailsOpen(true); setError('The shot needs a code, such as SH-010.'); return }
    setBusy(true); setError(undefined)
    try {
      const asset = !shot && image ? await studioApi.uploadImage(image, image.name) : undefined
      const common = {
        title: title.trim() || titleFromDescription(description),
        description: description.trim(),
        durationFrames: Math.max(1, Math.round(seconds * fps)),
        camera: camera.trim(),
        action: action.trim(),
        referenceIds,
        constraints: lines(constraintsText),
      }
      const saved = shot
        ? await studioApi.updateShot(shot.id, { expectedUpdatedAt: shot.updatedAt, ...common })
        : await studioApi.createShot({ code: code.trim(), ...common, initialImageAssetId: asset?.id })
      onSaved(saved)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'The shot could not be saved.') }
    finally { setBusy(false) }
  }

  const frames = Math.max(1, Math.round((seconds > 0 ? seconds : 0) * fps))

  return <Dialog className="modal-backdrop entity-backdrop" onClose={onClose} labelledBy="shot-editor-title" initialFocus="#shot-description">
    <form className="entity-dialog" onSubmit={submit} noValidate>
      <header><div className="entity-icon"><Clapperboard /></div><div><p className="eyebrow">{shot ? `${shot.code} · working intent` : 'New shot'}</p><h2 id="shot-editor-title">{shot ? 'Edit shot' : 'Add a shot'}</h2></div><button type="button" className="icon-button" onClick={onClose} aria-label="Close shot editor"><X /></button></header>
      <div className="entity-form">
        {!shot && <label className={`shot-intake-image ${imageDragging ? 'dragging' : ''}`}
          onDragEnter={event => { event.preventDefault(); setImageDragging(true) }}
          onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' }}
          onDragLeave={event => { if (event.currentTarget.contains(event.relatedTarget as Node)) return; setImageDragging(false) }}
          onDrop={dropImage}>
          <input type="file" accept="image/png,image/jpeg" onChange={event => chooseImage(event.target.files?.[0])} />
          <span className="shot-intake-preview">{preview ? <img src={preview} alt="New shot draft preview" /> : <ImagePlus size={28} />}</span>
          <span><strong>{image ? image.name : 'Have an image already? Drop it here'}</strong><small>{image ? 'This becomes the first draft of the shot.' : 'Optional. Or tap to choose a PNG or JPEG.'}</small></span>
          <Upload size={18} />
        </label>}
        <label><span>Describe the shot</span><textarea id="shot-description" value={description} onChange={event => { setDescription(event.target.value); setError(undefined) }} aria-required="true" maxLength={4000} placeholder="What happens, and what do we see? e.g. An old lighthouse on a cliff in a storm; the lamp sweeps across the waves." /></label>
        {!shot && <div className="shot-intake-suggest">
          <button type="button" className="secondary compact" onClick={() => void suggestDetails()} disabled={suggesting || !description.trim()}>{suggesting ? <LoaderCircle className="spin" /> : <Sparkles />}{suggesting ? 'Suggesting details…' : 'Suggest details with Codex'}</button>
          <small>Optional. Fills in the title, camera and action from your description for you to review.</small>
        </div>}
        {suggestionDetail && <p className="shot-intake-suggestion" role="status"><BadgeCheck size={14} />{suggestionDetail} Review the details below before adding the shot.</p>}
        <div className="form-grid two">
          <label><span>Title <small>optional</small></span><input id="shot-title" value={title} onChange={event => setTitle(event.target.value)} maxLength={120} placeholder={description.trim() ? titleFromDescription(description) : 'Uses the start of the description'} /></label>
          <label><span>Length in seconds <small>{frames} frames at {fps} fps</small></span><input type="number" min={0.1} max={600} step={0.5} value={Number.isFinite(seconds) ? seconds : ''} onChange={event => setSeconds(Number(event.target.value))} /></label>
        </div>
        <details className="entity-form-more" open={detailsOpen} onToggle={event => setDetailsOpen(event.currentTarget.open)}>
          <summary>Camera, action and references</summary>
          <div className="entity-form-more-body">
            <label><span>Camera <small>optional</small></span><input value={camera} onChange={event => setCamera(event.target.value)} maxLength={240} placeholder="Medium wide · low angle · 35 mm" /></label>
            <label><span>Action <small>optional</small></span><textarea value={action} onChange={event => setAction(event.target.value)} maxLength={1200} placeholder="The beat, the movement, and what changes through the shot." /></label>
            <fieldset><legend>References <small>{referenceIds.length} selected</small></legend>{references.length === 0 ? <p className="entity-form-empty">No references yet. Characters, places and props you add to the project appear here.</p> : <div className="authority-picker">{references.map(reference => <label key={reference.id} className={referenceIds.includes(reference.id) ? 'selected' : ''}><input type="checkbox" checked={referenceIds.includes(reference.id)} onChange={() => setReferenceIds(current => current.includes(reference.id) ? current.filter(id => id !== reference.id) : [...current, reference.id])} /><span><strong>{reference.name}</strong><small>{reference.category} · v{reference.version}</small></span><BadgeCheck size={16} /></label>)}</div>}</fieldset>
            <label><span>Must stay true <small>one per line</small></span><textarea value={constraintsText} onChange={event => setConstraintsText(event.target.value)} placeholder={'The lamp is always lit\nWaves move left to right'} /></label>
            <label><span>Shot code</span><input value={code} onChange={event => setCode(event.target.value)} readOnly={Boolean(shot)} maxLength={24} placeholder="SH-070" /></label>
          </div>
        </details>
        {shot?.approval === 'Ratified' && <div className="form-warning"><LockKeyhole size={16} /><span><strong>The approved version stays as it is.</strong> Saving creates a new working version; the approved one remains in history.</span></div>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>{busy ? <LoaderCircle className="spin" /> : shot ? <BadgeCheck /> : <Plus />}{busy ? 'Saving…' : shot ? 'Save changes' : image ? 'Add shot with image' : 'Add shot'}</button></footer>
    </form>
  </Dialog>
}

export function AuthorityEditorDialog({ authority, onClose, onSaved }: {
  authority?: ReferenceSummary
  onClose: () => void
  onSaved: (reference: ReferenceSummary) => void
}) {
  const [name, setName] = useState(authority?.name ?? '')
  const [category, setCategory] = useState(authority?.category ?? 'Character')
  const [description, setDescription] = useState(authority?.description ?? '')
  const [lockedConstraint, setLockedConstraint] = useState(authority?.lockedConstraint ?? '')
  const [accent, setAccent] = useState(authority?.accent ?? '#c8a96a')
  const [image, setImage] = useState<File>()
  const [preview, setPreview] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [versions, setVersions] = useState<ReferenceVersionSummary[]>([])
  const [historyLoading, setHistoryLoading] = useState(Boolean(authority))
  const imageLabel = useMemo(() => image?.name ?? (authority?.imageUrl ? 'Keep current approved image' : 'Optional PNG or JPEG'), [image, authority])
  useEffect(() => {
    if (!authority) return
    let active = true
    setHistoryLoading(true)
    void studioApi.referenceVersions(authority.id)
      .then(items => { if (active) setVersions(items) })
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'Could not load reference history.') })
      .finally(() => { if (active) setHistoryLoading(false) })
    return () => { active = false }
  }, [authority])

  const chooseImage = (file?: File) => {
    setImage(file)
    if (preview) URL.revokeObjectURL(preview)
    setPreview(file ? URL.createObjectURL(file) : undefined)
  }
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError(undefined)
    try {
      const asset = image ? await studioApi.uploadImage(image, image.name) : undefined
      const saved = authority
        ? await studioApi.createReferenceVersion(authority.id, { expectedVersion: authority.version, description, lockedConstraint, imageAssetId: asset?.id ?? authority.imageAssetId })
        : await studioApi.createReference({ name, category, description, lockedConstraint, accent, imageAssetId: asset?.id })
      onSaved(saved)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'The reference could not be saved.') }
    finally { setBusy(false) }
  }

  return <Dialog className="modal-backdrop entity-backdrop" onClose={onClose} labelledBy="authority-editor-title" initialFocus="#authority-name">
    <form className="entity-dialog authority-editor" onSubmit={submit}>
      <header><div className="entity-icon authority"><LockKeyhole /></div><div><p className="eyebrow">{authority ? `${authority.name} · v${authority.version}` : 'Approved production reference'}</p><h2 id="authority-editor-title">{authority ? 'Create reference version' : 'Add reference'}</h2></div><button type="button" className="icon-button" onClick={onClose} aria-label="Close reference editor"><X /></button></header>
      <div className="entity-form">
        <div className="authority-import">
          <div className="authority-import-preview">{preview || authority?.imageUrl ? <img src={preview ?? authority?.imageUrl} alt="Reference preview" /> : <ImagePlus />}</div>
          <label className="file-action"><ImagePlus size={17} /><span><strong>{imageLabel}</strong><small>Structural validation · stored by content hash</small></span><input type="file" accept="image/png,image/jpeg" onChange={event => chooseImage(event.target.files?.[0])} /></label>
        </div>
        <div className="form-grid two">
          <label><span>Name</span><input id="authority-name" value={name} onChange={event => setName(event.target.value)} readOnly={Boolean(authority)} required maxLength={120} /></label>
          <label><span>Category</span><select value={category} onChange={event => setCategory(event.target.value)} disabled={Boolean(authority)}>{categories.map(value => <option key={value}>{value}</option>)}</select></label>
        </div>
        <label><span>Identity and context</span><textarea value={description} onChange={event => setDescription(event.target.value)} required maxLength={1600} placeholder="Describe appearance, materials, age, silhouette, narrative role, and world context." /></label>
        <label><span>Locked constraint</span><textarea value={lockedConstraint} onChange={event => setLockedConstraint(event.target.value)} required maxLength={800} placeholder="State the detail that must never drift." /></label>
        {!authority && <label><span>Board accent</span><div className="color-field"><input type="color" value={accent} onChange={event => setAccent(event.target.value)} /><code>{accent}</code></div></label>}
        <div className="form-authority-note"><BadgeCheck size={17} /><span><strong>{authority ? `Version ${authority.version + 1} will become the approved reference.` : 'Version 1 becomes the approved reference.'}</strong> Earlier versions stay protected and available.</span></div>
        {authority && <section className="authority-history" aria-labelledby="authority-history-title"><div><h3 id="authority-history-title">Approved history</h3><small>Read-only reference versions · newest first</small></div>{historyLoading ? <p className="authority-history-loading"><LoaderCircle className="spin" size={15} />Loading immutable versions…</p> : versions.map(version => <article key={version.id} className={version.version === authority.version ? 'current' : ''}>{version.imageUrl ? <img src={version.imageUrl} alt={`${authority.name} version ${version.version}`} /> : <div className="authority-history-placeholder"><ImagePlus size={16} /></div>}<span><strong>v{version.version}{version.version === authority.version ? ' · Current' : ''}</strong><small>{new Date(version.ratifiedAt).toLocaleDateString()}</small><p>{version.description}</p><em><LockKeyhole size={11} />{version.lockedConstraint}</em></span></article>)}</section>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>{busy ? <LoaderCircle className="spin" /> : <BadgeCheck />}{busy ? 'Ratifying…' : authority ? 'Approve new version' : 'Create reference'}</button></footer>
    </form>
  </Dialog>
}

function lines(value: string) { return value.split(/\r?\n/).map(line => line.trim()).filter(Boolean) }
