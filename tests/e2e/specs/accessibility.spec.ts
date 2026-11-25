import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, test, type Page } from '@playwright/test'
import { chooseMenuItem, login, mutate, openRow, pages, ready, remove, testKey, testTeam, themeStorageKey, themes, undersizedControls, userMenu } from '../helpers'

// The design target is WCAG 2.2 AA. AAA rules (7:1 contrast and so on) are not part of the gate: the old
// high-contrast themes that met them are gone (the target is WCAG 2.2 AA, see docs/accessibility-statement.md).
const tags = [...new Set(axe.getRules().flatMap(rule => rule.tags))]
  .filter(tag => /^wcag(2|21|22)(a|aa)$/.test(tag))

test.afterEach(async ({ page }) => { await page.close() })

async function scan(page: Page, label: string): Promise<void> {
  const result = await new AxeBuilder({ page }).withTags(tags).analyze()
  const failures = result.violations.map(v => ({ id: v.id, targets: v.nodes.map(node => node.target) }))
  expect.soft(failures, `axe: ${label}`).toEqual([])
  // WCAG 2.2 AA target size (SC 2.5.8) is the design target, not 44 px (AAA).
  expect.soft(await undersizedControls(page), `target size (WCAG 2.5.8): ${label}`).toEqual([])
}

async function useTheme(page: Page, theme: string): Promise<void> {
  await page.evaluate(([key, value]) => localStorage.setItem(key, value), [themeStorageKey, theme])
}

for (const theme of themes) {
  test(`every routed page passes the WCAG A/AA axe rules in ${theme}`, async ({ page }) => {
    await login(page)
    await useTheme(page, theme)
    for (const path of pages) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await scan(page, `${theme}: ${path}`)
    }
  })

  test(`drawers and dialogs pass axe in ${theme}`, async ({ page }) => {
    await login(page)
    await useTheme(page, theme)
    const key = await testKey(page)
    const record = await (await page.request.get(`/api/keys/${key.id}`)).json()
    const { teamId, teamName, departmentName } = await testTeam(page)
    const ruleName = `Browser a11y rule ${crypto.randomUUID().slice(0, 8)}`
    const rule = await (await mutate(page, '/api/routing-rules', {
      name: ruleName, scope: 'Team', scopeIds: [teamId], condition: 'headers["x-e2e"] == "a11y"', targets: [{ model: 'fors/chat-onprem', weight: 1 }],
    })).json()
    const open = async (label: string, trigger: () => Promise<void>, dialogName: string | RegExp) => {
      await trigger()
      const dialog = page.getByRole('dialog', { name: dialogName })
      await expect(dialog).toBeVisible()
      await expect(page.locator('main .animate-spin, dialog .animate-spin')).toHaveCount(0)
      await scan(page, `${theme}: ${label}`)
      await page.keyboard.press('Escape')
      await expect(dialog).toBeHidden()
    }
    try {
      await ready(page, '/keys')
      await open('new key drawer', () => page.getByRole('button', { name: 'New key' }).click(), 'New key')
      await page.getByRole('searchbox', { name: 'Search keys' }).fill(record.name)
      await open('key detail drawer', () => openRow(page, record.name), record.name)

      await ready(page, '/organisation')
      await open('new department drawer', () => page.getByRole('button', { name: 'New department' }).click(), 'New department')
      await open('add team drawer', () => page.getByRole('button', { name: 'Add team' }).click(), 'Add team')

      await ready(page, '/routing-rules')
      await open('new rule drawer', () => page.getByRole('button', { name: 'New rule' }).click(), 'New rule')
      await open('test a request dialog', () => page.getByRole('button', { name: 'Test a request' }).click(), 'Test a request')
      await page.getByRole('navigation', { name: 'Routing rules' }).getByRole('button', { name: ruleName }).click()
      await open('change owner dialog', () => chooseMenuItem(page, 'Change owners'), 'Change owners')
      await open('delete rule dialog', () => chooseMenuItem(page, 'Delete…'), 'Delete rule?')

      await ready(page, '/organisation')
      await page.getByRole('navigation', { name: 'Departments' }).getByRole('button', { name: new RegExp(departmentName) }).click()
      const row = page.getByRole('row').filter({ hasText: teamName })
      await open('delete team dialog', () => chooseMenuItem(page, 'Delete team…', row), 'Delete team?')
      await chooseMenuItem(page, 'Delete team…', row)
      await page.getByRole('dialog', { name: 'Delete team?' }).getByRole('button', { name: 'Delete team' }).click()
      const scoped = page.getByRole('dialog', { name: /belongs? to this team/ })
      await expect(scoped).toBeVisible()
      await scan(page, `${theme}: routing rules belong to team dialog`)
      await scoped.getByRole('button', { name: 'Cancel' }).click()
    } finally {
      await remove(page, `/api/routing-rules/${rule.id}`)
      await mutate(page, `/api/keys/${key.id}/revoke`)
    }
  })
}

