import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { requestsLink } from '@/utils/requestsLink'
import UsageView from './UsageView.vue'

const summary = vi.fn()
const requests = vi.fn()
vi.mock('@/api', () => ({
  api: {
    usage: {
      summary: (...a: unknown[]) => summary(...a),
      requests: (...a: unknown[]) => requests(...a),
      exportCsvUrl: () => '/api/usage/export.csv',
      request: () => Promise.reject(new Error('not found')),
    },
    departments: { list: () => Promise.resolve([]) },
    teams: { list: () => Promise.resolve([]) },
    keys: { list: () => Promise.resolve([]) },
    catalog: { list: () => Promise.resolve([]) },
  },
}))

const emptySummary = { rows: [], totalRequests: 0, totalErrors: 0, totalCostUsd: 0, totalInputTokens: 0, totalOutputTokens: 0, monthlyTotalsBefore: null }

async function mountView(location: Parameters<ReturnType<typeof createRouter>['push']>[0]) {
  setActivePinia(createPinia())
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/usage', name: 'usage', component: { render: () => null } }] })
  await router.push(location)
  const wrapper = mount(UsageView, { global: { plugins: [router], stubs: { RouterLink: RouterLinkStub, UsageDashboard: true } } })
  await flushPromises()
  return { wrapper, router }
}

beforeEach(() => {
  summary.mockReset().mockResolvedValue(emptySummary)
  requests.mockReset().mockResolvedValue({ items: [], total: 3, page: 1, pageSize: 50 })
})

describe('UsageView requests and costs', () => {
  it('opens a link from another page with its filters set as chips', async () => {
    const { wrapper } = await mountView(requestsLink({ from: '2026-11-24', to: '2026-11-25', outcome: 'errors' }))
    expect(requests).toHaveBeenLastCalledWith(expect.objectContaining({ errors: true, page: 1 }))
    expect(new Date(requests.mock.lastCall![0].from).getDate()).toBe(24)
    expect(wrapper.get('[data-test="chip-Outcome"]').classes()).toContain('bg-accent-soft')
    expect(wrapper.get('[data-test=filter-count]').text()).toBe('3 requests')
  })

  it('keeps a changed filter in the address and loads again, and Clear filters empties it', async () => {
    const { wrapper, router } = await mountView('/usage?tab=requests')
    await wrapper.get('[data-test="chip-Outcome"] select').setValue('ProviderError')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ tab: 'requests', outcome: 'ProviderError' })
    expect(requests).toHaveBeenLastCalledWith(expect.objectContaining({ outcome: 'ProviderError' }))

    await wrapper.get('[data-test=clear-filters]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ tab: 'requests' })
    expect(requests.mock.lastCall![0]).not.toHaveProperty('outcome')
  })

  it('has one filter row for both tabs: the dashboard gets the filters, and the request list drops the endpoint', async () => {
    const { wrapper, router } = await mountView('/usage?departmentId=00000000-0000-4000-8000-000000000001&endpoint=Embeddings')
    expect(wrapper.find('[data-test="chip-Endpoint"]').exists()).toBe(true)
    const dashboard = wrapper.findComponent({ name: 'UsageDashboard' })
    expect(dashboard.props('filters')).toMatchObject({ period: '7d', endpoint: 'Embeddings' })

    await wrapper.findAll('[role=tab]')[1]!.trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="chip-Endpoint"]').exists()).toBe(false)
    expect(router.currentRoute.value.query).toEqual({ tab: 'requests', departmentId: '00000000-0000-4000-8000-000000000001' })
  })
})
