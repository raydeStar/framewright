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

/**
 * Quick review walks the library one asset at a time: the revision the library
 * shows (the current one of a stack, or a lone asset), never an archived one,
 * in name order so a family of props sits together and the order does not
 * shuffle while decisions land.
 *
 * An asset decided in this sitting stays in the walk even though it no longer
 * matches the filter, so the previous arrow still finds it and the count
 * does not shift under the reviewer. `kept` holds those.
 */
export function reviewQueue(assets: AssetSummary[], filter: ReviewFilter, kept: ReadonlySet<string> = new Set()) {
  return assets
    .filter(asset => (!asset.revisionFamilyId || asset.isCurrentRevision) && !asset.isArchived)
    .filter(asset => matchesReviewFilter(asset, filter) || kept.has(asset.id))
    .sort((a, b) => a.displayName.localeCompare(b.displayName) || a.createdAt.localeCompare(b.createdAt) || a.id.localeCompare(b.id))
}

/**
 * Where a decision moves on to: the next asset after this one that still
 * waits under the filter, wrapping round to the start for any skipped
 * earlier. Under "all" it is simply the next one. Nothing left is undefined.
 */
export function nextInQueue(queue: AssetSummary[], fromId: string, filter: ReviewFilter): string | undefined {
  const at = queue.findIndex(asset => asset.id === fromId)
  if (filter === 'all') return at >= 0 && at + 1 < queue.length ? queue[at + 1].id : undefined
  const ordered = at < 0 ? queue : [...queue.slice(at + 1), ...queue.slice(0, at)]
  return ordered.find(asset => asset.id !== fromId && matchesReviewFilter(asset, filter))?.id
}

/** How many of the walk still wait under the filter. Under "all", every one. */
export function waitingCount(queue: AssetSummary[], filter: ReviewFilter) {
  return filter === 'all' ? queue.length : queue.filter(asset => matchesReviewFilter(asset, filter)).length
}

/** Which revision this is out of its stack: "rev 2 of 3". A lone asset is rev 1 of 1. */
export function revisionLabel(asset: AssetSummary, all: AssetSummary[]) {
  const number = asset.revisionNumber ?? 1
  if (!asset.revisionFamilyId) return `rev ${number} of ${number}`
  const stack = all.filter(item => item.revisionFamilyId === asset.revisionFamilyId)
  const total = Math.max(number, stack.length, ...stack.map(item => item.revisionNumber ?? 1))
  return `rev ${number} of ${total}`
}

/**
 * The send-backs as plain text to paste to whoever makes the fixes: one line
 * per asset, its name, which revision, and what needs to change.
 */
export function sentBackList(assets: AssetSummary[]) {
  return reviewQueue(assets, 'ChangesRequested')
    .map(asset => `${asset.displayName} — ${revisionLabel(asset, assets)} — ${asset.reviewNote.replace(/\s+/g, ' ').trim()}`)
    .join('\n')
}

/**
 * In quick review the feedback is optional, but the server keeps its rule that
 * a send-back carries a reason. An empty box sends this, so whoever picks it
 * up knows there was no more to it rather than finding a blank.
 */
export const SENT_BACK_WITHOUT_NOTE = 'Sent back in quick review without a note.'

export function sendBackNote(typed: string) {
  return typed.trim() || SENT_BACK_WITHOUT_NOTE
}

/** One decision made in quick review, and what the asset was before it, so it can be put back. */
export interface QuickReviewUndo {
  assetId: string
  displayName: string
  /** The decision being undone. */
  made: AssetReviewDecision
  /** What it was before, restored as it was, note and all. */
  decision: AssetReviewDecision
  note: string
}

/**
 * Where a quick review sitting is, held by the library so stepping out to the
 * full inspection view and back resumes on the same asset.
 */
export interface QuickReviewSession {
  filter: ReviewFilter
  currentId?: string
  kept: Set<string>
  undo: QuickReviewUndo[]
}

export function newQuickReviewSession(): QuickReviewSession {
  return { filter: 'Pending', kept: new Set(), undo: [] }
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