for (const theme of themes) {
  // Layout rules 10 and 11: every page at phone width, in each theme.
  test(`every routed page passes the WCAG A/AA axe rules at 390 px in ${theme}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await login(page)
    await useTheme(page, theme)
    for (const path of pages) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await scan(page, `${theme}, 390 px: ${path}`)
    }
  })

  // The shared components in their open states: the ⋯ menu and its phone sheet, the user menu and Your access, filter
  // chips with a custom period, and the save bar.
  test(`menus, filter chips and the save bar pass axe while open in ${theme}`, async ({ page }) => {
    await login(page)
    await useTheme(page, theme)

    await ready(page, '/')
    await userMenu(page).click()
    await expect(page.getByRole('menu')).toBeVisible()
    await scan(page, `${theme}: user menu`)
    await page.getByRole('menuitem', { name: 'Your access' }).click()
    await expect(page.getByRole('dialog', { name: 'Your access' })).toBeVisible()
    await scan(page, `${theme}: your access`)
    await page.keyboard.press('Escape')

    await ready(page, '/organisation')
    await page.getByRole('main').getByRole('button', { name: /^More actions for / }).first().click()
    await expect(page.getByRole('menu')).toBeVisible()
    await scan(page, `${theme}: ⋯ menu`)
    await page.keyboard.press('Escape')

    await ready(page, '/pii-incidents?period=custom&category=Email')
    await expect(page.getByLabel('From')).toBeVisible()
    await scan(page, `${theme}: filter chips with a custom period`)

    await ready(page, '/settings')
    const name = page.getByLabel('Organisation name')
    await name.fill(`${await name.inputValue()} (a11y)`)
    const bar = page.getByRole('region', { name: 'Unsaved changes' })
    await expect(bar).toBeVisible()
    await scan(page, `${theme}: save bar`)
    await bar.getByRole('button', { name: 'Discard' }).click()
    await page.getByRole('dialog', { name: /^Discard / }).getByRole('button', { name: 'Discard' }).click()
    await expect(bar).toBeHidden()

    await page.setViewportSize({ width: 390, height: 844 })
    await ready(page, '/organisation')
    await page.getByRole('main').getByRole('button', { name: /^More actions for / }).first().click()
    const sheet = page.getByRole('dialog', { name: /^More actions for / })
    await expect(sheet).toBeVisible()
    await scan(page, `${theme}, 390 px: ⋯ menu sheet`)
    await sheet.getByRole('button', { name: 'Close' }).last().click()
    await ready(page, '/usage?tab=requests&outcome=errors')
    await scan(page, `${theme}, 390 px: filter chips`)
  })
}

test('all pages reflow at 320px and at 200 percent text size', async ({ page }) => {
  await login(page)
  for (const width of [320, 640]) {
    await page.setViewportSize({ width, height: 900 })
    for (const path of pages) {
      await ready(page, path)
      if (width === 640) await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
      expect(overflow, `${width}px: ${path}`).toBe(false)
    }
  }
})

test('keyboard: landmarks on desktop, skip link on small screens, page heading focus, themes by arrow keys', async ({ page }) => {
  await login(page)
  await ready(page, '/')
  await expect(page.getByRole('main')).toHaveCount(1)
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()

  // Route change moves focus to the page heading.
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Keys' }).click()
  await expect(page.locator('#page-title')).toBeFocused()

  // The user menu (an APG menu button) changes the theme by keyboard and returns focus to its button.
  const account = userMenu(page)
  await account.focus()
  await page.keyboard.press('ArrowDown')
  await expect(page.getByRole('menu')).toBeVisible()
  await expect(page.getByRole('menuitemradio').first()).toBeFocused()
  const before = await page.locator('html').getAttribute('data-theme')
  const next = page.getByRole('menuitemradio', { checked: false }).first()
  const nextName = await next.textContent()
  while (!(await next.evaluate(el => el === document.activeElement))) await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Enter')
  await expect(page.locator('html')).not.toHaveAttribute('data-theme', before!)
  await expect(account).toBeFocused()
  await page.keyboard.press('ArrowDown')
  await expect(page.getByRole('menuitemradio', { name: nextName!, checked: true })).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(account).toBeFocused()

  // The skip link is part of the mobile layout.
  await page.setViewportSize({ width: 390, height: 800 })
  await ready(page, '/')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Skip to main content' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.locator('#main')).toBeFocused()
})

test('forced colours and reduced motion keep a visible focus indicator', async ({ page }) => {
  await login(page)
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' })
  await ready(page, '/keys')
  const focusStyle = await page.getByRole('searchbox', { name: 'Search keys' }).evaluate(element => {
    (element as HTMLElement).focus()
    return getComputedStyle(element).outlineStyle
  })
  expect(focusStyle).not.toBe('none')
  expect(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches)).toBe(true)
  expect(await page.evaluate(() => matchMedia('(forced-colors: active)').matches)).toBe(true)
})
