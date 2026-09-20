import { lazy, Suspense, useEffect, useState } from 'react'
import { ArrowLeft, Box, LoaderCircle, LockKeyhole, Ruler } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetSummary, ModelProfileSummary } from '../types'

// three.js only loads when an artist actually opens a model, so the ordinary
// image and audio workflows keep their current start-up cost.
const ModelViewer = lazy(() => import('./ModelViewer'))

/**
 * The isolated inspection surface for one imported model: the geometry itself,
 * the numbers measured from the stored bytes, and the supported subset those
 * numbers were accepted under.
 */
export default function ModelInspectionWorkspace({ asset, onBack, onError }: {
  asset: AssetSummary
  onBack: () => void
  onError: (message: string) => void
}) {
  const [profile, setProfile] = useState<ModelProfileSummary>()
  const [failure, setFailure] = useState<string>()

  useEffect(() => {
    let current = true
    setProfile(undefined); setFailure(undefined)
    studioApi.modelProfile(asset.id)
      .then(loaded => { if (current) setProfile(loaded) })
      .catch(reason => { if (current) setFailure(reason instanceof Error ? reason.message : 'This model could not be inspected.') })
    return () => { current = false }
  }, [asset.id])

  const metres = (value: number) => `${value.toFixed(value < 10 ? 2 : 1)} m`

  return <main className="workspace model-workspace" data-testid="model-workspace">
    <header className="model-header">
      <button className="secondary compact" onClick={onBack}><ArrowLeft size={16} />Asset library</button>
      <div><p className="eyebrow">Model inspection</p><h1>{asset.displayName}</h1></div>
      <span className="model-format"><Box size={15} />{profile?.container ?? 'GLB'}</span>
    </header>

    {failure && <p className="asset-error" role="alert" data-testid="model-failure">{failure}</p>}

    <div className="model-layout">
      <section className="model-stage-shell">
        {profile
          ? <Suspense fallback={<div className="asset-loading"><LoaderCircle className="spin" /><span>Opening the 3D view…</span></div>}>
              <ModelViewer
                contentUrl={profile.contentUrl}
                label={profile.displayName}
                dimensions={[profile.dimensions[0], profile.dimensions[1], profile.dimensions[2]]}
                onError={onError}
              />
            </Suspense>
          : !failure && <div className="asset-loading"><LoaderCircle className="spin" /><span>Measuring the model…</span></div>}
      </section>

      <aside className="model-inspector" aria-label="Model details">
        {profile && <>
          <section data-testid="model-dimensions">
            <h2><Ruler size={15} />Dimensions</h2>
            <dl>
              <div><dt>Width (X)</dt><dd>{metres(profile.dimensions[0])}</dd></div>
              <div><dt>Height (Y)</dt><dd>{metres(profile.dimensions[1])}</dd></div>
              <div><dt>Depth (Z)</dt><dd>{metres(profile.dimensions[2])}</dd></div>
            </dl>
            <p className="model-note">Scene space, +Y up, metres. Measured from the stored file, not from the view.</p>
            <p className="model-bounds">min ({profile.boundsMin.map(value => value.toFixed(2)).join(', ')}) · max ({profile.boundsMax.map(value => value.toFixed(2)).join(', ')})</p>
          </section>

          <section data-testid="model-statistics">
            <h2>Geometry</h2>
            <dl>
              <div><dt>Vertices</dt><dd>{profile.vertexCount.toLocaleString()}</dd></div>
              <div><dt>Triangles</dt><dd>{profile.triangleCount.toLocaleString()}</dd></div>
              <div><dt>Meshes</dt><dd>{profile.meshCount.toLocaleString()}</dd></div>
              <div><dt>Nodes</dt><dd>{profile.nodeCount.toLocaleString()}</dd></div>
              <div><dt>File</dt><dd>{(profile.bytes / 1024).toFixed(0)} KB</dd></div>
              <div><dt>Embedded textures</dt><dd>{profile.imageCount === 0 ? 'None' : `${profile.imageCount} · ${(profile.embeddedTextureBytes / 1024).toFixed(0)} KB`}</dd></div>
            </dl>
          </section>

          <section data-testid="model-materials">
            <h2>Materials</h2>
            {profile.materials.length === 0 && <p className="model-note">This model declares no materials.</p>}
            <ul>{profile.materials.map(material => <li key={material.name}>
              <strong>{material.name}</strong>
              <small>{material.textured ? 'Textured' : 'Untextured'} · {material.alphaMode.toLowerCase()} · {material.doubleSided ? 'double sided' : 'single sided'}</small>
            </li>)}</ul>
          </section>

          <section data-testid="model-support">
            <h2><LockKeyhole size={15} />Supported subset</h2>
            <p className="model-note">
              glTF {profile.specificationVersion} in a self-contained GLB. Accepted up to {(profile.limits.maxBytes / (1024 * 1024)).toFixed(0)} MB,
              {' '}{profile.limits.maxVertices.toLocaleString()} vertices and {profile.limits.maxTriangles.toLocaleString()} triangles.
              {profile.limits.supportedRequiredExtensions.length === 0
                ? ' No required glTF extensions are supported yet.'
                : ` Supported required extensions: ${profile.limits.supportedRequiredExtensions.join(', ')}.`}
            </p>
            <p className="model-hash">sha256 {profile.contentHash.slice(0, 16)}…</p>
          </section>
        </>}
      </aside>
    </div>
  </main>
}
