import { Component, type ErrorInfo, type ReactNode } from 'react'
import { CircleAlert, RefreshCw } from 'lucide-react'

/**
 * Keeps one broken view from blanking the whole studio.
 *
 * Without it a render error unmounts everything and leaves an empty page, which
 * reads as "my work is gone" even though it is safe on the server. The boundary
 * is keyed by workspace in the shell, so moving to another view recovers
 * without a reload; the reload button is there for everything else.
 */
export default class ErrorBoundary extends Component<{ children: ReactNode; onLeave?: () => void; leaveLabel?: string }, { error?: Error }> {
  state: { error?: Error } = {}

  static getDerivedStateFromError(error: Error) { return { error } }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Kept in the browser console for diagnostics; nothing is sent anywhere.
    console.error('Framewright view failed to render', error, info.componentStack)
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children
    return <main className="workspace view-failure" role="alert" data-testid="view-failure">
      <div className="view-failure-card">
        <CircleAlert size={22} />
        <h2>This view stopped working</h2>
        <p>Your saved work is safe: projects, shots and versions live in the studio's database, not in this page. Reloading usually brings the view back.</p>
        <code>{error.message}</code>
        <div className="view-failure-actions">
          <button className="primary" onClick={() => window.location.reload()}><RefreshCw size={16} />Reload Framewright</button>
          {this.props.onLeave && <button className="secondary" onClick={() => { this.setState({ error: undefined }); this.props.onLeave?.() }}>{this.props.leaveLabel ?? 'Back to the board'}</button>}
        </div>
      </div>
    </main>
  }
}
