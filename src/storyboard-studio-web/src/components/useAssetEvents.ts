import { useEffect, useRef } from 'react'

/** What the server says changed in the library. `poll` and `reconnected` are the hook's own nudges. */
export interface AssetChangeEvent {
  type: 'asset-added' | 'revision-added' | 'asset-updated' | 'poll' | 'reconnected'
  assetId?: string
  projectId?: string
  kind?: string
  revisionFamilyId?: string | null
  revisionNumber?: number | null
}

/**
 * Hears about library changes as the server saves them, over server-sent
 * events, so an asset an agent uploads appears without a reload. If the
 * stream cannot be held open it falls back to asking every 15 seconds, and
 * after a reconnect it nudges once, since events sent while it was away are
 * not replayed.
 */
export function useAssetEvents(projectId: string, onChange: (event: AssetChangeEvent) => void) {
  const latest = useRef(onChange)
  useEffect(() => { latest.current = onChange })

  useEffect(() => {
    let poll: number | undefined
    let lostTouch = false
    const startPolling = () => {
      if (poll === undefined) poll = window.setInterval(() => latest.current({ type: 'poll' }), 15_000)
    }
    const stopPolling = () => {
      if (poll !== undefined) { window.clearInterval(poll); poll = undefined }
    }
    if (typeof EventSource === 'undefined') { startPolling(); return stopPolling }

    const source = new EventSource('/api/assets/events')
    source.addEventListener('asset', message => {
      try {
        const event = JSON.parse((message as MessageEvent<string>).data) as AssetChangeEvent
        if (!event.projectId || event.projectId === projectId) latest.current(event)
      } catch { /* A malformed event is not worth breaking the page over. */ }
    })
    source.onopen = () => {
      stopPolling()
      if (lostTouch) { lostTouch = false; latest.current({ type: 'reconnected' }) }
    }
    source.onerror = () => {
      lostTouch = true
      // The browser retries on its own; only a stream it has given up on needs the poll.
      if (source.readyState === EventSource.CLOSED) startPolling()
    }
    return () => { source.close(); stopPolling() }
  }, [projectId])
}
