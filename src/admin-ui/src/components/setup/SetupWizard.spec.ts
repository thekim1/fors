import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { ApiError } from '@/api/client'
import type { SetupState } from '@/api/types'
import { setupState, setupTest, signInSettings } from '@/test/fixtures'
import { SETUP_COMPLETE_REASON, SETUP_TEST_REASON } from '@/utils/labels'
import SetupWizard from './SetupWizard.vue'
import { navigation } from './testWindow'

const session = vi.fn()
const state = vi.fn()
const test = vi.fn()
const complete = vi.fn()
vi.mock('@/api', () => ({
  api: {
    setup: {
      session: (...a: unknown[]) => session(...a),
      state: (...a: unknown[]) => state(...a),
      test: (...a: unknown[]) => test(...a),
      complete: (...a: unknown[]) => complete(...a),
    },
  },
}))

const AUTHORITY = 'https://idp.example.org/realms/ume'
const AUTHORIZE = 'https://idp.example.org/realms/ume/protocol/openid-connect/auth?state=ume-setup-test.x'
const SESSION_ENDED = 'Your setup session has ended.'

const noSession = () => new ApiError(401, 'GET', { code: 'setup_session_required' })
const fakePopup = () => ({ closed: false, close: vi.fn(), location: { href: 'about:blank' } })

async function mountWizard(): Promise<VueWrapper> {
  const wrapper = mount(SetupWizard, { attachTo: document.body })
  await flushPromises()
  return wrapper
}

/** A session that resumes with `value` (a reload with a live setup cookie). */
async function resume(value: SetupState): Promise<VueWrapper> {
  state.mockResolvedValue(value)
  return mountWizard()
}

const heading = (wrapper: VueWrapper) => wrapper.get('[data-test=step-heading]').text()
const input = (wrapper: VueWrapper, id: string) => wrapper.get(`#${id}`).element as HTMLInputElement

async function enterCode(wrapper: VueWrapper, code: string) {
  await wrapper.get('#setup-code').setValue(code)
  await wrapper.get('[data-test=code-step]').trigger('submit')
  await flushPromises()
}

async function goTo(wrapper: VueWrapper, step: 'provider' | 'signIn' | 'save') {
  await wrapper.get(`[data-test=stepper-${step}]`).trigger('click')
  await flushPromises()
}

async function startTest(wrapper: VueWrapper) {
  await wrapper.get('[data-test=sign-in-step]').trigger('submit')
  await flushPromises()
}

async function chooseSecret(wrapper: VueWrapper, label: string) {
  const option = wrapper.findAll('[data-test=secret-choice] [role=radio]').find((b) => b.text() === label)
  if (!option) throw new Error(`No secret choice "${label}"`)
  await option.trigger('click')
}

beforeEach(() => {
  for (const fn of [session, state, test, complete]) fn.mockReset()
})

