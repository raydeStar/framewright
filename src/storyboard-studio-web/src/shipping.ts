import type { ShipmentPreview, ShippingTargetSummary } from './types'

/**
 * How a shipment reads before it is sent: what goes, what stays and why, and
 * where it would be written. The decisions behind it are made on the server.
 */

/** "2 will ship · 3 left out: 1 pending, 1 sent back, 1 archived". */
export function shipSummary(preview: ShipmentPreview) {
  const going = `${preview.shipCount} will ship`
  if (preview.skippedCount === 0) return going
  const reasons = new Map<string, number>()
  for (const item of preview.items.filter(entry => !entry.ships)) {
    const reason = item.reason?.startsWith('Sent back') ? 'sent back'
      : item.reason === 'Pending review' ? 'pending'
      : item.reason === 'Archived' ? 'archived'
      : 'missing a file'
    reasons.set(reason, (reasons.get(reason) ?? 0) + 1)
  }
  const detail = [...reasons].map(([reason, count]) => `${count} ${reason}`).join(', ')
  return `${going} · ${preview.skippedCount} left out: ${detail}`
}

/** The destination to offer first: the first one that can be written, else a zip. */
export function defaultDestination(targets: ShippingTargetSummary[]) {
  return targets.find(target => target.available)?.name ?? 'zip'
}
