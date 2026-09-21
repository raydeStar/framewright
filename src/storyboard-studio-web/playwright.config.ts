import { defineConfig, devices } from '@playwright/test'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const powerShell = process.platform === 'win32' ? 'powershell' : 'pwsh'
const e2ePort = Number(process.env.STUDIO_E2E_PORT ?? 5180)
if (!Number.isInteger(e2ePort) || e2ePort < 1024 || e2ePort > 65535) {
  throw new Error('STUDIO_E2E_PORT must be an integer between 1024 and 65535.')
}
const e2eUrl = `http://127.0.0.1:${e2ePort}`
const e2eStopFile = process.env.STUDIO_E2E_STOP_FILE ?? join(tmpdir(), `storyboard-studio-e2e-${process.pid}.stop`)

if (!process.env.STUDIO_JOURNEY) {
  process.env.STUDIO_E2E_STOP_FILE = e2eStopFile
}

export default defineConfig({
  testDir: './e2e',
  // The production journey really renders on the GPU and takes minutes.
  // Run it deliberately: npx playwright test production-journey --project=desktop
  testIgnore: process.env.STUDIO_JOURNEY ? [] : ['**/production-journey.spec.ts'],
  timeout: 30_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  // Desktop and tablet projects share one disposable studio database. Serial
  // execution keeps durable ordering/version assertions deterministic.
  workers: 1,
  globalTeardown: process.env.STUDIO_JOURNEY ? undefined : './e2e/global-teardown.ts',
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    // The journey runs against its own GPU-enabled server on 5181; the normal
    // suite stays on the sandboxed 5180 instance that cannot reach ComfyUI.
    baseURL: process.env.STUDIO_JOURNEY ? 'http://127.0.0.1:5181' : e2eUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 960 } },
    },
    { name: 'tablet', use: { ...devices['iPad Pro 11'] } },
  ],
  webServer: {
    command: process.env.STUDIO_JOURNEY
      ? `${powerShell} -NoProfile -ExecutionPolicy Bypass -File ../../scripts/run-journey-server.ps1`
      : `${powerShell} -NoProfile -ExecutionPolicy Bypass -File ../../scripts/run-e2e-server.ps1`,
    url: process.env.STUDIO_JOURNEY ? 'http://127.0.0.1:5181/health' : `${e2eUrl}/health`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: process.env.STUDIO_JOURNEY ? undefined : { STUDIO_E2E_STOP_FILE: e2eStopFile },
  },
})
