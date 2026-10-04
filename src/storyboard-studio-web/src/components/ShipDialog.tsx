import { useEffect, useState } from 'react'
import { Download, LoaderCircle, PackageCheck, Send } from 'lucide-react'
import { studioApi } from '../api'
import { REVIEW_LABELS } from '../assetReview'
import { defaultDestination, shipSummary } from '../shipping'
import type { ShipmentPreview, ShipmentReceipt, ShippingTargetSummary } from '../types'
import Dialog from './Dialog'

/** What is being shipped: one collection, or a chosen set of assets. */
export type ShipSource =
  | { kind: 'collection'; collectionId: string; name: string }
  | { kind: 'selection'; assetIds: string[]; name: string }

/**
 * Hands approved assets to a game. The artist sees what will go and what will
 * stay (and why) before anything is written, may let pending work go too, and
 * picks a destination their workstation configured -- or downloads a zip. A
 * shipment is always a new folder; nothing already shipped is written over.
 */
export default function ShipDialog({ source, onClose, onShipped }: {
  source: ShipSource
  onClose: () => void
  onShipped: (message: string) => void
}) {
  const [includePending, setIncludePending] = useState(false)
  const [preview, setPreview] = useState<ShipmentPreview>()
  const [targets, setTargets] = useState<ShippingTargetSummary[]>()
  const [destination, setDestination] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [receipt, setReceipt] = useState<ShipmentReceipt | { zip: string; items: number }>()

  const request = (target?: string) => ({
    ...(source.kind === 'collection' ? { collectionId: source.collectionId } : { assetIds: source.assetIds }),
    includePending,
    target,
  })

  useEffect(() => {
    let live = true
    studioApi.shippingTargets()
      .then(found => { if (live) { setTargets(found); setDestination(current => current ?? defaultDestination(found)) } })
      .catch(() => { if (live) { setTargets([]); setDestination(current => current ?? 'zip') } })
    return () => { live = false }
  }, [])

  useEffect(() => {
    let live = true
    setPreview(undefined); setError(undefined)
    studioApi.shipPreview(request())
      .then(found => { if (live) setPreview(found) })
      .catch(failure => { if (live) setError(failure instanceof Error ? failure.message : 'Could not read what would ship.') })
    return () => { live = false }
    // The request is rebuilt from these; rebuilding it on every render would ask again each time.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [includePending, source])

  const ship = async () => {
    if (!destination) return
    setBusy(true); setError(undefined)
    try {
      if (destination === 'zip') {
        const bundle = await studioApi.shipZip(request())
        const url = URL.createObjectURL(bundle.blob)
        const link = document.createElement('a')
        link.href = url; link.download = bundle.fileName
        document.body.appendChild(link); link.click(); link.remove()
        setTimeout(() => URL.revokeObjectURL(url), 60_000)
        setReceipt({ zip: bundle.fileName, items: bundle.items })
        onShipped(`${bundle.items} asset${bundle.items === 1 ? '' : 's'} bundled into ${bundle.fileName}.`)
      } else {
        const shipped = await studioApi.ship(request(destination))
        setReceipt(shipped)
        onShipped(`${shipped.itemCount} asset${shipped.itemCount === 1 ? '' : 's'} shipped to ${shipped.target}.`)
      }
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'The shipment could not be written.')
    } finally { setBusy(false) }
  }

  const configured = targets ?? []
  return <Dialog className="edit-dialog ship-dialog" onClose={onClose} labelledBy="ship-title">
    <header>
      <div><p className="eyebrow">Ship to game</p><h2 id="ship-title">{source.name}</h2></div>
      <button onClick={onClose} aria-label="Close ship to game">×</button>
    </header>
    <div className="edit-dialog-body">
      {receipt
        ? <div className="ship-receipt" data-testid="ship-receipt">
            <PackageCheck size={20} />
            {'zip' in receipt
              ? <p>Bundled {receipt.items} asset{receipt.items === 1 ? '' : 's'} into <code>{receipt.zip}</code>. Unzip it into your game project, or point your importer at it.</p>
              : <p>Shipped {receipt.itemCount} asset{receipt.itemCount === 1 ? '' : 's'} to {receipt.target}, as a new folder:
                  <code data-testid="ship-receipt-folder">{receipt.folder}</code>
                  Earlier shipments are untouched.</p>}
          </div>
        : <>
            {preview === undefined && !error && <p className="model-note"><LoaderCircle className="spin" size={14} /> Reading what would ship…</p>}
            {preview && <>
              <p className="ship-summary" data-testid="ship-summary">{shipSummary(preview)}</p>
              <ul className="ship-items" data-testid="ship-items">
                {preview.items.map(item => <li key={item.revisionId} data-ships={item.ships ? 'true' : 'false'}>
                  <strong>{item.displayName}</strong>
                  <small>{item.kind} · {REVIEW_LABELS[item.decision]}</small>
                  <em>{item.ships ? 'Ships' : item.reason}</em>
                </li>)}
              </ul>
            </>}
            <label className="ship-pending">
              <input type="checkbox" checked={includePending} data-testid="ship-include-pending"
                onChange={event => setIncludePending(event.target.checked)} />
              <span>Include pending work<small>Only approved work ships otherwise. Anything sent back never does.</small></span>
            </label>
            <fieldset className="ship-destinations">
              <legend>Destination</legend>
              {configured.map(target => <label key={target.name} className={target.available ? '' : 'unavailable'}>
                <input type="radio" name="ship-destination" value={target.name} disabled={!target.available}
                  checked={destination === target.name} onChange={() => setDestination(target.name)} />
                <span><strong>{target.name}</strong><small>{target.available ? target.path : target.problem}</small></span>
              </label>)}
              <label>
                <input type="radio" name="ship-destination" value="zip" data-testid="ship-destination-zip"
                  checked={destination === 'zip'} onChange={() => setDestination('zip')} />
                <span><strong>Download a .zip</strong><small>The same bundle, to unzip wherever you like.</small></span>
              </label>
              {targets !== undefined && configured.length === 0 && <p className="model-note" data-testid="ship-no-targets">
                No game folders are set up on this workstation. Add one under Integrations:Shipping:Targets in
                appsettings.Local.json to ship straight into a project.
              </p>}
            </fieldset>
          </>}
      {error && <p className="form-error" role="alert" data-testid="ship-error">{error}</p>}
    </div>
    <footer>
      <button className="secondary" onClick={onClose}>{receipt ? 'Done' : 'Cancel'}</button>
      {!receipt && <button className="primary" data-testid="ship-confirm"
        disabled={busy || !preview || preview.shipCount === 0 || !destination} onClick={() => void ship()}>
        {busy ? <LoaderCircle className="spin" size={16} /> : destination === 'zip' ? <Download size={16} /> : <Send size={16} />}
        {destination === 'zip' ? 'Download bundle' : 'Ship'}
      </button>}
    </footer>
  </Dialog>
}
