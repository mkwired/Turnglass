import { defineComponent, h, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { useLoader } from '@/composables/useLoader'
import { useAutoRefresh } from '@/composables/useAutoRefresh'

describe('request lifetimes', () => {
  it('ignores stale responses and clears loading on failure', async () => {
    let state!: ReturnType<typeof useLoader<string>>
    const wrapper = mount(defineComponent({ setup() { state = useLoader<string>(); return () => h('div') } }))
    let resolveOld!: (value: string) => void
    const old = state.load(() => new Promise(resolve => { resolveOld = resolve }))
    expect(state.loading.value).toBe(true)
    await state.load(async () => 'new')
    resolveOld('old'); expect(await old).toBe(false)
    expect(state.data.value).toBe('new')
    expect(await state.load(async () => { throw new Error('offline') })).toBe(false)
    expect(state.error.value).toBe('offline'); expect(state.loading.value).toBe(false)
    let rejectOld!: (error: Error) => void
    const failed = state.load(() => new Promise((_, reject) => { rejectOld = reject }))
    await state.load(async () => 'current')
    rejectOld(new Error('stale')); await failed
    expect(state.error.value).toBe('')
    const afterUnmount = state.load(() => new Promise(resolve => { resolveOld = resolve }))
    wrapper.unmount(); resolveOld('late'); await afterUnmount
    expect(state.data.value).toBe('current')
  })
  it('refreshes visible pages and cleans up intervals/listeners', async () => {
    vi.useFakeTimers()
    const refresh = vi.fn()
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    const wrapper = mount(defineComponent({ setup() { useAutoRefresh(refresh); return () => h('div') } }))
    await nextTick(); expect(refresh).toHaveBeenCalledTimes(1)
    vi.advanceTimersByTime(5000); expect(refresh).toHaveBeenCalledTimes(2)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    vi.advanceTimersByTime(5000); expect(refresh).toHaveBeenCalledTimes(2)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    document.dispatchEvent(new Event('visibilitychange')); expect(refresh).toHaveBeenCalledTimes(3)
    wrapper.unmount(); vi.advanceTimersByTime(5000); document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises(); expect(refresh).toHaveBeenCalledTimes(3)
  })
})
