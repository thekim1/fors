import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { routes } from '@/router'
import { api } from '@/api'
import type { BffUser } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { THEME_STORAGE_KEY, useUiStore } from '@/stores/ui'
import { openMenu } from '@/test/helpers'
import AppShell from './AppShell.vue'
import UserMenu from './UserMenu.vue'

function phone(on: boolean): void {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: on && query.includes('max-width'), addEventListener: () => undefined, removeEventListener: () => undefined }))
}

function signIn(user: Partial<BffUser> = {}): void {
  useAuthStore().user = {
    isAuthenticated: true,
    name: 'Anna Andersson',
    email: 'anna@example.org',
    roles: ['department-admin', 'team-admin'],
    departmentCodes: ['1100', '2200'],
    departmentSources: [{ code: '1100', source: 'claim' }, { code: '2200', source: 'group' }],
    teamCodes: ['AI'],
    sessionExpiresAt: null,
    ...user,
  }
  useAuthStore().status = 'authenticated'
}

const card = (wrapper: ReturnType<typeof mount>) => wrapper.get('[data-test=user-menu] [data-test=menu-button]')
const text = () => document.activeElement?.textContent?.trim()

beforeEach(() => {
  setActivePinia(createPinia())
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
  localStorage.clear()
})
afterEach(() => {
  vi.unstubAllGlobals()
  document.body.innerHTML = ''
})

