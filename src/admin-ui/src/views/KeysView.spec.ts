import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { VirtualKey } from '@/api/types'
import KeysView from './KeysView.vue'

const list = vi.fn()
vi.mock('@/api', () => ({ api: { keys: { list: (...a: unknown[]) => list(...a) } } }))

const key = (id: string, name: string, status: VirtualKey['status']) =>
  ({ id, name, status, prefix: `fors-sk-${id}`, teamName: 'Mottagning', departmentName: 'Socialtjänst', integrationName: null, allowedModels: [], allowedResidencies: [], lastUsedAt: null }) as unknown as VirtualKey

async function mountView(address = '/keys') {
  setActivePinia(createPinia())
  list.mockResolvedValue([key('a', 'Chat app', 'Active'), key('b', 'Old bot', 'Revoked'), key('c', 'Search', 'Active')])
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/keys', component: { render: () => null } }] })
  await router.push(address)
  const wrapper = mount(KeysView, { global: { plugins: [router], stubs: { KeyDetailDrawer: true, KeyFormDrawer: true, RotateKeyDialog: true } } })
  await flushPromises()
  return { wrapper, router }
}

const names = (wrapper: Awaited<ReturnType<typeof mountView>>['wrapper']) => wrapper.findAll('tbody tr').map((r) => r.find('th').text().replace(/fors-sk-\w+$/, ''))

describe('KeysView filters (layout rule 2)', () => {
  it('filters by a Status chip with the counts in its options, kept in the address with the search', async () => {
    const { wrapper, router } = await mountView()
    const status = wrapper.get('[data-test="chip-Status"] select')
    expect(status.findAll('option').map((o) => o.text())).toEqual(['All (3)', 'Active (2)', 'Grace period (0)', 'Expired (0)', 'Revoked (1)'])

    await status.setValue('Revoked')
    await flushPromises()
    expect(names(wrapper)).toEqual(['Old bot'])
    expect(router.currentRoute.value.query).toEqual({ status: 'Revoked' })

    await status.setValue('')
    await wrapper.get('#key-search').setValue('chat')
    await flushPromises()
    expect(names(wrapper)).toEqual(['Chat app'])
    expect(router.currentRoute.value.query).toEqual({ q: 'chat' })
  })

  it('opens with the filters in the address, and Clear filters empties it', async () => {
    const { wrapper, router } = await mountView('/keys?status=Active&q=search')
    expect(names(wrapper)).toEqual(['Search'])
    await wrapper.get('[data-test=clear-filters]').trigger('click')
    await flushPromises()
    expect(names(wrapper)).toHaveLength(3)
    expect(router.currentRoute.value.query).toEqual({})
  })
})
