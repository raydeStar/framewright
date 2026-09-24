import { useCallback, useEffect, useMemo, useState } from 'react'
import { ArrowDownToLine, BadgeCheck, CircleAlert, Library, LoaderCircle, RefreshCw, Search, X } from 'lucide-react'
import { ApiError, studioApi } from '../api'
import Dialog from './Dialog'
import type { LibraryAuthoritySummary } from '../types'

/**
 * The cross-project authority library.
 *
 * Every row states this project's relationship to the entry, because that is the
 * only thing the artist needs to decide from: not here yet, here and current, or
 * here but behind. An import is a copy — the wording throughout says so, since the
 * one thing that must never be assumed is that editing the library reaches into a
 * production that already took a version from it.
 */
export default function AuthorityLibraryDialog({ onClose, onChanged, onError }: {
  onClose: () => void
  onChanged: (message: string) => void
  onError: (message: string) => void
}) {
  const [entries, setEntries] = useState<LibraryAuthoritySummary[]>()
  const [busyId, setBusyId] = useState<string>()
  const [failure, setFailure] = useState<string>()
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState('All')
  const [show, setShow] = useState<'available' | 'all'>('available')

  const load = useCallback(async () => {
    try { setEntries(await studioApi.library()); setFailure(undefined) }
    catch (reason) { onError(reason instanceof Error ? reason.message : 'Could not read the reference library.') }
  }, [onError])
  useEffect(() => { void load() }, [load])

  const categories = useMemo(() => ['All', ...Array.from(new Set((entries ?? []).map(entry => entry.category))).sort()], [entries])
  const visibleEntries = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase()
    return (entries ?? [])
      .filter(entry => show === 'all' || !entry.importedAsReferenceId || entry.updateAvailable)
      .filter(entry => category === 'All' || entry.category === category)
      .filter(entry => !needle || `${entry.name} ${entry.category} ${entry.description} ${entry.lockedConstraint}`.toLocaleLowerCase().includes(needle))
      .sort((left, right) => Number(Boolean(left.importedAsReferenceId)) - Number(Boolean(right.importedAsReferenceId)) || left.name.localeCompare(right.name))
  }, [category, entries, query, show])

  const act = async (entry: LibraryAuthoritySummary, action: 'import' | 'pull') => {
    if (busyId) return
    setBusyId(entry.id); setFailure(undefined)
    try {
      if (action === 'import') {
        const imported = await studioApi.importLibraryAuthority(entry.id)
        onChanged(`${imported.name} imported from the library as a project reference at v${imported.version}.`)
      } else if (action === 'pull') {
        const pulled = await studioApi.pullLibraryUpdate(entry.importedAsReferenceId!)
        onChanged(`${pulled.name} updated to v${pulled.version} from library v${entry.version}. Earlier versions are unchanged.`)
      }
      await load()
    } catch (reason) {
      if (reason instanceof ApiError) setFailure(reason.message)
      else onError(reason instanceof Error ? reason.message : 'The library action failed.')
    } finally { setBusyId(undefined) }
  }

  return <Dialog className="modal-backdrop" onClose={onClose} labelledBy="library-title" initialFocus="[data-dialog-focus]">
    <section className="authority-library" data-testid="authority-library">
      <header>
        <div>
          <p className="eyebrow">Global library → current project</p>
          <h2 id="library-title">Import references</h2>
          <p>Choose only what this production needs. Imported references become project-local copies with exact version provenance; the wider library stays out of shot controls.</p>
        </div>
        <button type="button" onClick={onClose} aria-label="Close reference library" data-dialog-focus><X /></button>
      </header>

      <div className="library-scroll">
        {!entries && <div className="library-loading"><LoaderCircle className="spin" size={16} />Reading the library…</div>}
        {entries && entries.length > 0 && <div className="library-filters" aria-label="Filter reference library">
          <label className="library-search"><Search size={15} /><input aria-label="Search global references" value={query} onChange={event => setQuery(event.target.value)} placeholder="Search people, wardrobe, places, props…" /></label>
          <select aria-label="Reference category" value={category} onChange={event => setCategory(event.target.value)}>{categories.map(value => <option key={value}>{value}</option>)}</select>
          <div className="library-scope" role="group" aria-label="Reference availability"><button type="button" aria-pressed={show === 'available'} className={show === 'available' ? 'active' : ''} onClick={() => setShow('available')}>Available</button><button type="button" aria-pressed={show === 'all'} className={show === 'all' ? 'active' : ''} onClick={() => setShow('all')}>All</button></div>
        </div>}
        {entries?.length === 0 && <div className="library-empty">
          <Library />
          <h3>The library is empty</h3>
          <p>Open a project reference and choose <strong>Promote to library</strong> when it is ready for reuse elsewhere.</p>
        </div>}
        {entries && entries.length > 0 && visibleEntries.length === 0 && <div className="library-empty"><Search /><h3>No matching references</h3><p>Try another search or switch to <strong>All</strong> to include references already in this project.</p></div>}
        {visibleEntries.map(entry => {
          const busy = busyId === entry.id
          return <article key={entry.id} className={`library-card ${entry.updateAvailable ? 'has-update' : ''}`}>
            <div className="library-thumb" style={{ '--accent': entry.accent } as React.CSSProperties}>
              {/* Not lazy: these are 74px thumbnails in a short list, so deferring
                  them buys nothing, and lazy loading inside a top-layer dialog does
                  not reliably trigger — the images simply never fetched. */}
              {entry.imageUrl
                ? <img src={entry.imageUrl} alt={`${entry.name} library reference`} />
                : <span>{entry.name.slice(0, 1)}</span>}
            </div>
            <div className="library-copy">
              <div className="library-card-head">
                <strong>{entry.name}</strong>
                <span className="library-category">{entry.category}</span>
                <em>Library v{entry.version}</em>
              </div>
              <p>{entry.description}</p>
              <small className="library-lock"><BadgeCheck size={12} />{entry.lockedConstraint}</small>
              {entry.importedAsReferenceId && <small className="library-provenance">
                In this project as <code>{entry.importedAsReferenceId}</code>, taken from library v{entry.importedVersion}.
                {entry.updateAvailable
                  ? ` The library has moved on to v${entry.version}.`
                  : ' Up to date.'}
              </small>}
            </div>
            <div className="library-actions">
              {!entry.importedAsReferenceId && <button type="button" className="primary compact" disabled={busy} onClick={() => void act(entry, 'import')}>
                {busy ? <LoaderCircle className="spin" size={14} /> : <ArrowDownToLine size={14} />}Add to project
              </button>}
              {entry.updateAvailable && <button type="button" className="primary compact" disabled={busy} onClick={() => void act(entry, 'pull')}>
                {busy ? <LoaderCircle className="spin" size={14} /> : <RefreshCw size={14} />}Pull v{entry.version}
              </button>}
              {entry.importedAsReferenceId && !entry.updateAvailable && <span className="library-current"><BadgeCheck size={14} />In this project</span>}
            </div>
          </article>
        })}
        {failure && <p className="form-error" role="alert"><CircleAlert size={15} />{failure}</p>}
      </div>

      <footer>
        <small>Only project-local references appear in shot reference pickers. Pulling an update appends a new local version; earlier generations remain reproducible.</small>
        <button type="button" className="primary" onClick={onClose}>Done</button>
      </footer>
    </section>
  </Dialog>
}