describe('UserMenu (thekim1/fors#127)', () => {
  it('is the user card as a menu button: named by the person, their role and what it opens', () => {
    phone(false)
    signIn()
    const wrapper = mount(UserMenu, { attachTo: document.body })
    const button = card(wrapper)
    expect(button.element.tagName).toBe('BUTTON')
    expect(button.attributes('aria-haspopup')).toBe('menu')
    expect(button.attributes('aria-expanded')).toBe('false')
    expect(button.text()).toContain('Anna Andersson')
    expect(button.text()).toContain('Department admin, account menu')
    wrapper.unmount()
  })

  it('opens from the card with the three themes, Your access and Sign out', async () => {
    phone(false)
    signIn()
    useUiStore().setTheme('dark')
    const wrapper = mount(UserMenu, { attachTo: document.body })
    await card(wrapper).trigger('click')
    await flushPromises()
    const menu = openMenu()
    expect(menu.attributes('aria-label')).toBe('Your account')
    const themes = menu.get('[role=group]')
    expect(document.getElementById(themes.attributes('aria-labelledby')!)?.textContent).toBe('Theme')
    expect(themes.findAll('[role=menuitemradio]').map((i) => [i.attributes('data-test'), i.attributes('aria-checked')])).toEqual([
      ['menu-light', 'false'],
      ['menu-dark', 'true'],
      ['menu-lumen', 'false'],
    ])
    expect(menu.findAll('[role=menuitem]').map((i) => i.text())).toEqual(['badgeYour access', 'logoutSign out'])
    expect(text()).toBe('Light')
    wrapper.unmount()
  })

  it('switches the theme from the menu, kept under the same storage key', async () => {
    phone(false)
    signIn()
    useUiStore().setTheme('light')
    const wrapper = mount(UserMenu, { attachTo: document.body })
    for (const theme of ['lumen', 'dark', 'light'] as const) {
      await card(wrapper).trigger('click')
      await flushPromises()
      await openMenu().get(`[data-test=menu-${theme}]`).trigger('click')
      await flushPromises()
      expect(useUiStore().theme).toBe(theme)
      expect(document.documentElement.dataset.theme).toBe(theme)
      expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe(theme)
      expect(document.activeElement).toBe(card(wrapper).element)
    }
    wrapper.unmount()
  })

  it('moves with the keyboard and Esc returns focus to the card', async () => {
    phone(false)
    signIn()
    const wrapper = mount(UserMenu, { attachTo: document.body })
    await card(wrapper).trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(text()).toBe('logoutSign out')
    await openMenu().trigger('keydown', { key: 'ArrowUp' })
    expect(text()).toBe('badgeYour access')
    await openMenu().trigger('keydown', { key: 'Home' })
    expect(text()).toMatch(/^Light/)
    await openMenu().trigger('keydown', { key: 'Escape' })
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    expect(document.activeElement).toBe(card(wrapper).element)
    wrapper.unmount()
  })

  it('shows Your access: the roles, the departments with where each came from, and the teams; Close returns focus to the card', async () => {
    phone(false)
    signIn()
    const wrapper = mount(UserMenu, { attachTo: document.body })
    await card(wrapper).trigger('click')
    await flushPromises()
    await openMenu().get('[data-test=menu-access]').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('dialog')!
    expect(dialog.querySelector('h2')?.textContent).toBe('Your access')
    expect(dialog.querySelector('[data-test=access-roles]')?.textContent).toContain('Department admin')
    expect(dialog.querySelector('[data-test=access-roles]')?.textContent).toContain('Team admin')
    const departments = [...dialog.querySelectorAll('[data-test=access-departments] li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim())
    expect(departments).toEqual(['1100 from the department claim', '2200 from a member group of the department'])
    expect(dialog.querySelector('[data-test=access-teams]')?.textContent).toContain('AI')
    expect(dialog.querySelector('[data-test=access-source]')?.textContent).toContain('Fors does not record which')
    ;[...dialog.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Close')!.click()
    await flushPromises()
    expect(document.body.querySelector('dialog')).toBeNull()
    expect(document.activeElement).toBe(card(wrapper).element)
    wrapper.unmount()
  })

  it('says when there is no role, and that a gateway admin sees every department', async () => {
    phone(false)
    signIn({ roles: [], departmentCodes: [], departmentSources: [], teamCodes: [] })
    const wrapper = mount(UserMenu, { attachTo: document.body })
    expect(card(wrapper).text()).toContain('No role')
    await card(wrapper).trigger('click')
    await flushPromises()
    await openMenu().get('[data-test=menu-access]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=access-roles]')?.textContent).toBe('None: you see the catalogue only.')
    wrapper.unmount()

    signIn({ roles: ['gateway-admin'], departmentCodes: [] })
    const admin = mount(UserMenu, { attachTo: document.body })
    await card(admin).trigger('click')
    await flushPromises()
    await openMenu().get('[data-test=menu-access]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('dialog')?.textContent).toContain('As a gateway admin you see every department.')
    admin.unmount()
  })

  it('emits sign-out, and keeps Sign out focusable but inactive while signing out', async () => {
    phone(false)
    signIn()
    const wrapper = mount(UserMenu, { attachTo: document.body })
    await card(wrapper).trigger('click')
    await flushPromises()
    await openMenu().get('[data-test=menu-sign-out]').trigger('click')
    expect(wrapper.emitted('sign-out')).toHaveLength(1)

    await wrapper.setProps({ signingOut: true })
    await card(wrapper).trigger('click')
    await flushPromises()
    expect(openMenu().get('[data-test=menu-sign-out]').attributes('aria-disabled')).toBe('true')
    await openMenu().get('[data-test=menu-sign-out]').trigger('click')
    expect(wrapper.emitted('sign-out')).toHaveLength(1)
    wrapper.unmount()
  })

  it('is a bottom sheet on a phone, with the themes as pressed buttons', async () => {
    phone(true)
    signIn()
    useUiStore().setTheme('lumen')
    const wrapper = mount(UserMenu, { props: { place: 'sheet' }, attachTo: document.body })
    await card(wrapper).trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    const sheet = document.body.querySelector('dialog')!
    expect(sheet.querySelector('h2')?.textContent).toBe('Your account')
    expect([...sheet.querySelectorAll('[role=group] button')].map((b) => b.getAttribute('aria-pressed'))).toEqual(['false', 'false', 'true'])
    ;(sheet.querySelector('[data-test=menu-dark]') as HTMLButtonElement).click()
    await flushPromises()
    expect(useUiStore().theme).toBe('dark')
    expect(sheet.querySelector('[data-test=menu-close]')).not.toBeNull()
    wrapper.unmount()
  })
})

describe('AppShell user menu', () => {
  async function shell() {
    const router = createRouter({ history: createMemoryHistory(), routes })
    await router.push('/catalog')
    return mount(AppShell, { global: { plugins: [router] }, attachTo: document.body })
  }

  it('has no theme switch or sign-out button of its own beside the card any more', async () => {
    phone(false)
    signIn({ roles: ['viewer'] })
    const wrapper = await shell()
    const aside = wrapper.get('aside')
    expect(aside.find('[role=radiogroup]').exists()).toBe(false)
    expect(aside.findAll('button').map((b) => b.attributes('aria-haspopup'))).toEqual(['menu'])
    wrapper.unmount()
  })

  it('signs out from the user menu', async () => {
    phone(false)
    signIn({ roles: ['viewer'] })
    const logout = vi.spyOn(api.bff, 'logout').mockResolvedValue('https://idp.example.org/logout')
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    const wrapper = await shell()
    await wrapper.get('aside [data-test=menu-button]').trigger('click')
    await flushPromises()
    await openMenu().get('[data-test=menu-sign-out]').trigger('click')
    await flushPromises()
    expect(logout).toHaveBeenCalledOnce()
    expect(assign).toHaveBeenCalledWith('https://idp.example.org/logout')
    wrapper.unmount()
  })
})
