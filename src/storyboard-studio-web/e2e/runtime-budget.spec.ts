import { expect, test, type Page, type Route } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * A model's runtime triangle budget, as the artist meets it: the compiler's
 * number beside the triangle count, Auto as the default for Prepare for
 * runtime, a typed number one step away, and nothing invented when the
 * compiler cannot answer.
 *
 * The first journey runs the real API, gateway and process boundary against
 * the controlled compiler stand-in. The others stand in for the readiness and
 * budget answers at the browser, because the stand-in has no reduction route
 * and these states -- over budget, an older compiler -- are the studio's to
 * show, not the compiler's to produce on demand.
 */

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))

/** A per-run copy of a fixture, so each journey owns its own library asset. */
function ownFixture(source: string, label: string) {
  const bytes = readFileSync(source)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  document.scenes[0].name = `${document.scenes[0].name} ${label}`
  let json = Buffer.from(JSON.stringify(document), 'utf8')
  if (json.length % 4 !== 0) json = Buffer.concat([json, Buffer.alloc(4 - (json.length % 4), 0x20)])
  const binary = bytes.subarray(20 + jsonLength)
  const header = Buffer.alloc(20)
  header.writeUInt32LE(0x46546c67, 0)
  header.writeUInt32LE(2, 4)
  header.writeUInt32LE(12 + 8 + json.length + binary.length, 8)
  header.writeUInt32LE(json.length, 12)
  header.writeUInt32LE(0x4e4f534a, 16)
  return Buffer.concat([header, json, binary])
}

function failOnConsoleErrors(page: Page) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => expect(errors, `Browser errors: ${errors.join('\n')}`).toEqual([])
}

