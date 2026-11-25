import { spawn, type ChildProcess } from 'node:child_process'
import { access, readFile } from 'node:fs/promises'
import { createServer } from 'node:net'
import path from 'node:path'
import { test as base, expect, type Page } from '@playwright/test'
import { createFixture, type FixturePaths } from '../e2e/fixtures.mjs'

interface DesktopSession {
  page: Page
  paths: FixturePaths
  requests: string[]
  call<T>(path: string, method?: 'GET' | 'POST' | 'PUT', body?: unknown): Promise<T>
}

async function freePort(): Promise<number> {
  const server = createServer()
  await new Promise<void>((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve) })
  const address = server.address()
  if (!address || typeof address === 'string') throw new Error('Unable to allocate a desktop test port.')
  await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()))
  return address.port
}

const hasExited = (child: ChildProcess) => child.exitCode !== null || child.signalCode !== null

function waitForExit(child: ChildProcess, timeout: number): Promise<boolean> {
  if (hasExited(child)) return Promise.resolve(true)
  return new Promise(resolve => {
    const finish = (exited: boolean) => { clearTimeout(timer); child.removeListener('exit', onExit); resolve(exited) }
    const onExit = () => finish(true)
    const timer = setTimeout(() => finish(false), timeout)
    child.once('exit', onExit)
  })
}

async function closeDesktop(child: ChildProcess): Promise<string | undefined> {
  if (!Number.isSafeInteger(child.pid) || child.pid! <= 0) return 'The desktop process did not provide a valid process ID.'
  const deadline = Date.now() + 15000
  let closeRequest = 'The process had already exited.'
  if (!hasExited(child)) {
    closeRequest = await new Promise<string>(resolve => {
      const stop = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', `
$desktopProcess = Get-Process -Id ${child.pid} -ErrorAction SilentlyContinue
if ($null -eq $desktopProcess) { exit 0 }
if ($desktopProcess.CloseMainWindow()) { exit 0 }
exit 2
`], { windowsHide: true, stdio: 'ignore', timeout: 5000 })
      stop.once('exit', (code, signal) => resolve(`CloseMainWindow helper exited ${code ?? signal}.`))
      stop.once('error', error => resolve(`CloseMainWindow could not be requested: ${error.message}`))
    })
  }
  const exited = await waitForExit(child, Math.max(1, deadline - Date.now()))
  if (exited && child.exitCode === 0) return undefined
  const failure = exited
    ? `Desktop shutdown exited with ${child.exitCode ?? child.signalCode}, expected 0. ${closeRequest}`
    : `Desktop did not close within 15 seconds. ${closeRequest}`
  if (!hasExited(child)) {
    await new Promise<void>(resolve => {
      const stop = spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 5000 })
      stop.once('exit', () => resolve()); stop.once('error', () => { child.kill(); resolve() })
    })
    await waitForExit(child, 3000)
  }
  return failure
}

export const test = base.extend<{ desktop: DesktopSession }>({
  desktop: async ({ playwright }, use, testInfo) => {
    const executable = process.env.TURNGLASS_DESKTOP_EXE ?? path.resolve('../artifacts/publish/desktop/win-x64/Turnglass.Desktop.exe')
    await access(executable)
    const paths = await createFixture(path.resolve('test-results', 'desktop-runtime'))
    const port = await freePort()
    let diagnostics = ''
    let launchError: Error | undefined
    const child = spawn(executable, ['--desktop-test', '--data-dir', paths.data], {
      cwd: path.dirname(executable),
      env: { ...process.env, CODEX_HOME: paths.codex, TURNGLASS_COPILOT_AUTO_DISCOVER: 'false', TURNGLASS_DESKTOP_TEST_PORT: String(port), TURNGLASS_DESKTOP_TEST_DOWNLOAD_DIR: path.join(paths.runtime, 'downloads'), WEBVIEW2_USER_DATA_FOLDER: path.join(paths.runtime, 'webview2'), DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    const capture = (data: Buffer) => { diagnostics = (diagnostics + data.toString()).slice(-12000) }
    child.stdout.on('data', capture); child.stderr.on('data', capture)
    child.on('error', error => { launchError = error })
    let browser: Awaited<ReturnType<typeof playwright.chromium.connectOverCDP>> | undefined
    let completedSuccessfully = false
    try {
      await expect.poll(async () => {
        if (launchError) throw launchError
        if (child.exitCode !== null) throw new Error(`Desktop exited ${child.exitCode}: ${diagnostics}`)
        try { return (await fetch(`http://127.0.0.1:${port}/json/version`)).ok } catch { return false }
      }, { timeout: 45000, message: 'Waiting for the test-only WebView2 debugging endpoint' }).toBe(true)
      browser = await playwright.chromium.connectOverCDP(`http://127.0.0.1:${port}`, { noDefaults: true })
      const context = browser.contexts()[0]!
      const page = context.pages()[0] ?? await context.waitForEvent('page')
      await page.waitForURL('https://turnglass.example/**')
      const requests: string[] = []
      page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) requests.push(request.url()) })
      const desktop: DesktopSession = {
        page, paths, requests,
        call: async <T>(requestPath: string, method: 'GET' | 'POST' | 'PUT' = 'GET', body?: unknown): Promise<T> => page.evaluate(async input => {
          const host = window.chrome?.webview?.hostObjects?.turnglass
          if (!host) throw new Error('Desktop host object was not registered.')
          const id = crypto.randomUUID()
          const response = JSON.parse(await host.InvokeAsync(JSON.stringify({ version: 1, id, method: input.method, path: input.path, body: input.body }))) as { version: number; id: string; status: number; body: string | null }
          if (response.version !== 1 || response.id !== id) throw new Error('Desktop response correlation failed.')
          if (response.status >= 400) throw new Error(response.body ?? `Desktop request failed (${response.status}).`)
          return response.body === null ? undefined : JSON.parse(response.body)
        }, { path: requestPath, method, body: body === undefined ? null : JSON.stringify(body) }) as Promise<T>,
      }
      await use(desktop)
      completedSuccessfully = testInfo.status === 'passed'
      if (testInfo.status !== testInfo.expectedStatus) await page.screenshot({ path: testInfo.outputPath('desktop-failure.png'), fullPage: true }).catch(() => {})
    } finally {
      await browser?.close().catch(() => {})
      const shutdownFailure = launchError ? undefined : await closeDesktop(child)
      if (diagnostics) await testInfo.attach('desktop-output', { body: diagnostics, contentType: 'text/plain' })
      const nativeErrors = await readFile(path.join(paths.data, 'desktop-errors.log'), 'utf8').catch(() => '')
      if (nativeErrors) await testInfo.attach('desktop-native-errors', { body: nativeErrors, contentType: 'text/plain' })
      if (shutdownFailure) {
        await testInfo.attach('desktop-shutdown-failure', { body: shutdownFailure, contentType: 'text/plain' })
        if (completedSuccessfully) throw new Error(shutdownFailure)
      }
    }
  },
})

export { expect }
