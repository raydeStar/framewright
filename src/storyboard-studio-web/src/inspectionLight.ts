import { ACESFilmicToneMapping, NoToneMapping, type ToneMapping } from 'three'

/**
 * The two lights a model can be inspected under.
 *
 * The inspection light is even on purpose: flat ambient, a key, a fill from
 * below and a dim room, with no tone map, so no face of a prop is left
 * unreadable. That is the right light for finding a torn surface and the wrong
 * one for judging paint. It puts about twice the light on the lit side of a
 * surface that its base colour assumes, and shows the result without a tone
 * map, so a mid-value paint shows pastel, metal has little to reflect, and
 * highlights clip.
 *
 * The look-dev light is the other question: what will this surface look like
 * in an engine? The room is turned up to be the main light, the flat ambient
 * and the fill are turned off so occlusion and shading keep their depth, one
 * key stays for shape and a highlight, and the picture goes through a filmic
 * tone map. Its exposure is not a taste: it is the value at which a matte 18%
 * grey card turned to face the key reads 18% grey on screen (sRGB 118), so a
 * base colour on the lit side of a model reads as the colour that was
 * authored. Change the key or the room and the exposure has to be measured
 * again; the unit test holds the three together.
 *
 * Neither light touches the single-channel views. Those are unlit and bypass
 * the tone map, so they show the stored numbers under either.
 */
export const surfaceLights = ['inspection', 'lookdev'] as const

export type SurfaceLight = typeof surfaceLights[number]

/** One light, in three.js's own units. */
export interface LightRig {
  /** Flat light from every direction. */
  ambient: number
  /** The directional key, from above and in front. */
  key: number
  /** The directional fill, from below and behind. */
  fill: number
  /** How bright the room the surface reflects is. */
  environment: number
  toneMapping: ToneMapping
  /** Only read by a tone map; the inspection light has none. */
  exposure: number
}

export const lightRigs: Record<SurfaceLight, LightRig> = {
  inspection: { ambient: 0.75, key: 1.9, fill: 1.1, environment: 0.65, toneMapping: NoToneMapping, exposure: 1 },
  lookdev: { ambient: 0, key: 1.9, fill: 0, environment: 1, toneMapping: ACESFilmicToneMapping, exposure: 0.35 },
}
