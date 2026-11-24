import { useEffect } from 'react'
import { ArrowLeft, ArrowRight } from 'lucide-react'
import { stepAsked } from '../assetNavigation'

/**
 * Previous and next from an asset's own view, through the library list it was
 * opened from (same view, filter, sort and search), the way quick review moves.
 * The left and right arrow keys do the same, unless the key belongs to what
 * has the keyboard: a field being typed in, or a view that moves on the
 * arrows itself. A 3D view that has focus turns; it is never stepped away from.
 */
export default function AssetStepper({ position, total, onPrevious, onNext }: {
  position: number
  total: number
  onPrevious?: () => void
  onNext?: () => void
}) {
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null
      const asked = stepAsked({
        key: event.key,
        modified: event.ctrlKey || event.metaKey || event.altKey || event.shiftKey,
        used: event.defaultPrevented,
        typing: Boolean(target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)),
        dialogOpen: Boolean(document.querySelector('dialog[open]')),
      })
      const step = asked === 'previous' ? onPrevious : asked === 'next' ? onNext : undefined
      if (!step) return
      event.preventDefault()
      step()
    }
    // Heard last, on the key's way back up from whatever has the keyboard, so
    // that view has already said whether the key was its own. Hearing it first
    // is how the arrows were once taken from the 3D view they turn.
    window.addEventListener('keydown', keys)
    return () => window.removeEventListener('keydown', keys)
  }, [onPrevious, onNext])

  return <div className="asset-stepper" role="group" aria-label="Step through the library list" data-testid="asset-stepper">
    <button type="button" className="secondary compact" aria-keyshortcuts="ArrowLeft" disabled={!onPrevious} onClick={onPrevious} data-testid="asset-step-previous">
      <ArrowLeft size={15} />Previous<kbd>←</kbd>
    </button>
    <span data-testid="asset-step-position">{position} of {total}</span>
    <button type="button" className="secondary compact" aria-keyshortcuts="ArrowRight" disabled={!onNext} onClick={onNext} data-testid="asset-step-next">
      Next<ArrowRight size={15} /><kbd>→</kbd>
    </button>
  </div>
}
