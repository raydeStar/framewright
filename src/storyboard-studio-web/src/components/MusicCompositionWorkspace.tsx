import { useCallback, useEffect, useMemo, useState } from 'react'
import { BadgeCheck, ChevronDown, CircleAlert, Code2, GitBranch, LoaderCircle, Music2, Play, Plus, Save, Sparkles, Trash2, WandSparkles } from 'lucide-react'
import { studioApi } from '../api'
import type { JobSummary, MusicCompositionDocument, MusicCompositionRevisionSummary, MusicCompositionSummary, MusicGenerationStatus, MusicSection } from '../types'

const DEFAULT_DESCRIPTION = 'Dark cinematic synthwave song about escaping a dying space station. Melancholic verses and a huge triumphant chorus.'
const DEFAULT_STYLE = 'dark cinematic synthwave, female vocal, melancholic verses, huge triumphant chorus'

function emptySection(index: number): MusicSection {
  return { id: `section-${index + 1}`, type: index ? 'chorus' : 'verse', bars: 8, lyrics: '', chords: [], melody: 'AI-planned melodic contour' }
}

function cloneDocument(value: MusicCompositionDocument): MusicCompositionDocument {
  return { ...value, sections: value.sections.map(section => ({ ...section, chords: [...section.chords] })) }
}

