import { Armchair, ArrowRight, Copy, DoorOpen, Move, RotateCw, Square, TableProperties, Trash2, UserRound } from 'lucide-react'
import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import type { PosePresetSummary, ReferenceSummary, SketchJoint, SketchObject, SketchObjectKind } from '../types'
import { HUMAN_CONNECTIONS, HUMAN_POSES, applyPose, clamp, cloneJoints, jointMap } from './blockingKitModel'

export function BlockingPalette({ onAdd }: { onAdd: (kind: SketchObjectKind) => void }) {
  const items: Array<[SketchObjectKind, typeof UserRound, string]> = [['Human', UserRound, 'Poseable person'], ['Chair', Armchair, 'Chair'], ['Table', TableProperties, 'Table'], ['Doorway', DoorOpen, 'Doorway'], ['Arrow', ArrowRight, 'Movement arrow'], ['Block', Square, 'Mass / object']]
  return <div className="blocking-palette" role="dialog" aria-label="Blocking kit">
    <header><strong>Blocking kit</strong><small>Drag onto the frame, or tap to add</small></header>
    <div>{items.map(([kind, Icon, label]) => <button type="button" draggable key={kind} onDragStart={event => { event.dataTransfer.effectAllowed = 'copy'; event.dataTransfer.setData('application/x-framewright-blocker', kind); event.dataTransfer.setData('text/plain', kind) }} onClick={() => onAdd(kind)}><Icon size={20} /><span>{label}</span></button>)}</div>
  </div>
}

type DragState = { mode: 'move' | 'resize' | 'rotate' | 'joint'; objectId: string; joint?: string; startX: number; startY: number; original: SketchObject }

export function BlockingLayer({ objects, selectedId, interactive, onSelect, onCommit }: { objects: SketchObject[]; selectedId?: string; interactive: boolean; onSelect: (id?: string) => void; onCommit: (objects: SketchObject[]) => void }) {
  const rootRef = useRef<HTMLDivElement>(null)
  const drag = useRef<DragState | undefined>(undefined)
  const workingRef = useRef(objects)
  const [working, setWorking] = useState(objects)
  useEffect(() => { if (!drag.current) { workingRef.current = objects; setWorking(objects) } }, [objects])
  const updateWorking = (next: SketchObject[]) => { workingRef.current = next; setWorking(next) }
  const begin = (event: ReactPointerEvent<HTMLElement>, item: SketchObject, mode: DragState['mode'], joint?: string) => {
    event.preventDefault(); event.stopPropagation(); event.currentTarget.setPointerCapture(event.pointerId); onSelect(item.id)
    drag.current = { mode, objectId: item.id, joint, startX: event.clientX, startY: event.clientY, original: structuredClone(item) }
  }
  const move = (event: ReactPointerEvent<HTMLDivElement>) => {
    const active = drag.current; const root = rootRef.current
    if (!active || !root) return
    event.preventDefault()
    const rect = root.getBoundingClientRect(); const dx = (event.clientX - active.startX) / rect.width; const dy = (event.clientY - active.startY) / rect.height
    const next = workingRef.current.map(item => {
      if (item.id !== active.objectId) return item
      if (active.mode === 'move') return { ...item, x: clamp(active.original.x + dx), y: clamp(active.original.y + dy) }
      if (active.mode === 'resize') {
        const factor = Math.max(.2, 1 + Math.max(dx / Math.max(.02, active.original.width), dy / Math.max(.02, active.original.height)))
        return { ...item, width: clamp(active.original.width * factor, .03, 1), height: clamp(active.original.height * factor, .03, 1) }
      }
      if (active.mode === 'rotate') {
        const centerX = rect.left + active.original.x * rect.width; const centerY = rect.top + active.original.y * rect.height
        return { ...item, rotation: Math.round(Math.atan2(event.clientY - centerY, event.clientX - centerX) * 180 / Math.PI + 90) }
      }
      const radians = -item.rotation * Math.PI / 180
      const centerX = rect.left + item.x * rect.width; const centerY = rect.top + item.y * rect.height
      const px = event.clientX - centerX; const py = event.clientY - centerY
      const localX = px * Math.cos(radians) - py * Math.sin(radians); const localY = px * Math.sin(radians) + py * Math.cos(radians)
      return { ...item, pose: 'Custom', joints: item.joints.map(joint => joint.name === active.joint ? { ...joint, x: clamp(.5 + localX / (item.width * rect.width)), y: clamp(.5 + localY / (item.height * rect.height)) } : joint) }
    })
    updateWorking(next)
  }
  const end = () => { if (!drag.current) return; drag.current = undefined; onCommit(workingRef.current) }
  return <div ref={rootRef} className={`blocking-layer ${interactive ? 'interactive' : ''}`} onPointerMove={move} onPointerUp={end} onPointerCancel={end} onPointerDown={event => { if (event.target === event.currentTarget) onSelect(undefined) }}>
    {working.map(item => <div key={item.id} className={`blocking-object ${item.kind.toLowerCase()} ${selectedId === item.id ? 'selected' : ''}`} style={{ left: `${item.x * 100}%`, top: `${item.y * 100}%`, width: `${item.width * 100}%`, height: `${item.height * 100}%`, transform: `translate(-50%, -50%) rotate(${item.rotation}deg)`, color: item.color }} role="group" aria-label={`${item.label || item.kind}, ${item.kind} blocker`} onPointerDown={event => begin(event, item, 'move')} onClick={event => { event.stopPropagation(); onSelect(item.id) }}>
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">{item.kind === 'Human' ? <HumanSvg item={item} /> : <ObjectSvg kind={item.kind} />}</svg>
      <span className="blocker-label">{item.label || item.kind}</span>
      {selectedId === item.id && <><button type="button" className="blocker-rotate" aria-label={`Rotate ${item.label || item.kind}`} onPointerDown={event => begin(event, item, 'rotate')}><RotateCw size={13} /></button><button type="button" className="blocker-resize" aria-label={`Resize ${item.label || item.kind}`} onPointerDown={event => begin(event, item, 'resize')}><Move size={13} /></button>{item.kind === 'Human' && item.joints.map(joint => <span key={joint.name} className="joint-handle" data-joint={joint.name} aria-hidden="true" style={{ left: `${joint.x * 100}%`, top: `${joint.y * 100}%` }} onPointerDown={event => begin(event, item, 'joint', joint.name)} />)}</>}
    </div>)}
  </div>
}

