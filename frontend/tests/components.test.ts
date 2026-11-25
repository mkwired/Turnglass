import { mount, flushPromises } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import UsageCard from '@/components/UsageCard.vue'
import UsageChart from '@/components/UsageChart.vue'
import IndexNotice from '@/components/IndexNotice.vue'
import TranscriptEntry from '@/components/TranscriptEntry.vue'
import { api } from '@/api/client'
import { event, index, report, incomplete } from './fixtures'

describe('usage presentation', () => {
  it('distinguishes priced subtotals from complete cost estimates', () => {
    const complete = mount(UsageCard, { props: { period: report.periods[0]! } })
    expect(complete.text()).toContain('1.1M'); expect(complete.text()).toContain('$10.50'); expect(complete.text()).not.toContain('unpriced')
    const partial = mount(UsageCard, { props: { period: { ...report.periods[0]!, totals: incomplete } } })
    expect(partial.text()).toContain('priced subtotal'); expect(partial.text()).toContain('1.1M tokens unpriced')
  })
  it('renders partial/stale diagnostics without pretending a fresh index is complete', () => {
    expect(mount(IndexNotice, { props: { status: index } }).text()).toBe('')
    const partial = mount(IndexNotice, { props: { status: { ...index, initialIndexComplete: false, warnings: ['Malformed line'], sources: [{ ...index.sources[0]!, available: false, error: null }] } } })
    expect(partial.text()).toContain('totals are partial'); expect(partial.text()).toContain('Malformed line'); expect(partial.text()).toContain('retained data')
    expect(mount(IndexNotice, { props: { status: { ...index, sources: [{ ...index.sources[0]!, available: false, error: 'Access denied' }] } } }).text()).toContain('Access denied')
  })
  it('provides chart axes, token/cost series and an accessible table', async () => {
    const wrapper = mount(UsageChart, { props: { buckets: report.buckets, metric: 'tokens' } })
    expect(wrapper.find('table').text()).toContain('1,100,000')
    const axes = wrapper.findAllComponents({ name: 'VisAxis' })
    expect(axes[0]!.props('tickFormat')(new Date('2026-11-21T12:00:00Z').getTime())).toBe('Nov 21')
    expect(axes[1]!.props('tickFormat')(1100000)).toBe('1.1M')
    const line = wrapper.findComponent({ name: 'VisLine' })
    expect(line.props('x')({ x: 4 })).toBe(4); expect(line.props('y')({ y: 5 })).toBe(5)
    expect(wrapper.findComponent({ name: 'VisCrosshair' }).props('template')({ x: new Date('2026-11-21T12:00:00Z').getTime(), y: 1100000 })).toContain('1.1M')
    await wrapper.setProps({ metric: 'cost', buckets: [{ ...report.buckets[0]!, totals: incomplete }] })
    expect(axes[1]!.props('tickFormat')(10.5)).toBe('$10.50')
    expect(wrapper.find('table').text()).toContain('Incomplete')
    expect(wrapper.findComponent({ name: 'VisXYContainer' }).props('data')[0].y).toBe(0)
    await wrapper.setProps({ buckets: [] }); expect(wrapper.text()).toContain('No usage')
  })
})

describe('transcripts', () => {
  it('escapes HTML, chunks raw text, and reopens loaded content without duplication', async () => {
    const record = vi.spyOn(api, 'record').mockResolvedValueOnce({ eventId: event.id, text: '{"first":', nextOffset: 9, totalLength: 14 }).mockResolvedValueOnce({ eventId: event.id, text: 'true}', nextOffset: null, totalLength: 14 })
    const wrapper = mount(TranscriptEntry, { props: { event: { ...event, inherited: true, truncated: true } } })
    expect(wrapper.find('script').exists()).toBe(false); expect(wrapper.text()).toContain('<script>'); expect(wrapper.text()).toContain('Inherited context'); expect(wrapper.text()).toContain('Preview limited')
    await wrapper.find('button').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === 'Load more raw text')!.trigger('click'); await flushPromises()
    expect(record).toHaveBeenLastCalledWith('session-one', 'event-one', 9)
    expect(wrapper.text()).toContain('{"first":true}')
    await wrapper.findAll('button').find(button => button.text() === 'Hide')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === 'View raw record')!.trigger('click')
    expect(record).toHaveBeenCalledTimes(2)
  })
  it('shows encrypted and tool events, and exposes raw read errors', async () => {
    vi.spyOn(api, 'record').mockRejectedValue(new Error('Record unavailable'))
    const wrapper = mount(TranscriptEntry, { props: { event: { ...event, role: null, type: 'function_call', encrypted: true } } })
    expect(wrapper.text()).toContain('Encrypted reasoning is unavailable')
    await wrapper.setProps({ event: { ...event, role: null, type: 'function_call', encrypted: false } })
    expect(wrapper.find('details').exists()).toBe(true)
    await wrapper.find('button').trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Record unavailable')
    await wrapper.setProps({ event: { ...event, role: 'assistant' } })
    expect(wrapper.text()).toContain('<script>')
  })
})
