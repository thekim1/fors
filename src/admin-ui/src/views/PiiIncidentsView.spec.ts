import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { PiiIncident, PiiIncidentReport } from '@/api/types'
import { button } from '@/test/helpers'
import PiiIncidentsView from './PiiIncidentsView.vue'

// Ids are Guids: the filters in the address take nothing else (vi.hoisted, since vi.mock runs before the module body).
const { K1, T1, D1 } = vi.hoisted(() => ({
  K1: '00000000-0000-4000-8000-0000000000a1',
  T1: '00000000-0000-4000-8000-0000000000b1',
  D1: '00000000-0000-4000-8000-0000000000c1',
}))
const piiIncidents = vi.fn()
vi.mock('@/api', () => ({
  api: {
    usage: { piiIncidents: (...a: unknown[]) => piiIncidents(...a) },
    departments: { list: () => Promise.resolve([{ id: D1, name: 'Socialtjänst' }]) },
    teams: { list: () => Promise.resolve([{ id: T1, name: 'Mottagning', departmentId: D1 }]) },
    keys: { list: () => Promise.resolve([{ id: K1, name: 'Chat app', teamId: T1, departmentId: D1 }]) },
  },
}))

function incident(overrides: Partial<PiiIncident> = {}): PiiIncident {
  return {
    requestId: 'req_1',
    timestamp: '2026-10-11T08:00:00Z',
    keyId: K1,
    keyPrefix: 'fors-sk-ab12',
    keyName: 'Chat app',
    teamId: T1,
    teamName: 'Mottagning',
    departmentId: D1,
    departmentName: 'Socialtjänst',
    endpoint: '/v1/chat/completions',
    requestedModel: 'fors/chat',
    providerName: 'azure-se',
    action: 'Redact',
    categories: [
      { category: 'Personnummer', count: 2 },
      { category: 'Email', count: 1 },
    ],
    outcome: 'Success',
    statusCode: 200,
    routingRuleName: null,
    ...overrides,
  }
}

function report(items: PiiIncident[], overrides: Partial<PiiIncidentReport> = {}): PiiIncidentReport {
  return {
    from: '2026-09-11T00:00:00Z',
    to: '2026-10-11T23:59:59Z',
    items,
    total: items.length,
    totalIncidents: items.length,
    categories: [
      { category: 'Personnummer', requests: 2, occurrences: 3 },
      { category: 'Email', requests: 1, occurrences: 1 },
    ],
    actions: [
      { action: 'Redact', requests: 1 },
      { action: 'Block', requests: 1 },
    ],
    ...overrides,
  }
}

async function mountView(address = '/pii') {
  setActivePinia(createPinia())
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/pii', component: { render: () => null } }] })
  await router.push(address)
  const wrapper = mount(PiiIncidentsView, { global: { plugins: [router], stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return { wrapper, router }
}

/** The select inside the filter chip named `label`. */
const chip = (wrapper: ReturnType<typeof mount>, label: string) => wrapper.get(`[data-test="chip-${label}"] select`)

beforeEach(() => piiIncidents.mockReset())

describe('PiiIncidentsView', () => {
  it('sums up in four tiles and one table of every category and action, zeros grey, and links each incident to its request', async () => {
    piiIncidents.mockResolvedValue(report([incident(), incident({ requestId: 'req_2', action: 'Block', outcome: 'PiiBlocked', statusCode: 422 })]))
    const { wrapper } = await mountView()

    expect(wrapper.find('h1').text()).toBe('PII incidents')
    const tiles = wrapper.findAll('[data-testid="pii-tiles"] [data-test="summary-tile"]').map((t) => t.text())
    expect(tiles).toEqual(['Requests with PII2', 'Most foundPersonal identity number2 requests', 'Blocked1', 'Redacted1'])
    const counts = wrapper.findAll('[data-testid="pii-counts"] tbody tr').map((r) => r.text())
    expect(counts).toContain('Personal identity number23')
    expect(counts).toContain('Sent on-prem0—Not counted per action')
    const zero = wrapper.findAll('[data-testid="pii-counts"] td').find((td) => td.text() === '0')!
    expect(zero.classes()).toContain('text-fg-3')
    const rows = wrapper.findAll('table.data-table tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Personal identity number × 2')
    expect(rows[0]!.text()).toContain('Redacted')
    expect(rows[1]!.text()).toContain('PII blocked')
    expect(wrapper.findAllComponents(RouterLinkStub)[0]!.props('to')).toEqual({ name: 'usage', query: { request: 'req_1' } })
    expect(wrapper.find('[data-testid="pii-explainer"]').exists()).toBe(false)
  })

  it('sends the chosen filters and starts again on page one', async () => {
    piiIncidents.mockResolvedValue(report([incident()]))
    const { wrapper } = await mountView()
    expect(piiIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 50 }))

    await chip(wrapper, 'Key').setValue(K1)
    await chip(wrapper, 'Category').setValue('Personnummer')
    await chip(wrapper, 'Action').setValue('Block')
    await flushPromises()

    expect(piiIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ keyId: K1, category: 'Personnummer', action: 'Block', page: 1 }))
    expect(wrapper.get('[data-test=filter-count]').text()).toBe('1 request with PII')
  })

  it('explains that scanning is per key when nothing was found', async () => {
    piiIncidents.mockResolvedValue(report([], { categories: [], actions: [] }))
    const { wrapper } = await mountView()

    expect(wrapper.find('[data-testid="pii-explainer"]').text()).toContain('A key with PII policy Off is not scanned')
    expect(wrapper.text()).toContain('No PII found in this period.')
  })

  it('offers to clear the category and action filters when they hide everything', async () => {
    piiIncidents.mockResolvedValue(report([], { totalIncidents: 2 }))
    const { wrapper } = await mountView()
    await chip(wrapper, 'Action').setValue('RerouteToOnPrem')
    await flushPromises()
    expect(wrapper.text()).toContain('No incidents match these filters.')

    await button(wrapper, 'Clear filters').trigger('click')
    await flushPromises()
    expect(piiIncidents).toHaveBeenLastCalledWith(expect.not.objectContaining({ action: expect.anything() }))
  })

  it('reads its filters from the address and keeps them there, without the defaults', async () => {
    piiIncidents.mockResolvedValue(report([incident()]))
    const { wrapper, router } = await mountView('/pii?period=custom&from=2026-10-01&to=2026-10-07&category=Email&action=Explode')
    expect(piiIncidents).toHaveBeenLastCalledWith(expect.objectContaining({ category: 'Email' }))
    expect(piiIncidents.mock.lastCall![0]).not.toHaveProperty('action') // a value it does not know reads as unset
    expect(new Date(piiIncidents.mock.lastCall![0].from).getDate()).toBe(1)
    expect(wrapper.get('[data-test="chip-Category"]').classes()).toContain('bg-accent-soft')

    await chip(wrapper, 'Period').setValue('7d')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ period: '7d', category: 'Email' })
  })
})
