import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { request } from '@/api/client'
import { createAppHistory } from '@/router'

interface Invocation { version: number; id: string; method: string; path: string; body: string | null }
const envelope = (value: Invocation, body: unknown = { ok: true }, status = 200) => JSON.stringify({ version: 1, id: value.id, status, body: status === 204 ? null : JSON.stringify(body) })
function host(reply: (value: Invocation) => string | Promise<string> = value => envelope(value)) {
  const bridge = { InvokeAsync: vi.fn((raw: string) => reply(JSON.parse(raw) as Invocation)), Cancel: vi.fn().mockResolvedValue(undefined) }
  vi.stubGlobal('__TURNGLASS_DESKTOP__', { version: 1 })
  vi.stubGlobal('chrome', { webview: { hostObjects: { turnglass: bridge } } })
  return bridge
}

beforeEach(() => { vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Desktop must not use HTTP.'))) })
afterEach(() => { window.history.replaceState({}, '', '/') })

describe('desktop transport', () => {
  it('preserves endpoint paths, queries, methods and JSON bodies without using HTTP', async () => {
    const bridge = host()
    await expect(request('/usage?model=claude%2Fsonnet')).resolves.toEqual({ ok: true })
    await request('/settings', { method: 'PUT', body: '{"timezone":"UTC"}' })
    await request('/copilot/index/rescan', { method: 'POST' })
    const calls = bridge.InvokeAsync.mock.calls.map(([raw]) => JSON.parse(raw) as Invocation)
    expect(calls.map(({ method, path, body }) => ({ method, path, body }))).toEqual([
      { method: 'GET', path: '/usage?model=claude%2Fsonnet', body: null },
      { method: 'PUT', path: '/settings', body: '{"timezone":"UTC"}' },
      { method: 'POST', path: '/copilot/index/rescan', body: null },
    ])
    expect(new Set(calls.map(value => value.id)).size).toBe(3)
    expect(calls.every(value => value.version === 1)).toBe(true)
    expect(fetch).not.toHaveBeenCalled()
  })

  it('preserves problem details, fallback errors and no-content results', async () => {
    const bridge = host(value => envelope(value, { detail: 'Revision conflict' }, 409))
    await expect(request('/insights/review', { method: 'POST' })).rejects.toThrow('Revision conflict')
    bridge.InvokeAsync.mockImplementation(raw => envelope(JSON.parse(raw), { title: 'Invalid settings' }, 400))
    await expect(request('/settings')).rejects.toThrow('Invalid settings')
    bridge.InvokeAsync.mockImplementation(raw => JSON.stringify({ version: 1, id: JSON.parse(raw).id, status: 503, body: 'not json' }))
    await expect(request('/index')).rejects.toThrow('503')
    bridge.InvokeAsync.mockImplementation(raw => envelope(JSON.parse(raw), null, 204))
    await expect(request('/index/rebuild', { method: 'POST' })).resolves.toBeUndefined()
  })

  it.each([
    () => 'not json',
    () => 'null',
    (value: Invocation) => JSON.stringify({ version: 2, id: value.id, status: 200, body: '{}' }),
    () => JSON.stringify({ version: 1, id: 'wrong-request', status: 200, body: '{}' }),
    (value: Invocation) => JSON.stringify({ version: 1, id: value.id, status: 999, body: '{}' }),
    (value: Invocation) => JSON.stringify({ version: 1, id: value.id, status: 200.5, body: '{}' }),
    (value: Invocation) => JSON.stringify({ version: 1, id: value.id, status: 200, body: {} }),
    (value: Invocation) => JSON.stringify({ version: 1, id: value.id, status: 200 }),
  ])('rejects malformed or mismatched bridge envelopes', async reply => {
    host(reply)
    await expect(request('/index')).rejects.toThrow('invalid response')
    expect(fetch).not.toHaveBeenCalled()
  })

  it('does not fall back to HTTP when desktop startup or invocation fails', async () => {
    vi.stubGlobal('__TURNGLASS_DESKTOP__', { version: 2 })
    await expect(request('/index')).rejects.toThrow('version is not supported')
    vi.stubGlobal('__TURNGLASS_DESKTOP__', { version: 1 })
    await expect(request('/index')).rejects.toThrow('connection is unavailable')
    const bridge = host(() => Promise.reject(new Error('Native service stopped')))
    await expect(request('/index')).rejects.toThrow('Native service stopped')
    bridge.InvokeAsync.mockImplementation(() => { throw new Error('Host disconnected') })
    await expect(request('/index')).rejects.toThrow('Host disconnected')
    bridge.InvokeAsync.mockImplementation(() => Promise.reject('native failure'))
    await expect(request('/index')).rejects.toThrow('desktop request failed')
    expect(fetch).not.toHaveBeenCalled()
  })

  it('rejects unsupported request bodies and methods before crossing the bridge', async () => {
    const bridge = host()
    await expect(request('/settings', { method: 'DELETE' })).rejects.toThrow('Unsupported desktop request')
    await expect(request('/settings', { method: 'PUT', body: new FormData() })).rejects.toThrow('Unsupported desktop request')
    expect(bridge.InvokeAsync).not.toHaveBeenCalled()
  })

  it('cancels in-flight native work and ignores its late response', async () => {
    let complete!: (value: string) => void
    const bridge = host(() => new Promise(resolve => { complete = resolve }))
    const controller = new AbortController()
    const result = request('/usage', { signal: controller.signal })
    const failure = expect(result).rejects.toMatchObject({ name: 'AbortError' })
    controller.abort()
    await failure
    const invocation = JSON.parse(bridge.InvokeAsync.mock.calls[0]![0]) as Invocation
    expect(bridge.Cancel).toHaveBeenCalledWith(invocation.id)
    complete(envelope(invocation))
    await Promise.resolve()
    expect(bridge.Cancel).toHaveBeenCalledTimes(1)
    await expect(request('/usage', { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' })
    expect(bridge.InvokeAsync).toHaveBeenCalledTimes(1)
  })

  it('cancels pending requests on document replacement, tolerating host shutdown', async () => {
    const bridge = host(() => new Promise(() => {}))
    const first = expect(request('/usage')).rejects.toMatchObject({ name: 'AbortError' })
    const second = expect(request('/index')).rejects.toMatchObject({ name: 'AbortError' })
    bridge.Cancel.mockImplementationOnce(() => { throw new Error('Closed') }).mockRejectedValueOnce(new Error('Closing'))
    window.dispatchEvent(new Event('pagehide'))
    await Promise.all([first, second])
    expect(bridge.Cancel).toHaveBeenCalledTimes(2)
  })

  it('removes abort handlers after normal completion', async () => {
    const bridge = host()
    const controller = new AbortController()
    await request('/index', { signal: controller.signal })
    controller.abort()
    window.dispatchEvent(new Event('pagehide'))
    expect(bridge.Cancel).not.toHaveBeenCalled()
  })
})

describe('desktop navigation', () => {
  it('uses reload-safe hash paths and preserves queries only for the desktop host', () => {
    host()
    window.history.replaceState({}, '', '/index.html#/copilot?client=vscode')
    const desktop = createAppHistory()
    expect(desktop.location).toBe('/copilot?client=vscode')
    expect(desktop.createHref('/sessions?session=a')).toBe('#/sessions?session=a')
    desktop.destroy()
    vi.stubGlobal('__TURNGLASS_DESKTOP__', undefined)
    window.history.replaceState({}, '', '/copilot?client=vscode')
    const browser = createAppHistory()
    expect(browser.location).toBe('/copilot?client=vscode')
    expect(browser.createHref('/sessions?session=a')).toBe('/sessions?session=a')
    browser.destroy()
  })
})
