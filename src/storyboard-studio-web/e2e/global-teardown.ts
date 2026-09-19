import { access, writeFile } from 'node:fs/promises'

const wait = (milliseconds: number) => new Promise(resolve => setTimeout(resolve, milliseconds))

export default async function globalTeardown() {
  const stopFile = process.env.STUDIO_E2E_STOP_FILE
  if (!stopFile) return

  await writeFile(stopFile, 'stop', 'utf8')

  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      await access(stopFile)
      await wait(100)
    } catch {
      return
    }
  }

  throw new Error(`E2E server did not acknowledge shutdown: ${stopFile}`)
}
