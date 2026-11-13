import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DoubleSide, MeshBasicMaterial, MeshStandardMaterial, ShaderChunk, ShaderLib, SRGBColorSpace, Texture } from 'three'
import { channelMaterial, openingLook, surfaceChannels, type SurfaceChannel } from '../src/surfaceChannels.ts'

/**
 * What each single-channel view is made of, without a GPU. Nothing here draws
 * a pixel: the rules under test are which map and which number each stand-in
 * reads, that the file's own material is left alone, and that the one line of
 * shader the data channels add still has somewhere to go in this three.js.
 */

const painted = () => {
  const colour = new Texture()
  colour.colorSpace = SRGBColorSpace
  const normal = new Texture()
  // glTF packs roughness (green) and metalness (blue) into one texture.
  const packed = new Texture()
  const own = new MeshStandardMaterial({
    color: 0x336699, map: colour, normalMap: normal,
    roughness: 0.8, roughnessMap: packed, metalness: 0.6, metalnessMap: packed,
    side: DoubleSide, alphaTest: 0.5, vertexColors: true,
  })
  return { own, colour, normal, packed }
}

/** The fragment shader a stand-in would be compiled from. */
const compiled = (stand: MeshBasicMaterial) => {
  const shader = { fragmentShader: ShaderLib.basic.fragmentShader }
  stand.onBeforeCompile(shader as never, undefined as never)
  return shader.fragmentShader
}

const grey = (stand: MeshBasicMaterial) => [stand.color.r, stand.color.g, stand.color.b]

test('the control offers Lit first, then the four channels', () => {
  assert.deepEqual(surfaceChannels.map(item => item.id), ['lit', 'colour', 'normal', 'roughness', 'metal'])
  assert.deepEqual(surfaceChannels.map(item => item.label), ['Lit', 'Colour', 'Normal', 'Roughness', 'Metal'])
})

test('Colour is the base colour map with its factor, as a picture', () => {
  const { own, colour } = painted()
  const stand = channelMaterial(own, 'colour')
  assert.equal(stand.map, colour)
  assert.equal(stand.color.getHex(), own.color.getHex())
  assert.notEqual(stand.color, own.color)
  assert.equal(stand.vertexColors, true)
  assert.equal(stand.alphaTest, 0.5)
  // A picture is already decoded by its colour space: no shader tweak.
  assert.equal(compiled(stand), ShaderLib.basic.fragmentShader)
})

test('Normal shows the normal map as stored', () => {
  const { own, normal } = painted()
  const stand = channelMaterial(own, 'normal')
  assert.equal(stand.map, normal)
  assert.deepEqual(grey(stand), [1, 1, 1])
  assert.match(compiled(stand), /diffuseColor\.rgb = sRGBTransferEOTF\( vec4\( diffuseColor\.rgb, 1\.0 \) \)\.rgb;/)
})

test('Roughness reads green and Metal reads blue of the packed texture, times the factor', () => {
  const { own, packed } = painted()
  const roughness = channelMaterial(own, 'roughness')
  assert.equal(roughness.map, packed)
  assert.deepEqual(grey(roughness), [0.8, 0.8, 0.8])
  assert.match(compiled(roughness), /diffuseColor\.rgb = sRGBTransferEOTF\( vec4\( diffuseColor\.ggg, 1\.0 \) \)\.rgb;/)

  const metal = channelMaterial(own, 'metal')
  assert.equal(metal.map, packed)
  assert.deepEqual(grey(metal), [0.6, 0.6, 0.6])
  assert.match(compiled(metal), /diffuseColor\.rgb = sRGBTransferEOTF\( vec4\( diffuseColor\.bbb, 1\.0 \) \)\.rgb;/)
})

test('with no map, the material’s own number is shown as a flat colour', () => {
  const own = new MeshStandardMaterial({ color: 0xcc8844, roughness: 0.35, metalness: 1 })
  const colour = channelMaterial(own, 'colour')
  assert.equal(colour.map, null)
  assert.equal(colour.color.getHex(), 0xcc8844)
  const roughness = channelMaterial(own, 'roughness')
  assert.equal(roughness.map, null)
  assert.deepEqual(grey(roughness), [0.35, 0.35, 0.35])
  const metal = channelMaterial(own, 'metal')
  assert.equal(metal.map, null)
  assert.deepEqual(grey(metal), [1, 1, 1])
})

test('a missing normal map is the flat mid-blue of an unchanged surface', () => {
  const stand = channelMaterial(new MeshStandardMaterial(), 'normal')
  assert.equal(stand.map, null)
  assert.deepEqual(grey(stand), [0.5, 0.5, 1])
})

test('an unlit material has no roughness or metalness, and none is invented as a map', () => {
  const own = new MeshBasicMaterial({ color: 0x808080 })
  assert.deepEqual(grey(channelMaterial(own, 'roughness')), [1, 1, 1])
  assert.deepEqual(grey(channelMaterial(own, 'metal')), [0, 0, 0])
})

test('every channel is unlit, untoned and faces the way the file’s material does', () => {
  const { own } = painted()
  for (const channel of ['colour', 'normal', 'roughness', 'metal'] as const) {
    const stand = channelMaterial(own, channel)
    assert.ok(stand instanceof MeshBasicMaterial, channel)
    assert.equal(stand.side, DoubleSide, channel)
    assert.equal(stand.toneMapped, false, channel)
  }
})

test('the file’s own material is left exactly as it was', () => {
  const { own, colour, normal, packed } = painted()
  const before = JSON.stringify(own.toJSON())
  const untouched = own.onBeforeCompile
  for (const channel of surfaceChannels.map(item => item.id).filter((id): id is Exclude<SurfaceChannel, 'lit'> => id !== 'lit'))
    channelMaterial(own, channel)
  assert.equal(JSON.stringify(own.toJSON()), before)
  assert.equal(own.onBeforeCompile, untouched)
  assert.deepEqual([own.map, own.normalMap, own.roughnessMap, own.metalnessMap], [colour, normal, packed, packed])
  assert.equal(colour.colorSpace, SRGBColorSpace)
})

test('the three data channels compile to three different programs', () => {
  const { own } = painted()
  const keys = (['normal', 'roughness', 'metal'] as const).map(channel => channelMaterial(own, channel).customProgramCacheKey())
  assert.equal(new Set(keys).size, 3)
})

test('this three.js still has the place and the function the data channels rely on', () => {
  // An upgrade that renamed either would leave the channels drawing without
  // their decode, and nothing but the eye would notice.
  assert.equal(ShaderLib.basic.fragmentShader.split('#include <map_fragment>').length, 2)
  assert.match(ShaderChunk.colorspace_pars_fragment, /vec4 sRGBTransferEOTF\( in vec4 value \)/)
})

test('a newly opened model is painted, lit and without wireframe, and each viewer gets its own look', () => {
  const first = openingLook()
  assert.deepEqual(first, { painted: true, wired: false, channel: 'lit' })
  // A look is mutated in place as the reviewer changes it, so two holders
  // must never share one: changing channel on one stack would change another.
  first.channel = 'normal'
  assert.equal(openingLook().channel, 'lit')
})
