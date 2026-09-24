import { useId, type ReactNode } from 'react'
import Dialog from './Dialog'

/**
 * A plain "are you sure" for actions that lose work. It opens with focus on the
 * safe choice, so Enter or a stray second key press keeps what is there.
 */
export default function ConfirmDialog({ eyebrow = 'Check before continuing', title, children, confirmLabel, cancelLabel = 'Keep it', danger = true, busy = false, onConfirm, onCancel }: {
  eyebrow?: string
  title: string
  children: ReactNode
  confirmLabel: string
  cancelLabel?: string
  danger?: boolean
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const id = useId()
  return <Dialog className="modal-backdrop" onClose={onCancel} labelledBy={`${id}-title`} initialFocus="[data-confirm-cancel]">
    <section className="edit-dialog delete-shot" data-testid="confirm-dialog">
      <header><div><p className="eyebrow">{eyebrow}</p><h2 id={`${id}-title`}>{title}</h2></div><button onClick={onCancel} aria-label="Cancel">×</button></header>
      <div className="edit-dialog-body">{children}</div>
      <footer>
        <span /><span />
        <button className="secondary" data-confirm-cancel onClick={onCancel}>{cancelLabel}</button>
        <button className={danger ? 'primary danger' : 'primary'} disabled={busy} onClick={onConfirm}>{busy ? 'Working…' : confirmLabel}</button>
      </footer>
    </section>
  </Dialog>
}
