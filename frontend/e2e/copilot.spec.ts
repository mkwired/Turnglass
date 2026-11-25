import { readFile, mkdir } from 'node:fs/promises'
import path from 'node:path'
import { test, expect } from '@playwright/test'
import type { CopilotIndexStatus, CopilotSettings, CopilotUsageReport } from '../src/api/copilot'
import type { UsageReport } from '../src/api/types'

test('displays measured Copilot usage separately, deduplicates SDK mirrors, and saves sources independently', async ({ page, request }) => {
  const paths = JSON.parse(await readFile('test-results/fixture-paths.json', 'utf8')) as { copilotRoot: string; vscodeRoot: string; emptySource: string }
  const originalSources = await (await request.get('/api/copilot/settings')).json() as CopilotSettings
  await expect.poll(async () => (await (await request.get('/api/index')).json()).initialIndexComplete).toBe(true)
  const originalCodex = await (await request.get('/api/usage')).json() as UsageReport
  const headers = { Origin: 'http://127.0.0.1:5390' }
  try {
    const configured = await request.put('/api/copilot/settings', { headers, data: { autoDiscover: false, sources: [{ kind: 'visual-studio', path: paths.copilotRoot, enabled: true }, { kind: 'vscode', path: paths.vscodeRoot, enabled: true }] } })
    expect(configured.ok()).toBe(true)
    await request.post('/api/copilot/index/rescan', { headers })
    await expect.poll(async () => (await (await request.get('/api/copilot/index')).json() as CopilotIndexStatus).recordCount).toBe(2)
    const usage = await (await request.get('/api/copilot/usage?timezone=UTC')).json() as CopilotUsageReport
    expect(usage.periods[0]!.totals.tokens.totalTokens).toBe(14300)
    expect(usage.matchingRecordCount).toBe(2)
    const afterCodex = await (await request.get('/api/usage')).json() as UsageReport
    expect(afterCodex.periods[0]!.totals).toEqual(originalCodex.periods[0]!.totals)

    await page.goto('/copilot')
    await expect(page.getByRole('heading', { name: 'Your Copilot, in perspective.' })).toBeVisible()
    await expect(page.getByRole('article').first()).toContainText('14.3K')
    await expect(page.getByRole('region', { name: 'Copilot source coverage' })).toContainText('2 measured calls indexed')
    await page.getByRole('combobox', { name: 'Filter Copilot by client' }).selectOption('vscode')
    await expect(page).toHaveURL(/client=vscode/)
    await expect(page.getByRole('article').first()).toContainText('2.3K')
    await page.getByRole('combobox', { name: 'Filter Copilot by client' }).selectOption('visual-studio')
    await expect(page.getByRole('article').first()).toContainText('12K')
    await page.getByRole('combobox', { name: 'Filter Copilot by client' }).selectOption('')
    await page.getByRole('button', { name: 'Cost (USD)', exact: true }).click()
    await expect(page.getByRole('img', { name: /Daily estimated USD/ })).toBeVisible()
    await mkdir(path.resolve('test-results', 'screenshots'), { recursive: true })
    await page.screenshot({ path: 'test-results/screenshots/copilot-desktop.png', fullPage: true, animations: 'disabled' })
    await page.setViewportSize({ width: 390, height: 844 })
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: 'test-results/screenshots/copilot-mobile.png', fullPage: true, animations: 'disabled' })

    await page.getByRole('link', { name: 'Settings', exact: true }).click()
    await page.getByRole('textbox', { name: 'Source directories · one per line' }).fill('D:\\unsaved-codex-change')
    await page.getByRole('textbox', { name: 'Copilot source directory 1' }).fill(paths.emptySource)
    await page.getByRole('checkbox', { name: 'Enable Copilot source 2' }).uncheck()
    await page.getByRole('button', { name: 'Save Copilot sources', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Copilot sources saved' })).toBeVisible()
    await page.getByRole('button', { name: 'Rescan Copilot sources', exact: true }).click()
    await expect(page.getByRole('status').filter({ hasText: 'Copilot rescan requested' })).toBeVisible()
    await expect.poll(async () => (await (await request.get('/api/copilot/index')).json() as CopilotIndexStatus).initialScanComplete).toBe(true)
    expect((await (await request.get('/api/copilot/index')).json() as CopilotIndexStatus).recordCount).toBe(2)
    const savedSources = await (await request.get('/api/copilot/settings')).json() as CopilotSettings
    expect(savedSources.sources[0]!.path).toBe(paths.emptySource)
    expect(savedSources.sources[1]!.enabled).toBe(false)
    expect((await (await request.get('/api/usage')).json() as UsageReport).periods[0]!.totals).toEqual(originalCodex.periods[0]!.totals)
    await page.getByRole('link', { name: 'Copilot', exact: true }).click()
    await expect(page.getByRole('article').first()).toContainText('14.3K')
    await page.goto('/copilot?model=unobserved-model')
    await expect(page.getByRole('heading', { name: 'No measured usage available' })).toBeVisible()
    await expect(page.getByRole('article')).toHaveCount(0)
  } finally {
    await request.put('/api/copilot/settings', { headers, data: originalSources })
  }
})