describe('setup wizard: the setup code', () => {
  it('starts at the code without a setup session, and says where to find it', async () => {
    state.mockRejectedValue(noSession())
    const wrapper = await mountWizard()
    expect(heading(wrapper)).toBe('Setup code')
    expect(wrapper.find('[data-test=setup-notice]').exists()).toBe(false)
    expect(wrapper.text()).toContain('docker compose logs adminapi')
    expect(wrapper.text()).toContain('kubectl logs deploy/adminapi')
    wrapper.unmount()
  })

  it('sends the code (trimmed only), keeps it nowhere, and goes on to the provider with the redirect URIs', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem')
    state.mockRejectedValueOnce(noSession()).mockResolvedValue(setupState())
    session.mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
    const wrapper = await mountWizard()
    await enterCode(wrapper, '  abcde-FGHIJ-klmno ')
    expect(session).toHaveBeenCalledWith('abcde-FGHIJ-klmno')
    expect(heading(wrapper)).toBe('Identity provider')
    expect(wrapper.get('[data-test=redirect-sign-in]').text()).toContain('https://admin.example.org/signin-oidc')
    expect(wrapper.get('[data-test=redirect-sign-out]').text()).toContain('https://admin.example.org/signout-callback-oidc')
    expect(wrapper.get('[data-test=setup-ends]').text()).toContain('(in 50 min)')
    expect(setItem).not.toHaveBeenCalled()
    expect(window.location.href).not.toContain('abcde')
    wrapper.unmount()
  })

  it('says a wrong code is wrong and stays', async () => {
    state.mockRejectedValue(noSession())
    session.mockRejectedValue(new ApiError(400, 'POST', { code: 'setup_code_invalid' }))
    const wrapper = await mountWizard()
    await enterCode(wrapper, 'wrong')
    expect(wrapper.text()).toContain('That is not the setup code.')
    expect(input(wrapper, 'setup-code').getAttribute('aria-invalid')).toBe('true')
    expect(heading(wrapper)).toBe('Setup code')
    wrapper.unmount()
  })

  it('asks for a code before sending', async () => {
    state.mockRejectedValue(noSession())
    const wrapper = await mountWizard()
    await enterCode(wrapper, '  ')
    expect(wrapper.text()).toContain('Enter the setup code.')
    expect(session).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('says setup is closed when the code answers 404', async () => {
    state.mockRejectedValue(noSession())
    session.mockRejectedValue(new ApiError(404, 'POST', null))
    const wrapper = await mountWizard()
    await enterCode(wrapper, 'abc')
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain('Setup is closed')
    wrapper.unmount()
  })

  describe('with the attempt limit', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('counts down Retry-After with the button disabled', async () => {
      state.mockRejectedValue(noSession())
      session.mockRejectedValue(new ApiError(429, 'POST', { code: 'setup_attempts_exhausted' }, undefined, 3))
      const wrapper = await mountWizard()
      await enterCode(wrapper, 'abc')
      expect(wrapper.get('[data-test=code-wait]').text()).toBe('Too many attempts. Try again in 3 seconds.')
      expect(wrapper.get('[data-test=code-submit]').attributes('aria-disabled')).toBe('true')
      vi.advanceTimersByTime(1000)
      await flushPromises()
      expect(wrapper.get('[data-test=code-wait]').text()).toBe('Too many attempts. Try again in 2 seconds.')
      vi.advanceTimersByTime(2000)
      await flushPromises()
      expect(wrapper.find('[data-test=code-wait]').exists()).toBe(false)
      expect(wrapper.get('[data-test=code-submit]').attributes('aria-disabled')).toBeUndefined()
      wrapper.unmount()
    })
  })
})

