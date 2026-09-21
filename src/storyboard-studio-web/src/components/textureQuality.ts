import { Mesh, type Object3D, type WebGLRenderer } from 'three'

/**
 * Sample every texture on a loaded model with as much anisotropy as the GPU
 * offers.
 *
 * Three's default is 1: a texture seen at a grazing angle -- the side of a
 * sleeve, the top of a shoulder as the camera pans past -- is filtered with an
 * isotropic footprint and smears along the surface, and the 4096 sheet a hero
 * was painted onto reads no sharper there than a 512 one would. Anisotropic
 * filtering is the difference between a pan that holds and one that does not,
 * and it costs nothing the viewer would notice.
 *
 * The file's own textures are changed in place, because the plain-clay toggle
 * puts the same material objects back and would otherwise lose the setting.
 */
export function sharpenTextures(root: Object3D, renderer: WebGLRenderer) {
  const most = renderer.capabilities.getMaxAnisotropy()
  if (most <= 1) return
  root.traverse(node => {
    if (!(node instanceof Mesh) || !node.material) return
    for (const material of Array.isArray(node.material) ? node.material : [node.material]) {
      for (const value of Object.values(material)) {
        const texture = value as { isTexture?: boolean; anisotropy?: number; needsUpdate?: boolean } | null
        if (!texture || typeof texture !== 'object' || !texture.isTexture) continue
        if ((texture.anisotropy ?? 1) >= most) continue
        texture.anisotropy = most
        texture.needsUpdate = true
      }
    }
  })
}
