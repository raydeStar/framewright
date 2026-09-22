import type { SceneEnvironmentSummary, ScenePointLightSummary } from '../types'

/** Local lights stay ordinary saved scene data: candles deserve controls too. */
export default function SceneLightingControls({ environment, onChange }: {
  environment: SceneEnvironmentSummary
  onChange: (value: SceneEnvironmentSummary) => void
}) {
  const lights = environment.pointLights ?? []
  const changeLight = (id: string, patch: Partial<ScenePointLightSummary>) =>
    onChange({ ...environment, pointLights: lights.map(light => light.id === id ? { ...light, ...patch } : light) })
  return <details>
    <summary>Color and local lights</summary>
    <label>Key color <input type="color" aria-label="Key light color" value={environment.keyColor ?? '#ffffff'} onChange={event => onChange({ ...environment, keyColor: event.target.value })} /></label>
    <label>Ambient color <input type="color" aria-label="Ambient light color" value={environment.ambientColor ?? '#ffffff'} onChange={event => onChange({ ...environment, ambientColor: event.target.value })} /></label>
    <label>Background <input type="color" aria-label="Scene background color" value={environment.backgroundColor ?? '#171b19'} onChange={event => onChange({ ...environment, backgroundColor: event.target.value })} /></label>
    <label><input type="checkbox" checked={environment.showGrid ?? true} onChange={event => onChange({ ...environment, showGrid: event.target.checked })} /> Show floor grid</label>
    <label><input type="checkbox" checked={environment.cinematic ?? false} onChange={event => onChange({ ...environment, cinematic: event.target.checked })} /> Filmic light response</label>
    <label>Exposure <input type="range" min={0.1} max={5} step={0.05} aria-label="Scene exposure" value={environment.exposure ?? 1} onChange={event => onChange({ ...environment, exposure: Number(event.target.value) })} /></label>
    {lights.map(light => <fieldset key={light.id}>
      <legend>{light.name}</legend>
      <label>Name <input aria-label={`${light.name} name`} value={light.name} maxLength={80} onChange={event => changeLight(light.id, { name: event.target.value })} /></label>
      <label>Color <input type="color" aria-label={`${light.name} color`} value={light.color} onChange={event => changeLight(light.id, { color: event.target.value })} /></label>
      <label>Brightness <input type="number" min={0} max={3000} step={5} aria-label={`${light.name} brightness`} value={light.intensity} onChange={event => changeLight(light.id, { intensity: Number(event.target.value) })} /></label>
      <label>Reach (m) <input type="number" min={0.1} max={100} step={0.5} aria-label={`${light.name} reach`} value={light.distance} onChange={event => changeLight(light.id, { distance: Number(event.target.value) })} /></label>
      {(['X', 'Y', 'Z'] as const).map((axis, index) => <label key={axis}>{axis} (m)
        <input type="number" step={0.1} aria-label={`${light.name} ${axis}`} value={light.position[index]} onChange={event => {
          const position = [...light.position] as [number, number, number]
          position[index] = Number(event.target.value)
          changeLight(light.id, { position })
        }} />
      </label>)}
      <label><input type="checkbox" checked={light.castShadow} disabled={!light.castShadow && lights.filter(item => item.castShadow).length >= 2} onChange={event => changeLight(light.id, { castShadow: event.target.checked })} /> Cast shadows</label>
      <button type="button" onClick={() => onChange({ ...environment, pointLights: lights.filter(item => item.id !== light.id) })}>Remove {light.name}</button>
    </fieldset>)}
    <button type="button" disabled={lights.length >= 12} onClick={() => onChange({ ...environment, pointLights: [...lights, {
      id: crypto.randomUUID(), name: `Local light ${lights.length + 1}`, position: [0, 2, 0], color: '#ffbd78', intensity: 40, distance: 8, castShadow: false,
    }] })}>Add local light</button>
    <p className="model-note">Up to twelve lights; two can cast shadows. Save the scene to keep changes.</p>
  </details>
}
