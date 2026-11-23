import { test } from 'node:test'
import assert from 'node:assert/strict'
import { ACESFilmicToneMapping, NoToneMapping, ShaderChunk } from 'three'
import { lightRigs, surfaceLights } from '../src/inspectionLight.ts'
import { openingLook } from '../src/surfaceChannels.ts'

/**
 * The two lights, without a GPU. Nothing here draws a pixel: the rules under
 * test are that the inspection light is still the light it always was, that
 * the look-dev light is the rig it claims to be, and that its exposure still
 * belongs to its key and its room.
 */

test('there are two lights, and a newly opened model is under the inspection one', () => {
  assert.deepEqual([...surfaceLights], ['inspection', 'lookdev'])
  assert.deepEqual(Object.keys(lightRigs), ['inspection', 'lookdev'])
  assert.equal(openingLook().light, 'inspection')
})

test('the inspection light is exactly the light the viewer had before there were two', () => {
  // These are the numbers the viewer was built with. Changing one changes the
  // default picture of every model in every library, which is never a side
  // effect of working on the other light.
  assert.deepEqual(lightRigs.inspection, {
    ambient: 0.75, key: 1.9, fill: 1.1, environment: 0.65, toneMapping: NoToneMapping, exposure: 1,
  })
})

test('the look-dev light is the room and one key, through a filmic tone map', () => {
  const rig = lightRigs.lookdev
  assert.equal(rig.ambient, 0, 'no flat ambient: it fills the occlusion a surface is judged by')
  assert.equal(rig.fill, 0, 'one key and no second light')
  assert.ok(rig.key > 0)
  assert.ok(Math.abs(rig.environment - 1) <= 0.1, 'the room is the main light, near its own brightness')
  assert.equal(rig.toneMapping, ACESFilmicToneMapping)
})

/**
 * What three.js's filmic curve does to a grey: the same arithmetic as its
 * shader, where a neutral colour passes through both colour matrices
 * unchanged. Display-linear out for scene-linear in.
 */
const filmic = (sceneLinear: number, exposure: number) => {
  const value = sceneLinear * exposure / 0.6
  return (value * (value + 0.0245786) - 0.000090537) / (value * (0.983729 * value + 0.4329510) + 0.238081)
}

test('this three.js still tone-maps with the curve the exposure was worked out for', () => {
  const chunk = ShaderChunk.tonemapping_pars_fragment
  assert.match(chunk, /vec3 a = v \* \( v \+ 0\.0245786 \) - 0\.000090537;/)
  assert.match(chunk, /vec3 b = v \* \( 0\.983729 \* v \+ 0\.4329510 \) \+ 0\.238081;/)
  assert.match(chunk, /color \*= toneMappingExposure \/ 0\.6;/)
})

test('under the look-dev light a grey card facing the key reads the grey it is', () => {
  const rig = lightRigs.lookdev
  // Measured, not derived: what a matte card facing the key picks up from the
  // viewer's room at brightness 1, as a multiple of the card's own colour.
  // Read back from a rendered card in three.js 0.186; a different room, or a
  // three.js that builds this one differently, needs it measured again.
  const roomTowardsKey = 1.88
  // A directional light of strength s adds s / pi to a matte surface facing it.
  const onTheCard = roomTowardsKey * rig.environment + rig.key / Math.PI
  const shown = filmic(0.18 * onTheCard, rig.exposure)
  assert.ok(Math.abs(shown - 0.18) < 0.01, `an 18% card shows as ${(shown * 100).toFixed(1)}%`)
})

test('the same card under the inspection light is more than twice as bright as it is', () => {
  // The reason there is a second light at all. If this ever stops being true
  // the inspection light has been changed, and the test above it says so too.
  const rig = lightRigs.inspection
  const onTheCard = 1.88 * rig.environment + rig.key / Math.PI + rig.ambient / Math.PI
  assert.ok(onTheCard > 2, `a surface facing the key is lit ${onTheCard.toFixed(2)} times over`)
})

test('the filmic curve leaves room above white paint instead of clipping it', () => {
  const rig = lightRigs.lookdev
  const onTheCard = 1.88 * rig.environment + rig.key / Math.PI
  const white = filmic(0.8 * onTheCard, rig.exposure)
  assert.ok(white > 0.5 && white < 0.8, `80% white shows as ${(white * 100).toFixed(1)}%`)
  // And the scale keeps its order: darker paint is still darker.
  const shown = [0.02, 0.05, 0.18, 0.4, 0.8].map(albedo => filmic(albedo * onTheCard, rig.exposure))
  assert.deepEqual([...shown].sort((a, b) => a - b), shown)
})
