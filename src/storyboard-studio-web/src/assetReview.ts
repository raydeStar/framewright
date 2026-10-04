import type { AssetReviewDecision, AssetReviewNoteSummary, AssetSummary } from './types'

/**
 * How a library review reads: the decision on a revision, the filter over the
 * library, and where a note is attached. Presentation only; the decisions are
 * a person's and live on the server.
 */

/** The library filter: every asset, or only those with one decision. */
export type ReviewFilter = 'all' | AssetReviewDecision

export const REVIEW_LABELS: Record<AssetReviewDecision, string> = {
  Pending: 'Pending',
  Approved: 'Approved',
  ChangesRequested: 'Changes requested',
}

export function matchesReviewFilter(asset: AssetSummary, filter: ReviewFilter) {
  return filter === 'all' || asset.reviewDecision === filter
}

/**
 * The badge on a library card. A pending revision that answers a send-back
 * says so, because "pending" alone would hide that it is the reply somebody
 * is waiting to look at.
 */
export function reviewBadge(asset: AssetSummary): { decision: AssetReviewDecision; label: string; title: string } {
  const decision = asset.reviewDecision
  if (decision === 'Pending' && asset.reviewAnswersAssetId)
    return { decision, label: 'Answers send-back', title: `Pending review. It answers: “${asset.reviewAnswersNote}”` }
  return {
    decision,
    label: REVIEW_LABELS[decision],
    title: asset.reviewNote ? `${REVIEW_LABELS[decision]}: “${asset.reviewNote}”` : REVIEW_LABELS[decision],
  }
}

/** A moment the way a player shows it: 0:07.5, 1:02.0. */
export function formatMoment(seconds: number) {
  const whole = Math.max(0, seconds)
  const minutes = Math.floor(whole / 60)
  const rest = whole - minutes * 60
  return `${minutes}:${rest.toFixed(1).padStart(4, '0')}`
}

/** Where a note is attached, in words. */
export function noteAnchorLabel(note: AssetReviewNoteSummary) {
  switch (note.anchor) {
    case 'Point': return `${Math.round(note.x * 100)}% across · ${Math.round(note.y * 100)}% down`
    case 'Time': return note.timeSeconds == null ? 'Whole asset' : `At ${formatMoment(note.timeSeconds)}`
    case 'View': return note.viewYaw == null || note.viewPitch == null
      ? 'Whole asset'
      : `From the view at yaw ${note.viewYaw.toFixed(2)} · pitch ${note.viewPitch.toFixed(2)}`
    default: return 'Whole asset'
  }
}

/** Open notes first; timed ones in playback order, the rest oldest first. */
export function orderNotes(notes: AssetReviewNoteSummary[]) {
  return [...notes].sort((a, b) => {
    if (a.state !== b.state) return a.state === 'Open' ? -1 : 1
    const at = a.timeSeconds ?? Number.POSITIVE_INFINITY
    const bt = b.timeSeconds ?? Number.POSITIVE_INFINITY
    if (at !== bt) return at - bt
    return a.createdAt.localeCompare(b.createdAt)
  })
}
