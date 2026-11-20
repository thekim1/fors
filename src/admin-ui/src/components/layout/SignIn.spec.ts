import { describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import SignIn from './SignIn.vue'

async function signInPage(flags: { configured: boolean; open: boolean }, path = '/keys') {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = {
    isAuthenticated: false, name: null, email: null, roles: [], departmentCodes: [], sessionExpiresAt: null,
    signIn: { configured: flags.configured }, setup: { open: flags.open },
  }
  auth.status = 'unauthenticated'
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: {} }, { path: '/keys', component: {} }, { path: '/setup', name: 'setup', component: {} }],
  })
  await router.push(path)
  const wrapper = mount(SignIn, { global: { plugins: [router] } })
  return { wrapper, router }
}

describe('the sign-in page in setup mode', () => {
  it('offers only the sign-in when sign-in is set up and setup is closed', async () => {
    const { wrapper } = await signInPage({ configured: true, open: false })
    expect(wrapper.get('[data-test=sign-in]').attributes('href')).toBe('/bff/login?returnUrl=%2Fkeys')
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(false)
    expect(wrapper.find('[data-test=not-configured]').exists()).toBe(false)
  })

  it('leads to the wizard, and offers no sign-in, when sign-in is not set up and setup is open', async () => {
    const { wrapper, router } = await signInPage({ configured: false, open: true })
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(false)
    expect(wrapper.get('[data-test=setup-open]').text()).toContain('Sign-in is not set up yet')
    const setUp = wrapper.get('[data-test=set-up]')
    expect(setUp.attributes('href')).toBe('/setup')
    await setUp.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('setup')
  })

  it('keeps the sign-in beside the wizard while a re-open is active', async () => {
    const { wrapper } = await signInPage({ configured: true, open: true })
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(true)
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(true)
  })

  it('says how to set up sign-in, and offers no sign-in, when it is not set up and setup is closed', async () => {
    const { wrapper } = await signInPage({ configured: false, open: false })
    const text = wrapper.get('[data-test=not-configured]').text()
    expect(text).toContain('FORS_OIDC_AUTHORITY')
    expect(text).toContain('FORS_SETUP_REOPEN')
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(false)
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(false)
    expect(wrapper.find('a').exists()).toBe(false)
  })

  it('explains a sign-in the admin API could not complete, and signs in again without the marker', async () => {
    const { wrapper } = await signInPage({ configured: true, open: false }, '/?signin=failed')
    expect(wrapper.get('[data-test=sign-in-failed]').attributes('role')).toBe('alert')
    expect(wrapper.get('[data-test=sign-in-failed]').text()).toContain('The sign-in could not be completed')
    expect(wrapper.get('[data-test=sign-in]').attributes('href')).toBe('/bff/login?returnUrl=%2F')
  })
})
