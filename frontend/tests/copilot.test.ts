import { type Component } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CopilotPage from '@/pages/CopilotPage.vue'
import SettingsPage from '@/pages/SettingsPage.vue'
import { api } from '@/api/client'
import { copilotApi } from '@/api/copilot'
import { copilotIndex, copilotReport, copilotSettings, index, settings, totals } from './fixtures'

async function mountPage(component: Component = CopilotPage, location = '/copilot') {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }] })
  await router.push(location); await router.isReady()
  const wrapper = mount(component, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}
function button(wrapper: Awaited<ReturnType<typeof mountPage>>['wrapper'], text: string) { return wrapper.findAll('button').find(item => item.text() === text)! }

beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime('2026-11-21T16:00:00Z')
  vi.spyOn(api, 'settings').mockResolvedValue(structuredClone(settings))
  vi.spyOn(api, 'status').mockResolvedValue(structuredClone(index))
  vi.spyOn(api, 'saveSettings').mockImplementation(async value => value)
  vi.spyOn(copilotApi, 'usage').mockResolvedValue(structuredClone(copilotReport))
  vi.spyOn(copilotApi, 'filters').mockResolvedValue({ clients: ['visual-studio', 'vscode', 'shared'], models: ['gpt-6-astra'] })
  vi.spyOn(copilotApi, 'status').mockResolvedValue(structuredClone(copilotIndex))
  vi.spyOn(copilotApi, 'settings').mockResolvedValue(structuredClone(copilotSettings))
  vi.spyOn(copilotApi, 'saveSettings').mockImplementation(async value => value)
  vi.spyOn(copilotApi, 'rescan').mockResolvedValue(undefined)
})

