import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { pageRouter } from '@/test/helpers'
import { useListDetail, type ListDetail } from './useListDetail'

interface Item {
  id: string
  name: string
}
const items: Item[] = [
  { id: '1', name: 'fors-chat' },
  { id: '2', name: 'fors-chat-advanced' },
]

/** Narrow (the width at which a list-and-detail page stacks) or wide, as useMedia sees it. */
function width(narrow: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: narrow && query.includes('max-width'), addEventListener: () => undefined, removeEventListener: () => undefined }))
}

async function mountPage(narrow: boolean, route?: string, loaded = true) {
  width(narrow)
  const router = await pageRouter('routes', route ? { route } : {}, { q: 'chat' })
  const focused = vi.fn()
  const ready = ref(loaded)
  let state!: ListDetail<Item>
  const Page = defineComponent({
    setup() {
      state = useListDetail({ param: 'route', items: () => (ready.value ? items : []), keyOf: (i) => i.name, first: () => (ready.value ? items[0] : undefined), ready: () => ready.value, focusDetail: focused })
      return () => h('div')
    },
  })
  mount(Page, { global: { plugins: [router] } })
  await flushPromises()
  return { router, state: () => state, focused, ready }
}

afterEach(() => vi.unstubAllGlobals())

describe('useListDetail', () => {
  it('shows the first item beside the list on a wide screen, and the address follows a choice by replacing', async () => {
    const { router, state } = await mountPage(false)
    expect(state().selected.value?.name).toBe('fors-chat')
    expect(state().showList.value).toBe(true)
    expect(state().detailPage.value).toBe(false)
    const replace = vi.spyOn(router, 'replace')
    state().select('fors-chat-advanced')
    await flushPromises()
    expect(replace).toHaveBeenCalled()
    expect(router.currentRoute.value.fullPath).toBe('/routes/fors-chat-advanced?q=chat')
    expect(state().selected.value?.name).toBe('fors-chat-advanced')
  })

  it('opens the item the address names, and focuses its heading when the address changes', async () => {
    const { state, focused } = await mountPage(false, 'fors-chat-advanced')
    expect(state().selected.value?.name).toBe('fors-chat-advanced')
    state().select('fors-chat')
    await flushPromises()
    expect(focused).toHaveBeenCalledTimes(1)
  })

  it('shows only the list on a phone, and the chosen item as a page of its own that Back leaves', async () => {
    const { router, state } = await mountPage(true)
    expect(state().selected.value).toBeNull()
    expect(state().showList.value).toBe(true)
    const push = vi.spyOn(router, 'push')
    state().select('fors-chat-advanced')
    await flushPromises()
    expect(push).toHaveBeenCalled()
    expect(state().detailPage.value).toBe(true)
    expect(state().showList.value).toBe(false)
    expect(state().listTo.value).toEqual({ name: 'routes', query: { q: 'chat' } })
    router.back()
    await flushPromises()
    await new Promise((r) => setTimeout(r, 0))
    expect(state().detailPage.value).toBe(false)
  })

  it('drops an address that names no item once the items are in, and waits until then', async () => {
    const { router, ready } = await mountPage(false, 'gone', false)
    expect(router.currentRoute.value.fullPath).toBe('/routes/gone?q=chat')
    ready.value = true
    await flushPromises()
    expect(router.currentRoute.value.fullPath).toBe('/routes?q=chat')
  })
})
