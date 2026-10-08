import { Box3, BoxGeometry, CapsuleGeometry, Group, Mesh, SphereGeometry, type BufferGeometry, type Material } from 'three'
import { PERSON_MODEL_HEIGHT, poseAngles, type PersonJoint } from '../scenePoses'

/**
 * A simple posable figure for blocking: capsules on a joint tree, posed from a
 * named preset and scaled to the stand-in's height. It is drawn, not saved;
 * the scene stores only the shape, height, and pose name.
 *
 * Its origin sits at the middle of its height, like every other stand-in, and
 * whatever the pose its lowest point is half its height below that, so the
 * same resting height puts a standing, seated, or lying figure on the floor.
 */
export function buildMannequin(height: number, pose: string | null | undefined, material: Material) {
  const angles = poseAngles(pose)
  const part = (geometry: BufferGeometry, x: number, y: number, z = 0) => {
    const mesh = new Mesh(geometry, material)
    mesh.position.set(x, y, z)
    return mesh
  }
  const joint = (name: PersonJoint, x: number, y: number, ...children: (Mesh | Group)[]) => {
    const group = new Group()
    group.name = name
    group.position.set(x, y, 0)
    const turn = angles[name]
    if (turn) group.rotation.set(turn[0], turn[1], turn[2])
    group.add(...children)
    return group
  }

  // Proportions of a 1.75 m adult, measured from the pelvis.
  const arm = (side: 1 | -1) => joint(side === 1 ? 'leftShoulder' : 'rightShoulder', 0.19 * side, 0.48,
    part(new CapsuleGeometry(0.045, 0.21, 4, 10), 0, -0.15),
    joint(side === 1 ? 'leftElbow' : 'rightElbow', 0, -0.3,
      part(new CapsuleGeometry(0.04, 0.19, 4, 10), 0, -0.13),
      part(new SphereGeometry(0.048, 12, 8), 0, -0.29)))
  const leg = (side: 1 | -1) => joint(side === 1 ? 'leftHip' : 'rightHip', 0.09 * side, -0.05,
    part(new CapsuleGeometry(0.065, 0.32, 4, 12), 0, -0.22),
    joint(side === 1 ? 'leftKnee' : 'rightKnee', 0, -0.45,
      part(new CapsuleGeometry(0.05, 0.33, 4, 10), 0, -0.21),
      part(new BoxGeometry(0.09, 0.06, 0.24), 0, -0.44, 0.05)))

  const body = joint('body', 0, 0.95,
    part(new BoxGeometry(0.32, 0.16, 0.2), 0, 0),
    joint('spine', 0, 0.06,
      part(new CapsuleGeometry(0.15, 0.24, 4, 14), 0, 0.22),
      joint('neck', 0, 0.44,
        part(new CapsuleGeometry(0.045, 0.05, 4, 8), 0, 0.03),
        part(new SphereGeometry(0.11, 18, 12), 0, 0.17, 0.01)),
      arm(1), arm(-1)),
    leg(1), leg(-1))

  const figure = new Group()
  figure.add(body)
  figure.scale.setScalar(height / PERSON_MODEL_HEIGHT)
  figure.updateMatrixWorld(true)
  const bounds = new Box3().setFromObject(figure)
  // Rest the lowest point at -height/2 and centre the figure over its origin.
  figure.position.set(
    -(bounds.min.x + bounds.max.x) / 2,
    -height / 2 - bounds.min.y,
    -(bounds.min.z + bounds.max.z) / 2)
  const root = new Group()
  root.add(figure)
  return root
}
