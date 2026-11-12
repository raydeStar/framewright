import { MeshBasicMaterial, type Material, type MeshStandardMaterial } from 'three'

/**
 * One part of a painted surface at a time.
 *
 * The lit view mixes base colour, normal map, roughness and metalness into one
 * picture, so a fault seen there could live in any of them. Each channel here
 * is shown alone and unlit, which answers "which map is wrong?" in the time it
 * takes to press a button.
 *
 * Every channel is a stand-in material beside the file's own, sharing its
 * textures. The file's material is never edited, so going back to Lit is
 * putting the original back, not rebuilding it.
 */
export const surfaceChannels = [
  { id: 'lit', label: 'Lit', hint: 'The finished surface, lit' },
  { id: 'colour', label: 'Colour', hint: 'Base colour only, unlit' },
  { id: 'normal', label: 'Normal', hint: 'The normal map as colour, unlit' },
  { id: 'roughness', label: 'Roughness', hint: 'Roughness as grey, unlit: white is rough' },
  { id: 'metal', label: 'Metal', hint: 'Metalness as grey, unlit: white is metal' },
] as const

export type SurfaceChannel = typeof surfaceChannels[number]['id']

/**
 * How a surface is being looked at: paint or clay, wireframe, and which
 * channel. A reviewer comparing two revisions' normal maps is comparing that
 * channel, so the look is carried to the next revision like the camera is.
 */
export interface SurfaceLook {
  painted: boolean
  wired: boolean
  channel: SurfaceChannel
}

/** A newly opened model: painted, no wireframe, lit. */
export const openingLook = (): SurfaceLook => ({ painted: true, wired: false, channel: 'lit' })

/** What a file's material may carry. An unlit one has no roughness or metalness at all. */
type Painted = Material & Partial<Pick<MeshStandardMaterial,
  'color' | 'map' | 'normalMap' | 'roughness' | 'roughnessMap' | 'metalness' | 'metalnessMap'>>

const mapChunk = '#include <map_fragment>'

/**
 * Colour maps are pictures; normal, roughness and metalness maps are numbers.
 * The renderer encodes whatever it draws for an sRGB screen, which is right
 * for a picture and would lift a stored 0.5 to a pale 0.74. Decoding first
 * cancels that encoding, so the screen shows the number the file holds.
 *
 * glTF packs roughness in green and metalness in blue of one texture, so the
 * same line also picks the channel to read.
 */
function asStored(stand: MeshBasicMaterial, pick: 'rgb' | 'ggg' | 'bbb') {
  stand.onBeforeCompile = shader => {
    shader.fragmentShader = shader.fragmentShader.replace(mapChunk,
      `${mapChunk}\n\tdiffuseColor.rgb = sRGBTransferEOTF( vec4( diffuseColor.${pick}, 1.0 ) ).rgb;`)
  }
  // Compiled programs are shared by this key, and the three variants differ
  // only in the letters picked: without it, metal would reuse roughness's.
  stand.customProgramCacheKey = () => pick
  return stand
}

/**
 * The unlit stand-in that shows one channel of a file's material.
 *
 * The material's own factor multiplies its map, as it does when lit, so what
 * is shown is the value the lit surface uses. With no map the factor alone is
 * shown as a flat colour, and a missing normal map as the flat mid-blue that
 * means "no change to the surface".
 */
export function channelMaterial(own: Material, channel: Exclude<SurfaceChannel, 'lit'>): MeshBasicMaterial {
  const painted = own as Painted
  const stand = new MeshBasicMaterial({ side: own.side, toneMapped: false })
  if (channel === 'colour') {
    if (painted.color) stand.color.copy(painted.color)
    stand.map = painted.map ?? null
    // Vertex colour and a cut-out edge are part of the base colour too.
    stand.vertexColors = own.vertexColors
    stand.alphaTest = own.alphaTest
    return stand
  }
  if (channel === 'normal') {
    stand.map = painted.normalMap ?? null
    if (!stand.map) stand.color.setRGB(0.5, 0.5, 1)
    return asStored(stand, 'rgb')
  }
  if (channel === 'roughness') {
    stand.color.setScalar(painted.roughness ?? 1)
    stand.map = painted.roughnessMap ?? null
    return asStored(stand, 'ggg')
  }
  stand.color.setScalar(painted.metalness ?? 0)
  stand.map = painted.metalnessMap ?? null
  return asStored(stand, 'bbb')
}
