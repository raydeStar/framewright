/**
 * Looking through the shot camera. A render keeps the shot camera's vertical
 * field of view and takes the delivery aspect, so the stage shows the same
 * picture by fitting a delivery-shaped frame inside itself.
 *
 * When the delivery frame is wider than the stage (a 16:9 shot on a portrait
 * tablet), keeping the vertical field would crop the sides of the shot off
 * screen. The stage then widens its own field of view until the shot's full
 * width fits, and the frame is letterboxed top and bottom instead.
 */

const toRadians = (degrees: number) => degrees * Math.PI / 180
const toDegrees = (radians: number) => radians * 180 / Math.PI

/** The vertical field of view the stage draws with so the whole delivery frame is visible. */
export function framedFieldOfView(shotFieldOfView: number, frameAspect: number, stageAspect: number) {
  if (!(frameAspect > 0) || !(stageAspect > 0) || frameAspect <= stageAspect) return shotFieldOfView
  return toDegrees(2 * Math.atan(Math.tan(toRadians(shotFieldOfView) / 2) * frameAspect / stageAspect))
}

/** Where the delivery frame sits on the stage, in stage pixels. */
export function frameGuide(stageWidth: number, stageHeight: number, frameAspect: number) {
  if (!(stageWidth > 0) || !(stageHeight > 0) || !(frameAspect > 0)) return { left: 0, top: 0, width: 0, height: 0 }
  const stageAspect = stageWidth / stageHeight
  const width = frameAspect <= stageAspect ? stageHeight * frameAspect : stageWidth
  const height = frameAspect <= stageAspect ? stageHeight : stageWidth / frameAspect
  return { left: (stageWidth - width) / 2, top: (stageHeight - height) / 2, width, height }
}