describe('setup wizard: closed setup', () => {
  it('says setup is closed when the API answers 404', async () => {
    state.mockRejectedValue(new ApiError(404, 'GET', null))
    const wrapper = await mountWizard()
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain('Setup is closed')
    expect(wrapper.get('[data-test=to-sign-in]').attributes('href')).toBe('/')
    expect(wrapper.find('[data-test=stepper]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('setup wizard: the identity provider', () => {
  it('names the groups that make a gateway admin', async () => {
    const wrapper = await resume(setupState({ gatewayAdminGroups: ['gw-admins', 'it-ops'] }))
    expect(wrapper.get('[data-test=admin-groups]').text()).toContain('gw-admins, it-ops')
    wrapper.unmount()
  })
})

describe('setup wizard: sign-in values and the test', () => {
  let popup: ReturnType<typeof fakePopup>
  let open: ReturnType<typeof vi.spyOn>
  beforeEach(() => {
    popup = fakePopup()
    open = vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window)
  })

  it('resumes a live session at the sign-in values with the latest test shown', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    expect(heading(wrapper)).toBe('Sign-in values')
    expect(input(wrapper, 'setup-authority').value).toBe(AUTHORITY)
    expect(wrapper.find('[data-test=test-passed]').exists()).toBe(true)
    expect(wrapper.get('[data-test=is-admin]').text()).toContain('Anna Andersson is a gateway admin.')
    wrapper.unmount()
  })

  it('shows a value the deployment sets read-only with its source, and sends null for it', async () => {
    const wrapper = await resume(setupState({ signIn: signInSettings({ authority: { value: AUTHORITY, source: 'deployment', locked: true } }) }))
    await goTo(wrapper, 'signIn')
    expect(input(wrapper, 'setup-authority').disabled).toBe(true)
    expect(input(wrapper, 'setup-authority').value).toBe(AUTHORITY)
    expect(wrapper.get('#setup-authority-hint').text()).toContain('Set by the deployment')
    expect(wrapper.get('#setup-clientId-hint').text()).toContain('Leave empty for the default: ume-admin.')
    test.mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    await startTest(wrapper)
    expect(test).toHaveBeenCalledWith(expect.objectContaining({ authority: null, clientId: null }))
    wrapper.unmount()
  })

  it('maps the secret choices to null, "" and the typed value, and opens the provider in the window it opened on the click', async () => {
    const stored = signInSettings({ clientSecret: { set: true, source: 'wizard', setAt: null, expiresOn: null, unreadable: false, locked: false } })
    const wrapper = await resume(setupState({ signIn: stored }))
    await goTo(wrapper, 'signIn')
    test.mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    await wrapper.get('#setup-authority').setValue(AUTHORITY)

    await startTest(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ authority: AUTHORITY, clientSecret: null }))
    expect(open).toHaveBeenCalledWith('about:blank', 'ume-gateway-setup-test', expect.any(String))
    expect(popup.location.href).toBe(AUTHORIZE)

    await chooseSecret(wrapper, 'No secret (public client)')
    expect(wrapper.find('#setup-clientSecret').exists()).toBe(false)
    await startTest(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ clientSecret: '' }))

    await chooseSecret(wrapper, 'Type a new one')
    const field = input(wrapper, 'setup-clientSecret')
    expect(field.type).toBe('password')
    expect(field.getAttribute('autocomplete')).toBe('new-password')
    await wrapper.get('#setup-clientSecret').setValue('s3cret')
    await startTest(wrapper)
    expect(test).toHaveBeenLastCalledWith(expect.objectContaining({ clientSecret: 's3cret' }))
    wrapper.unmount()
  })

  it('needs a provider address, and a typed secret when typing one is chosen, before it tests', async () => {
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'signIn')
    await startTest(wrapper)
    expect(wrapper.text()).toContain('Enter the provider address.')
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await chooseSecret(wrapper, 'Type a new one')
    await startTest(wrapper)
    expect(wrapper.text()).toContain('Type the client secret, or choose another option.')
    expect(test).not.toHaveBeenCalled()
    expect(open).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows a link when the browser blocks the window', async () => {
    open.mockReturnValue(null)
    state.mockResolvedValue(setupState())
    const wrapper = await mountWizard()
    await goTo(wrapper, 'signIn')
    test.mockResolvedValue({ testId: 't2', authorizeUrl: AUTHORIZE, expiresAt: '' })
    state.mockResolvedValue(setupState({ latestTest: setupTest({ testId: 't2', status: 'pending', result: null }) }))
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await startTest(wrapper)
    expect(wrapper.find('[data-test=test-pending]').exists()).toBe(true)
    expect(wrapper.get('[data-test=popup-blocked] a').attributes('href')).toBe(AUTHORIZE)
    expect(wrapper.get('[data-test=popup-blocked] a').attributes('target')).toBe('ume-gateway-setup-test')
    wrapper.unmount()
  })

  it('says why a test could not start, and closes the window', async () => {
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'signIn')
    test.mockRejectedValue(new ApiError(400, 'POST', { code: 'setup_test_refused', reason: 'not_discovery' }))
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await startTest(wrapper)
    expect(wrapper.get('[data-test=test-error]').text()).toContain(SETUP_TEST_REASON.not_discovery)
    expect(popup.close).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('says why a test failed at the callback', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest({ status: 'failed', reason: 'token_refused', result: null }) }))
    expect(wrapper.get('[data-test=test-failed]').text()).toContain(SETUP_TEST_REASON.token_refused)
    await goTo(wrapper, 'save')
    expect(wrapper.get('[data-test=save]').attributes('aria-disabled')).toBe('true')
    wrapper.unmount()
  })

  it('says prominently when the person is not a gateway admin, what to change, and refuses Save', async () => {
    const base = setupTest()
    const notAdmin = setupTest({ result: { ...base.result!, isGatewayAdmin: false, roles: ['viewer'], groups: ['a', 'b'], groupCount: 140 } })
    const wrapper = await resume(setupState({ latestTest: notAdmin, gatewayAdminGroups: ['gw-admins'] }))
    expect(wrapper.get('[data-test=is-admin]').text()).toContain('is not a gateway admin, so saving is refused')
    expect(wrapper.get('[data-test=groups-capped]').text()).toContain('2 of 140 shown')
    const help = wrapper.get('[data-test=not-admin-help]').text()
    expect(help).toContain('gateway-admin')
    expect(help).toContain('roles')
    expect(help).toContain('gw-admins')
    await goTo(wrapper, 'save')
    expect(wrapper.get('[data-test=save]').attributes('aria-disabled')).toBe('true')
    wrapper.unmount()
  })

  it('marks a passed test stale when a value or the secret changes after it, and then refuses Save', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(false)
    await wrapper.get('#setup-clientId').setValue('another-client')
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(true)
    await wrapper.get('#setup-clientId').setValue('')
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(false)
    await chooseSecret(wrapper, 'No secret (public client)')
    expect(wrapper.find('[data-test=test-stale]').exists()).toBe(true)
    await goTo(wrapper, 'save')
    expect(wrapper.get('[data-test=save]').attributes('aria-disabled')).toBe('true')
    await wrapper.get('[data-test=save]').trigger('click')
    expect(complete).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  describe('while a test waits', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('reads the state every 2 seconds until the result is there', async () => {
      state.mockResolvedValue(setupState({ latestTest: setupTest({ status: 'pending', result: null }) }))
      const wrapper = await mountWizard()
      expect(wrapper.get('[data-test=test-pending]').text()).toContain('Waiting for the sign-in window')
      state.mockResolvedValue(setupState({ latestTest: setupTest() }))
      vi.advanceTimersByTime(2000)
      await flushPromises()
      expect(wrapper.find('[data-test=test-passed]').exists()).toBe(true)
      const calls = state.mock.calls.length
      vi.advanceTimersByTime(4000)
      await flushPromises()
      expect(state.mock.calls.length).toBe(calls)
      wrapper.unmount()
    })
  })
})

