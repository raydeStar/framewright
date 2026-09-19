import { useEffect, useMemo, useState, type DragEvent, type FormEvent } from 'react'
import { BadgeCheck, Clapperboard, ImagePlus, LoaderCircle, LockKeyhole, Plus, Sparkles, Upload, X } from 'lucide-react'
import { studioApi } from '../api'
import type { ReferenceSummary, ReferenceVersionSummary, ShotSummary } from '../types'
import Dialog from './Dialog'

const categories = ['Character', 'Role', 'Wardrobe', 'Pose', 'Location', 'Architecture', 'Prop', 'Style', 'World']

export function ShotEditorDialog({ shot, initialImage, references, suggestedCode, onClose, onSaved }: {
  shot?: ShotSummary
  initialImage?: File
  references: ReferenceSummary[]
  suggestedCode?: string
  onClose: () => void
  onSaved: (shot: ShotSummary) => void
}) {
  const [code, setCode] = useState(shot?.code ?? suggestedCode ?? '')
  const [title, setTitle] = useState(shot?.title ?? '')
  const [description, setDescription] = useState(shot?.description ?? '')
  const [durationFrames, setDurationFrames] = useState(shot?.durationFrames ?? 72)
  const [camera, setCamera] = useState(shot?.camera ?? 'Medium wide · eye level')
  const [action, setAction] = useState(shot?.action ?? '')
  const [referenceIds, setReferenceIds] = useState<string[]>(shot?.referenceIds ?? [])
  const [constraintsText, setConstraintsText] = useState((shot?.constraints ?? []).join('\n'))
  const [image, setImage] = useState<File | undefined>(initialImage)
  const [preview, setPreview] = useState<string>()
  const [plainDescription, setPlainDescription] = useState('')
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
  const fillWithCodex = async () => {
    if (!plainDescription.trim()) { setError('Describe the shot in a sentence or two first.'); return }
    setSuggesting(true); setError(undefined); setSuggestionDetail(undefined)
    try {
      const suggestion = await studioApi.suggestShotIntent(plainDescription.trim())
      setTitle(suggestion.title); setDescription(suggestion.description); setDurationFrames(suggestion.durationFrames)
      setCamera(suggestion.camera); setAction(suggestion.action); setReferenceIds(suggestion.referenceIds)
      setConstraintsText(suggestion.constraints.join('\n')); setSuggestionDetail(suggestion.detail)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Codex could not structure this shot.') }
    finally { setSuggesting(false) }
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true); setError(undefined)
    try {
      const asset = !shot && image ? await studioApi.uploadImage(image, image.name) : undefined
      const common = { title, description, durationFrames, camera, action, referenceIds, constraints: lines(constraintsText) }
      const saved = shot
        ? await studioApi.updateShot(shot.id, { expectedUpdatedAt: shot.updatedAt, ...common })
        : await studioApi.createShot({ code, ...common, initialImageAssetId: asset?.id })
      onSaved(saved)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'The shot could not be saved.') }
    finally { setBusy(false) }
  }

  return <Dialog className="modal-backdrop entity-backdrop" onClose={onClose} labelledBy="shot-editor-title" initialFocus={initialImage ? '#plain-shot-description' : '#shot-title'}>
    <form className="entity-dialog" onSubmit={submit}>
      <header><div className="entity-icon"><Clapperboard /></div><div><p className="eyebrow">{shot ? `${shot.code} · working intent` : 'New named slot'}</p><h2 id="shot-editor-title">{shot ? 'Edit shot intent' : 'Add shot card'}</h2></div><button type="button" className="icon-button" onClick={onClose} aria-label="Close shot editor"><X /></button></header>
      <div className="entity-form">
        {!shot && <label className={`shot-intake-image ${imageDragging ? 'dragging' : ''}`}
          onDragEnter={event => { event.preventDefault(); setImageDragging(true) }}
          onDragOver={event => { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' }}
          onDragLeave={event => { if (event.currentTarget.contains(event.relatedTarget as Node)) return; setImageDragging(false) }}
          onDrop={dropImage}>
          <input type="file" accept="image/png,image/jpeg" onChange={event => chooseImage(event.target.files?.[0])} />
          <span className="shot-intake-preview">{preview ? <img src={preview} alt="New shot draft preview" /> : <ImagePlus size={28} />}</span>
          <span><strong>{image ? image.name : 'Drop an image to start this shot'}</strong><small>{image ? 'This becomes the first draft, not a sketch.' : 'Or tap to choose a PNG or JPEG. Starting from text is fine too.'}</small></span>
          <Upload size={18} />
        </label>}
        {!shot && <section className="shot-intake-assistant" aria-labelledby="shot-intake-assistant-title">
          <div><Sparkles size={17} /><span><strong id="shot-intake-assistant-title">Tell Codex what this shot is</strong><small>Plain language in; editable production fields out.</small></span></div>
          <textarea id="plain-shot-description" value={plainDescription} onChange={event => setPlainDescription(event.target.value)} maxLength={5000} placeholder="Example: Ennix enters the chain court cautiously. Keep the three chains visible, use a tense medium-wide frame, and reference her approved flight coat." />
          <button type="button" className="secondary" onClick={() => void fillWithCodex()} disabled={suggesting || !plainDescription.trim()}>{suggesting ? <LoaderCircle className="spin" /> : <Sparkles />}{suggesting ? 'Structuring shot…' : 'Fill form with Codex'}</button>
          {suggestionDetail && <p role="status"><BadgeCheck size={14} />{suggestionDetail} Review anything below before adding the card.</p>}
        </section>}
        <div className="form-grid two">
          <label><span>Shot code</span><input value={code} onChange={event => setCode(event.target.value)} readOnly={Boolean(shot)} required placeholder="SH-070" /></label>
          <label><span>Duration in frames</span><input type="number" min={1} max={2400} value={durationFrames} onChange={event => setDurationFrames(Number(event.target.value))} required /></label>
        </div>
        <label><span>Title</span><input id="shot-title" value={title} onChange={event => setTitle(event.target.value)} required maxLength={120} placeholder="A clear, scannable shot name" /></label>
        <label><span>Description</span><textarea value={description} onChange={event => setDescription(event.target.value)} required maxLength={1200} placeholder="What is visible in this frame?" /></label>
        <label><span>Action</span><textarea value={action} onChange={event => setAction(event.target.value)} required maxLength={1200} placeholder="Describe the beat, movement, and intended change through the shot." /></label>
        <label><span>Camera</span><input value={camera} onChange={event => setCamera(event.target.value)} required maxLength={240} placeholder="Medium wide · low angle · 35 mm" /></label>
        <fieldset><legend>Authority packet <small>{referenceIds.length} selected</small></legend><div className="authority-picker">{references.map(reference => <label key={reference.id} className={referenceIds.includes(reference.id) ? 'selected' : ''}><input type="checkbox" checked={referenceIds.includes(reference.id)} onChange={() => setReferenceIds(current => current.includes(reference.id) ? current.filter(id => id !== reference.id) : [...current, reference.id])} /><span><strong>{reference.name}</strong><small>{reference.category} · v{reference.version}</small></span><BadgeCheck size={16} /></label>)}</div></fieldset>
        <label><span>Locked shot constraints <small>one per line</small></span><textarea value={constraintsText} onChange={event => setConstraintsText(event.target.value)} placeholder={'Ennix eyes remain amber\nKeep the sky bridge behind the Aerie'} /></label>
        {shot?.approval === 'Ratified' && <div className="form-warning"><LockKeyhole size={16} /><span><strong>Approved authority stays immutable.</strong> Saving creates a new working sketch version; the ratified version remains in history.</span></div>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>{busy ? <LoaderCircle className="spin" /> : shot ? <BadgeCheck /> : <Plus />}{busy ? 'Saving…' : shot ? 'Save new intent' : image ? 'Create image draft' : 'Add card'}</button></footer>
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
      .catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'Could not load authority history.') })
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
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'The authority could not be saved.') }
    finally { setBusy(false) }
  }

  return <Dialog className="modal-backdrop entity-backdrop" onClose={onClose} labelledBy="authority-editor-title" initialFocus="#authority-name">
    <form className="entity-dialog authority-editor" onSubmit={submit}>
      <header><div className="entity-icon authority"><LockKeyhole /></div><div><p className="eyebrow">{authority ? `${authority.name} · v${authority.version}` : 'Approved production reference'}</p><h2 id="authority-editor-title">{authority ? 'Create authority version' : 'Add authority'}</h2></div><button type="button" className="icon-button" onClick={onClose} aria-label="Close authority editor"><X /></button></header>
      <div className="entity-form">
        <div className="authority-import">
          <div className="authority-import-preview">{preview || authority?.imageUrl ? <img src={preview ?? authority?.imageUrl} alt="Authority preview" /> : <ImagePlus />}</div>
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
        {authority && <section className="authority-history" aria-labelledby="authority-history-title"><div><h3 id="authority-history-title">Approved history</h3><small>Read-only authority versions · newest first</small></div>{historyLoading ? <p className="authority-history-loading"><LoaderCircle className="spin" size={15} />Loading immutable versions…</p> : versions.map(version => <article key={version.id} className={version.version === authority.version ? 'current' : ''}>{version.imageUrl ? <img src={version.imageUrl} alt={`${authority.name} version ${version.version}`} /> : <div className="authority-history-placeholder"><ImagePlus size={16} /></div>}<span><strong>v{version.version}{version.version === authority.version ? ' · Current' : ''}</strong><small>{new Date(version.ratifiedAt).toLocaleDateString()}</small><p>{version.description}</p><em><LockKeyhole size={11} />{version.lockedConstraint}</em></span></article>)}</section>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button type="button" className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={busy}>{busy ? <LoaderCircle className="spin" /> : <BadgeCheck />}{busy ? 'Ratifying…' : authority ? 'Ratify new version' : 'Create authority'}</button></footer>
    </form>
  </Dialog>
}

function lines(value: string) { return value.split(/\r?\n/).map(line => line.trim()).filter(Boolean) }
