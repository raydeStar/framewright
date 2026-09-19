import type { SketchJoint, SketchObject, SketchObjectKind } from '../types'

export const HUMAN_POSES: Record<string, SketchJoint[]> = {
  Neutral: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.31, .46], rightElbow: [.69, .46], leftWrist: [.29, .67], rightWrist: [.71, .67], pelvis: [.5, .53], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.41, .75], rightKnee: [.59, .75], leftAnkle: [.4, .96], rightAnkle: [.6, .96] }),
  Attention: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.35, .25], rightShoulder: [.65, .25], leftElbow: [.37, .47], rightElbow: [.63, .47], leftWrist: [.39, .67], rightWrist: [.61, .67], pelvis: [.5, .53], leftHip: [.44, .55], rightHip: [.56, .55], leftKnee: [.44, .76], rightKnee: [.56, .76], leftAnkle: [.43, .96], rightAnkle: [.57, .96] }),
  Seated: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.32, .44], rightElbow: [.68, .44], leftWrist: [.4, .55], rightWrist: [.6, .55], pelvis: [.5, .53], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.3, .7], rightKnee: [.7, .7], leftAnkle: [.3, .95], rightAnkle: [.7, .95] }),
  Kneeling: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.31, .45], rightElbow: [.69, .45], leftWrist: [.37, .62], rightWrist: [.63, .62], pelvis: [.5, .51], leftHip: [.42, .54], rightHip: [.58, .54], leftKnee: [.34, .74], rightKnee: [.64, .75], leftAnkle: [.52, .91], rightAnkle: [.78, .91] }),
  Prone: joints({ head: [.88, .35], neck: [.78, .43], leftShoulder: [.7, .35], rightShoulder: [.7, .51], leftElbow: [.81, .22], rightElbow: [.84, .62], leftWrist: [.94, .22], rightWrist: [.95, .65], pelvis: [.44, .49], leftHip: [.43, .42], rightHip: [.43, .56], leftKnee: [.23, .45], rightKnee: [.24, .59], leftAnkle: [.04, .56], rightAnkle: [.05, .69] }),
  Walking: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.24, .42], rightElbow: [.76, .42], leftWrist: [.18, .59], rightWrist: [.82, .6], pelvis: [.5, .52], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.3, .73], rightKnee: [.68, .74], leftAnkle: [.18, .92], rightAnkle: [.79, .94] }),
  Running: joints({ head: [.55, .08], neck: [.52, .19], leftShoulder: [.36, .24], rightShoulder: [.67, .2], leftElbow: [.24, .37], rightElbow: [.78, .31], leftWrist: [.38, .45], rightWrist: [.65, .43], pelvis: [.5, .5], leftHip: [.42, .53], rightHip: [.58, .52], leftKnee: [.26, .67], rightKnee: [.73, .68], leftAnkle: [.09, .78], rightAnkle: [.62, .91] }),
  Pointing: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.31, .46], rightElbow: [.82, .27], leftWrist: [.29, .67], rightWrist: [.98, .27], pelvis: [.5, .53], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.41, .75], rightKnee: [.59, .75], leftAnkle: [.4, .96], rightAnkle: [.6, .96] }),
  Reaching: joints({ head: [.5, .1], neck: [.5, .21], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.26, .4], rightElbow: [.76, .12], leftWrist: [.22, .58], rightWrist: [.84, .02], pelvis: [.5, .53], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.41, .75], rightKnee: [.59, .75], leftAnkle: [.4, .96], rightAnkle: [.6, .96] }),
  Conversation: joints({ head: [.5, .09], neck: [.5, .2], leftShoulder: [.34, .25], rightShoulder: [.66, .25], leftElbow: [.22, .4], rightElbow: [.77, .39], leftWrist: [.34, .47], rightWrist: [.87, .3], pelvis: [.5, .53], leftHip: [.42, .55], rightHip: [.58, .55], leftKnee: [.4, .75], rightKnee: [.6, .75], leftAnkle: [.38, .96], rightAnkle: [.62, .96] }),
}

export const HUMAN_CONNECTIONS = [['head', 'neck'], ['neck', 'leftShoulder'], ['neck', 'rightShoulder'], ['leftShoulder', 'leftElbow'], ['leftElbow', 'leftWrist'], ['rightShoulder', 'rightElbow'], ['rightElbow', 'rightWrist'], ['neck', 'pelvis'], ['pelvis', 'leftHip'], ['pelvis', 'rightHip'], ['leftHip', 'leftKnee'], ['leftKnee', 'leftAnkle'], ['rightHip', 'rightKnee'], ['rightKnee', 'rightAnkle']] as const

const POSE_DIMENSIONS: Record<string, { width: number; height: number }> = { Prone: { width: .38, height: .2 }, Seated: { width: .17, height: .38 }, Kneeling: { width: .18, height: .4 }, Running: { width: .22, height: .46 } }