function HumanSvg({ item }: { item: SketchObject }) {
  const map = jointMap(item); const point = (name: string) => map.get(name) ?? { name, x: .5, y: .5 }; const head = point('head')
  return <g className={`human-lines build-${item.build.toLowerCase()}`} stroke="currentColor" fill="none" vectorEffect="non-scaling-stroke">{HUMAN_CONNECTIONS.map(([from, to]) => <line key={`${from}-${to}`} x1={point(from).x * 100} y1={point(from).y * 100} x2={point(to).x * 100} y2={point(to).y * 100} />)}<circle cx={head.x * 100} cy={head.y * 100} r="7" fill="color-mix(in srgb, currentColor 15%, transparent)" /></g>
}

function ObjectSvg({ kind }: { kind: SketchObjectKind }) {
  if (kind === 'Arrow') return <g stroke="currentColor" fill="none"><path d="M8 50h82M70 22l20 28-20 28" /></g>
  if (kind === 'Chair') return <g stroke="currentColor" fill="none"><path d="M22 15v48h57M22 44h50v20M30 64l-7 28M67 64l7 28" /></g>
  if (kind === 'Table') return <g stroke="currentColor" fill="none"><path d="M8 38h84v16H8zM20 54v39M80 54v39" /></g>
  if (kind === 'Doorway') return <g stroke="currentColor" fill="none"><path d="M14 94V8h72v86M55 54h2" /></g>
  return <rect x="8" y="8" width="84" height="84" rx="4" stroke="currentColor" fill="color-mix(in srgb, currentColor 12%, transparent)" />
}

