import { useEffect, useRef, type ReactNode } from 'react'

/**
 * Modal dialog built on the native `<dialog>` element.
 *
 * `showModal()` gives us focus containment, Escape handling, the top layer, and
 * background inertness from the platform. The previous hand-rolled overlays set
 * `aria-modal="true"` but left the whole page tabbable behind them, so Tab
 * walked straight out of the dialog and the dialog effectively stopped existing
 * for a screen-reader user. Do not reintroduce a div-based overlay.
 *
 * `cancel` and `close` are attached natively rather than through React props:
 * the synthetic versions do not fire reliably here, and Escape silently failing
 * to dismiss a modal is worse than no keyboard shortcut at all.
 *
 * Focus restoration is done explicitly. The spec returns focus on `close()`,
 * but this app unmounts dialogs rather than closing them, so we capture the
 * invoking element and restore it on teardown.
 */
export default function Dialog({ onClose, className = '', labelledBy, describedBy, children, initialFocus }: {
  onClose: () => void
  className?: string
  labelledBy?: string
  describedBy?: string
  children: ReactNode
  /** Selector for the control that should hold focus on open. Defaults to the
   *  first tabbable element, which is what the platform does anyway. */
  initialFocus?: string
}) {
  const ref = useRef<HTMLDialogElement>(null)
  // Kept in a ref so a new onClose identity each render never re-runs the
  // effect — re-running it would close and reopen the dialog mid-interaction.
  const close = useRef(onClose)
  close.current = onClose

  useEffect(() => {
    const node = ref.current
    if (!node) return
    const restoreTo = document.activeElement as HTMLElement | null
    if (!node.open) node.showModal()
    if (initialFocus) node.querySelector<HTMLElement>(initialFocus)?.focus()

    const onCancel = (event: Event) => { event.preventDefault(); close.current() }
    const onNativeClose = () => close.current()
    // Escape belt-and-braces. Not every engine emits `cancel` for a modal
    // dialog — WebKit in particular has been inconsistent, and this app ships
    // iPad WebKit tests. Focus is always inside a modal, so this keydown always
    // reaches us. Double-firing is harmless: the component unmounts either way.
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      event.stopPropagation()
      close.current()
    }
    node.addEventListener('cancel', onCancel)
    node.addEventListener('close', onNativeClose)
    node.addEventListener('keydown', onKeyDown)

    return () => {
      node.removeEventListener('cancel', onCancel)
      node.removeEventListener('close', onNativeClose)
      node.removeEventListener('keydown', onKeyDown)
      if (node.open) node.close()
      if (restoreTo && document.contains(restoreTo)) restoreTo.focus()
    }
  }, [initialFocus])

  return <dialog
    ref={ref}
    className={`studio-dialog ${className}`}
    aria-labelledby={labelledBy}
    aria-describedby={describedBy}
    // A click on ::backdrop is dispatched with the dialog itself as target.
    // The dialog carries no padding, so this only ever means "outside".
    onMouseDown={event => { if (event.target === event.currentTarget) close.current() }}
  >{children}</dialog>
}
