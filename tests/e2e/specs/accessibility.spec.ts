import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, test, type Page } from '@playwright/test'
import { chooseMenuItem, login, mutate, openRow, openUserMenu, pages, ready, remove, testKey, testTeam, themeStorageKey, themes, undersizedControls, userMenuButton, watchCsp } from '../helpers'

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
    const csp = watchCsp(page)
    await login(page)
    await useTheme(page, theme)
    for (const path of pages) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await scan(page, `${theme}: ${path}`)
    }
    expect(csp, 'Content-Security-Policy violations').toEqual([])
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
      await openUserMenu(page)
      await scan(page, `${theme}: user menu`)
      await open('your access dialog', () => page.getByRole('menuitem', { name: 'Your access' }).click(), 'Your access')
      await expect(userMenuButton(page)).toBeFocused()
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
  // Layout rules 10 and 11: every page at phone width, in each theme. Signing in needs the side menu, so the window
  // narrows after it.
  test(`every routed page passes the WCAG A/AA axe rules at 390 px in ${theme}`, async ({ page }) => {
    await login(page)
    await useTheme(page, theme)
    const csp = watchCsp(page)
    await page.setViewportSize({ width: 390, height: 844 })
    for (const path of pages) {
      await ready(page, path)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      await scan(page, `${theme}, 390 px: ${path}`)
    }
    expect(csp, 'Content-Security-Policy violations').toEqual([])
  })

  // The shared components in their open states: the ⋯ menu and its phone sheet, filter chips with a custom period, and
  // the save bar.
  test(`menus, filter chips and the save bar pass axe while open in ${theme}`, async ({ page }) => {
    await login(page)
    await useTheme(page, theme)

    await ready(page, '/organisation')
    await page.getByRole('main').getByRole('button', { name: /^More actions for / }).first().click()
    await expect(page.getByRole('menu')).toBeVisible()
    await scan(page, `${theme}: ⋯ menu`)
    await page.keyboard.press('Escape')

    // A drawer's ⋯ menu opens inside the drawer (a modal dialog), so axe sees it as part of the dialog.
    await ready(page, '/keys')
    await page.getByRole('table').getByRole('row').nth(1).getByRole('button').first().click()
    const drawer = page.getByRole('dialog').last()
    await drawer.getByRole('button', { name: /^More actions for / }).click()
    await expect(drawer.getByRole('menu')).toBeVisible()
    await scan(page, `${theme}: a drawer's ⋯ menu`)
    await page.keyboard.press('Escape')
    await page.keyboard.press('Escape')
    await expect(drawer).toBeHidden()

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
    await ready(page, '/keys')
    await page.getByRole('list').or(page.getByRole('table')).first().getByRole('button').first().click()
    const phoneDrawer = page.getByRole('dialog').last()
    await phoneDrawer.getByRole('button', { name: /^More actions for / }).click()
    const drawerSheet = page.getByRole('dialog', { name: /^More actions for / })
    await expect(drawerSheet).toBeVisible()
    await scan(page, `${theme}, 390 px: a drawer's ⋯ menu as a sheet`)
    await drawerSheet.getByRole('button', { name: 'Close' }).last().click()
    await page.keyboard.press('Escape')
    await ready(page, '/usage?tab=requests&outcome=errors')
    await scan(page, `${theme}, 390 px: filter chips`)
  })
}

// One test per width: every page waits for its content, and twenty pages twice do not fit one test's time.
for (const [width, label] of [[320, 'at 320px'], [640, 'at 200 percent text size']] as const) {
  test(`all pages reflow ${label}`, async ({ page }) => {
    await login(page)
    await page.setViewportSize({ width, height: 900 })
    for (const path of pages) {
      await ready(page, path)
      if (width === 640) await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1)
      expect.soft(overflow, `${width}px: ${path}`).toBe(false)
    }
  })
}

test('keyboard: landmarks on desktop, skip link on small screens, page heading focus, the user menu', async ({ page }) => {
  await login(page)
  await ready(page, '/')
  await expect(page.getByRole('main')).toHaveCount(1)
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()

  // Route change moves focus to the page heading.
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Keys' }).click()
  await expect(page.locator('#page-title')).toBeFocused()

  // The user menu (thekim1/fors#127): an APG menu button. Enter opens it on the first item, the arrows move,
  // Enter chooses a theme and focus returns to the card; Esc closes it and returns focus too.
  const card = userMenuButton(page)
  await expect(card).toHaveAttribute('aria-haspopup', 'menu')
  await card.focus()
  await page.keyboard.press('Enter')
  const menu = page.getByRole('menu', { name: 'Your account' })
  await expect(menu).toBeVisible()
  await expect(card).toHaveAttribute('aria-expanded', 'true')
  await expect(menu.getByRole('menuitemradio', { name: 'Light' })).toBeFocused()
  const before = await page.locator('html').getAttribute('data-theme')
  const next = before === 'dark' ? 'Lumen' : 'Dark'
  await page.keyboard.press('ArrowDown')
  if (next === 'Lumen') await page.keyboard.press('ArrowDown')
  await expect(menu.getByRole('menuitemradio', { name: next })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(menu).toBeHidden()
  await expect(page.locator('html')).toHaveAttribute('data-theme', next.toLowerCase())
  await expect(card).toBeFocused()
  await page.keyboard.press('ArrowDown')
  await expect(menu.getByRole('menuitemradio', { name: next })).toHaveAttribute('aria-checked', 'true')
  await page.keyboard.press('End')
  await expect(menu.getByRole('menuitem', { name: 'Sign out' })).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(menu).toBeHidden()
  await expect(card).toBeFocused()

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
