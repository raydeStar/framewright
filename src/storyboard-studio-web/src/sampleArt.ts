import { createContext } from 'react'

/**
 * Whether placeholder frames may use the illustrated demo art.
 *
 * The seeded sample production has no images of its own, so its shots and
 * references are drawn procedurally. Anywhere else that art would pass off
 * someone else's composition as the artist's first sketch, so an empty frame
 * says plainly that there is no image yet.
 */
export const SampleArtContext = createContext(false)
