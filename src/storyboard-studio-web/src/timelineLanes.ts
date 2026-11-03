/**
 * Rows inside one Sequence audio lane. Clips on a lane may overlap in time
 * (an ambience bed under spot effects, a voice line over a guide), so each
 * clip takes the first row where it clears everything already drawn there,
 * and the lane grows a row only when nothing fits. Overlap is judged on the
 * drawn extent: a very short clip is drawn wider than its length so it stays
 * tappable, and must not hide its neighbour.
 */
export interface LaneClip { id: string; startFrame: number; durationFrames: number }

export interface LaneStack { rows: number; rowOf: Record<string, number> }

/** The narrowest a clip is drawn, as a share of the sequence. */
export const MIN_CLIP_SHARE = 0.03

export function stackClips(clips: readonly LaneClip[], totalFrames: number): LaneStack {
  const minimumFrames = Math.ceil(Math.max(1, totalFrames) * MIN_CLIP_SHARE)
  // Earliest first; at the same start the longer clip (usually a bed) takes the upper row.
  const ordered = [...clips].sort((a, b) => a.startFrame - b.startFrame || b.durationFrames - a.durationFrames || a.id.localeCompare(b.id))
  const rowEnds: number[] = []
  const rowOf: Record<string, number> = {}
  for (const clip of ordered) {
    const end = clip.startFrame + Math.max(clip.durationFrames, minimumFrames)
    let row = rowEnds.findIndex(rowEnd => rowEnd <= clip.startFrame)
    if (row < 0) row = rowEnds.push(end) - 1
    else rowEnds[row] = end
    rowOf[clip.id] = row
  }
  return { rows: Math.max(1, rowEnds.length), rowOf }
}

/**
 * The timeline grid's rows: the picture row, then one per audio lane. A lane
 * with stacked clips gets a share of the height in proportion to its rows,
 * and a floor that keeps each row readable on a 1024 x 768 screen.
 */
export function laneTemplate(laneRows: readonly number[]): string {
  return ['minmax(64px, 1.35fr)', ...laneRows.map(rows => `minmax(${46 + (rows - 1) * 18}px, ${rows}fr)`)].join(' ')
}
