import { test, expect } from '@playwright/test'
import { connectFixtureMcp } from './mcp'
import type { InsightPage } from '../src/api/knowledge-types'

type Packet = {
  packetId: string | null
  nextCursor: string | null
  session: { id: string; project: string | null } | null
  events: { eventId: string; ordinal: number; text: string }[]
}

test('reviews MCP findings, follows evidence, approves and exports reusable guidance', async ({ page, request }) => {
  await expect.poll(async () => (await (await request.get('/api/index')).json()).initialIndexComplete).toBe(true)
  const mcp = await connectFixtureMcp()
  const statement = 'Prefer concise summaries and preserve .editorconfig.'
  const revised = 'Use concise summaries while preserving the project .editorconfig.'
  let evidenceOrdinal = 0
  try {
    let cursor: string | undefined
    let submitted = false
    for (let attempt = 0; attempt < 20; attempt++) {
      const packet = await mcp.call<Packet>('get_review_packet', { cursor })
      if (!packet.packetId || !packet.session) break
      const evidence = packet.events.find(event => event.text.includes('For this project, use concise summaries'))
      const findings = evidence ? [{
        statement,
        category: 'preference',
        scope: 'project',
        project: packet.session.project,
        sessionId: null,
        basis: 'explicit',
        rationale: 'The user explicitly requested concise summaries and preserving the project formatting configuration.',
        evidence: [{ sessionId: packet.session.id, eventId: evidence.eventId, quote: 'For this project, use concise summaries and preserve .editorconfig.' }],
      }] : []
      const receipt = await mcp.call<{ insightIds: string[] }>('submit_review', { packetId: packet.packetId, idempotencyKey: `browser-${packet.packetId}`, findings })
      if (evidence) {
        expect(receipt.insightIds).toHaveLength(1)
        evidenceOrdinal = evidence.ordinal
        submitted = true
        break
      }
      if (!packet.nextCursor) break
      cursor = packet.nextCursor
    }
    expect(submitted).toBe(true)
    const before = await mcp.call<InsightPage>('search_insights', { query: 'concise' })
    expect(before.items).toHaveLength(0)

    await page.goto('/insights')
    await expect(page.getByRole('heading', { name: 'Keep what you learn.' })).toBeVisible()
    const draft = page.getByRole('article', { name: `Insight: ${statement}`, exact: true })
    await expect(draft).toBeVisible()
    await draft.getByRole('link', { name: `Open event ${evidenceOrdinal}`, exact: true }).click()
    await expect(page).toHaveURL(/session=synthetic-session-one.*event=/)
    await expect(page.getByText('Linked evidence event', { exact: true })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Session detail' })).toContainText('For this project, use concise summaries and preserve .editorconfig.')
    expect(await page.locator('img[src="x"]').count()).toBe(0)

    await page.getByRole('link', { name: 'Insights', exact: true }).click()
    await draft.getByRole('button', { name: 'Edit', exact: true }).click()
    await draft.getByRole('textbox', { name: 'Insight statement' }).fill(revised)
    await draft.getByRole('button', { name: 'Save changes', exact: true }).click()
    const edited = page.getByRole('article', { name: `Insight: ${revised}`, exact: true })
    await edited.getByRole('button', { name: 'Approve', exact: true }).click()
    await page.getByRole('button', { name: 'Approved', exact: true }).click()
    await expect(edited).toBeVisible()
    const approved = await mcp.call<InsightPage>('search_insights', { query: 'concise', project: 'D:\\projects\\session-viewer' })
    expect(approved.items).toHaveLength(1)
    expect(approved.items[0]!.statement).toBe(revised)
    expect(approved.items[0]!.status).toBe('approved')
    expect(approved.items[0]!.history.length).toBeGreaterThanOrEqual(3)

    await page.getByRole('checkbox', { name: `Select ${revised}`, exact: true }).check()
    await page.getByRole('button', { name: /Preview export/ }).click()
    await expect(page.getByRole('region', { name: 'Export preview' })).toContainText(revised)
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download export', exact: true }).click()
    expect((await download).suggestedFilename()).toMatch(/\.md$/)
    await page.setViewportSize({ width: 390, height: 844 })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.screenshot({ path: 'test-results/screenshots/insights-mobile.png', fullPage: true, animations: 'disabled' })
    expect(await page.evaluate(() => (window as Window & { xssDetected?: boolean }).xssDetected)).toBeUndefined()
  } finally { await mcp.close() }
})
