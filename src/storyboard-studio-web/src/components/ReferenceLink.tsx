import { useMemo, useState } from 'react'
import { Image as ImageIcon, Link2, LoaderCircle, Search, Unlink } from 'lucide-react'
import { studioApi } from '../api'
import { exactReference, referenceSuggestions } from '../assetNavigation'
import type { AssetSummary } from '../types'

/**
 * The image a model was made from: its thumbnail, which opens the image, and
 * a way to link one when the upload did not. The likeliest image, the one
 * with the model's own name, is offered first.
 */
export default function ReferenceLink({ asset, library, onOpen, onChanged }: {
  asset: AssetSummary
  /** The whole library, for finding the linked image and offering others. */
  library: AssetSummary[]
  onOpen: (assetId: string) => void
  onChanged: (updated: AssetSummary) => void | Promise<void>
}) {
  const [picking, setPicking] = useState(false)
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const reference = library.find(item => item.id === asset.referenceAssetId)
  const exact = useMemo(() => (asset.referenceAssetId ? undefined : exactReference(asset, library)), [asset, library])
  const suggestions = useMemo(() => (picking ? referenceSuggestions(asset, library, search).slice(0, 12) : []), [asset, library, picking, search])

  const link = async (referenceAssetId: string | null) => {
    setBusy(true); setError(undefined)
    try { await onChanged(await studioApi.setAssetReference(asset.id, referenceAssetId)); setPicking(false); setSearch('') }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not change the reference.') }
    finally { setBusy(false) }
  }

  return <section className="reference-link" data-testid="reference-link">
    <h2><ImageIcon size={15} />Reference image</h2>
    {reference
      ? <button type="button" className="reference-thumb" data-testid="reference-open" onClick={() => onOpen(reference.id)} title="Open the reference image">
          <img src={reference.contentUrl} alt="" />
          <span><strong>{reference.displayName}</strong><small>Open the image</small></span>
        </button>
      : asset.referenceAssetId
        ? <p className="model-note">The linked image is no longer in the library.</p>
        : <p className="model-note">No reference image is linked.</p>}
    <div className="reference-actions">
      {exact && <button type="button" className="primary compact" disabled={busy} data-testid="reference-link-exact" onClick={() => void link(exact.id)}>
        <Link2 size={14} />Link “{exact.displayName}”
      </button>}
      <button type="button" className="secondary compact" disabled={busy} aria-expanded={picking} onClick={() => setPicking(value => !value)} data-testid="reference-choose">
        {asset.referenceAssetId ? 'Change' : 'Choose an image'}
      </button>
      {asset.referenceAssetId && <button type="button" className="secondary compact" disabled={busy} onClick={() => void link(null)}><Unlink size={14} />Unlink</button>}
      {busy && <LoaderCircle className="spin" size={15} />}
    </div>
    {picking && <div className="reference-picker" data-testid="reference-picker">
      <label className="asset-search"><Search size={14} /><input autoFocus value={search} onChange={event => setSearch(event.target.value)} placeholder="Search images by name" aria-label="Search images" /></label>
      {suggestions.length === 0
        ? <p className="model-note">No images match.</p>
        : <div className="reference-options">{suggestions.map(image => <button type="button" key={image.id} disabled={busy}
            className={image.id === asset.referenceAssetId ? 'is-on' : undefined} onClick={() => void link(image.id)} title={image.displayName}>
            <img src={image.contentUrl} alt="" loading="lazy" /><small>{image.displayName}</small>
          </button>)}</div>}
    </div>}
    {error && <p className="form-error" role="alert">{error}</p>}
  </section>
}