export function BlockingInspector({ item, references, posePresets, onChange, onDuplicate, onDelete, onSavePose, onDeletePose }: { item: SketchObject; references: ReferenceSummary[]; posePresets: PosePresetSummary[]; onChange: (item: SketchObject) => void; onDuplicate: () => void; onDelete: () => void; onSavePose: (name: string, joints: SketchJoint[]) => Promise<void>; onDeletePose: (id: string) => Promise<void> }) {
  const [selectedJointName, setSelectedJointName] = useState('rightWrist')
  const [selectedPoseId, setSelectedPoseId] = useState('')
  const [poseName, setPoseName] = useState('')
  const [poseEditing, setPoseEditing] = useState(false)
  const [poseBusy, setPoseBusy] = useState(false)
  const [poseError, setPoseError] = useState<string>()
  const characters = references.filter(reference => reference.category === 'Character')
  const wardrobes = references.filter(reference => reference.category === 'Wardrobe' || reference.category === 'Role' || reference.category === 'Character')
  const selectedJoint = item.joints.find(joint => joint.name === selectedJointName) ?? item.joints[0]
  const updateSelectedJoint = (axis: 'x' | 'y', value: number) => onChange({ ...item, pose: 'Custom', joints: item.joints.map(joint => joint.name === selectedJoint?.name ? { ...joint, [axis]: value } : joint) })
  const applySavedPose = (id: string) => {
    setSelectedPoseId(id)
    const preset = posePresets.find(candidate => candidate.id === id)
    if (preset) onChange({ ...item, pose: 'Custom', joints: cloneJoints(preset.joints) })
  }
  const savePose = async () => {
    if (!poseName.trim() || poseBusy) return
    setPoseBusy(true); setPoseError(undefined)
    try { await onSavePose(poseName.trim(), cloneJoints(item.joints)); setPoseName(''); setPoseEditing(false) }
    catch (reason) { setPoseError(reason instanceof Error ? reason.message : 'Could not save this pose.') }
    finally { setPoseBusy(false) }
  }
  const deletePose = async () => {
    if (!selectedPoseId || poseBusy) return
    setPoseBusy(true); setPoseError(undefined)
    try { await onDeletePose(selectedPoseId); setSelectedPoseId('') }
    catch (reason) { setPoseError(reason instanceof Error ? reason.message : 'Could not delete this pose.') }
    finally { setPoseBusy(false) }
  }
  return <section className="blocking-inspector" aria-label="Selected blocking object">
    <div className="route-heading"><span>Selected blocker</span><small>Editable staging</small></div>
    <label><span>Name</span><input value={item.label} maxLength={100} onChange={event => onChange({ ...item, label: event.target.value })} /></label>
    <div className="blocking-field-grid blocking-ranges"><label><span>Left / right</span><input aria-label="Blocker horizontal position" type="range" min="0" max="1" step="0.01" value={item.x} onChange={event => onChange({ ...item, x: Number(event.target.value) })} /></label><label><span>Up / down</span><input aria-label="Blocker vertical position" type="range" min="0" max="1" step="0.01" value={item.y} onChange={event => onChange({ ...item, y: Number(event.target.value) })} /></label></div>
    <div className="blocking-field-grid blocking-ranges"><label><span>Size</span><input aria-label="Blocker size" type="range" min="0.04" max="0.7" step="0.01" value={item.width} onChange={event => { const nextWidth = Number(event.target.value); const factor = nextWidth / item.width; onChange({ ...item, width: nextWidth, height: Math.min(1, item.height * factor) }) }} /></label><label><span>Rotation</span><input aria-label="Blocker rotation" type="range" min="-180" max="180" step="1" value={item.rotation} onChange={event => onChange({ ...item, rotation: Number(event.target.value) })} /></label></div>
    {item.kind === 'Human' ? <>
      <div className="blocking-field-grid"><label><span>Pose</span><select aria-label="Pose" value={item.pose} onChange={event => onChange(applyPose(item, event.target.value))}>{[...Object.keys(HUMAN_POSES), 'Custom'].map(pose => <option key={pose}>{pose}</option>)}</select></label><label><span>Facing</span><select value={item.facing} onChange={event => onChange({ ...item, facing: event.target.value })}>{['Front', 'Three-quarter left', 'Three-quarter right', 'Profile left', 'Profile right', 'Back'].map(value => <option key={value}>{value}</option>)}</select></label></div>
      <div className="pose-library"><div className="route-heading"><span>My poses</span><small>Project library</small></div><div className="pose-library-row"><select aria-label="Saved poses" value={selectedPoseId} disabled={poseBusy || posePresets.length === 0} onChange={event => applySavedPose(event.target.value)}><option value="">{posePresets.length === 0 ? 'No saved poses yet' : 'Choose pose…'}</option>{posePresets.map(preset => <option key={preset.id} value={preset.id}>{preset.name}</option>)}</select><button type="button" className="secondary compact" onClick={() => { setPoseEditing(value => !value); setPoseError(undefined) }}>{poseEditing ? 'Cancel' : 'Save current'}</button>{selectedPoseId && <button type="button" className="pose-delete" aria-label="Delete selected saved pose" disabled={poseBusy} onClick={() => void deletePose()}><Trash2 size={14} /></button>}</div>{poseEditing && <form className="pose-save-row" onSubmit={event => { event.preventDefault(); void savePose() }}><input autoFocus aria-label="New pose name" maxLength={60} placeholder="e.g. Ceremonial salute" value={poseName} onChange={event => setPoseName(event.target.value)} /><button type="submit" className="primary compact" disabled={poseBusy || !poseName.trim()}>{poseBusy ? 'Saving…' : 'Save pose'}</button></form>}{poseError && <p className="form-error" role="alert">{poseError}</p>}</div>
      <div className="blocking-field-grid"><label><span>Build</span><select value={item.build} onChange={event => onChange({ ...item, build: event.target.value })}>{['Neutral', 'Slender', 'Broad', 'Compact', 'Child'].map(value => <option key={value}>{value}</option>)}</select></label><label><span>Identity</span><select value={item.identityMode} onChange={event => onChange({ ...item, identityMode: event.target.value, characterReferenceId: event.target.value === 'Exact character' ? item.characterReferenceId : undefined })}>{['Unique person', 'Exact character', 'Anonymous'].map(value => <option key={value}>{value}</option>)}</select></label></div>
      {item.identityMode === 'Exact character' && <label><span>Character authority</span><select value={item.characterReferenceId ?? ''} onChange={event => onChange({ ...item, characterReferenceId: event.target.value || undefined })}><option value="">Choose character…</option>{characters.map(reference => <option value={reference.id} key={reference.id}>{reference.name} · v{reference.version}</option>)}</select></label>}
      <label><span>Appearance / role authority</span><select value={item.wardrobeReferenceId ?? ''} onChange={event => onChange({ ...item, wardrobeReferenceId: event.target.value || undefined })}><option value="">Use shot direction</option>{wardrobes.map(reference => <option value={reference.id} key={reference.id}>{reference.name} · v{reference.version}</option>)}</select><small>Transfers clothing, equipment, insignia, and demeanor—not the reference face.</small></label>
      <label className="blocking-check"><input type="checkbox" checked={item.fullBody} onChange={event => onChange({ ...item, fullBody: event.target.checked })} /><span><strong>Keep full body in frame</strong><small>Generation must preserve head-to-toe visibility.</small></span></label>
      {selectedJoint && <div className="joint-controls"><div className="blocking-field-grid"><label><span>Joint</span><select aria-label="Joint to fine tune" value={selectedJoint.name} onChange={event => setSelectedJointName(event.target.value)}>{item.joints.map(joint => <option value={joint.name} key={joint.name}>{joint.name.replace(/([A-Z])/g, ' $1').toLowerCase()}</option>)}</select></label><span className="joint-control-note">Fine positioning</span></div><div className="blocking-field-grid blocking-ranges"><label><span>Joint left / right</span><input aria-label="Joint horizontal position" type="range" min="0" max="1" step="0.01" value={selectedJoint.x} onChange={event => updateSelectedJoint('x', Number(event.target.value))} /></label><label><span>Joint up / down</span><input aria-label="Joint vertical position" type="range" min="0" max="1" step="0.01" value={selectedJoint.y} onChange={event => updateSelectedJoint('y', Number(event.target.value))} /></label></div></div>}
      <p className="blocking-tip">Drag the gold joint handles on the figure, or use the precise joint controls above. Identity and wardrobe stay attached.</p>
    </> : <label><span>Object type</span><input value={item.kind} disabled /></label>}
    <div className="blocking-object-actions"><button type="button" className="secondary compact" onClick={onDuplicate}><Copy size={14} />Duplicate</button><button type="button" className="danger compact" onClick={onDelete}><Trash2 size={14} />Delete</button></div>
  </section>
}
