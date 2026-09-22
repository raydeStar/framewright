import { useCallback, useEffect, useRef, useState } from 'react'
import { BadgeCheck, Check, ChevronDown, CircleAlert, FolderPlus, LoaderCircle, Plus, Sparkles, Trash2, Upload, X } from 'lucide-react'
import { ApiError, studioApi } from '../api'
import Dialog from './Dialog'
import type { ProjectInterviewProposal, ProjectListItem, ProjectSummary } from '../types'

/**
 * The context bar's project identity, turned into a switcher.
 *
 * Switching projects replaces every entity on screen, so this deliberately does
 * not behave like a filter: it closes, reloads the whole snapshot, and reports
 * what it landed in. The counts come from the server because they are the only
 * read that legitimately crosses the project scope.
 */
export default function ProjectSwitcher({ project, onSwitched, onError }: {
  project: ProjectSummary
  onSwitched: (name: string) => void
  onError: (message: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [projects, setProjects] = useState<ProjectListItem[]>()
  const [busyId, setBusyId] = useState<string>()
  const [creating, setCreating] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState<ProjectListItem>()
  const trigger = useRef<HTMLButtonElement>(null)
  const packageInput = useRef<HTMLInputElement>(null)

  const load = useCallback(async () => {
    try { setProjects(await studioApi.projects()) }
    catch (reason) { onError(reason instanceof Error ? reason.message : 'Could not list projects.') }
  }, [onError])
  // Re-read on every open rather than caching: the counts go stale as soon as a
  // shot or authority is added, and a switcher showing yesterday's numbers is
  // worse than one that takes a moment.
  useEffect(() => { if (open) void load() }, [load, open])

  const activate = async (target: ProjectListItem) => {
    if (target.isActive) { setOpen(false); return }
    setBusyId(target.id)
    try {
      const switched = await studioApi.activateProject(target.id)
      setOpen(false)
      onSwitched(switched.name)
    } catch (reason) { onError(reason instanceof Error ? reason.message : 'Could not switch project.') }
    finally { setBusyId(undefined) }
  }

  const importPackage = async (file?: File) => {
    if (!file || busyId) return
    setBusyId('import')
    try {
      const imported = await studioApi.importProject(file)
      setOpen(false); setProjects(undefined)
      onSwitched(`${imported.name} imported with ${imported.assetCount} assets and ${imported.sceneCount} scenes. Switch to it when you are ready.`)
    } catch (reason) { onError(reason instanceof Error ? reason.message : 'Could not import the project package.') }
    finally {
      setBusyId(undefined)
      if (packageInput.current) packageInput.current.value = ''
    }
  }

  return <div className="project-identity">
    <button
      ref={trigger}
      type="button"
      className="project-switch"
      aria-label="Switch project"
      aria-haspopup="menu"
      aria-expanded={open}
      onClick={() => setOpen(current => !current)}
      data-testid="project-switcher">
      <div>
        <p>{project.production}</p>
        <strong>{project.name}</strong>
      </div>
      <ChevronDown size={15} />
    </button>

    {open && <>
      <div className="project-menu-scrim" onClick={() => setOpen(false)} aria-hidden="true" />
      <div className="project-menu" role="menu" data-testid="project-menu">
        <p className="project-menu-label">Projects</p>
        {!projects && <div className="project-menu-loading"><LoaderCircle className="spin" size={15} />Reading projects…</div>}
        {projects?.map(item => <div key={item.id} className={`project-menu-row ${item.isActive ? 'is-active' : ''}`}>
          <button
            type="button"
            role="menuitemradio"
            aria-checked={item.isActive}
            disabled={Boolean(busyId)}
            onClick={() => void activate(item)}>
            <span className="project-menu-check">{item.isActive ? <Check size={15} /> : busyId === item.id ? <LoaderCircle className="spin" size={15} /> : null}</span>
            <span className="project-menu-copy">
              <strong>{item.name}</strong>
              <small>{item.production} · {item.sequenceCode}</small>
            </span>
            <span className="project-menu-counts">
              <em>{item.shotCount}</em> shots
              <i />
              <em>{item.authorityCount}</em> authorities
            </span>
          </button>
          {/* Deleting the active project is refused by the server; hiding the
              control here means the artist never aims at a dead end. */}
          {!item.isActive && <button
            type="button"
            className="project-menu-delete"
            aria-label={`Delete ${item.name}`}
            disabled={Boolean(busyId)}
            onClick={() => { setConfirmDelete(item); setOpen(false) }}><Trash2 size={14} /></button>}
        </div>)}
        <div className="project-menu-foot">
          <button type="button" role="menuitem" onClick={() => { setOpen(false); setCreating(true) }}><Plus size={15} />New project</button>
          <button type="button" role="menuitem" disabled={Boolean(busyId)} onClick={() => packageInput.current?.click()}>
            {busyId === 'import' ? <LoaderCircle className="spin" size={15} /> : <Upload size={15} />}{busyId === 'import' ? 'Checking package…' : 'Import package'}
          </button>
          <input ref={packageInput} type="file" accept=".zip,application/zip" hidden onChange={event => void importPackage(event.target.files?.[0])} />
        </div>
      </div>
    </>}

    {creating && <CreateProjectDialog
      onClose={() => { setCreating(false); trigger.current?.focus() }}
      onCreated={name => { setCreating(false); setProjects(undefined); onSwitched(name) }}
      onError={onError} />}

    {confirmDelete && <DeleteProjectDialog
      project={confirmDelete}
      onClose={() => { setConfirmDelete(undefined); trigger.current?.focus() }}
      onDeleted={message => { setConfirmDelete(undefined); setProjects(undefined); onSwitched(message) }}
      onError={onError} />}
  </div>
}

/**
 * A new project starts from the studio defaults for style and canon; those are
 * refined in World afterwards. Asking for four essays before the first shot
 * exists would be the wrong order of work.
 */
function CreateProjectDialog({ onClose, onCreated, onError }: { onClose: () => void; onCreated: (message: string) => void; onError: (message: string) => void }) {
  const [name, setName] = useState('')
  const [production, setProduction] = useState('')
  const [sequenceCode, setSequenceCode] = useState('SQ-01')
  const [sequenceName, setSequenceName] = useState('')
  const [framesPerSecond, setFramesPerSecond] = useState(24)
  const [aspectRatio, setAspectRatio] = useState('2.39:1')
  const [deliveryWidth, setDeliveryWidth] = useState(2304)
  const [deliveryHeight, setDeliveryHeight] = useState(960)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string>()
  const [interviewOpen, setInterviewOpen] = useState(false)
  const [proposal, setProposal] = useState<ProjectInterviewProposal>()
  const [world, setWorld] = useState<{ visualStyle: string; worldCanon: string; promptDirectives: string; negativeDirectives: string }>()

  const valid = name.trim() && production.trim() && sequenceCode.trim() && sequenceName.trim()
    && framesPerSecond >= 1 && framesPerSecond <= 120
    && deliveryWidth >= 320 && deliveryHeight >= 180 && deliveryWidth % 2 === 0 && deliveryHeight % 2 === 0

  const create = async () => {
    if (!valid || busy) return
    setBusy(true); setFailure(undefined)
    try {
      const created = await studioApi.createProject({ name: name.trim(), production: production.trim(), sequenceCode: sequenceCode.trim(), sequenceName: sequenceName.trim(), framesPerSecond, aspectRatio: aspectRatio.trim(), deliveryWidth, deliveryHeight, ...world })
      // Deliberately not activated: a new project is empty, and replacing the
      // board the artist is working on is not what "create" asked for.
      onCreated(`${created.name} created. Switch to it from the project menu when you are ready.`)
    } catch (reason) {
      if (reason instanceof ApiError) setFailure(reason.message)
      else onError(reason instanceof Error ? reason.message : 'Could not create the project.')
    } finally { setBusy(false) }
  }

  return <Dialog className="modal-backdrop" onClose={onClose} labelledBy="create-project-title" initialFocus="input">
    <form className="create-project" onSubmit={event => { event.preventDefault(); void create() }} data-testid="create-project">
      <header>
        <div><p className="eyebrow">New production</p><h2 id="create-project-title">Create a project</h2></div>
        <button type="button" onClick={onClose} aria-label="Close create project"><X /></button>
      </header>
      {/* The interview is an optional way to fill this form, never a separate path
          that writes on its own: whatever Codex proposes lands in these same
          editable fields and is created by the same button. */}
      {!interviewOpen && !proposal && <button type="button" className="interview-invite" onClick={() => setInterviewOpen(true)}>
        <Sparkles size={16} />
        <span><strong>Describe it instead</strong><small>Answer four questions and let Codex propose the format, style, and world canon.</small></span>
      </button>}
      {interviewOpen && <ProjectInterviewPanel
        onCancel={() => setInterviewOpen(false)}
        onError={onError}
        onProposed={next => {
          setProposal(next)
          setInterviewOpen(false)
          // Fill the form rather than saving. The artist edits from here.
          setName(next.name); setProduction(next.production)
          setSequenceCode(next.sequenceCode); setSequenceName(next.sequenceName)
          setFramesPerSecond(next.framesPerSecond); setAspectRatio(next.aspectRatio)
          setDeliveryWidth(next.deliveryWidth); setDeliveryHeight(next.deliveryHeight)
          setWorld({ visualStyle: next.visualStyle, worldCanon: next.worldCanon, promptDirectives: next.promptDirectives, negativeDirectives: next.negativeDirectives })
        }} />}
      {proposal && <div className="interview-proposal" data-testid="interview-proposal">
        <div className="interview-proposal-head">
          <BadgeCheck size={15} />
          <strong>{proposal.live ? 'Codex proposal — review before creating' : 'Editable defaults — review before creating'}</strong>
          <button type="button" onClick={() => { setProposal(undefined); setWorld(undefined) }} aria-label="Discard proposal"><X size={14} /></button>
        </div>
        <p>{proposal.rationale}</p>
        <small>{proposal.detail}</small>
        {proposal.starterAuthorities.length > 0 && <ul className="interview-authorities">
          {proposal.starterAuthorities.map(authority => <li key={authority.name}>
            <span className="interview-authority-swatch" style={{ background: authority.accent }} />
            <span><strong>{authority.name}</strong><small>{authority.category} · {authority.lockedConstraint}</small></span>
          </li>)}
          <li className="interview-authorities-note">Starter authorities are not created with the project. Add the ones you want from the board once it exists.</li>
        </ul>}
      </div>}
      <div className="form-grid">
        <label>Project name<input value={name} onChange={event => setName(event.target.value)} placeholder="The Aerie Sequence" /></label>
        <label>Production<input value={production} onChange={event => setProduction(event.target.value)} placeholder="Skychasers - local production" /></label>
        <label>Sequence code<input value={sequenceCode} onChange={event => setSequenceCode(event.target.value)} /></label>
        <label>Sequence name<input value={sequenceName} onChange={event => setSequenceName(event.target.value)} placeholder="The Court Above" /></label>
        <label>Frame rate<input type="number" min={1} max={120} value={framesPerSecond} onChange={event => setFramesPerSecond(Number(event.target.value))} /></label>
        <label>Aspect ratio<input value={aspectRatio} onChange={event => setAspectRatio(event.target.value)} /></label>
        {/* Finished delivery dimensions only need codec-safe even values. Model
            proxy canvases are normalized independently by their workflows. */}
        <label>Delivery width<input type="number" min={320} max={7680} step={2} value={deliveryWidth} onChange={event => setDeliveryWidth(Number(event.target.value))} /></label>
        <label>Delivery height<input type="number" min={180} max={4320} step={2} value={deliveryHeight} onChange={event => setDeliveryHeight(Number(event.target.value))} /></label>
      </div>
      <p className="create-project-note">Delivery dimensions must be even numbers and match the named aspect ratio within 1%. The project starts in Rec.709 with a 48 kHz audio master; adjust the delivery preset later in Production setup.</p>
      {failure && <p className="form-error" role="alert"><CircleAlert size={15} />{failure}</p>}
      <footer>
        <button type="button" className="secondary" onClick={onClose}>Cancel</button>
        <button className="primary" disabled={!valid || busy}><FolderPlus size={16} />{busy ? 'Creating…' : 'Create project'}</button>
      </footer>
    </form>
  </Dialog>
}

/**
 * The four interview questions.
 *
 * Plain language on purpose: the artist describes the piece, and Codex maps that
 * onto the contract fields. Asking them to pick a delivery canvas before they have
 * described the work is the wrong order.
 */
function ProjectInterviewPanel({ onProposed, onCancel, onError }: { onProposed: (proposal: ProjectInterviewProposal) => void; onCancel: () => void; onError: (message: string) => void }) {
  const [kind, setKind] = useState('')
  const [look, setLook] = useState('')
  const [cast, setCast] = useState('')
  const [locked, setLocked] = useState('')
  const [busy, setBusy] = useState(false)
  const answered = [kind, look, cast, locked].some(value => value.trim().length > 0)

  const ask = async () => {
    if (!answered || busy) return
    setBusy(true)
    try { onProposed(await studioApi.projectInterview({ kind: kind.trim(), look: look.trim(), cast: cast.trim(), locked: locked.trim() })) }
    catch (reason) { onError(reason instanceof Error ? reason.message : 'Codex could not propose a project.') }
    finally { setBusy(false) }
  }

  return <section className="project-interview" data-testid="project-interview">
    <div className="project-interview-head">
      <div className="codex-orb"><Sparkles /></div>
      <div><strong>Describe the piece</strong><small>Nothing is saved. Codex proposes an editable contract and you decide what to keep.</small></div>
    </div>
    <label>What is this?<textarea rows={2} value={kind} onChange={event => setKind(event.target.value)} maxLength={600} placeholder="A thirty second launch spot. A short. An episode." /></label>
    <label>What does it look like?<textarea rows={2} value={look} onChange={event => setLook(event.target.value)} maxLength={600} placeholder="Painterly cel animation, restrained palette, heavy sea haze." /></label>
    <label>Who or what is in it?<textarea rows={2} value={cast} onChange={event => setCast(event.target.value)} maxLength={600} placeholder="Two deck officers and the airship itself." /></label>
    <label>Anything already locked?<textarea rows={2} value={locked} onChange={event => setLocked(event.target.value)} maxLength={600} placeholder="The hull registration must always read HV-9." /></label>
    <div className="project-interview-actions">
      <button type="button" className="secondary compact" onClick={onCancel} disabled={busy}>Fill it in myself</button>
      <button type="button" className="primary compact" onClick={() => void ask()} disabled={!answered || busy}>
        {busy ? <LoaderCircle className="spin" size={15} /> : <Sparkles size={15} />}{busy ? 'Asking Codex…' : 'Propose a project'}
      </button>
    </div>
  </section>
}

/**
 * Project deletion destroys approval evidence, so it asks twice in the way the
 * shot deletion does: the count is shown first, and ratified versions require a
 * second explicit acceptance rather than a bolder button.
 */
function DeleteProjectDialog({ project, onClose, onDeleted, onError }: { project: ProjectListItem; onClose: () => void; onDeleted: (message: string) => void; onError: (message: string) => void }) {
  const [acceptRatifiedLoss, setAcceptRatifiedLoss] = useState(false)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string>()
  const [confirmName, setConfirmName] = useState('')

  const remove = async () => {
    if (busy || confirmName.trim() !== project.name) return
    setBusy(true); setFailure(undefined)
    try {
      const summary = await studioApi.deleteProject(project.id, acceptRatifiedLoss)
      onDeleted(`${summary.name} deleted: ${summary.shots} shot${summary.shots === 1 ? '' : 's'}, ${summary.authorities} authorit${summary.authorities === 1 ? 'y' : 'ies'}, and ${summary.ratifiedVersions} ratified version${summary.ratifiedVersions === 1 ? '' : 's'}.`)
    } catch (reason) {
      if (reason instanceof ApiError) setFailure(reason.message)
      else onError(reason instanceof Error ? reason.message : 'Could not delete the project.')
    } finally { setBusy(false) }
  }

  return <Dialog className="modal-backdrop" onClose={onClose} labelledBy="delete-project-title" initialFocus="input">
    <form className="delete-project" onSubmit={event => { event.preventDefault(); void remove() }} data-testid="delete-project">
      <header>
        <div><p className="eyebrow">Permanent</p><h2 id="delete-project-title">Delete {project.name}?</h2></div>
        <button type="button" onClick={onClose} aria-label="Close delete project"><X /></button>
      </header>
      <p className="delete-project-scope">
        This removes <strong>{project.shotCount} shot{project.shotCount === 1 ? '' : 's'}</strong> and <strong>{project.authorityCount} authorit{project.authorityCount === 1 ? 'y' : 'ies'}</strong> with every candidate, comment, manifest, and ratified version scoped to this project.
      </p>
      <p className="delete-project-note">Stored asset files are kept: the asset store is content-addressed and shared, so another project may hold the same bytes. The audit trail is also kept, as the record that this project existed.</p>
      <label className="delete-project-confirm">Type <strong>{project.name}</strong> to confirm
        <input value={confirmName} onChange={event => setConfirmName(event.target.value)} placeholder={project.name} />
      </label>
      <label className="delete-project-accept">
        <input type="checkbox" checked={acceptRatifiedLoss} onChange={event => setAcceptRatifiedLoss(event.target.checked)} />
        <span>I accept the loss of ratified approval evidence in this project.</span>
      </label>
      {failure && <p className="form-error" role="alert"><CircleAlert size={15} />{failure}</p>}
      <footer>
        <button type="button" className="secondary" onClick={onClose}>Keep project</button>
        <button className="danger" disabled={busy || confirmName.trim() !== project.name}><Trash2 size={16} />{busy ? 'Deleting…' : 'Delete permanently'}</button>
      </footer>
    </form>
  </Dialog>
}
