import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import PeriodPicker from './PeriodPicker.vue'

describe('PeriodPicker', () => {
  it('offers the last 7 and 30 days, this month and a custom range by default, 30 days meaning not filtered', () => {
    const wrapper = mount(PeriodPicker, { props: { period: '30d', idPrefix: 't' } })
    expect(wrapper.findAll('option').map((o) => o.text())).toEqual(['Last 30 days', 'Last 7 days', 'This month', 'Custom…'])
  })

  it("takes a page's own presets and default, without date fields when it has no custom range", async () => {
    const wrapper = mount(PeriodPicker, { props: { period: '24h', idPrefix: 't', presets: ['24h', '7d', '30d'], defaultPeriod: '24h' } })
    expect(wrapper.findAll('option').map((o) => o.text())).toEqual(['Last 24 hours', 'Last 7 days', 'Last 30 days'])
    expect(wrapper.find('[data-test="chip-Period"]').classes()).not.toContain('bg-accent-soft')
    await wrapper.setProps({ period: '7d' })
    expect(wrapper.find('[data-test="chip-Period"]').classes()).toContain('bg-accent-soft')
    expect(wrapper.find('input[type=date]').exists()).toBe(false)
  })
})
