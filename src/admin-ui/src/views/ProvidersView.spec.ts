import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { Model, Provider } from '@/api/types'
import ProvidersView from './ProvidersView.vue'

const provider = (id: string, name: string, type: Provider['type']) =>
  ({ id, name, displayName: null, type, residency: 'Eu', baseUrl: 'https://example.test/v1', isEnabled: true, isDrained: false, deploymentCount: 1 }) as unknown as Provider
const model = (id: string, name: string, kind: Model['kind']) =>
  ({ id, name, publicName: name, upstreamModel: name, providerId: 'p1', providerName: 'eu', kind, residency: 'Eu', features: [], contextWindow: null, currentPrice: null, priceMode: 'Manual', isEnabled: true }) as unknown as Model

vi.mock('@/api', () => ({
  api: {
    providers: { list: () => Promise.resolve([provider('p1', 'eu', 'OpenAICompatible'), provider('p2', 'claude', 'Anthropic')]) },
    models: { list: () => Promise.resolve([model('m1', 'eu/chat', 'Chat'), model('m2', 'eu/embed', 'Embedding')]) },
  },
}))

async function mountView(address: string) {
  setActivePinia(createPinia())
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/providers', component: { render: () => null } }] })
  await router.push(address)
  const wrapper = mount(ProvidersView, { global: { plugins: [router], stubs: { ProviderFormDrawer: true, ModelFormDrawer: true } } })
  await flushPromises()
  return { wrapper, router }
}

const rows = (wrapper: Awaited<ReturnType<typeof mountView>>['wrapper']) => wrapper.findAll('tbody tr').length

describe('ProvidersView (layout rules 2 and 6)', () => {
  it('has Providers and Models as tabs kept in the address, and an older ?show=models link opens Models', async () => {
    const { wrapper, router } = await mountView('/providers?show=models')
    expect(router.currentRoute.value.query).toEqual({ tab: 'models' })
    expect(wrapper.get('[role=tab][aria-selected=true]').text()).toContain('Models (2)')
    expect(wrapper.get('[role=tabpanel]').attributes('aria-labelledby')).toBe('providers-tab-models')
  })

  it('filters with chips kept in the address, and the other tab drops the filters it does not have, keeping the search', async () => {
    const { wrapper, router } = await mountView('/providers?tab=models')
    await wrapper.get('[data-test="chip-Kind"] select').setValue('Embedding')
    await flushPromises()
    expect(rows(wrapper)).toBe(1)
    expect(router.currentRoute.value.query).toEqual({ tab: 'models', kind: 'Embedding' })

    await wrapper.get('#model-search').setValue('e')
    await flushPromises()
    await wrapper.findAll('[role=tab]')[0]!.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ q: 'e' })
    expect(rows(wrapper)).toBe(2)

    await wrapper.get('[data-test="chip-Type"] select').setValue('Anthropic')
    await flushPromises()
    expect(rows(wrapper)).toBe(1)
    expect(router.currentRoute.value.query).toEqual({ q: 'e', type: 'Anthropic' })
  })
})
