import type { AssetReviewDecision, SfxReviewCounts, SfxSoundSummary, SfxTakeSummary } from './types'

/**
 * How sound review reads: which sounds a filter shows, where a decision moves
 * on to, which take plays first, and the text a rejection list copies as.
 * Presentation only; the decisions are a person's and live on the server.
 */

export type SfxFilter = 'Pending' | 'ChangesRequested' | 'Approved' | 'NotGenerated' | 'all'

export const SFX_LABELS: Record<AssetReviewDecision, string> = {
  Pending: 'Pending',
  Approved: 'Accepted',
  ChangesRequested: 'Rejected',
}

export function matchesSfxFilter(sound: SfxSoundSummary, filter: SfxFilter) {
  switch (filter) {
    case 'all': return true
    case 'NotGenerated': return !sound.generated && sound.decision === 'Pending'
    case 'Pending': return sound.generated && sound.decision === 'Pending'
    default: return sound.decision === filter
  }
}

/**
 * The sounds a sitting walks, in the list's own order (it is priority order).
 * A sound decided in this sitting stays in the walk so the previous arrow
 * still finds it; `kept` holds those.
 */
export function sfxQueue(sounds: SfxSoundSummary[], filter: SfxFilter, kept: ReadonlySet<string> = new Set()) {
  return sounds.filter(sound => matchesSfxFilter(sound, filter) || kept.has(sound.name))
}

/** Where a decision moves on to: the next sound still waiting, wrapping round; under "all", the next one. */
export function nextSfx(queue: SfxSoundSummary[], fromName: string, filter: SfxFilter): string | undefined {
  const at = queue.findIndex(sound => sound.name === fromName)
  if (filter === 'all') return at >= 0 && at + 1 < queue.length ? queue[at + 1].name : undefined
  const ordered = at < 0 ? queue : [...queue.slice(at + 1), ...queue.slice(0, at)]
  return ordered.find(sound => sound.name !== fromName && matchesSfxFilter(sound, filter))?.name
}

export function sfxCounts(sounds: SfxSoundSummary[]): SfxReviewCounts {
  return {
    total: sounds.length,
    pending: sounds.filter(sound => matchesSfxFilter(sound, 'Pending')).length,
    approved: sounds.filter(sound => sound.decision === 'Approved').length,
    changesRequested: sounds.filter(sound => sound.decision === 'ChangesRequested').length,
    notGenerated: sounds.filter(sound => matchesSfxFilter(sound, 'NotGenerated')).length,
  }
}

/**
 * The take to hear first: the accepted one, else the first made after a
 * rejection (that is what is being answered), else the first take.
 */
export function openingTake(sound: SfxSoundSummary): SfxTakeSummary | undefined {
  return sound.takes.find(take => take.sha256 === sound.acceptedTakeSha256)
    ?? sound.takes.find(take => take.afterRejection)
    ?? sound.takes[0]
}

/** The rejected sounds as plain text to hand back for regeneration: one line each. */
export function rejectedSfxList(sounds: SfxSoundSummary[]) {
  return sounds
    .filter(sound => sound.decision === 'ChangesRequested')
    .map(sound => `${sound.name} (${sound.category || 'no category'}) — ${sound.note.replace(/\s+/g, ' ').trim() || 'no note'}`)
    .join('\n')
}

/**
 * A waveform's outline: the loudest sample in each of `buckets` equal slices,
 * 0 to 1. Drawn mirrored, it reads the way an editor shows a clip.
 */
export function waveformPeaks(samples: Float32Array, buckets: number): number[] {
  const count = Math.max(1, Math.floor(buckets))
  const size = samples.length / count
  const peaks: number[] = []
  for (let bucket = 0; bucket < count; bucket++) {
    const start = Math.floor(bucket * size)
    const end = Math.max(start + 1, Math.floor((bucket + 1) * size))
    let peak = 0
    for (let index = start; index < end && index < samples.length; index++) {
      const value = Math.abs(samples[index])
      if (value > peak) peak = value
    }
    peaks.push(Math.min(1, peak))
  }
  return peaks
}

/** Seconds the way a clip length reads: 0.6 s, 19.5 s. */
export function formatSeconds(seconds: number | null | undefined) {
  return seconds == null || !Number.isFinite(seconds) ? '—' : `${seconds < 10 ? seconds.toFixed(2).replace(/0$/, '') : seconds.toFixed(1)} s`
}
