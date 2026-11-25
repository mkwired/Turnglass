import { readFile, readdir } from 'node:fs/promises'
import path from 'node:path'
import { test, expect } from './fixture'
import { connectFixtureMcp } from '../e2e/mcp'
import type { IndexStatus, UsageReport, ViewerSettings } from '../src/api/types'
import type { CopilotIndexStatus, CopilotUsageReport } from '../src/api/copilot'

const location = (route: string) => `https://turnglass.example/index.html#${route}`

test('uses the native backend for Codex and Copilot, survives reload, and keeps settings independent', async ({ desktop }, testInfo) => {
  const { page, paths, call } = desktop
  await expect.poll(async () => (await call<IndexStatus>('/index')).initialIndexComplete).toBe(true)
  const runtime = await readFile(path.join(paths.data, 'desktop-runtime.txt'), 'utf8')
  expect(runtime).toContain('NativeAOT=True')
  expect(runtime).toContain('ReflectionJson=False')
  await expect(page).toHaveTitle('Turnglass')
  await expect(page.getByRole('article').first()).toContainText('25K')
  const codex = await call<UsageReport>('/usage')
  const originalSettings = await call<ViewerSettings>('/settings')
  await call('/copilot/settings', 'PUT', { autoDiscover: false, sources: [{ kind: 'visual-studio', path: paths.copilotRoot, enabled: true }, { kind: 'vscode', path: paths.vscodeRoot, enabled: true }] })
  await call('/copilot/index/rescan', 'POST')
  await expect.poll(async () => (await call<CopilotIndexStatus>('/copilot/index')).recordCount).toBe(2)
  expect((await call<CopilotUsageReport>('/copilot/usage')).periods[0]!.totals.tokens.totalTokens).toBe(14300)
  await page.getByRole('link', { name: 'Copilot', exact: true }).click()
  await expect(page).toHaveURL(/#\/copilot/)
  await expect(page.getByRole('article').first()).toContainText('14.3K')
  await page.getByRole('combobox', { name: 'Filter Copilot by client' }).selectOption('vscode')
  await expect(page.getByRole('article').first()).toContainText('2.3K')
  await page.getByRole('button', { name: 'rolling', exact: true }).click()
  await page.reload()
  await expect(page).toHaveURL(/#\/copilot\?client=vscode/)
  await expect(page.getByRole('article').first()).toContainText('2.3K')
  await expect(page.getByRole('button', { name: 'rolling', exact: true })).toHaveAttribute('aria-pressed', 'true')
  const beforeSkip = page.url()
  await page.getByRole('link', { name: 'Skip to content' }).focus()
  await page.keyboard.press('Enter')
  await expect(page.locator('main')).toBeFocused()
  expect(page.url()).toBe(beforeSkip)
  await page.getByRole('combobox', { name: 'Filter Copilot by client' }).selectOption('visual-studio')
  await expect(page.getByRole('article').first()).toContainText('12K')
  await page.getByRole('button', { name: 'Cost (USD)', exact: true }).click()
  await expect(page.getByRole('img', { name: /Daily estimated USD/ })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('copilot-native.png'), fullPage: true })

  await page.getByRole('link', { name: 'Settings', exact: true }).click()
  await page.getByRole('textbox', { name: 'Source directories · one per line' }).fill('D:\\unsaved-desktop-test')
  await page.getByRole('checkbox', { name: 'Enable Copilot source 2' }).uncheck()
  await page.getByRole('button', { name: 'Save Copilot sources', exact: true }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Copilot sources saved' })).toBeVisible()
  expect((await call<ViewerSettings>('/settings')).sources).toEqual(originalSettings.sources)
  expect((await call<UsageReport>('/usage')).periods[0]!.totals).toEqual(codex.periods[0]!.totals)
  await page.getByRole('button', { name: 'Copy configuration', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Copied configuration', exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'Codex', exact: true }).click()
  await page.getByRole('button', { name: /Build the session viewer/ }).click()
  await expect(page.getByRole('region', { name: 'Session detail' })).toContainText('Please build the session viewer.')
  expect(await page.locator('img[src="x"]').count()).toBe(0)
  await page.goto(location('/copilot?model=unobserved-model'))
  await expect(page.getByRole('heading', { name: 'No measured usage available' })).toBeVisible()
  const trustedUrl = page.url()
  await expect(page.goto('https://example.invalid/turnglass-navigation-test')).rejects.toThrow()
  expect(page.url()).toBe(trustedUrl)
  expect((await call<IndexStatus>('/index')).initialIndexComplete).toBe(true)
  expect(await readFile(path.join(paths.data, 'desktop-events.log'), 'utf8')).not.toContain('Error ')
  const frameAttached = page.waitForEvent('frameattached')
  await page.evaluate(() => {
    const frame = document.createElement('iframe')
    frame.src = 'https://turnglass.example/index.html'
    document.body.append(frame)
  })
  const frame = await frameAttached
  await expect.poll(async () => {
    try {
      return await frame.evaluate(async () => {
        try {
          const host = window.chrome?.webview?.hostObjects?.turnglass
          if (!host) return false
          await host.InvokeAsync(JSON.stringify({ version: 1, id: crypto.randomUUID(), method: 'GET', path: '/index', body: null }))
          return true
        } catch { return false }
      })
    } catch { return false }
  }).toBe(false)
  expect(desktop.requests).toEqual([])
})

test('shares approved insights with MCP and exports through the native download handler', async ({ desktop }) => {
  const { page, paths, call } = desktop
  await expect.poll(async () => (await call<IndexStatus>('/index')).initialIndexComplete).toBe(true)
  const mcp = await connectFixtureMcp(paths)
  const statement = 'Prefer concise summaries and preserve .editorconfig.'
  try {
    let cursor: string | undefined
    let submitted = false
    for (let attempt = 0; attempt < 20; attempt++) {
      const packet = await mcp.call<{ packetId: string | null; nextCursor: string | null; session: { id: string; project: string | null } | null; events: { eventId: string; text: string }[] }>('get_review_packet', { cursor })
      if (!packet.packetId || !packet.session) break
      const evidence = packet.events.find(event => event.text.includes('For this project, use concise summaries'))
      const findings = evidence ? [{ statement, category: 'preference', scope: 'project', project: packet.session.project, sessionId: null, basis: 'explicit', rationale: 'An explicit user instruction in the synthetic fixture.', evidence: [{ sessionId: packet.session.id, eventId: evidence.eventId, quote: 'For this project, use concise summaries and preserve .editorconfig.' }] }] : []
      await mcp.call('submit_review', { packetId: packet.packetId, idempotencyKey: `desktop-${packet.packetId}`, findings })
      if (evidence) { submitted = true; break }
      if (!packet.nextCursor) break
      cursor = packet.nextCursor
    }
    expect(submitted).toBe(true)
    await page.getByRole('link', { name: 'Insights', exact: true }).click()
    const draft = page.getByRole('article', { name: `Insight: ${statement}`, exact: true })
    await expect(draft).toBeVisible()
    await draft.getByRole('button', { name: 'Approve', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: '1 insight updated.' })).toBeVisible()
    await page.getByRole('button', { name: 'Approved', exact: true }).click()
    await expect(draft).toBeVisible()
    const approved = await mcp.call<{ items: { statement: string }[] }>('search_insights', { query: 'concise', project: 'D:\\projects\\session-viewer' })
    expect(approved.items[0]!.statement).toBe(statement)
    await page.getByRole('checkbox', { name: `Select ${statement}`, exact: true }).check()
    await page.getByRole('button', { name: /Preview export/ }).click()
    await expect(page.getByRole('region', { name: 'Export preview' })).toContainText(statement)
    await page.getByRole('button', { name: 'Copy export', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Copied export.' })).toBeVisible()
    await page.getByRole('button', { name: 'Download export', exact: true }).click()
    const downloads = path.join(paths.runtime, 'downloads')
    await expect.poll(async () => (await readdir(downloads).catch(() => [])).filter(file => file.endsWith('.md')).length).toBe(1)
    const exported = (await readdir(downloads)).find(file => file.endsWith('.md'))!
    await expect.poll(() => readFile(path.join(downloads, exported), 'utf8')).toContain(statement)
    expect(desktop.requests).toEqual([])
  } finally { await mcp.close() }
})