async function openOwnModel(page: Page, name: string) {
  // Opening a new model also renders its library thumbnail. Headless WebKit
  // hands back an image the server rightly refuses, which is a thumbnail
  // matter and not what these journeys are about, so the upload is accepted
  // here without being stored.
  await page.route('**/api/assets/*/poster', route =>
    route.request().method() === 'PUT' ? route.fulfill({ status: 204 }) : route.fallback())
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
  await page.locator('input[type="file"]').setInputFiles({
    name: `${name}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, name),
  })
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()
}

const readyToPrepare = {
  installed: true, commissioned: true, canRun: true,
  compilerVersion: 'reference-asset-compiler (browser stand-in)', checkout: null, blender: null,
  missing: [], detail: 'Ready. Preparing a derivative will run on this workstation.',
  sizes: null, colours: null, details: null,
  blenderInstall: {
    executable: '/opt/steam/steamapps/common/Blender/blender', version: '5.2.2 LTS', source: 'steam',
    foundBy: 'found in your Steam library',
    override: 'To use a different Blender, set Integrations:ReferenceAssetCompiler:BlenderPath in appsettings.Local.json, or the BLENDER environment variable, to the full path of its executable.',
    problem: null,
  },
}

/** Answers for readiness and the budget at the browser; the profile is the real one, made dense. */
async function standIn(page: Page, budget: Record<string, unknown>) {
  await page.route('**/api/models/preparation/readiness', route => route.fulfill({ json: readyToPrepare }))
  await page.route('**/api/assets/*/triangle-budget', route => route.fulfill({ json: budget }))
  // The fixture has 24 triangles, too few for any reducer. The real profile
  // is fetched and given a dense count, so the panel can be read as it would
  // be for a model worth reducing.
  await page.route('**/api/assets/*/model-profile', async route => {
    const response = await route.fetch()
    await route.fulfill({ response, json: { ...(await response.json()), triangleCount: 100_000 } })
  })
}

/** Captures what Prepare for runtime sends, and answers with a queued job. */
async function capturePreparation(page: Page) {
  const sent: Array<{ triangleBudget: number | null }> = []
  const job = {
    id: '6f2a7e0c-3c4e-4e1e-9c1b-0b2f6b1c9a01', shotId: '00000000-0000-0000-0000-000000000000',
    shotCode: 'Stand-in (runtime)', kind: 'Runtime derivative', state: 'Queued', progress: 0,
    phase: 'Frozen model queued', backend: 'Reference Asset Compiler', createdAt: new Date().toISOString(),
    completedAt: null, error: null, manifestId: null, adapterId: null, outputAssetId: null, outputUrl: null,
    providerRequestId: null, attempt: 1, retryOfJobId: null, lastHeartbeatAt: null, workType: 'Model',
  }
  await page.route('**/api/models/preparation', async (route: Route) => {
    if (route.request().method() !== 'POST') return route.fallback()
    sent.push(route.request().postDataJSON() as { triangleBudget: number | null })
    await route.fulfill({ json: job })
  })
  await page.route(`**/api/jobs/${job.id}`, route => route.fulfill({ json: job }))
  return sent
}

test('the compiler’s budget sits beside the triangle count, through the real gateway', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `budget-block-${testInfo.project.name}-${Date.now()}`
  await openOwnModel(page, name)

  // Asked with this model's name and measured size; answered by the stand-in
  // in the compiler's shape. 24 triangles is well within it, so no flag.
  const triangles = page.getByTestId('model-triangles')
  await expect(page.getByTestId('model-triangle-budget')).toHaveText(/budget 5\D?000 \(prop\)/)
  await expect(triangles).toHaveAttribute('data-over-budget', 'false')
  await expect(page.getByTestId('model-over-budget')).toHaveCount(0)

  // Which Blender the compiler is handed is said where its readiness is. The
  // journey names an absent one, which is reported rather than replaced.
  const blender = page.getByTestId('model-preparation').getByTestId('model-blender')
  await expect(blender).toHaveAttribute('data-usable', 'false')
  await expect(blender).toHaveAttribute('data-source', 'setting')
  await expect(blender).toContainText('blender-absent-for-e2e')
  await expect(blender).toContainText('Integrations:ReferenceAssetCompiler:BlenderPath')
  verifyConsole()
})

test('Prepare for runtime defaults to Auto, shows the compiler’s reasoning, and sends no number', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await standIn(page, {
    assetId: '00000000-0000-0000-0000-000000000000', triangleCount: 100_000, state: 'Decided', detail: null,
    role: 'prop', roleReason: "its name says 'chest'", sizeClass: 'medium', longestMetres: 2,
    triangleBudget: 5_000, maximumP99Metres: 0.004, maximumMaxMetres: 0.016, ladder: [5_000, 7_500, 11_300],
    summary: 'A medium prop: about 5,000 triangles.',
  })
  const sent = await capturePreparation(page)
  await openOwnModel(page, `auto-chest-${testInfo.project.name}-${Date.now()}`)

  // Over its budget, and said gently: a note, not an error.
  await expect(page.getByTestId('model-triangle-budget')).toHaveText(/budget 5\D?000 \(prop\)/)
  await expect(page.getByTestId('model-triangles')).toHaveAttribute('data-over-budget', 'true')
  await expect(page.getByTestId('model-over-budget')).toBeVisible()
  await expect(page.getByTestId('model-over-budget')).not.toHaveAttribute('role', 'alert')

  // Auto is chosen before anything is touched, in the compiler's own words.
  const choice = page.getByTestId('model-preparation-budget-choice')
  await expect(choice).toHaveAttribute('data-mode', 'auto')
  const auto = page.getByTestId('model-preparation-auto')
  await expect(auto).toContainText('A medium prop: about 5,000 triangles.')
  await expect(auto).toContainText(/This model has 100\D?000\./)
  await expect(auto).toContainText("Why: its name says 'chest'.")
  await expect(page.getByTestId('model-preparation').getByTestId('model-blender'))
    .toContainText('Blender 5.2.2 LTS, found in your Steam library.')
  // The number is there, one step away, and not filled in on anyone's behalf.
  await expect(page.getByTestId('model-preparation-budget')).toBeHidden()
  // This compiler cannot bake a reduced model's paint back, so nothing promises it.
  await expect(page.getByTestId('model-preparation-keeps-paint')).toHaveCount(0)

  await page.getByTestId('model-prepare').click()
  await expect(page.getByText(/queued\. It keeps going if you leave this screen/)).toBeVisible()
  expect(sent).toEqual([expect.objectContaining({ triangleBudget: null })])

  // Opening the override switches to it, empty, with Auto's figure only as a hint.
  await page.getByTestId('model-preparation-manual').locator('summary').click()
  await expect(choice).toHaveAttribute('data-mode', 'manual')
  await expect(page.getByTestId('model-preparation-budget')).toHaveValue('')
  await expect(page.getByTestId('model-preparation-budget')).toHaveAttribute('placeholder', '5000')
  verifyConsole()
})

test('a compiler that can keep a painted model’s paint says so beside Prepare for runtime', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await standIn(page, {
    assetId: '00000000-0000-0000-0000-000000000000', triangleCount: 100_000, state: 'Decided', detail: null,
    role: 'prop', roleReason: 'nothing in its name or notes marks it as anything but a prop', sizeClass: 'medium',
    longestMetres: 1.2, triangleBudget: 10_000, maximumP99Metres: 0.004, maximumMaxMetres: 0.017,
    ladder: [10_000, 15_000], summary: 'A medium prop: about 10,000 triangles.',
  })
  // Registered after the stand-in, so this readiness is the one answered.
  await page.route('**/api/models/preparation/readiness',
    route => route.fulfill({ json: { ...readyToPrepare, preparationKeepsPaint: true } }))
  await openOwnModel(page, `painted-brazier-${testInfo.project.name}-${Date.now()}`)

  // Said before the artist presses anything: the paint is baked back and
  // compared, and a look no budget can keep stops the preparation.
  const note = page.getByTestId('model-preparation-keeps-paint')
  await expect(note).toBeVisible()
  await expect(note).toContainText('baked back from this original')
  await expect(note).toContainText('rather than deliver a smear')
  await expect(page.getByTestId('model-prepare')).toBeEnabled()
  verifyConsole()
})

test('with no suggestion from the compiler the number is the artist’s, and nothing is invented', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await standIn(page, {
    assetId: '00000000-0000-0000-0000-000000000000', triangleCount: 100_000, state: 'Outdated',
    detail: 'The installed Reference Asset Compiler is older than this: it has no budget command, so it cannot choose a budget itself. Enter one, or update the compiler.',
    role: null, roleReason: null, sizeClass: null, longestMetres: null, triangleBudget: null,
    maximumP99Metres: null, maximumMaxMetres: null, ladder: null, summary: null,
  })
  const sent = await capturePreparation(page)
  await openOwnModel(page, `older-compiler-${testInfo.project.name}-${Date.now()}`)

  // No budget beside the count, no Auto, and the reason in plain words.
  await expect(page.getByTestId('model-statistics')).toBeVisible()
  await expect(page.getByTestId('model-triangle-budget')).toHaveCount(0)
  await expect(page.getByTestId('model-preparation-auto')).toHaveCount(0)
  await expect(page.getByTestId('model-preparation-auto-unavailable')).toContainText('older')
  await expect(page.getByTestId('model-preparation-budget-choice')).toHaveAttribute('data-mode', 'manual')

  // The number is shown outright, empty, and Prepare waits for one.
  const number = page.getByTestId('model-preparation-budget')
  await expect(number).toBeVisible()
  await expect(number).toHaveValue('')
  await expect(page.getByTestId('model-prepare')).toBeDisabled()

  await number.fill('500')
  await expect(page.getByTestId('model-preparation-budget-note')).toContainText('between')
  await expect(page.getByTestId('model-prepare')).toBeDisabled()

  await number.fill('12000')
  await expect(page.getByTestId('model-prepare')).toBeEnabled()
  await page.getByTestId('model-prepare').click()
  await expect(page.getByText(/queued\. It keeps going if you leave this screen/)).toBeVisible()
  expect(sent).toEqual([expect.objectContaining({ triangleBudget: 12_000 })])
  verifyConsole()
})
