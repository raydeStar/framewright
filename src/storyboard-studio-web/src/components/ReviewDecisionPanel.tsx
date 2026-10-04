import { useEffect, useState } from 'react'
import { BadgeCheck, CornerUpLeft, LoaderCircle, RotateCcw } from 'lucide-react'
import { studioApi } from '../api'
import { REVIEW_LABELS } from '../assetReview'
import type { AssetReviewDecision, AssetSummary } from '../types'

/**
 * A person's decision about one asset revision: approve it, send it back with
 * a reason, or return it to pending. The same panel on every kind of asset.
 *
 * Sending back asks for the reason in place, because the reason is what the
 * next revision answers -- and that revision shows it, so whoever makes it
 * does not have to go looking for why.
 */
export default function ReviewDecisionPanel({ asset, onDecided }: {
  asset: AssetSummary
  onDecided: (updated: AssetSummary, message: string) => void | Promise<void>
}) {
  const [sending, setSending] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  // A reason belongs to the revision it was typed for.
  useEffect(() => { setSending(false); setReason(''); setError(undefined) }, [asset.id])

  const decide = async (decision: AssetReviewDecision, note?: string) => {
    setBusy(true); setError(undefined)
    try {
      const updated = await studioApi.setAssetReview(asset.id, decision, note)
      setSending(false); setReason('')
      await onDecided(updated, decision === 'Approved'
        ? `${updated.displayName} approved.`
        : decision === 'ChangesRequested'
        ? `${updated.displayName} sent back: ${note}`
        : `${updated.displayName} is pending review again.`)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'That decision could not be saved.')
    } finally { setBusy(false) }
  }

  const decision = asset.reviewDecision
  return <section className="asset-review-decision" data-testid="asset-review-decision" data-decision={decision}>
    <h3>Review</h3>
    <p className="asset-review-state" data-testid="asset-review-state">
      <span className={`decision-chip decision-${decision}`}>{REVIEW_LABELS[decision]}</span>
      {asset.reviewDecidedAt && <small>{new Date(asset.reviewDecidedAt).toLocaleString()}</small>}
    </p>
    {asset.reviewNote && <p className="asset-review-reason" data-testid="asset-review-reason-shown">“{asset.reviewNote}”</p>}
    {asset.reviewAnswersNote && <p className="asset-review-answers" data-testid="asset-review-answers">
      <CornerUpLeft size={13} />Answers a send-back: “{asset.reviewAnswersNote}”
    </p>}
    {asset.isArchived
      ? <p className="model-note">Archived. Restore it to decide about it.</p>
      : <div className="asset-review-actions">
          <button type="button" className="primary compact" data-testid="asset-review-approve"
            disabled={busy || decision === 'Approved'} onClick={() => void decide('Approved')}>
            {busy ? <LoaderCircle className="spin" size={15} /> : <BadgeCheck size={15} />}Approve
          </button>
          <button type="button" className="secondary compact" data-testid="asset-review-send-back"
            disabled={busy} aria-expanded={sending} onClick={() => setSending(value => !value)}>
            <CornerUpLeft size={15} />Send back
          </button>
          {decision !== 'Pending' && <button type="button" className="secondary compact" data-testid="asset-review-reset"
            disabled={busy} onClick={() => void decide('Pending')}>
            <RotateCcw size={14} />Back to pending
          </button>}
        </div>}
    {sending && <form className="asset-review-send-back" onSubmit={event => { event.preventDefault(); if (reason.trim()) void decide('ChangesRequested', reason.trim()) }}>
      <label>What needs to change?
        <textarea rows={2} maxLength={1000} value={reason} autoFocus data-testid="asset-review-reason"
          onChange={event => setReason(event.target.value)}
          placeholder="e.g. Improve the texture on the lid; the effect reads weak at the swing." />
      </label>
      <div>
        <button type="button" className="secondary compact" onClick={() => { setSending(false); setReason('') }}>Cancel</button>
        <button type="submit" className="secondary compact" data-testid="asset-review-send-back-confirm"
          disabled={busy || !reason.trim()}>Send back</button>
      </div>
    </form>}
    {error && <p className="form-error" role="alert">{error}</p>}
  </section>
}
