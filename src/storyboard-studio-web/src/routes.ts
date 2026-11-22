import type { ReviewFilter } from './assetReview'
import type { Workspace } from './types'

/**
 * Where the page is, in its address, so a refresh lands back in the same
 * place: /assets/models, an asset of its own (/assets/models/<id>), quick
 * review (/assets/models/review), and the library's filter, sort and search
 * as ?review=&sort=&q=. The server answers any such path with the app, which
 * reads it back here.
 */

export type AssetView = 'all' | 'images' | 'audio' | 'video' | 'models' | 'authorities' | 'archived' | `collection:${string}`
export type AssetSort = 'newest' | 'name' | 'type'

export interface AssetRoute {
  view: AssetView
  assetId?: string
  reviewing?: boolean
  review: ReviewFilter
  sort: AssetSort
  query: string
}

export interface AppRoute {
  workspace: Workspace
  shotId?: string
  assets?: AssetRoute
}

const VIEWS = ['images', 'audio', 'video', 'models', 'authorities', 'archived'] as const
const PLAIN_WORKSPACES: Workspace[] = ['board', 'sounds', 'world', 'scene', 'sequence']
const SHOT_WORKSPACES: Workspace[] = ['shot', 'review']
const REVIEWS: ReviewFilter[] = ['all', 'Pending', 'Approved', 'ChangesRequested']
const SORTS: AssetSort[] = ['newest', 'name', 'type']
const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export const DEFAULT_ASSET_ROUTE: AssetRoute = { view: 'all', review: 'all', sort: 'newest', query: '' }

export function parseRoute(pathname: string, search: string): AppRoute {
  const parts = pathname.split('/').filter(Boolean).map(part => { try { return decodeURIComponent(part) } catch { return part } })
  const [first, ...rest] = parts
  if (first === 'assets') return { workspace: 'assets', assets: parseAssets(rest, new URLSearchParams(search)) }
  if (first && SHOT_WORKSPACES.includes(first as Workspace) && rest[0] && ID.test(rest[0])) return { workspace: first as Workspace, shotId: rest[0] }
  if (first && PLAIN_WORKSPACES.includes(first as Workspace)) return { workspace: first as Workspace }
  return { workspace: 'board' }
}

function parseAssets(parts: string[], query: URLSearchParams): AssetRoute {
  let view: AssetView = 'all'
  let at = 0
  if ((VIEWS as readonly string[]).includes(parts[0])) { view = parts[0] as AssetView; at = 1 }
  else if (parts[0] === 'collection' && parts[1]) { view = `collection:${parts[1]}`; at = 2 }
  const next = parts[at]
  const review = query.get('review') as ReviewFilter | null
  const sort = query.get('sort') as AssetSort | null
  return {
    view,
    assetId: next && ID.test(next) ? next : undefined,
    reviewing: next === 'review' || undefined,
    review: review && REVIEWS.includes(review) ? review : 'all',
    sort: sort && SORTS.includes(sort) ? sort : 'newest',
    query: query.get('q') ?? '',
  }
}

export function formatAssetRoute(route: AssetRoute): string {
  const view = route.view === 'all' ? ''
    : route.view.startsWith('collection:') ? `/collection/${encodeURIComponent(route.view.slice('collection:'.length))}`
    : `/${route.view}`
  const tail = route.reviewing ? '/review' : route.assetId ? `/${route.assetId}` : ''
  const query = new URLSearchParams()
  if (route.review !== 'all') query.set('review', route.review)
  if (route.sort !== 'newest') query.set('sort', route.sort)
  if (route.query) query.set('q', route.query)
  const search = query.toString()
  return `/assets${view}${tail}${search ? `?${search}` : ''}`
}

/** The address of a workspace other than the asset library, which keeps its own. */
export function formatWorkspaceRoute(workspace: Workspace, shotId?: string): string {
  if (SHOT_WORKSPACES.includes(workspace) && shotId) return `/${workspace}/${shotId}`
  if (workspace === 'board' || !PLAIN_WORKSPACES.includes(workspace)) return '/'
  return `/${workspace}`
}

/** The library's place as the current address describes it, or the default when the address is elsewhere. */
export function currentAssetRoute(): AssetRoute {
  if (typeof window === 'undefined') return DEFAULT_ASSET_ROUTE
  return parseRoute(window.location.pathname, window.location.search).assets ?? DEFAULT_ASSET_ROUTE
}

/** Replaces the address without adding a history entry or reloading. */
export function showRoute(path: string) {
  if (typeof window === 'undefined') return
  const current = window.location.pathname + window.location.search
  if (current !== path) window.history.replaceState(window.history.state, '', path)
}
