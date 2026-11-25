import { afterEach, vi } from 'vitest'
import { enableAutoUnmount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'

enableAutoUnmount(afterEach)
Object.defineProperty(window, 'matchMedia', { writable: true, value: vi.fn().mockImplementation((query: string) => ({ matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn() })) })
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn().mockResolvedValue(undefined) } })
vi.mock('@unovis/vue', () => {
  const stub = (name: string) => defineComponent({ name, props: ['data', 'x', 'y', 'tickFormat', 'template', 'type'], setup: (_, { slots }) => () => h('div', slots.default?.()) })
  return Object.fromEntries(['VisXYContainer', 'VisAxis', 'VisArea', 'VisLine', 'VisCrosshair', 'VisTooltip'].map(name => [name, stub(name)]))
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
  localStorage.clear()
})