describe('setup wizard: save', () => {
  it('saves the tested values with the test id, drops the form and goes to the sign-in', async () => {
    const assign = vi.spyOn(navigation, 'assign').mockImplementation(() => undefined)
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await goTo(wrapper, 'save')
    expect(wrapper.get('[data-test=save-authority]').text()).toContain('Set here')
    expect(wrapper.get('[data-test=save-clientId]').text()).toContain('ume-admin')
    expect(wrapper.get('[data-test=save-clientId]').text()).toContain('Default')
    expect(wrapper.get('[data-test=save-secret]').text()).toContain('The secret typed for the test')
    expect(wrapper.get('[data-test=save-person]').text()).toContain('Anna Andersson')
    complete.mockResolvedValue({ signInUrl: '/bff/login' })
    await wrapper.get('[data-test=save]').trigger('click')
    await flushPromises()
    expect(complete).toHaveBeenCalledWith({ ...setupTest().values, testId: 't1' })
    expect(complete.mock.calls[0]![0]).not.toHaveProperty('clientSecret')
    expect(assign).toHaveBeenCalledWith('/bff/login')
    expect(wrapper.get('[data-test=setup-done]').text()).toContain('Sign-in is set up')
    expect(wrapper.get('[data-test=done-sign-in]').attributes('href')).toBe('/bff/login')
    expect(wrapper.find('input').exists()).toBe(false)
    wrapper.unmount()
  })

  it.each(['unknown_test', 'not_latest', 'not_passed', 'not_gateway_admin', 'values_changed'] as const)('says why saving was refused (%s)', async (reason) => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await goTo(wrapper, 'save')
    complete.mockRejectedValue(new ApiError(409, 'POST', { code: 'setup_test_required', reason }))
    await wrapper.get('[data-test=save]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=save-error]').text()).toContain(SETUP_COMPLETE_REASON[reason])
    wrapper.unmount()
  })

  it('says setup has closed when Save loses the race (404)', async () => {
    const wrapper = await resume(setupState({ latestTest: setupTest() }))
    await goTo(wrapper, 'save')
    complete.mockRejectedValue(new ApiError(404, 'POST', null))
    await wrapper.get('[data-test=save]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain('Setup has closed')
    expect(wrapper.get('[data-test=to-sign-in]').attributes('href')).toBe('/')
    wrapper.unmount()
  })
})

