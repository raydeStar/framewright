import { useEffect } from 'react'
import { ArrowLeft, ArrowRight } from 'lucide-react'

/**
 * Previous and next from an asset's own view, through the library list it was
 * opened from (same view, filter, sort and search), the way quick review moves.
 * The left and right arrow keys do the same unless something is being typed;
 * Shift with an arrow is left alone, so a 3D view can still pan.
 */
export default function AssetStepper({ position, total, onPrevious, onNext }: {
  position: number
  total: number
  onPrevious?: () => void
  onNext?: () => void
}) {
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
      const target = event.target as HTMLElement | null
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return
      if (document.querySelector('dialog[open]')) return
      const step = event.key === 'ArrowLeft' ? onPrevious : onNext
      if (!step) return
      event.preventDefault(); event.stopPropagation()
      step()
    }
    window.addEventListener('keydown', keys, true)
    return () => window.removeEventListener('keydown', keys, true)
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
