import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import SettingsView from './SettingsView.vue'

// The cards load their own data; here only the page's leave guard is under test.
const card = (name: string) =>
  defineComponent({ name, emits: ['dirty'], setup: (_, { emit }) => () => h('button', { 'data-test': name, onClick: () => emit('dirty', true) }) })
const stubs = Object.fromEntries(
  ['TrustedProxiesCard', 'RetentionCard', 'ModelPricesCard', 'RequestLogCard', 'BrandingCard', 'SignInCard', 'VersionCard'].map((n) => [n, card(n)]),
)

describe('SettingsView', () => {
  it('asks once before leaving while any card has unsaved changes', async () => {
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/settings', component: SettingsView }, { path: '/other', component: { render: () => null } }],
    })
    await router.push('/settings')
    const wrapper = mount({ render: () => h(RouterView) }, { global: { plugins: [router], stubs } })
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
