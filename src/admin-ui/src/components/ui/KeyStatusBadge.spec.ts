import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import KeyStatusBadge from './KeyStatusBadge.vue'

describe('KeyStatusBadge', () => {
  it('shows Active as plain text and the states that need attention as a badge (layout rule 8)', () => {
    const active = mount(KeyStatusBadge, { props: { status: 'Active' } })
    expect(active.text()).toBe('Active')
    expect(active.findComponent({ name: 'UiBadge' }).exists()).toBe(false)

    for (const status of ['InGracePeriod', 'Expired', 'Revoked', 'Disabled'] as const) {
      expect(mount(KeyStatusBadge, { props: { status } }).findComponent({ name: 'UiBadge' }).exists()).toBe(true)
    }
  })
})
