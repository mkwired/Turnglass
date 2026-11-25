import { describe, expect, it, vi } from 'vitest'
import { api, queryString, request } from '@/api/client'
import { compact, dollars, number, dateBounds, dateRange, dateTime, shortProject, messageOf } from '@/lib/format'
import { formatMappings, parseMappings, mcpConfiguration, validateSettings } from '@/lib/settings'
import { settings } from './fixtures'

describe('formats and reporting boundaries', () => {
  it('formats precise small costs without disguising them as zero', () => {
    expect(dollars(0.000012)).toBe('$0.000012')
    expect(dollars(12.5)).toBe('$12.50')
    expect(dollars(0)).toBe('$0.00')
    expect(compact(1100000)).toBe('1.1M')
    expect(number(1100000)).toBe('1,100,000')
  })
  it('uses inclusive date selection with an exclusive upper API boundary', () => {
    vi.useFakeTimers(); vi.setSystemTime('2026-11-21T16:00:00Z')
    expect(dateRange()).toEqual({ from: '2026-10-23', to: '2026-11-21' })
    expect(dateRange(1)).toEqual({ from: '2026-11-21', to: '2026-11-21' })
    expect(dateBounds('2026-11-01', '2026-11-21')).toEqual({ from: '2026-11-01T00:00:00.000Z', to: '2026-11-22T00:00:00.000Z' })
    expect(dateBounds('2026-11-01', '2026-11-01', 'America/New_York')).toEqual({ from: '2026-11-01T04:00:00.000Z', to: '2026-11-02T05:00:00.000Z' })
    expect(dateBounds('2026-03-08', '2026-03-08', 'America/New_York')).toEqual({ from: '2026-03-08T05:00:00.000Z', to: '2026-03-09T04:00:00.000Z' })
    expect(dateBounds('', '')).toEqual({ from: undefined, to: undefined })
    expect(dateTime('2026-11-21T16:00:00Z')).toContain('11:00')
    expect(dateTime('2026-11-21T16:00:00Z', 'UTC')).toContain('4:00')
    vi.setSystemTime('2026-11-22T01:00:00Z')
    expect(dateRange(1, 'America/New_York')).toEqual({ from: '2026-11-21', to: '2026-11-21' })
    expect(dateRange(1, 'UTC')).toEqual({ from: '2026-11-22', to: '2026-11-22' })
  })
  it('normalizes readable project names and error messages', () => {
    expect(shortProject('D:\\source\\viewer')).toBe('viewer')
    expect(shortProject('/work/viewer/')).toBe('viewer')
    expect(shortProject('/')).toBe('/')
    expect(shortProject(null)).toBe('No project')
    expect(messageOf(new Error('Failed'))).toBe('Failed')
    expect(messageOf(null)).toContain('retry')
  })
})

describe('settings validation', () => {
  it('roundtrips explicit mappings and rejects malformed or duplicate entries', () => {
    expect(parseMappings(formatMappings({ custom: 'gpt-6-astra' }))).toEqual({ custom: 'gpt-6-astra' })
    expect(parseMappings('\n')).toEqual({})
    expect(() => parseMappings('no separator')).toThrow('name = value')
    expect(() => parseMappings(' = value')).toThrow()
    expect(() => parseMappings('key = ')).toThrow()
    expect(() => parseMappings('key=a\nkey=b')).toThrow('Duplicate')
  })
  it('rejects incomplete rates and invalid boundaries before saving', () => {
    expect(() => validateSettings(settings)).not.toThrow()
    const change = (update: (value: typeof settings) => void) => { const value = structuredClone(settings); update(value); return () => validateSettings(value) }
    expect(change(value => { value.sources = [] })).toThrow('directory')
    expect(change(value => { value.timezone = 'Invalid/Zone' })).toThrow('timezone')
    expect(change(value => { value.pricing.version = '' })).toThrow('version')
    expect(change(value => { value.pricing.rates[0]!.model = '' })).toThrow('model and tier')
    expect(change(value => { value.pricing.rates[0]!.tier = '' })).toThrow('model and tier')
    expect(change(value => { value.pricing.rates[0]!.inputPerMillion = -1 })).toThrow('nonnegative')
    expect(change(value => { value.pricing.rates[0]!.outputPerMillion = Number.NaN })).toThrow('nonnegative')
    expect(change(value => { value.pricing.rates[0]!.minInputTokens = 300000 })).toThrow('maximum')
    expect(change(value => { value.pricing.rates[0]!.maxInputTokens = null; value.pricing.rates[0]!.cacheWrite30mPerMillion = null })).not.toThrow()
  })
  it('escapes Windows paths in a usable Codex TOML configuration', () => {
    expect(mcpConfiguration('C:\\tools\\Turnglass.Mcp.exe', '')).toContain('[mcp_servers.turnglass]\ncommand = "C:\\\\tools\\\\Turnglass.Mcp.exe"')
    expect(mcpConfiguration('C:\\tools\\mcp.exe', '')).toContain('args = []')
    expect(mcpConfiguration('C:\\tools\\mcp.exe', ' C:\\data ')).toContain('args = ["--data-dir","C:\\\\data"]')
  })
})

describe('API contract', () => {
  it('encodes query parameters and omits absent filters', () => {
    expect(queryString({ query: '<foo>', absent: null, blank: '', missing: undefined, offset: 0 })).toBe('?query=%3Cfoo%3E&offset=0')
    expect(queryString({})).toBe('')
  })
  it('surfaces structured and unstructured HTTP failures', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 400, json: async () => ({ detail: 'Invalid zone' }) })
      .mockResolvedValueOnce({ ok: false, status: 500, json: async () => ({ title: 'Failed' }) })
      .mockResolvedValueOnce({ ok: false, status: 503, json: async () => { throw new Error() } })
      .mockResolvedValueOnce({ ok: true, status: 204 })
    vi.stubGlobal('fetch', fetcher)
    await expect(request('/usage')).rejects.toThrow('Invalid zone')
    await expect(request('/usage')).rejects.toThrow('Failed')
    await expect(request('/usage')).rejects.toThrow('503')
    await expect(request('/index/rebuild')).resolves.toBeUndefined()
  })
  it('sends all public operations to their typed endpoints', async () => {
    const fetcher = vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ ok: true }) })
    vi.stubGlobal('fetch', fetcher)
    await api.usage({ mode: 'rolling' }); await api.sessions({ query: 'parser' }); await api.session('a/b')
    await api.events('a', 'next'); await api.record('a', 'b'); await api.record('a', 'b', 16000)
    await api.search('hello', 'a', 'next'); await api.filters(); await api.status(); await api.settings(); await api.saveSettings(settings); await api.rebuild()
    expect(fetcher.mock.calls.map(call => call[0])).toEqual(['/api/usage?mode=rolling', '/api/sessions?query=parser', '/api/sessions/a%2Fb', '/api/sessions/a/events?cursor=next', '/api/sessions/a/records/b?offset=0', '/api/sessions/a/records/b?offset=16000', '/api/search?query=hello&sessionId=a&cursor=next', '/api/filters', '/api/index', '/api/settings', '/api/settings', '/api/index/rebuild'])
    expect(fetcher.mock.calls[10]![1]).toMatchObject({ method: 'PUT', body: JSON.stringify(settings) })
  })
})
