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
  it('starts with four tiles that say in text what needs attention, and lists only the components that are not healthy', async () => {
    const wrapper = await mountView(answer())
    const tiles = wrapper.findAll('[data-test="summary-tile"]')
    expect(tiles.map((t) => t.get('p').text())).toEqual(['Components', 'Usage writer', 'Providers degraded or down', 'Version'])
    expect(tiles[0]!.get('[data-test="summary-tile-attention"]').text()).toBe('StackExchange.Redis: degraded')
    expect(tiles[1]!.find('[data-test="summary-tile-attention"]').exists()).toBe(false)
    expect(tiles[3]!.get('[data-test="summary-tile-value"]').text()).toBe('0.13.0')

    const rows = wrapper.findAll('[aria-labelledby="ops-components"] li')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('Slow to answer')
    expect(rows[0]!.findComponent({ name: 'UiBadge' }).exists()).toBe(true)
    expect(wrapper.find('[data-testid="components-details"]').exists()).toBe(false)
  })

  it('folds the component list under Details while every component is healthy', async () => {
    const wrapper = await mountView(answer({ components: [{ name: 'GatewayDbContext', status: 'Healthy', description: 'OK' }] }))
    expect(wrapper.get('[data-test="summary-tile-value"]').text()).toBe('Healthy')
    const details = wrapper.get('[data-testid="components-details"]')
    expect(details.text()).toContain('GatewayDbContext')
    expect(details.text()).not.toContain('OK')
    expect(details.findComponent({ name: 'UiBadge' }).exists()).toBe(false)
  })

  it('shows no failed writes as grey text and failed writes as a red badge', async () => {
    const quiet = await mountView(answer())
    expect(quiet.findAllComponents({ name: 'UiBadge' }).map((b) => b.text())).not.toContain('0')

    const failing = await mountView(answer({ usageWriter: { queueDepth: 5, capacity: 10_000, inFlightRecords: 0, lastWriteAt: null, consecutiveFailures: 3 } }))
    expect(failing.findAllComponents({ name: 'UiBadge' }).map((b) => b.text())).toContain('3')
    expect(failing.findAll('[data-test="summary-tile-attention"]').map((a) => a.text())).toContain('3 writes failed in a row')
  })
})
