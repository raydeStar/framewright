import type { BlenderInstallSummary } from '../types'

/**
 * Which Blender the compiler is handed, said where compiler readiness is said.
 *
 * One line when all is well -- the version and how it was found -- with the
 * path and how to choose a different one folded away. When there is no usable
 * Blender the reason and the fix are shown outright, because then they are the
 * thing the artist needs.
 */
export default function BlenderInstallNote({ install }: { install?: BlenderInstallSummary | null }) {
  if (!install) return null
  const usable = install.version !== null
  return <div className="model-blender" data-testid="model-blender" data-source={install.source}
    data-usable={usable ? 'true' : 'false'}>
    {usable
      ? <details>
          <summary>Blender {install.version}, {install.foundBy}.</summary>
          {install.executable && <code>{install.executable}</code>}
          <p>{install.override}</p>
        </details>
      : <>
          {/* The problem names the path when there is one, so it is said once. */}
          <p>{install.problem ?? 'No usable Blender was found.'}</p>
          <p>{install.override}</p>
        </>}
  </div>
}
