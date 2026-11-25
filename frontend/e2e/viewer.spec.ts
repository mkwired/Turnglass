import { readFile, mkdir } from 'node:fs/promises'
import path from 'node:path'
import { test, expect } from '@playwright/test'
import type { IndexStatus, UsageReport, ViewerSettings } from '../src/api/types'

test('ingests logs, displays exact totals, searches safely and reprices without reimport', async ({ page, request }) => {
  await expect.poll(async () => (await (await request.get('/api/index')).json() as IndexStatus).initialIndexComplete).toBe(true)
  await page.goto('/')
  await expect(page).toHaveTitle('Turnglass')
  await expect(page.getByRole('link', { name: 'Turnglass home' })).toContainText('Understand your AI work.')
  await expect(page.getByRole('heading', { name: 'Your Codex, at a glance.' })).toBeVisible()
  const usage = await (await request.get('/api/usage')).json() as UsageReport
  await expect(page.getByRole('article')).toHaveCount(3)
  expect(usage.periods[0]!.totals.tokens.totalTokens).toBe(25000)
  await expect(page.getByRole('article').first()).toContainText('25K')
  await expect(page.getByRole('article').first()).toContainText('$0.32')
  await page.getByRole('button', { name: 'rolling', exact: true }).click()
  await expect(page.getByRole('button', { name: 'rolling', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.reload()
  await expect(page.getByRole('button', { name: 'rolling', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await page.getByRole('button', { name: 'calendar', exact: true }).click()
  await page.getByRole('button', { name: 'Cost (USD)', exact: true }).click()
  await expect(page.getByRole('img', { name: /Daily estimated USD/ })).toBeVisible()
  await page.getByRole('button', { name: 'Tokens', exact: true }).click()
  await expect(page.getByRole('img').getByText(/\d+K/).first()).toBeVisible()
  await mkdir(path.resolve('test-results', 'screenshots'), { recursive: true })
  await page.screenshot({ path: 'test-results/screenshots/overview-desktop.png', fullPage: true, animations: 'disabled' })
  await page.getByRole('link', { name: 'Codex', exact: true }).click()
  await page.getByRole('textbox', { name: 'Search sessions', exact: true }).fill('session viewer')
  await expect(page).toHaveURL(/query=session/)
  await page.getByRole('button', { name: /Build the session viewer/ }).click()
  await expect(page.getByRole('region', { name: 'Session detail' })).toContainText('Please build the session viewer.')
  expect(await page.evaluate(() => (window as Window & { xssDetected?: boolean }).xssDetected)).toBeUndefined()
  expect(await page.locator('img[src="x"]').count()).toBe(0)
  await page.getByRole('textbox', { name: 'Search within session' }).fill('parser')
  await page.getByRole('button', { name: 'Search logs' }).click()
  await expect(page.getByRole('region', { name: 'Session detail' })).toContainText('matching events')
  await page.getByRole('button', { name: 'View raw record' }).first().click()
  await expect(page.getByRole('region', { name: 'Session detail' })).toContainText('Raw record')
  await page.getByRole('link', { name: 'Settings', exact: true }).click()
  const original = await (await request.get('/api/settings')).json() as ViewerSettings
  await page.getByRole('textbox', { name: 'Catalog version' }).fill('browser-test-rates')
  await page.getByRole('spinbutton', { name: 'inputPerMillion 1', exact: true }).fill('20')
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Settings saved' })).toBeVisible()
  const changed = await (await request.get('/api/usage')).json() as UsageReport
  expect(changed.periods[0]!.totals.knownCostUsd).toBeGreaterThan(usage.periods[0]!.totals.knownCostUsd)
  expect(changed.periods[0]!.totals.tokens.totalTokens).toBe(usage.periods[0]!.totals.tokens.totalTokens)
  await request.put('/api/settings', { data: original, headers: { Origin: 'http://127.0.0.1:5390' } })
})

test('supports narrow layouts and keyboard navigation', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await expect(page.getByRole('article')).toHaveCount(3)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Skip to content' })).toBeFocused()
  await page.keyboard.press('Enter')
  await page.getByRole('link', { name: 'Codex', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Every session tells a story.' })).toBeVisible()
  await page.screenshot({ path: 'test-results/screenshots/sessions-mobile.png', fullPage: true, animations: 'disabled' })
})

test('shows a meaningful empty-source state', async ({ page, request }) => {
  const paths = JSON.parse(await readFile('test-results/fixture-paths.json', 'utf8')) as { emptySource: string }
  const original = await (await request.get('/api/settings')).json() as ViewerSettings
  await request.put('/api/settings', { data: { ...original, sources: [paths.emptySource] }, headers: { Origin: 'http://127.0.0.1:5390' } })
  await request.post('/api/index/rebuild', { headers: { Origin: 'http://127.0.0.1:5390' } })
  await expect.poll(async () => (await (await request.get('/api/index')).json() as IndexStatus).sessionCount).toBe(0)
  await page.goto('/sessions')
  await expect(page.getByRole('heading', { name: 'No sessions found' })).toBeVisible()
  await request.put('/api/settings', { data: original, headers: { Origin: 'http://127.0.0.1:5390' } })
})