describe('Copilot usage', () => {
  it('shows measured totals, filters independently, and supports dates, timezones and cost trends', async () => {
    const { wrapper, router } = await mountPage()
    expect(wrapper.findAll('article')).toHaveLength(3)
    expect(wrapper.text()).toContain('Shared Copilot / CLI')
    expect(wrapper.text()).toContain('0 known · incomplete')
    expect(wrapper.text()).toContain('separate from your Copilot bill')
    expect(wrapper.text()).toContain('Standard service tier assumed')
    await wrapper.find('[aria-label="Filter Copilot by client"]').setValue('vscode')
    await wrapper.find('[aria-label="Filter Copilot by model"]').setValue('gpt-6-astra')
    await wrapper.find('[aria-label="Copilot reporting timezone"]').setValue('UTC')
    await wrapper.find('#copilot-trend-from').setValue('2026-11-01')
    await button(wrapper, 'rolling').trigger('click'); await flushPromises()
    expect(copilotApi.usage).toHaveBeenLastCalledWith(expect.objectContaining({ client: 'vscode', model: 'gpt-6-astra', timezone: 'UTC', mode: 'rolling', from: '2026-11-01T00:00:00.000Z' }))
    expect(router.currentRoute.value.query).toEqual({ client: 'vscode', model: 'gpt-6-astra' })
    expect(localStorage.getItem('copilot-period-mode')).toBe('rolling')
    expect(localStorage.getItem('period-mode')).toBeNull()
    await button(wrapper, 'Cost (USD)').trigger('click')
    expect(wrapper.find('[role="img"]').attributes('aria-label')).toContain('USD cost')
    await button(wrapper, 'Refresh').trigger('click'); await flushPromises()
  })

  it('does not disguise unavailable observations as zero usage', async () => {
    vi.mocked(copilotApi.usage).mockResolvedValue({ ...copilotReport, matchingRecordCount: 0, index: { ...copilotIndex, recordCount: 0, sources: [], observedFrom: null, observedTo: null, warnings: ['No measured token records found.'] } })
    const { wrapper } = await mountPage()
    expect(wrapper.findAll('article')).toHaveLength(0)
    expect(wrapper.find('[role="img"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('No measured usage available')
    expect(wrapper.text()).toContain('does not mean no tokens were used')
    expect(wrapper.text()).toContain('No measured token records found')
  })

  it('retains measured totals during scans, source failures and refresh failures', async () => {
    vi.mocked(copilotApi.usage).mockResolvedValue({ ...copilotReport, index: { ...copilotIndex, initialScanComplete: false, isIndexing: true, lastSuccessfulScanUtc: null, skippedRecords: 2, ambiguousRecords: 1, sources: [{ ...copilotIndex.sources[0]!, available: false, error: 'Directory unavailable' }] } })
    const { wrapper } = await mountPage()
    expect(wrapper.findAll('article')).toHaveLength(3)
    expect(wrapper.text()).toContain('Scanning Copilot sources')
    expect(wrapper.text()).toContain('No fully successful scan yet')
    expect(wrapper.text()).toContain('Directory unavailable')
    expect(wrapper.text()).toContain('1 ambiguous records excluded')
    vi.mocked(copilotApi.usage).mockResolvedValue({ ...copilotReport, periods: [{ ...copilotReport.periods[0]!, totals: { ...totals, isPricingComplete: false, unpricedTokens: 1000 } }] })
    await button(wrapper, 'Refresh').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('priced subtotal')
    expect(wrapper.text()).toContain('1K tokens unpriced')
    vi.mocked(copilotApi.usage).mockRejectedValue(new Error('Copilot refresh failed'))
    await button(wrapper, 'Refresh').trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe('Copilot refresh failed')
    expect(wrapper.findAll('article')).toHaveLength(1)
  })

  it('shows warmup without invented zero totals before any observations are indexed', async () => {
    vi.mocked(copilotApi.usage).mockResolvedValue({ ...copilotReport, matchingRecordCount: 0, index: { ...copilotIndex, recordCount: 0, initialScanComplete: false, isIndexing: true, lastSuccessfulScanUtc: null } })
    const { wrapper } = await mountPage()
    expect(wrapper.findAll('article')).toHaveLength(0)
    expect(wrapper.text()).toContain('Copilot usage is not ready yet')
  })

  it('reports invalid timezones through the loading error path without an uncaught watcher error', async () => {
    const { wrapper } = await mountPage()
    await wrapper.find('[aria-label="Copilot reporting timezone"]').setValue('Invalid/Zone'); await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
  })
})

describe('Copilot settings', () => {
  it('saves source settings separately and rescans without saving unrelated edits', async () => {
    const { wrapper } = await mountPage(SettingsPage, '/settings')
    await wrapper.find('#source-directories').setValue('D:\\unsaved-codex')
    await wrapper.find('[aria-label="Copilot source directory 1"]').setValue(' D:\\copilot\\session-state ')
    await wrapper.find('[aria-label="Copilot source type 1"]').setValue('visual-studio')
    await button(wrapper, 'Save Copilot sources').trigger('click'); await flushPromises()
    expect(copilotApi.saveSettings).toHaveBeenCalledWith({ autoDiscover: true, sources: [{ kind: 'visual-studio', path: 'D:\\copilot\\session-state', enabled: true }] })
    expect(api.saveSettings).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Copilot sources saved')
    await button(wrapper, 'Rescan Copilot sources').trigger('click'); await flushPromises()
    expect(copilotApi.rescan).toHaveBeenCalled()
    expect(wrapper.text()).toContain('Copilot rescan requested')
    await button(wrapper, 'Add Copilot source').trigger('click')
    await button(wrapper, 'Save Copilot sources').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('Every Copilot source needs a directory')
    await wrapper.find('[aria-label="Remove Copilot source 2"]').trigger('click')
    expect(wrapper.find('[aria-label="Copilot source directory 2"]').exists()).toBe(false)
  })

  it('edits 5 minute and 1 hour cache rates and rejects negative rates', async () => {
    const { wrapper } = await mountPage(SettingsPage, '/settings')
    await wrapper.find('[aria-label="cacheWrite5mPerMillion 1"]').setValue('1.25')
    await wrapper.find('[aria-label="cacheWrite1hPerMillion 1"]').setValue('2')
    await button(wrapper, 'Save settings').trigger('click'); await flushPromises()
    expect(api.saveSettings).toHaveBeenCalledWith(expect.objectContaining({ pricing: expect.objectContaining({ rates: [expect.objectContaining({ cacheWrite5mPerMillion: 1.25, cacheWrite1hPerMillion: 2 })] }) }))
    await wrapper.find('[aria-label="cacheWrite1hPerMillion 1"]').setValue('-1')
    await button(wrapper, 'Save settings').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('nonnegative')
  })

  it('shows save and rescan errors within the Copilot section', async () => {
    const { wrapper } = await mountPage(SettingsPage, '/settings')
    vi.mocked(copilotApi.saveSettings).mockRejectedValue(new Error('Invalid Copilot source'))
    await button(wrapper, 'Save Copilot sources').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('Invalid Copilot source')
    vi.mocked(copilotApi.rescan).mockRejectedValue(new Error('Copilot scan failed'))
    await button(wrapper, 'Rescan Copilot sources').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('Copilot scan failed')
  })
})

describe('Copilot API client', () => {
  it('uses independent typed endpoints and encodes filters', async () => {
    vi.mocked(copilotApi.usage).mockRestore(); vi.mocked(copilotApi.status).mockRestore(); vi.mocked(copilotApi.settings).mockRestore()
    vi.mocked(copilotApi.filters).mockRestore(); vi.mocked(copilotApi.saveSettings).mockRestore(); vi.mocked(copilotApi.rescan).mockRestore()
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({}) })
    vi.stubGlobal('fetch', fetcher)
    await copilotApi.usage({ client: 'vscode', model: 'claude/sonnet' }); await copilotApi.status(); await copilotApi.filters()
    await copilotApi.settings(); await copilotApi.saveSettings(copilotSettings); await copilotApi.rescan()
    expect(fetcher.mock.calls.map(call => call[0])).toEqual(['/api/copilot/usage?client=vscode&model=claude%2Fsonnet', '/api/copilot/index', '/api/copilot/filters', '/api/copilot/settings', '/api/copilot/settings', '/api/copilot/index/rescan'])
    expect(fetcher.mock.calls[4]![1]).toMatchObject({ method: 'PUT', body: JSON.stringify(copilotSettings) })
    expect(fetcher.mock.calls[5]![1]).toMatchObject({ method: 'POST' })
  })
})
