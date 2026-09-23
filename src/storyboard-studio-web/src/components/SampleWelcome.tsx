import { useState } from 'react'
import { FolderPlus, X } from 'lucide-react'

const dismissedKey = (projectId: string) => `framewright.sample-welcome-dismissed.${projectId}`

function readDismissed(projectId: string) {
  try { return localStorage.getItem(dismissedKey(projectId)) === 'true' } catch { return false }
}

/**
 * The first thing a new workstation shows is the seeded demo production. Without
 * a word of explanation the artist is standing in someone else's project, so the
 * sample says what it is and offers the way out. Dismissing is remembered per
 * browser only; the Sample tag in the project switcher stays either way.
 */
export default function SampleWelcome({ projectId, onStartProject }: { projectId: string; onStartProject: () => void }) {
  const [dismissed, setDismissed] = useState(() => readDismissed(projectId))
  if (dismissed) return null
  const dismiss = () => {
    setDismissed(true)
    try { localStorage.setItem(dismissedKey(projectId), 'true') } catch { /* A remembered dismissal is a convenience. */ }
  }
  return <section className="sample-welcome" aria-labelledby="sample-welcome-title" data-testid="sample-welcome">
    <div className="sample-welcome-copy">
      <p className="eyebrow">Sample production</p>
      <h2 id="sample-welcome-title">Welcome to Framewright</h2>
      <p>This is a sample project, so you can see how shots, references, review and the sequence fit together. Look around freely. Your own work starts in a project of its own.</p>
    </div>
    <div className="sample-welcome-actions">
      <button type="button" className="primary" onClick={onStartProject}><FolderPlus size={16} />Start your own project</button>
      <button type="button" className="secondary" onClick={dismiss}>Keep exploring</button>
    </div>
    <button type="button" className="sample-welcome-close" onClick={dismiss} aria-label="Dismiss welcome"><X size={16} /></button>
  </section>
}
