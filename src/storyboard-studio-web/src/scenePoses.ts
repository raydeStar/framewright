/**
 * Pose presets for the 3D person stand-in. The names match the Sketch blocking
 * kit's 2D poses and the service's list (SceneService.SupportedPoses), so a
 * pose means the same thing everywhere.
 *
 * The figure faces +Z with its left side on +X, and limbs hang along -Y from
 * their joints. Each entry is an XYZ Euler turn in radians at that joint:
 * a negative X turn swings a limb forward, and a positive X turn at a knee
 * folds the shin back. Joints not listed stay at rest.
 */
export const PERSON_POSES = ['Neutral', 'Walking', 'Running', 'Seated', 'Kneeling', 'Pointing', 'Reaching', 'Conversation', 'Prone'] as const
export type PersonPose = typeof PERSON_POSES[number]

export type PersonJoint =
  | 'body' | 'spine' | 'neck'
  | 'leftShoulder' | 'leftElbow' | 'rightShoulder' | 'rightElbow'
  | 'leftHip' | 'leftKnee' | 'rightHip' | 'rightKnee'

export type PoseAngles = Partial<Record<PersonJoint, [number, number, number]>>

const QUARTER = Math.PI / 2

export const POSE_ANGLES: Record<PersonPose, PoseAngles> = {
  Neutral: { leftShoulder: [0, 0, 0.12], rightShoulder: [0, 0, -0.12], leftElbow: [-0.1, 0, 0], rightElbow: [-0.1, 0, 0] },
  Walking: {
    leftHip: [-0.4, 0, 0], leftKnee: [0.15, 0, 0], rightHip: [0.3, 0, 0], rightKnee: [0.35, 0, 0],
    rightShoulder: [-0.35, 0, -0.08], rightElbow: [-0.3, 0, 0], leftShoulder: [0.3, 0, 0.08], leftElbow: [-0.2, 0, 0],
  },
  Running: {
    spine: [0.2, 0, 0],
    leftHip: [-0.9, 0, 0], leftKnee: [0.5, 0, 0], rightHip: [0.5, 0, 0], rightKnee: [1.3, 0, 0],
    rightShoulder: [-0.7, 0, -0.1], rightElbow: [-1.4, 0, 0], leftShoulder: [0.6, 0, 0.1], leftElbow: [-1.2, 0, 0],
  },
  Seated: {
    leftHip: [-QUARTER, 0, 0.05], rightHip: [-QUARTER, 0, -0.05], leftKnee: [QUARTER, 0, 0], rightKnee: [QUARTER, 0, 0],
    leftShoulder: [-0.25, 0, 0.1], rightShoulder: [-0.25, 0, -0.1], leftElbow: [-0.9, 0, 0], rightElbow: [-0.9, 0, 0],
  },
  Kneeling: {
    spine: [0.05, 0, 0],
    leftHip: [-1.4, 0, 0], leftKnee: [1.45, 0, 0], rightHip: [0.1, 0, 0], rightKnee: [QUARTER, 0, 0],
    leftShoulder: [0, 0, 0.12], rightShoulder: [0, 0, -0.12],
  },
  Pointing: { rightShoulder: [-1.5, 0, 0], leftShoulder: [0, 0, 0.12], neck: [0, -0.15, 0] },
  Reaching: { spine: [-0.05, 0, 0], rightShoulder: [-2.9, 0, -0.1], leftShoulder: [0, 0, 0.12] },
  Conversation: {
    neck: [0, 0.2, 0],
    rightShoulder: [-0.35, 0, -0.15], rightElbow: [-1.3, 0, 0], leftShoulder: [-0.15, 0, 0.12], leftElbow: [-0.6, 0, 0],
  },
  // Face down with the arms stretched past the head.
  Prone: { body: [QUARTER, 0, 0], leftShoulder: [-2.9, 0, 0.15], rightShoulder: [-2.9, 0, -0.15] },
}

/** A pose the studio knows, or Neutral for anything else (an older or hand-edited value). */
export function poseAngles(pose: string | null | undefined): PoseAngles {
  return (PERSON_POSES as readonly string[]).includes(pose ?? '') ? POSE_ANGLES[pose as PersonPose] : POSE_ANGLES.Neutral
}

/** The height the figure is modelled at; a stand-in's height scales it uniformly. */
export const PERSON_MODEL_HEIGHT = 1.75
