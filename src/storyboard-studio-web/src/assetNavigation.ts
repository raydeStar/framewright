import type { AssetSummary } from './types'

/**
 * Stepping through the library from an asset's own view, and finding the
 * image a model was made from. Presentation only.
 */

/** The ids either side of one in a list: what the previous and next arrows go to. */
export function neighbours(ids: string[], id: string): { position: number; total: number; previous?: string; next?: string } | undefined {
  const at = ids.indexOf(id)
  if (at < 0) return undefined
  return { position: at + 1, total: ids.length, previous: ids[at - 1], next: ids[at + 1] }
}

/** "SM_Gear (req 1.2) - concept.png" → "sm_gear (req 1.2)": a name without its file type or its "- concept" label. */
const stem = (name: string) => name.trim().toLowerCase()
  .replace(/\.(png|jpe?g|webp|glb)$/i, '')
  .replace(/\s*[-–]\s*(concept|reference|ref|concept art)\s*$/i, '')
  .trim()
/** "SM_Gear (req 1.2)" → "sm_gear": the name without its requirement tag either. */
const base = (name: string) => stem(name).replace(/\s*\(req[^)]*\)/i, '').trim()

/**
 * The images that could be a model's reference, likeliest first: the image
 * with exactly the model's name (uploads name both "<name> (req x.y)"), then
 * the same name without its requirement tag, then names containing it, then
 * the newest. A search narrows them by name.
 */
export function referenceSuggestions(model: AssetSummary, assets: AssetSummary[], search = ''): AssetSummary[] {
  const name = stem(model.displayName)
  const root = base(model.displayName)
  const wanted = search.trim().toLowerCase()
  const score = (image: AssetSummary) => {
    const candidate = stem(image.displayName)
    if (candidate === name) return 3
    if (root && base(image.displayName) === root) return 2
    if (root && candidate.includes(root)) return 1
    return 0
  }
  return assets
    .filter(asset => asset.kind === 'Image' && !asset.isArchived && (!asset.revisionFamilyId || asset.isCurrentRevision))
    .filter(asset => !wanted || asset.displayName.toLowerCase().includes(wanted))
    .map(asset => ({ asset, score: score(asset) }))
    .sort((a, b) => b.score - a.score || b.asset.createdAt.localeCompare(a.asset.createdAt))
    .map(item => item.asset)
}

/** The image a suggestion list is sure about: one with exactly the model's name. */
export function exactReference(model: AssetSummary, assets: AssetSummary[]): AssetSummary | undefined {
  const name = stem(model.displayName)
  return referenceSuggestions(model, assets).find(image => stem(image.displayName) === name)
}

/**
 * Every model without a reference whose name matches exactly one image's
 * (ignoring a "- concept" label): what "link by name" would do.
 */
export function referencesByName(assets: AssetSummary[]): { model: AssetSummary; image: AssetSummary }[] {
  const images = assets.filter(asset => asset.kind === 'Image' && !asset.isArchived && (!asset.revisionFamilyId || asset.isCurrentRevision))
  return assets
    .filter(asset => asset.kind === 'Model' && !asset.isArchived && !asset.referenceAssetId && (!asset.revisionFamilyId || asset.isCurrentRevision))
    .flatMap(model => {
      const matches = images.filter(image => stem(image.displayName) === stem(model.displayName))
      return matches.length === 1 ? [{ model, image: matches[0] }] : []
    })
}

/** The models made from an image, by any revision of it. */
export function madeFrom(imageIds: string[], assets: AssetSummary[]): AssetSummary[] {
  return assets
    .filter(asset => asset.kind === 'Model' && !asset.isArchived && (!asset.revisionFamilyId || asset.isCurrentRevision)
      && asset.referenceAssetId != null && imageIds.includes(asset.referenceAssetId))
    .sort((a, b) => a.displayName.localeCompare(b.displayName))
}
