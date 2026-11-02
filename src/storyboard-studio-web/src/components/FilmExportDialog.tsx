import { useEffect, useState } from 'react'
import { CircleAlert, Clapperboard, Download, Film, Image, LoaderCircle, Square } from 'lucide-react'
import { studioApi } from '../api'
import type { FilmExportRequest, FilmExportStatus, FilmShotSource, JobSummary, StudioSnapshot } from '../types'
import Dialog from './Dialog'

const SOURCE_LABELS: Record<FilmShotSource['source'], string> = { Production: 'Max take', Take: 'Video take', Still: 'Still held', Missing: 'No picture' }

/**
 * Renders the sequence as one finished film. The dialog says, shot by shot,
 * which picture will be used before anything is rendered; the render itself
 * is an ordinary durable job, so it keeps going if the dialog is closed.
 */
export default function FilmExportDialog({ studio, onClose, onJobQueued }: { studio: StudioSnapshot; onClose: () => void; onJobQueued: (job: JobSummary) => void }) {
  const [status, setStatus] = useState<FilmExportStatus>()
  const [options, setOptions] = useState<FilmExportRequest>({ fades: true, captions: true, normalizeLoudness: true })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  // The snapshot is replaced on every poll while a job runs; ask again only when the cut or its takes change.
  const takesDone = studio.jobs.filter(job => job.workType === 'Shot' && job.state === 'Completed').length
  const cutKey = `${takesDone}|${studio.shots.map(shot => `${shot.id}:${shot.version}:${shot.updatedAt}:${shot.durationFrames}:${shot.productionVideoAssetId ?? ''}`).join('|')}`
  useEffect(() => { void studioApi.filmExportStatus().then(setStatus).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not check the film.')) }, [cutKey])
  const films = studio.jobs.filter(job => job.workType === 'Film').sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  const latest = films[0]
  const rendering = latest && (latest.state === 'Queued' || latest.state === 'Running')
  const finished = films.find(job => job.state === 'Completed' && job.outputAssetUrl)
  const seconds = status ? status.totalFrames / status.framesPerSecond : 0
  const render = async () => {
    setBusy(true); setError(undefined)
    try { onJobQueued(await studioApi.renderFilm(options)) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not start the film render.') }
    finally { setBusy(false) }
  }
  const option = (key: keyof FilmExportRequest, label: string, detail: string) => <label className="film-option"><input type="checkbox" checked={options[key]} onChange={event => setOptions(current => ({ ...current, [key]: event.target.checked }))} /><span><strong>{label}</strong><small>{detail}</small></span></label>

  return <Dialog className="edit-dialog film-export-dialog" onClose={onClose} labelledBy="film-export-title">
    <header><div><p className="eyebrow">Finished cut</p><h2 id="film-export-title">Export film</h2></div><button onClick={onClose} aria-label="Close film export">×</button></header>
    <div className="edit-dialog-body">
      {status ? <p className="film-summary"><Clapperboard size={16} /><span><strong>{studio.project.sequenceCode} · {studio.project.sequenceName}</strong><small>{seconds.toFixed(1)} s · {status.deliveryWidth} × {status.deliveryHeight} · {status.framesPerSecond} fps · {status.audioClips} audio {status.audioClips === 1 ? 'clip' : 'clips'} · {status.captions} {status.captions === 1 ? 'caption' : 'captions'}</small></span></p>
        : !error && <p className="film-summary"><LoaderCircle className="spin" size={16} /><span><strong>Checking the sequence</strong><small>Finding the best picture for every shot.</small></span></p>}
      {status && <ol className="film-sources" aria-label="Picture used for each shot">
        {status.shots.map(shot => <li key={shot.shotId} className={`source-${shot.source.toLowerCase()}`}>
          <span className="film-source-icon" aria-hidden="true">{shot.source === 'Production' || shot.source === 'Take' ? <Film size={14} /> : shot.source === 'Still' ? <Image size={14} /> : <Square size={14} />}</span>
          <span><strong>{shot.code} · {shot.title}</strong><small>{shot.detail}</small></span>
          <em>{SOURCE_LABELS[shot.source]}</em>
        </li>)}
      </ol>}
      <div className="film-options">
        {option('fades', 'Fade in and out', 'Half a second from black at the start, back to black at the end.')}
        {option('captions', 'English captions', 'A soft subtitle track from the dialogue clips’ text, switchable in any player.')}
        {option('normalizeLoudness', 'Normalize loudness', 'Measures the mix and sets it to −16 LUFS, keeping its dynamics.')}
      </div>
      {status && <p className={`film-readiness ${status.canRender ? '' : 'is-blocked'}`} role="status">{status.canRender ? null : <CircleAlert size={14} />}{status.detail}</p>}
      {latest && (rendering || latest.state === 'Failed') && <div className={`film-latest state-${latest.state.toLowerCase()}`} role="status" data-testid="film-latest">
        {rendering ? <><LoaderCircle className="spin" size={15} /><span><strong>Rendering the film · {latest.progress}%</strong><small>{latest.phase}. It keeps going if you close this.</small></span></>
          : latest.state === 'Failed' ? <><CircleAlert size={15} /><span><strong>The last render failed</strong><small>{latest.error ?? 'The renderer did not finish.'} Retry it from the work queue, or render again.</small></span></> : null}
      </div>}
      {finished && <div className="film-ready" data-testid="film-ready"><Film size={15} /><span><strong>Film ready</strong><small>Rendered {new Date(finished.completedAt ?? finished.createdAt).toLocaleString()} · also in the Asset library</small></span><a className="primary compact" href={finished.outputAssetUrl} download={`${studio.project.sequenceCode} ${studio.project.sequenceName} film.mp4`}><Download size={15} />Download film</a></div>}
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
    <footer><span /><span /><button className="secondary" onClick={onClose}>Close</button><button className="primary" disabled={busy || !status?.canRender || Boolean(rendering)} onClick={() => void render()}>{busy ? <><LoaderCircle className="spin" size={16} />Starting…</> : rendering ? 'Rendering…' : <><Film size={16} />{finished ? 'Render again' : 'Render film'}</>}</button></footer>
  </Dialog>
}