function joints(source: Record<string, [number, number]>): SketchJoint[] {
  return Object.entries(source).map(([name, [x, y]]) => ({ name, x, y }))
}

export function createBlockingObject(kind: SketchObjectKind, ordinal = 1): SketchObject {
  const dimensions = kind === 'Human' ? { width: .14, height: .5 } : kind === 'Doorway' ? { width: .2, height: .48 } : kind === 'Arrow' ? { width: .24, height: .1 } : { width: .2, height: .2 }
  return {
    id: crypto.randomUUID(), kind, x: .5, y: .5, ...dimensions, rotation: 0, color: '#302b27', label: kind === 'Human' ? `Person ${ordinal}` : kind,
    pose: kind === 'Human' ? 'Neutral' : '', facing: kind === 'Human' ? 'Front' : '', build: kind === 'Human' ? 'Neutral' : '', identityMode: kind === 'Human' ? 'Unique person' : '', fullBody: kind === 'Human',
    joints: kind === 'Human' ? cloneJoints(HUMAN_POSES.Neutral) : [],
  }
}

export function cloneJoints(source: SketchJoint[]) { return source.map(joint => ({ ...joint })) }
export function jointMap(item: SketchObject) { return new Map(item.joints.map(joint => [joint.name, joint])) }
export function clamp(value: number, min = 0, max = 1) { return Math.min(max, Math.max(min, value)) }

export function applyPose(item: SketchObject, pose: string): SketchObject {
  const nextDimensions = POSE_DIMENSIONS[pose]
  return { ...item, pose, ...(nextDimensions ?? (item.pose === 'Prone' ? { width: .14, height: .5 } : {})), joints: cloneJoints(HUMAN_POSES[pose] ?? item.joints) }
}

export function drawBlockingObjects(context: CanvasRenderingContext2D, objects: SketchObject[], width: number, height: number) {
  for (const item of objects) {
    context.save()
    context.translate(item.x * width, item.y * height)
    context.rotate(item.rotation * Math.PI / 180)
    context.strokeStyle = item.color
    context.fillStyle = `${item.color}33`
    context.lineWidth = Math.max(3, Math.min(width, height) * .008)
    context.lineCap = 'round'
    context.lineJoin = 'round'
    const objectWidth = item.width * width
    const objectHeight = item.height * height
    if (item.kind === 'Human') {
      const map = jointMap(item)
      const point = (name: string) => { const joint = map.get(name)!; return { x: (joint.x - .5) * objectWidth, y: (joint.y - .5) * objectHeight } }
      for (const [fromName, toName] of HUMAN_CONNECTIONS) {
        const from = point(fromName); const to = point(toName)
        context.beginPath(); context.moveTo(from.x, from.y); context.lineTo(to.x, to.y); context.stroke()
      }
      const head = point('head')
      context.beginPath(); context.arc(head.x, head.y, Math.max(7, objectWidth * .13), 0, Math.PI * 2); context.fill(); context.stroke()
    } else if (item.kind === 'Arrow') {
      context.beginPath(); context.moveTo(-objectWidth / 2, 0); context.lineTo(objectWidth / 2, 0); context.lineTo(objectWidth * .3, -objectHeight / 2); context.moveTo(objectWidth / 2, 0); context.lineTo(objectWidth * .3, objectHeight / 2); context.stroke()
    } else if (item.kind === 'Chair') {
      context.strokeRect(-objectWidth * .36, -objectHeight * .12, objectWidth * .72, objectHeight * .35); context.beginPath(); context.moveTo(-objectWidth * .36, objectHeight * .23); context.lineTo(-objectWidth * .42, objectHeight / 2); context.moveTo(objectWidth * .36, objectHeight * .23); context.lineTo(objectWidth * .42, objectHeight / 2); context.moveTo(-objectWidth * .36, -objectHeight * .12); context.lineTo(-objectWidth * .36, -objectHeight / 2); context.stroke()
    } else if (item.kind === 'Table') {
      context.strokeRect(-objectWidth / 2, -objectHeight * .18, objectWidth, objectHeight * .2); context.beginPath(); context.moveTo(-objectWidth * .4, objectHeight * .02); context.lineTo(-objectWidth * .4, objectHeight / 2); context.moveTo(objectWidth * .4, objectHeight * .02); context.lineTo(objectWidth * .4, objectHeight / 2); context.stroke()
    } else if (item.kind === 'Doorway') {
      context.strokeRect(-objectWidth / 2, -objectHeight / 2, objectWidth, objectHeight); context.beginPath(); context.arc(0, objectHeight / 2, objectWidth * .4, Math.PI, 0); context.stroke()
    } else context.strokeRect(-objectWidth / 2, -objectHeight / 2, objectWidth, objectHeight)
    context.restore()
  }
}
