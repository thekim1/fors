import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import UiCard, { CARD_HEADING_LEVEL } from '@/components/ui/UiCard.vue'
import SettingsView from './SettingsView.vue'

const cards = ['TrustedProxiesCard', 'RetentionCard', 'RequestLogCard', 'ModelPricesCard', 'BrandingCard', 'SignInCard', 'VersionCard']

function mountView() {
  setActivePinia(createPinia())
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { render: () => null } }] })
  return mount(SettingsView, {
    attachTo: document.body,
    global: { plugins: [router], stubs: Object.fromEntries(cards.map((c) => [c, { template: `<div data-card="${c}" />` }])) },
  })
}

// The cards load their own data; here only the page's leave guard is under test.
const card = (name: string) =>
  defineComponent({ name, emits: ['dirty'], setup: (_, { emit }) => () => h('button', { 'data-test': name, onClick: () => emit('dirty', true) }) })
const guardStubs = Object.fromEntries(cards.map((n) => [n, card(n)]))

describe('SettingsView', () => {
  it('asks once before leaving while any card has unsaved changes', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/settings', component: SettingsView }, { path: '/other', component: { render: () => null } }],
    })
    await router.push('/settings')
    const wrapper = mount({ render: () => h(RouterView) }, { global: { plugins: [router], stubs: guardStubs } })
    await flushPromises()

    await wrapper.get('[data-test=RetentionCard]').trigger('click')
    await wrapper.get('[data-test=BrandingCard]').trigger('click')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await router.push('/other')
    expect(confirm).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.path).toBe('/settings')

    confirm.mockReturnValue(true)
    await router.push('/other')
    expect(router.currentRoute.value.path).toBe('/other')
  })
})

describe('SettingsView (Q6 A)', () => {
  it('groups the cards under headings by the question, in that order, with a section menu that moves to each', async () => {
    const wrapper = mountView()
    expect(wrapper.findAll('h2').map((x) => x.text())).toEqual(['Network', 'Data', 'Prices', 'This console'])
    expect(wrapper.findAll('section').map((s) => s.findAll('[data-card]').map((c) => c.attributes('data-card')))).toEqual([
      ['TrustedProxiesCard'], ['RetentionCard', 'RequestLogCard'], ['ModelPricesCard'], ['BrandingCard', 'SignInCard', 'VersionCard'],
    ])

    Element.prototype.scrollIntoView = vi.fn()
    await wrapper.get('nav a[href="#settings-prices"]').trigger('click')
    expect(document.activeElement?.id).toBe('settings-prices-h')

    await wrapper.get('nav select').setValue('settings-console')
    expect(document.activeElement?.id).toBe('settings-console-h')
    wrapper.unmount()
  })

  it("gives a card's heading the level the page asks for", () => {
    const grouped = mount(UiCard, { props: { title: 'Branding', headingId: 'b' }, global: { provide: { [CARD_HEADING_LEVEL as symbol]: 3 } } })
    expect(grouped.find('h3#b').text()).toBe('Branding')
    expect(mount(UiCard, { props: { title: 'Alone', headingId: 'a' } }).find('h2#a').exists()).toBe(true)
  })
})
