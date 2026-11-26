import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import type { OpsHealth } from '@/api/types'
import OpsView from './OpsView.vue'

const health = vi.fn()
vi.mock('@/api', () => ({ api: { ops: { health: (...a: unknown[]) => health(...a) } } }))

const answer = (overrides: Partial<OpsHealth> = {}): OpsHealth => ({
  checkedAt: '2026-11-26T08:00:00Z',
  components: [
    { name: 'GatewayDbContext', status: 'Healthy', description: 'OK' },
    { name: 'StackExchange.Redis', status: 'Degraded', description: 'Slow to answer' },
  ],
  versions: { adminApi: '0.13.0', gateway: '0.13.0', schema: '20261120' },
  usageWriter: { queueDepth: 0, capacity: 10_000, inFlightRecords: 0, lastWriteAt: null, consecutiveFailures: 0 },
  providers: [],
  ...overrides,
})

async function mountView(value: OpsHealth) {
  setActivePinia(createPinia())
  health.mockResolvedValue(value)
  const wrapper = mount(OpsView, { global: { stubs: { RouterLink: RouterLinkStub, ProviderCircuitTable: true, ConfigDialog: true } } })
  await flushPromises()
  return wrapper
}

describe('OpsView (layout rule 8)', () => {
  it('shows a healthy component as plain text without its "OK", and a component that needs attention with a badge and why', async () => {
    const wrapper = await mountView(answer())
    const rows = wrapper.findAll('li')
    expect(rows[0]!.text()).toContain('Healthy')
    expect(rows[0]!.text()).not.toContain('OK')
    expect(rows[0]!.findComponent({ name: 'UiBadge' }).exists()).toBe(false)
    expect(rows[1]!.text()).toContain('Slow to answer')
    expect(rows[1]!.findComponent({ name: 'UiBadge' }).exists()).toBe(true)
  })

  it('shows no failed writes as grey text and failed writes as a red badge', async () => {
    const quiet = await mountView(answer())
    expect(quiet.findAllComponents({ name: 'UiBadge' }).map((b) => b.text())).not.toContain('0')

    const failing = await mountView(answer({ usageWriter: { queueDepth: 5, capacity: 10_000, inFlightRecords: 0, lastWriteAt: null, consecutiveFailures: 3 } }))
    expect(failing.findAllComponents({ name: 'UiBadge' }).map((b) => b.text())).toContain('3')
  })
})
