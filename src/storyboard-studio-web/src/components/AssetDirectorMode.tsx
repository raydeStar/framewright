import { Maximize2, Minimize2 } from 'lucide-react'
import type { DirectorAssetView } from '../types'

export interface AssetDirectorControls {
  directorMode: boolean
  onDirectorMode: (enabled: boolean) => void
  onDirectorView: (view: DirectorAssetView | undefined) => void
}

export function DirectorModeButton({ active, onClick, subject }: {
  active: boolean; onClick: () => void; subject?: string
}) {
  return <button type="button" className={`secondary director-mode-toggle${active ? ' active' : ''}`}
    aria-pressed={active} aria-label={subject ? `Direct ${subject}` : undefined}
    title={active ? 'Leave full view (Escape)' : 'Open full view'} onClick={onClick}>
    {active ? <Minimize2 size={16} /> : <Maximize2 size={16} />}
    {active ? 'Exit Director Mode' : 'Director Mode'}
  </button>
}