describe('setup wizard: the session ends', () => {
  it('goes back to the code on a 401, keeps the typed values and drops the secret', async () => {
    vi.spyOn(window, 'open').mockReturnValue(fakePopup() as unknown as Window)
    state.mockResolvedValue(setupState())
    session.mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
    const wrapper = await mountWizard()
    await goTo(wrapper, 'signIn')
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    await chooseSecret(wrapper, 'Type a new one')
    await wrapper.get('#setup-clientSecret').setValue('s3cret')
    test.mockRejectedValue(new ApiError(401, 'POST', { code: 'setup_session_required' }))
    await startTest(wrapper)
    expect(heading(wrapper)).toBe('Setup code')
    expect(wrapper.get('[data-test=setup-notice]').text()).toContain(SESSION_ENDED)

    await enterCode(wrapper, 'abc')
    await goTo(wrapper, 'signIn')
    expect(input(wrapper, 'setup-authority').value).toBe(AUTHORITY)
    expect(input(wrapper, 'setup-clientSecret').value).toBe('')
    wrapper.unmount()
  })

  it('says setup has closed when a read answers 404 meanwhile', async () => {
    vi.spyOn(window, 'open').mockReturnValue(fakePopup() as unknown as Window)
    const wrapper = await resume(setupState())
    await goTo(wrapper, 'signIn')
    await wrapper.get('#setup-authority').setValue(AUTHORITY)
    test.mockRejectedValue(new ApiError(404, 'POST', null))
    await startTest(wrapper)
    expect(wrapper.get('[data-test=setup-closed]').text()).toContain('Setup has closed')
    wrapper.unmount()
  })

  describe('in time', () => {
    beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] }))
    afterEach(() => vi.useRealTimers())

    it('goes back to the code when the session expires, and ends when setup closes', async () => {
      const now = Date.now()
      state.mockResolvedValue(setupState({}, now))
      session.mockResolvedValue({ expiresAt: '', setupEndsAt: '' })
      const wrapper = await mountWizard()
      expect(heading(wrapper)).toBe('Identity provider')
      vi.advanceTimersByTime(25 * 60_000)
      await flushPromises()
      expect(heading(wrapper)).toBe('Setup code')
      expect(wrapper.get('[data-test=setup-notice]').text()).toContain(SESSION_ENDED)

      // A new session that outlasts the opening (which still closes 50 minutes after the start).
      state.mockResolvedValue(setupState({ sessionExpiresAt: new Date(now + 80 * 60_000).toISOString() }, now))
      await enterCode(wrapper, 'abc')
      expect(heading(wrapper)).toBe('Identity provider')
      vi.advanceTimersByTime(25 * 60_000)
      await flushPromises()
      expect(wrapper.get('[data-test=setup-closed]').text()).toContain('Setup mode’s hour has ended')
      expect(wrapper.text()).toContain('UME_SETUP_REOPEN')
      wrapper.unmount()
    })
  })
})
