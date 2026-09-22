import { useLayoutEffect } from 'react'
import type { AssetSummary } from '../types'
import type { AssetDirectorControls } from './AssetDirectorMode'

/** Publish the displayed revision, never the library's current head. */
export function useAssetDirectorView(asset: AssetSummary, controls: AssetDirectorControls, comparing = false) {
  const { directorMode, onDirectorView } = controls
  useLayoutEffect(() => {
    onDirectorView({ kind: 'asset', assetId: asset.id, contentHash: asset.contentHash, directorMode, comparing })
    // The butler clears the place setting when its guest leaves.
    return () => onDirectorView(undefined)
  }, [asset.id, asset.contentHash, directorMode, comparing, onDirectorView])
}
