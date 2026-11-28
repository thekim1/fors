import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { Catalog } from '@/api/types'
import CatalogView from './CatalogView.vue'

const get = vi.fn()
vi.mock('@/api', () => ({ api: { catalog: { get: (...a: unknown[]) => get(...a) } } }))

const catalog: Catalog = {
  gatewayBaseUrl: 'https://llm.example.se/',
  routes: [
    { name: 'fors-chat', description: 'Everyday chat', kind: 'Chat', residencies: ['Eu'], capabilities: ['ChatCompletions', 'Streaming'], inputUsdPerMillion: 0.15, outputUsdPerMillion: 0.6 },
    { name: 'fors-embed', description: null, kind: 'Embedding', residencies: ['OnPrem'], capabilities: ['Embeddings'], inputUsdPerMillion: 0.02, outputUsdPerMillion: null },
  ],
  models: [
    { name: 'gpt-chat', kind: 'Chat', residency: 'Eu', capabilities: ['ChatCompletions'], inputUsdPerMillion: 0.15, outputUsdPerMillion: 0.6, description: null, features: null },
    { name: 'bge-m3', kind: 'Embedding', residency: 'OnPrem', capabilities: ['Embeddings'], inputUsdPerMillion: null, outputUsdPerMillion: null, description: 'Multilingual embeddings', features: null },
  ],
}

async function mountView(query: Record<string, string> = {}) {
  setActivePinia(createPinia())
  get.mockResolvedValue(catalog)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/catalog', name: 'catalog', component: { template: '<div />' } }] })
  await router.push({ name: 'catalog', query })
  const wrapper = mount(CatalogView, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

const names = (wrapper: Awaited<ReturnType<typeof mountView>>['wrapper'], caption: string) =>
  wrapper.findAll('table').find((t) => t.find('caption').text() === caption)!.findAll('tbody tr .font-mono').map((n) => n.text())

beforeEach(() => get.mockReset())

describe('CatalogView (layout audit step 10)', () => {
  it('shows each alias’s capabilities as short chips with their full names for screen readers', async () => {
    const { wrapper } = await mountView()
    const chips = wrapper.find('[aria-label="Capabilities of fors-chat"]').findAll('li')
    expect(chips.map((c) => c.find('[aria-hidden="true"]').text())).toEqual(['Chat', 'Streaming'])
    expect(chips.map((c) => c.find('.sr-only').text())).toEqual(['Chat completions', 'Streaming'])
  })

  it('searches names and descriptions in both tables and keeps the search in the address', async () => {
    const { wrapper, router } = await mountView()
    await wrapper.find('#catalog-search').setValue('multilingual')
    await flushPromises()
    expect(names(wrapper, 'Models')).toEqual(['bge-m3'])
    expect(wrapper.text()).toContain('No aliases match your filters.')
    expect(router.currentRoute.value.query.q).toBe('multilingual')
  })

  it('filters by kind from the address, offers only the kinds there are, and clears', async () => {
    const { wrapper, router } = await mountView({ kind: 'Embedding' })
    expect(names(wrapper, 'Model aliases')).toEqual(['fors-embed'])
    expect(names(wrapper, 'Models')).toEqual(['bge-m3'])
    expect(wrapper.text()).toContain('Embedding (2)')
    expect(wrapper.text()).not.toContain('Speech to text (')
    await wrapper.find('[data-test="clear-filters"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.kind).toBeUndefined()
    expect(names(wrapper, 'Models')).toEqual(['gpt-chat', 'bge-m3'])
  })
})
