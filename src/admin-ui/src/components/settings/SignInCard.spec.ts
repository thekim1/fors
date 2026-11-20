import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { SignInSettings } from '@/api/types'
import { signInSettings } from '@/test/fixtures'
import SignInCard from './SignInCard.vue'

const signIn = vi.fn()
vi.mock('@/api', () => ({
  api: { settings: { signIn: (...a: unknown[]) => signIn(...a) } },
}))

async function mountCard(value: SignInSettings) {
  signIn.mockResolvedValue(value)
  const wrapper = mount(SignInCard)
  await flushPromises()
  return wrapper
}

beforeEach(() => signIn.mockReset())

describe('SignInCard', () => {
  it('shows each value with its source, and says the values change only in setup mode', async () => {
    const wrapper = await mountCard(signInSettings({
      authority: { value: 'https://idp.example.org/realms/fors', source: 'wizard', locked: false },
      groupClaim: { value: 'groups', source: 'deployment', locked: true },
      clientSecret: { set: true, source: 'wizard', setAt: '2026-11-16T08:00:00Z', expiresOn: '2027-11-01', unreadable: false, locked: false },
      updatedAt: '2026-11-16T08:00:00Z',
    }))
    expect(wrapper.get('[data-test=sign-in-authority]').text()).toContain('https://idp.example.org/realms/fors')
    expect(wrapper.get('[data-test=sign-in-authority]').text()).toContain('Set in setup')
    expect(wrapper.get('[data-test=sign-in-groupClaim]').text()).toContain('Set by the deployment')
    expect(wrapper.get('[data-test=sign-in-roleClaim]').text()).toContain('Default')
    expect(wrapper.get('[data-test=sign-in-clientSecret]').text()).toContain('expires 1 Nov 2027')
    expect(wrapper.text()).toContain('change only in setup mode')
    expect(wrapper.text()).toContain('FORS_SETUP_REOPEN')
    expect(wrapper.find('input').exists()).toBe(false)
  })

  it('warns about a stored secret the admin API cannot read', async () => {
    const wrapper = await mountCard(signInSettings({
      clientSecret: { set: true, source: 'wizard', setAt: null, expiresOn: null, unreadable: true, locked: false },
    }))
    expect(wrapper.get('[data-test=sign-in-clientSecret]').text()).toContain('cannot read it')
  })
})
