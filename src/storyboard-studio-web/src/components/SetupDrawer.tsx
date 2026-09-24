import { useCallback, useEffect, useId, useState, type ReactNode } from 'react'
import { BadgeCheck, Blocks, Bot, Check, CircleAlert, Download, LoaderCircle, LockKeyhole, PlugZap, RefreshCw, Sparkles, Unplug, WandSparkles, X } from 'lucide-react'
import { studioApi } from '../api'
import { DELIVERY_PRESETS } from '../projectFormats'
import type { BackupStatus, ComfyUiConnectionTest, CredentialStatus, GenerationSetup, IntegrationSummary, PairingStatusSummary, ProjectSummary, RuntimeReadinessSummary } from '../types'
import Dialog from './Dialog'

/**
 * Production setup, ordered by what an artist needs first: something that can
 * make images, then this project's format, then safety copies and tablet
 * access. The workstation's health checks, paths and build identity are still
 * here, folded into Diagnostics, because they matter when something is wrong
 * and are noise when nothing is.
 */
export default function SetupDrawer({ project, integrations, pairing, busy, onProjectSaved, onRefresh, onRotatePairing, onRevokePairing, onClose, onAskCodex }: {
  project: ProjectSummary
  integrations: IntegrationSummary[]
  pairing?: PairingStatusSummary
  busy: boolean
  onProjectSaved: () => void
  onRefresh: () => void
  onRotatePairing: () => void
  onRevokePairing: () => void
  onClose: () => void
  onAskCodex: () => void
}) {
  const [draft, setDraft] = useState(project)
  const [credential, setCredential] = useState<CredentialStatus>()
  const [backup, setBackup] = useState<BackupStatus>()
  const [runtime, setRuntime] = useState<RuntimeReadinessSummary>()
  const [apiKey, setApiKey] = useState('')
  const [saving, setSaving] = useState(false)
  const [setupError, setSetupError] = useState<string>()
  const [projectStatus, setProjectStatus] = useState<'saved' | 'dirty'>('saved')
  const projectDirty = projectStatus === 'dirty'
  const projectValid = Boolean(draft.name.trim() && draft.production.trim() && draft.sequenceCode.trim() && draft.sequenceName.trim() && draft.aspectRatio.trim() && draft.framesPerSecond >= 1 && draft.framesPerSecond <= 120 && draft.deliveryWidth >= 320 && draft.deliveryHeight >= 180 && draft.deliveryWidth % 2 === 0 && draft.deliveryHeight % 2 === 0 && ['Rec.709', 'Display P3 D65', 'Rec.2020'].includes(draft.colorSpace) && [44100, 48000, 96000].includes(draft.audioSampleRate))
  useEffect(() => { setDraft(project); setProjectStatus('saved') }, [project])
  useEffect(() => { if (pairing?.isLoopback) void studioApi.openAiCredentialStatus().then(setCredential).catch(() => undefined) }, [pairing?.isLoopback])
  const refreshOperationalStatus = useCallback(() => {
    void Promise.all([
      studioApi.backupStatus().then(setBackup),
      studioApi.runtimeStatus().then(setRuntime),
    ]).catch(() => undefined)
  }, [])
  useEffect(() => { refreshOperationalStatus() }, [refreshOperationalStatus])
  const saveProject = async () => { if (!projectDirty || !projectValid) return; setSaving(true); setSetupError(undefined); try { const saved = await studioApi.updateProject(draft); setDraft(saved); setProjectStatus('saved'); onProjectSaved() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not save project settings.') } finally { setSaving(false) } }
  const changeProject = (next: ProjectSummary) => { setDraft(next); setProjectStatus('dirty') }
  const saveCredential = async () => { setSaving(true); setSetupError(undefined); try { setCredential(await studioApi.saveOpenAiCredential(apiKey)); setApiKey(''); onRefresh() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not save the credential.') } finally { setSaving(false) } }
  const removeCredential = async () => { setSaving(true); setSetupError(undefined); try { setCredential(await studioApi.deleteOpenAiCredential()); onRefresh() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not remove the stored credential.') } finally { setSaving(false) } }
  const integration = (id: string) => integrations.find(item => item.id === id)

  // Focus the panel, not the first tabbable control — otherwise the first thing
  // a screen reader announces on open is "Close production setup".
  return <Dialog className="drawer-backdrop" onClose={onClose} labelledBy="setup-title" initialFocus="[data-dialog-focus]"><aside className="setup-drawer" data-testid="setup-drawer" data-dialog-focus tabIndex={-1}>
    <header><div><p className="eyebrow">Settings</p><h2 id="setup-title">Production setup</h2><p>Connect the tools that make images and video, and set this project's format.</p></div><button onClick={onClose} aria-label="Close production setup"><X /></button></header>
    <div className="setup-scroll">
      <section className="setup-section" aria-labelledby="setup-generation-title">
        <h3 className="setup-section-title" id="setup-generation-title">Image and video generation</h3>
        <p className="setup-section-note">Framewright needs at least one generator to make images. You can always sketch, or import images you already have, without one.</p>
        {busy && integrations.length === 0 && <div className="integration-loading"><LoaderCircle className="spin" />Checking what is connected…</div>}
        <GenerationSettings onChanged={onRefresh} comfyUi={integration('comfyui')} codex={integration('codex')} />
        {integration('openai') && <IntegrationCard integration={integration('openai')!}>
          {pairing?.isLoopback && <div className="credential-controls">{credential?.canManageHere ? <><label>API key<input type="password" autoComplete="off" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={credential.isConfigured ? 'Replace configured key' : 'Paste a dedicated project key'} /></label><div className="pairing-actions"><button className="secondary compact" disabled={saving || apiKey.trim().length < 20} onClick={() => void saveCredential()}>Save encrypted</button>{credential.isConfigured && credential.source === 'Windows user credential' && <button className="secondary compact" disabled={saving} onClick={() => void removeCredential()}>Remove stored key</button>}</div><small className="credential-note">{credential.detail}</small></> : <div className="service-credential-status" role="status"><LockKeyhole size={16} /><span><strong>{credential?.isConfigured ? 'Credential supplied by the service environment' : credential ? 'Configure the service environment' : 'Checking service credential status…'}</strong><small>{credential ? credential.isConfigured ? `${credential.source} is active. Change or remove it where the Framewright service is launched.` : 'Set OPENAI_API_KEY in the service or container environment, then restart Framewright.' : 'Interactive credential controls appear only when this host can store them safely.'}</small></span></div>}</div>}
        </IntegrationCard>}
      </section>

      <section className="setup-section" aria-labelledby="setup-project-title">
        <h3 className="setup-section-title" id="setup-project-title">This project</h3>
        <section className="settings-card"><div className="settings-card-title"><strong>Project contract</strong><small>One delivery standard for every approved shot</small></div><div className="delivery-presets" role="group" aria-label="Delivery presets">{DELIVERY_PRESETS.map(preset => <button type="button" key={preset.id} className={draft.deliveryWidth === preset.deliveryWidth && draft.deliveryHeight === preset.deliveryHeight && draft.framesPerSecond === preset.framesPerSecond ? 'selected' : ''} onClick={() => changeProject({ ...draft, aspectRatio: preset.aspectRatio, deliveryWidth: preset.deliveryWidth, deliveryHeight: preset.deliveryHeight, framesPerSecond: preset.framesPerSecond, colorSpace: 'Rec.709', audioSampleRate: 48000 })}><strong>{preset.label}</strong><small>{preset.detail}</small></button>)}</div><div className="form-grid"><label>Project name<input value={draft.name} onChange={event => changeProject({ ...draft, name: event.target.value })} /></label><label>Production<input value={draft.production} onChange={event => changeProject({ ...draft, production: event.target.value })} /></label><label>Sequence code<input value={draft.sequenceCode} onChange={event => changeProject({ ...draft, sequenceCode: event.target.value })} /></label><label>Sequence name<input value={draft.sequenceName} onChange={event => changeProject({ ...draft, sequenceName: event.target.value })} /></label><label>Frame rate<input type="number" min={1} max={120} value={draft.framesPerSecond} onChange={event => changeProject({ ...draft, framesPerSecond: Number(event.target.value) })} /></label><label>Aspect ratio<input value={draft.aspectRatio} onChange={event => changeProject({ ...draft, aspectRatio: event.target.value })} /></label><label>Delivery width<input type="number" min={320} max={7680} step={2} value={draft.deliveryWidth} onChange={event => changeProject({ ...draft, deliveryWidth: Number(event.target.value) })} /></label><label>Delivery height<input type="number" min={180} max={4320} step={2} value={draft.deliveryHeight} onChange={event => changeProject({ ...draft, deliveryHeight: Number(event.target.value) })} /></label><label>Color space<select value={draft.colorSpace} onChange={event => changeProject({ ...draft, colorSpace: event.target.value as ProjectSummary['colorSpace'] })}><option>Rec.709</option><option>Display P3 D65</option><option>Rec.2020</option></select></label><label>Audio master<select value={draft.audioSampleRate} onChange={event => changeProject({ ...draft, audioSampleRate: Number(event.target.value) as ProjectSummary['audioSampleRate'] })}><option value={44100}>44.1 kHz</option><option value={48000}>48 kHz</option><option value={96000}>96 kHz</option></select></label></div><p className="settings-contract-note">Still references may be larger. Review passes use lower-cost proxy canvases; approved output returns to {draft.deliveryWidth} × {draft.deliveryHeight} · {draft.framesPerSecond}fps · {draft.colorSpace} · {draft.audioSampleRate / 1000} kHz.</p><div className="settings-save-row"><span className={projectDirty ? 'dirty' : ''}>{projectDirty ? 'Unsaved project changes' : 'Project contract saved'}</span><button className="secondary compact" disabled={saving || !projectDirty || !projectValid} onClick={() => void saveProject()}>{saving ? 'Saving…' : 'Save project contract'}</button></div></section>
      </section>

      <section className="setup-section" aria-labelledby="setup-safety-title">
        <h3 className="setup-section-title" id="setup-safety-title">Safety copies and tablet access</h3>
        {backup && <section className="settings-card backup-status-card"><div className="settings-card-title"><strong>Project safety copies</strong><small>{backup.scheduled ? 'Automatic daily protection is on' : 'Automatic protection is off'}</small></div><p>{backup.policy}</p><div className="backup-facts"><span><strong>{backup.databaseIntegrity === 'ok' ? 'Healthy' : backup.databaseIntegrity}</strong><small>Database check</small></span><span><strong>{backup.retainedBackups}</strong><small>Retained backups</small></span><span><strong>{backup.latestBackupAt ? new Date(backup.latestBackupAt).toLocaleString() : 'Not yet'}</strong><small>Latest automatic copy</small></span></div></section>}
        <section className={`pairing-card ${pairing?.lanEnabled ? 'enabled' : ''}`}><div><span><strong>Tablet access <em className="preview-tag">Preview</em></strong><small>{pairing?.lanEnabled ? 'Short-lived, in-person pairing' : 'Loopback only - safest default'}</small></span>{pairing?.pairingCode && <code>{pairing.pairingCode.slice(0, 4)} {pairing.pairingCode.slice(4)}</code>}</div><p>{pairing?.securityNote ?? 'Checking the local access boundary...'}</p>{pairing?.lanEnabled && pairing.isLoopback && <div className="pairing-actions"><button className="secondary" onClick={onRotatePairing}>Rotate code</button><button className="secondary" onClick={onRevokePairing}>End tablet sessions</button></div>}</section>
      </section>

      {setupError && <p className="form-error" role="alert">{setupError}</p>}

      <details className="setup-diagnostics">
        <summary>Diagnostics</summary>
        <div className="setup-diagnostics-body">
          <div className="safety-banner"><BadgeCheck size={19} /><div><strong>Production guard is active</strong><p>Framewright only watches work it started. It cannot clear or interrupt jobs it does not own.</p></div></div>
          {runtime && <section className={`settings-card runtime-status-card state-${runtime.status.toLowerCase()}`}><div className="settings-card-title"><strong>Workstation readiness</strong><small>{runtime.status === 'Ready' ? 'Ready to accept durable generation work' : runtime.status === 'Degraded' ? 'Available with an operational warning' : 'New production work is blocked'}</small></div><div className="build-identity" data-testid="build-identity"><span><strong>{runtime.build.version}</strong><small>{runtime.build.channel.replaceAll('-', ' ')}</small></span><code title={runtime.build.commit}>{runtime.build.commit === 'unknown' ? 'commit unknown' : runtime.build.commit.slice(0, 12)}</code><time dateTime={runtime.build.builtAtUtc === 'unknown' ? undefined : runtime.build.builtAtUtc}>{runtime.build.builtAtUtc === 'unknown' ? 'unstamped development build' : `built ${new Date(runtime.build.builtAtUtc).toLocaleString()}`}</time></div><div className="runtime-queue"><span><strong>{runtime.queue.running}</strong><small>Running</small></span><span><strong>{runtime.queue.queued}</strong><small>Queued</small></span><span><strong>{runtime.queue.failed}</strong><small>Failed history</small></span></div><ul className="runtime-checks" tabIndex={0} aria-label="Runtime readiness checks">{runtime.checks.map(check => <li key={check.id} className={`state-${check.state.toLowerCase()}`}><span>{check.state === 'Ready' ? <Check size={14} /> : <Unplug size={14} />}</span><div><strong>{check.id.replaceAll('-', ' ')}</strong><small>{check.detail}</small></div></li>)}</ul></section>}
          <section className="setup-codex"><div className="codex-orb"><Sparkles /></div><div><h3>Let Codex check the setup</h3><p>Codex can inspect this workstation's configuration and suggest what to change. It does not change anything on its own.</p><button onClick={onAskCodex}><Bot size={17} />Open a read-only setup pass</button></div></section>
        </div>
      </details>
    </div>
    <footer><div className="setup-downloads"><a className="secondary backup-link" href="/api/maintenance/backup" download><Download size={16} />Backup</a>{pairing?.isLoopback && <a className="secondary backup-link diagnostic-link" href="/api/maintenance/diagnostics" download><Download size={16} />QA diagnostics</a>}</div><button className="secondary" onClick={() => { onRefresh(); refreshOperationalStatus() }} disabled={busy}><RefreshCw className={busy ? 'spin' : ''} size={16} />Refresh status</button><button className="primary" onClick={onClose}>Done</button></footer>
  </aside></Dialog>
}

/**
 * The switches that decide whether Framewright may send work to ComfyUI or
 * Codex. They are off on a new workstation, and each one is still only a
 * permission: nothing is sent until the artist presses Generate.
 */
function GenerationSettings({ comfyUi, codex, onChanged }: { comfyUi?: IntegrationSummary; codex?: IntegrationSummary; onChanged: () => void }) {
  const [setup, setSetup] = useState<GenerationSetup>()
  const [endpoint, setEndpoint] = useState('')
  const [imagesEnabled, setImagesEnabled] = useState(false)
  const [videoEnabled, setVideoEnabled] = useState(false)
  const [codexOneClick, setCodexOneClick] = useState(false)
  const [loadError, setLoadError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string>()
  const [saved, setSaved] = useState(false)
  const [testing, setTesting] = useState(false)
  const [test, setTest] = useState<ComfyUiConnectionTest>()

  const adopt = (next: GenerationSetup) => {
    setSetup(next)
    setEndpoint(next.comfyUi.endpoint); setImagesEnabled(next.comfyUi.imagesEnabled); setVideoEnabled(next.comfyUi.videoEnabled)
    setCodexOneClick(next.codex.oneClickImages)
  }
  useEffect(() => { studioApi.generationSetup().then(adopt).catch(reason => setLoadError(reason instanceof Error ? reason.message : 'Could not read the generation settings.')) }, [])

  const dirty = Boolean(setup) && (endpoint.trim() !== setup!.comfyUi.endpoint || imagesEnabled !== setup!.comfyUi.imagesEnabled || videoEnabled !== setup!.comfyUi.videoEnabled || codexOneClick !== setup!.codex.oneClickImages)
  const editable = Boolean(setup?.canManageHere)
  const save = async () => {
    setSaving(true); setSaveError(undefined); setSaved(false)
    try {
      adopt(await studioApi.saveGenerationSetup({ comfyUiEndpoint: endpoint.trim(), comfyUiImagesEnabled: imagesEnabled, comfyUiVideoEnabled: videoEnabled, codexOneClickImages: codexOneClick }))
      setSaved(true); onChanged()
    } catch (reason) { setSaveError(reason instanceof Error ? reason.message : 'Could not save the generation settings.') }
    finally { setSaving(false) }
  }
  const runTest = async () => {
    setTesting(true); setTest(undefined)
    try { setTest(await studioApi.testComfyUi(endpoint.trim())) }
    catch (reason) { setTest({ reachable: false, detail: reason instanceof Error ? reason.message : 'The connection test could not run.' }) }
    finally { setTesting(false) }
  }
  const isLocked = (locked: string[] | undefined, key: string) => Boolean(locked?.includes(key))
  const lockedNote = (locked: string[] | undefined, key: string) => locked?.includes(key) ? <small className="setting-locked"><LockKeyhole size={12} />Set outside Framewright, by an environment variable or launch option.</small> : null

  if (loadError) return <p className="form-error" role="alert"><CircleAlert size={15} />{loadError}</p>
  return <div className="generation-settings" data-testid="generation-settings">
    {comfyUi && <IntegrationCard integration={comfyUi}>
      <div className="generation-setting-controls">
        <label className="setting-field">ComfyUI address
          <span className="setting-field-row">
            <input value={endpoint} onChange={event => { setEndpoint(event.target.value); setTest(undefined); setSaved(false) }} disabled={!editable || isLocked(setup?.comfyUi.locked, 'endpoint')} placeholder="http://127.0.0.1:8188" spellCheck={false} />
            <button type="button" className="secondary compact" onClick={() => void runTest()} disabled={testing || !endpoint.trim()}>{testing ? <LoaderCircle className="spin" size={15} /> : <PlugZap size={15} />}{testing ? 'Testing…' : 'Test connection'}</button>
          </span>
        </label>
        {test && <p className={`setting-test ${test.reachable ? 'is-ok' : 'is-failed'}`} role="status">{test.reachable ? <Check size={14} /> : <Unplug size={14} />}{test.detail}</p>}
        {lockedNote(setup?.comfyUi.locked, 'endpoint')}
        <Toggle label="Make draft images with ComfyUI" detail="Adds jobs to ComfyUI's queue when you press Generate. Never interrupts work Framewright didn't start." checked={imagesEnabled} disabled={!editable || isLocked(setup?.comfyUi.locked, 'imagesEnabled')} onChange={value => { setImagesEnabled(value); setSaved(false) }} />
        {lockedNote(setup?.comfyUi.locked, 'imagesEnabled')}
        <Toggle label="Make video with ComfyUI" detail="Turns an approved frame into a short video take. Needs the video workflow and models in ComfyUI." checked={videoEnabled} disabled={!editable || isLocked(setup?.comfyUi.locked, 'videoEnabled')} onChange={value => { setVideoEnabled(value); setSaved(false) }} />
        {lockedNote(setup?.comfyUi.locked, 'videoEnabled')}
      </div>
    </IntegrationCard>}
    {codex && <IntegrationCard integration={codex}>
      <div className="generation-setting-controls">
        <Toggle label="One-click Codex images" detail="Generate buttons send the shot to Codex ImageGen with your ChatGPT sign-in. No API key is needed." checked={codexOneClick} disabled={!editable || isLocked(setup?.codex.locked, 'oneClickImages')} onChange={value => { setCodexOneClick(value); setSaved(false) }} />
        {lockedNote(setup?.codex.locked, 'oneClickImages')}
      </div>
    </IntegrationCard>}
    {setup && !editable && <p className="setting-readonly"><LockKeyhole size={14} />These switches can only be changed on the workstation itself, not from a paired tablet.</p>}
    {setup && editable && <div className="settings-save-row"><span className={dirty ? 'dirty' : ''}>{dirty ? 'Unsaved generation changes' : saved ? 'Generation settings saved' : 'Nothing is sent until you press Generate'}</span><button className="primary compact" disabled={saving || !dirty} onClick={() => void save()}>{saving ? 'Saving…' : 'Save generation settings'}</button></div>}
    {saveError && <p className="form-error" role="alert"><CircleAlert size={15} />{saveError}</p>}
  </div>
}

// The switch is named by its label alone; the explanation is its description,
// so assistive technology (and a search for "API key") hears one short name.
function Toggle({ label, detail, checked, disabled, onChange }: { label: string; detail: string; checked: boolean; disabled: boolean; onChange: (value: boolean) => void }) {
  const id = useId()
  return <label className={`setting-toggle ${disabled ? 'is-disabled' : ''}`}>
    <input type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={event => onChange(event.target.checked)} aria-labelledby={`${id}-label`} aria-describedby={`${id}-detail`} />
    <span className="setting-toggle-track" aria-hidden="true"><span /></span>
    <span className="setting-toggle-copy"><strong id={`${id}-label`}>{label}</strong><small id={`${id}-detail`}>{detail}</small></span>
  </label>
}

function IntegrationCard({ integration, children }: { integration: IntegrationSummary; children?: ReactNode }) {
  const stateIcon = integration.state === 'Connected' || integration.state === 'Ready' ? <Check size={15} /> : integration.state === 'Protected' ? <BadgeCheck size={15} /> : <Unplug size={15} />
  return <article className={`integration-card state-${integration.state.toLowerCase()}`}><div className="integration-icon">{integration.id === 'codex' ? <Bot /> : integration.id === 'comfyui' ? <Blocks /> : <WandSparkles />}</div><div className="integration-copy"><div><h3>{integration.name}{integration.id === 'openai' && <em className="preview-tag">Preview</em>}</h3><span>{stateIcon}{integration.headline}</span></div><p>{integration.detail}</p>{integration.endpoint && integration.id !== 'comfyui' && <code>{integration.endpoint}</code>}{children}</div></article>
}
