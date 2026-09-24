import { useMemo, useState } from 'react'
import { BadgeCheck, BookOpenText, Brush, Check, CircleAlert, Globe2, LoaderCircle, Save, ShieldCheck, Sparkles } from 'lucide-react'
import { studioApi } from '../api'
import type { ProjectSummary } from '../types'

const painterlyCelPreset = {
  visualStyle: 'Prestige stylized animation with painterly cel shading, hand-painted textures, graphic shape language, controlled linework, expressive faces, and cinematic lighting. Aim for the visual qualities of premium adult animated drama without imitating a specific copyrighted character or frame.',
  negativeDirectives: 'No photoreal live-action people, glossy game-engine rendering, waxy skin, generic cosplay photography, neon sci-fi bloom, literal stick figures, interface chrome, watermarks, or unintended text.',
}

export default function WorldWorkspace({ project, onSaved }: { project: ProjectSummary; onSaved: (project: ProjectSummary, message: string) => void }) {
  const [draft, setDraft] = useState(project)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [previewOpen, setPreviewOpen] = useState(true)
  const dirty = useMemo(() => ['visualStyle', 'worldCanon', 'promptDirectives', 'negativeDirectives'].some(key => draft[key as keyof ProjectSummary] !== project[key as keyof ProjectSummary]), [draft, project])

  const update = (field: 'visualStyle' | 'worldCanon' | 'promptDirectives' | 'negativeDirectives', value: string) => setDraft(current => ({ ...current, [field]: value }))
  const save = async () => {
    if (!dirty || busy) return
    setBusy(true); setError('')
    try { const saved = await studioApi.updateProject(draft); setDraft(saved); onSaved(saved, 'World settings saved. New generations will use them automatically.') }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'World settings could not be saved.') }
    finally { setBusy(false) }
  }

  // Carries the shared .workspace base like every other surface, so it inherits
  // height, min-width/height and overflow instead of redeclaring them.
  return <main className="workspace world-workspace">
    <header className="world-hero">
      <div><p className="eyebrow"><Globe2 size={14} />Project-wide reference</p><h1>World settings</h1><p>Define the visual language and universal laws once. Every new image, revision, Codex brief, and video manifest inherits them automatically.</p></div>
      <div className="world-inheritance"><ShieldCheck /><span><strong>Used everywhere</strong><small>Included automatically in every new generation</small></span></div>
    </header>

    <section className="world-preset" aria-label="Visual language preset">
      <div><Sparkles /><span><strong>Painterly cel-shaded drama</strong><small>Premium animated storytelling, cinematic light, designed—not photographic—characters.</small></span></div>
      <button onClick={() => setDraft(current => ({ ...current, ...painterlyCelPreset }))}><Check size={15} />Use this direction</button>
    </section>

    <div className="world-grid">
      <WorldField icon={<Brush />} title="Visual language" hint="How every frame should look and feel." value={draft.visualStyle} onChange={value => update('visualStyle', value)} />
      <WorldField icon={<BookOpenText />} title="World canon" hint="Truth that exists even when it is outside the crop." value={draft.worldCanon} onChange={value => update('worldCanon', value)} />
      <WorldField icon={<BadgeCheck />} title="Always preserve" hint="Identity, materials, architecture, and continuity laws." value={draft.promptDirectives} onChange={value => update('promptDirectives', value)} />
      <WorldField icon={<CircleAlert />} title="Avoid / never drift into" hint="Explicit failure modes the generators must reject." value={draft.negativeDirectives} onChange={value => update('negativeDirectives', value)} tone="warning" />
    </div>

    <section className="world-preview">
      <button onClick={() => setPreviewOpen(value => !value)} aria-expanded={previewOpen}><Sparkles size={16} /><span><strong>Inherited prompt preview</strong><small>What providers and Codex receive before the shot-specific brief</small></span><span>{previewOpen ? 'Hide' : 'Show'}</span></button>
      {previewOpen && <pre>PROJECT WORLD SETTINGS (UNIVERSAL - APPLY TO EVERY OUTPUT){'\n'}VISUAL LANGUAGE: {draft.visualStyle}{'\n'}WORLD CANON: {draft.worldCanon}{'\n'}ALWAYS PRESERVE: {draft.promptDirectives}{'\n'}AVOID / NEVER DRIFT INTO: {draft.negativeDirectives}{'\n'}CONSISTENCY PROTOCOL: Face and wardrobe authorities remain separate compatible layers. Facial identity and small marks come from the face authority; clothing and asymmetric accessories come from the wardrobe authority.{'\n'}DIRECTIONAL FEATURES: Left and right mean the subject's anatomical sides, never the viewer's side. Do not mirror scars, eyebrow cuts, sleeves, bracers, jewelry, props, or injuries.{'\n'}SMALL IDENTITY MARKS: Preserve approved marks at their exact location, scale, shape, and subtlety. Keep them readable for the shot scale without exaggerating them.{'\n'}Shot and asset directions add specifics; they do not silently erase these project laws.</pre>}
    </section>

    {error && <p className="world-error" role="alert"><CircleAlert size={16} />{error}</p>}
    <footer className="world-footer"><p><ShieldCheck size={15} />Existing images and videos stay unchanged. New generations use the saved settings.</p><div><button className="secondary" disabled={!dirty || busy} onClick={() => setDraft(project)}>Reset</button><button className="primary" disabled={!dirty || busy} onClick={() => void save()}>{busy ? <LoaderCircle className="spin" /> : <Save />} {busy ? 'Saving world...' : dirty ? 'Save world settings' : 'World settings saved'}</button></div></footer>
  </main>
}

function WorldField({ icon, title, hint, value, onChange, tone }: { icon: React.ReactNode; title: string; hint: string; value: string; onChange: (value: string) => void; tone?: 'warning' }) {
  return <label className={`world-field ${tone ?? ''}`}><span className="world-field-heading">{icon}<span><strong>{title}</strong><small>{hint}</small></span></span><textarea value={value} maxLength={4000} onChange={event => onChange(event.target.value)} /><small className="world-count">{value.length.toLocaleString()} / 4,000</small></label>
}