export default function MusicCompositionWorkspace({ jobs, framesPerSecond, onPlaced, onJobQueued }: { jobs: JobSummary[]; framesPerSecond: number; onPlaced: () => void; onJobQueued: (job: JobSummary) => void }) {
  const [status, setStatus] = useState<MusicGenerationStatus>()
  const [compositions, setCompositions] = useState<MusicCompositionSummary[]>([])
  const [selectedId, setSelectedId] = useState('')
  const [selectedRevisionId, setSelectedRevisionId] = useState('')
  const [title, setTitle] = useState('Untitled song')
  const [description, setDescription] = useState(DEFAULT_DESCRIPTION)
  const [style, setStyle] = useState(DEFAULT_STYLE)
  const [lyrics, setLyrics] = useState('')
  const [tempo, setTempo] = useState(108)
  const [meter, setMeter] = useState('4/4')
  const [songKey, setSongKey] = useState('A minor')
  const [performancePrompt, setPerformancePrompt] = useState('female vocal, cinematic synths, huge drums')
  const [document, setDocument] = useState<MusicCompositionDocument>()
  const [abc, setAbc] = useState('')
  const [editSummary, setEditSummary] = useState('Manual composition edit')
  const [agentEdit, setAgentEdit] = useState('')
  const [advanced, setAdvanced] = useState(false)
  const [busy, setBusy] = useState<'compose' | 'save' | 'edit' | 'render' | 'place'>()
  const [pendingJobId, setPendingJobId] = useState<string>()
  const [error, setError] = useState<string>()

  const selected = compositions.find(item => item.id === selectedId)
  const revision = selected?.revisions.find(item => item.id === selectedRevisionId)
    ?? selected?.revisions.find(item => item.id === selected.currentRevisionId)
    ?? selected?.revisions[0]

  const refresh = useCallback(async (preferredId?: string) => {
    const all = await studioApi.musicCompositions()
    const id = preferredId || selectedId || all[0]?.id || ''
    if (!id) { setCompositions([]); setSelectedId(''); return }
    const detail = await studioApi.musicComposition(id)
    setCompositions([detail, ...all.filter(item => item.id !== id)])
    setSelectedId(id)
  }, [selectedId])

  useEffect(() => {
    void Promise.all([studioApi.musicStatus(), studioApi.musicCompositions()]).then(([nextStatus, nextCompositions]) => {
      setStatus(nextStatus); setCompositions(nextCompositions); setSelectedId(nextCompositions[0]?.id ?? '')
    }).catch(problem => setError(problem instanceof Error ? problem.message : 'Music workspace could not load.'))
  }, [])

  useEffect(() => {
    if (!selected) return
    const current = selected.revisions.find(item => item.id === selected.currentRevisionId) ?? selected.revisions[0]
    if (!current) return
    setSelectedRevisionId(current.id)
  }, [selected])

  useEffect(() => {
    if (!revision) return
    setDocument(cloneDocument(revision.composition)); setAbc(revision.abcNotation); setEditSummary('Manual composition edit'); setAgentEdit('')
  }, [revision])

  useEffect(() => {
    if (!pendingJobId) return
    const job = jobs.find(item => item.id === pendingJobId)
    if (!job) return
    if (job.state === 'Completed') {
      void refresh(selectedId).finally(() => { setPendingJobId(undefined); setBusy(undefined) })
    } else if (job.state === 'Failed' || job.state === 'Cancelled') {
      setError(job.error || 'YuE2 rendering stopped.'); setPendingJobId(undefined); setBusy(undefined)
    }
  }, [jobs, pendingJobId, refresh, selectedId])

  const compose = async () => {
    if (!description.trim() || busy) return
    setBusy('compose'); setError(undefined)
    try {
      const created = await studioApi.createMusicComposition({ title, description, style, lyrics: lyrics || undefined, tempo, meter, key: songKey, performancePrompt })
      await refresh(created.id)
    } catch (problem) { setError(problem instanceof Error ? problem.message : 'Composition planning failed.') }
    finally { setBusy(undefined) }
  }

  const saveRevision = async () => {
    if (!selected || !revision || !document || busy) return
    setBusy('save'); setError(undefined)
    try {
      const updated = await studioApi.reviseMusicComposition(selected.id, { expectedRevisionId: selected.currentRevisionId, editSummary, composition: document, abcNotation: abc })
      setCompositions(current => [updated, ...current.filter(item => item.id !== updated.id)])
    } catch (problem) { setError(problem instanceof Error ? problem.message : 'The revision could not be saved.') }
    finally { setBusy(undefined) }
  }

  const editWithCodex = async () => {
    if (!selected || !agentEdit.trim() || busy) return
    setBusy('edit'); setError(undefined)
    try {
      const updated = await studioApi.editMusicComposition(selected.id, { expectedRevisionId: selected.currentRevisionId, instruction: agentEdit })
      setCompositions(current => [updated, ...current.filter(item => item.id !== updated.id)])
    } catch (problem) { setError(problem instanceof Error ? problem.message : 'Codex could not revise the composition.') }
    finally { setBusy(undefined) }
  }

  const render = async () => {
    if (!revision || busy) return
    setBusy('render'); setError(undefined)
    try {
      const job = await studioApi.renderMusicRevision(revision.id)
      setPendingJobId(job.id); onJobQueued(job)
    } catch (problem) { setError(problem instanceof Error ? problem.message : 'YuE2 could not queue this revision.'); setBusy(undefined) }
  }

  const place = async (rendered: MusicCompositionRevisionSummary['renders'][number]) => {
    setBusy('place'); setError(undefined)
    try {
      const assets = await studioApi.assets()
      const asset = assets.find(item => item.id === rendered.assetId)
      const durationFrames = Math.max(1, Math.round((asset?.durationSeconds ?? 30) * framesPerSecond))
      await studioApi.saveTimelineClip({ track: 'Music', label: `${selected?.title ?? 'Song'} · v${revision?.revisionNumber ?? 1}`, startFrame: 0, durationFrames, trimStartSeconds: 0, volume: 0.7, assetId: rendered.assetId, text: `YuE2 composition revision ${revision?.revisionNumber ?? 1}` })
      onPlaced()
    } catch (problem) { setError(problem instanceof Error ? problem.message : 'The render could not be placed on the Music lane.') }
    finally { setBusy(undefined) }
  }

  const updateSection = (index: number, next: Partial<MusicSection>) => {
    if (!document) return
    setDocument({ ...document, sections: document.sections.map((section, position) => position === index ? { ...section, ...next } : section) })
  }

  const renderJob = pendingJobId ? jobs.find(item => item.id === pendingJobId) : undefined
  const currentRenders = revision?.renders ?? []
  const canCompose = status?.canCompose === true
  const canRender = status?.canRender === true
  const revisionChanged = useMemo(() => revision && document
    ? JSON.stringify(document) !== JSON.stringify(revision.composition) || abc !== revision.abcNotation
    : false, [abc, document, revision])

  return <div className="music-composition-workspace" data-testid="music-composition-workspace">
    <aside className="music-composition-list">
      <button className={!selectedId ? 'active' : ''} onClick={() => setSelectedId('')}><Plus size={15} /><span><strong>New composition</strong><small>Intent → editable score</small></span></button>
      {compositions.map(item => <button key={item.id} className={selectedId === item.id ? 'active' : ''} onClick={() => setSelectedId(item.id)}><Music2 size={15} /><span><strong>{item.title}</strong><small>v{item.currentRevisionNumber} · {new Date(item.updatedAt).toLocaleDateString()}</small></span></button>)}
    </aside>

    <section className="music-composition-main">
      {!selectedId ? <>
        <div className="music-engine"><span><Music2 size={16} /><strong>YuE2 symbolic composition</strong></span><small>Compose → inspect/edit → render</small></div>
        <p className="asset-creation-note"><BadgeCheck size={15} />Codex proposes lyrics and structure; YuE2 plans editable melody and harmony as ABC before any audio render.</p>
        <label>Song title<input value={title} maxLength={160} onChange={event => setTitle(event.target.value)} /></label>
        <label>Song description<textarea rows={4} value={description} onChange={event => setDescription(event.target.value)} /></label>
        <label>Lyrics <small>optional — Codex can draft original lyrics</small><textarea rows={6} value={lyrics} onChange={event => setLyrics(event.target.value)} placeholder="[Verse]\nYour words here…" /></label>
        <div className="music-field-grid"><label>Key<input value={songKey} onChange={event => setSongKey(event.target.value)} /></label><label>Tempo<input type="number" min={30} max={300} value={tempo} onChange={event => setTempo(Number(event.target.value))} /></label><label>Meter<input value={meter} onChange={event => setMeter(event.target.value)} /></label></div>
        <label>Style / instrumentation<textarea rows={3} value={style} onChange={event => setStyle(event.target.value)} /></label>
        <label>Performance direction<textarea rows={2} value={performancePrompt} onChange={event => setPerformancePrompt(event.target.value)} /></label>
        <button className="primary music-primary-action" disabled={!canCompose || busy === 'compose' || !description.trim()} onClick={() => void compose()}>{busy === 'compose' ? <><LoaderCircle className="spin" />Planning score…</> : <><Sparkles />Compose editable song</>}</button>
      </> : selected && revision && document ? <>
        <div className="music-composition-head"><div><p className="eyebrow">Editable composition</p><h3>{selected.title}</h3></div><label>Revision<select value={revision.id} onChange={event => setSelectedRevisionId(event.target.value)}>{selected.revisions.map(item => <option key={item.id} value={item.id}>v{item.revisionNumber} · {item.editSummary}</option>)}</select></label></div>
        <div className="music-revision-line"><GitBranch size={15} /><span>v{revision.revisionNumber} is immutable. Saving creates v{selected.currentRevisionNumber + 1}; prior scores and renders remain available.</span></div>
        <div className="music-field-grid"><label>Title<input value={document.title} onChange={event => setDocument({ ...document, title: event.target.value })} /></label><label>Key<input value={document.key} onChange={event => setDocument({ ...document, key: event.target.value })} /></label><label>Tempo<input type="number" min={30} max={300} value={document.tempo} onChange={event => setDocument({ ...document, tempo: Number(event.target.value) })} /></label><label>Meter<input value={document.meter} onChange={event => setDocument({ ...document, meter: event.target.value })} /></label></div>
        <label>Style<textarea rows={2} value={document.style} onChange={event => setDocument({ ...document, style: event.target.value })} /></label>
        <label>Performance<textarea rows={2} value={document.performancePrompt} onChange={event => setDocument({ ...document, performancePrompt: event.target.value })} /></label>
        <div className="music-sections-head"><span><strong>Song structure</strong><small>{document.sections.length} section{document.sections.length === 1 ? '' : 's'}</small></span><button className="secondary compact" onClick={() => setDocument({ ...document, sections: [...document.sections, emptySection(document.sections.length)] })}><Plus size={14} />Section</button></div>
        <div className="music-section-editor">{document.sections.map((section, index) => <article key={`${section.id}-${index}`}><header><input aria-label={`Section ${index + 1} type`} value={section.type} onChange={event => updateSection(index, { type: event.target.value })} /><label>Bars<input type="number" min={1} max={128} value={section.bars} onChange={event => updateSection(index, { bars: Number(event.target.value) })} /></label><button aria-label={`Remove ${section.type}`} disabled={document.sections.length === 1} onClick={() => setDocument({ ...document, sections: document.sections.filter((_, position) => position !== index) })}><Trash2 size={14} /></button></header><label>Lyrics<textarea rows={4} value={section.lyrics} onChange={event => updateSection(index, { lyrics: event.target.value })} /></label><label>Chords <small>comma separated · applied to ABC on save</small><input value={section.chords.join(', ')} onChange={event => updateSection(index, { chords: event.target.value.split(',').map(item => item.trim()).filter(Boolean) })} /></label><label>Melody intent <small>descriptive — use Codex or Advanced ABC to change performed notes</small><textarea rows={2} value={section.melody} onChange={event => updateSection(index, { melody: event.target.value })} /></label></article>)}</div>
        <button className="music-advanced-toggle" onClick={() => setAdvanced(value => !value)}><Code2 size={15} />Advanced ABC score <ChevronDown size={14} className={advanced ? 'open' : ''} /></button>
        {advanced && <label className="music-abc-editor">ABC notation<textarea spellCheck={false} rows={16} value={abc} onChange={event => setAbc(event.target.value)} /><small>YuE2 uses this exact score on the next render. Invalid notation fails visibly; Framewright does not repair it behind your back.</small></label>}
        <div className="music-save-row"><label>Revision note<input value={editSummary} maxLength={300} onChange={event => setEditSummary(event.target.value)} /></label><button className="secondary" disabled={!revisionChanged || !editSummary.trim() || busy === 'save'} onClick={() => void saveRevision()}>{busy === 'save' ? <LoaderCircle className="spin" /> : <Save />}Save new revision</button></div>
        <div className="music-agent-edit"><WandSparkles size={18} /><label>Ask Codex for a bounded edit<textarea rows={2} value={agentEdit} onChange={event => setAgentEdit(event.target.value)} placeholder="Keep everything else the same but change the chorus chord progression." /></label><button className="secondary" disabled={!agentEdit.trim() || busy === 'edit' || revision.id !== selected.currentRevisionId} onClick={() => void editWithCodex()}>{busy === 'edit' ? 'Revising…' : 'Create revision'}</button></div>
        <button className="primary music-primary-action" disabled={!canRender || busy === 'render' || revisionChanged} onClick={() => void render()}>{busy === 'render' ? <><LoaderCircle className="spin" />{renderJob?.phase ?? 'Queued safely'}</> : <><Play />Render v{revision.revisionNumber} with YuE2</>}</button>
        {revisionChanged && <p className="music-protected"><CircleAlert size={14} />Save the edited score as a new revision before rendering.</p>}
        <div className="music-render-list"><strong>Renders for v{revision.revisionNumber}</strong>{currentRenders.length === 0 ? <p>No renders yet. The score remains useful and editable without audio.</p> : currentRenders.map(item => <article key={item.id}><audio controls preload="metadata" src={item.assetUrl} /><span><small>{item.renderer} · {new Date(item.createdAt).toLocaleString()}</small><code>{item.assetId.slice(0, 12)}…</code></span><button className="secondary compact" disabled={busy === 'place'} onClick={() => void place(item)}>Add to Music lane</button></article>)}</div>
      </> : null}
      {status && !status.canCompose && <p className="music-protected"><CircleAlert size={14} />{status.detail}</p>}
      {error && <p className="form-error" role="alert">{error}</p>}
    </section>
  </div>
}
