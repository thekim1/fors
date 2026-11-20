import AxeBuilder from '@axe-core/playwright'
import axe from 'axe-core'
import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { appHostSecret, keycloakAuthority, setupCode, typeSecret, undersizedControls } from '../helpers'

// The setup wizard (docs/admin-ui.md, "Setup wizard"; docs/identity-and-roles.md, "Setup mode") end to end against a stack in
// setup mode: a fresh database and `Setup__Try=true` (docs/local-development.md, "Trying setup mode"), so the admin API starts
// without a sign-in and prints a setup code. Saving closes setup, so the tests run in order in one browser context and a second
// run needs a fresh stack; on any other stack the whole file is skipped. Run it alone: `npx playwright test specs/setup.spec.ts`.
//
// Secrets and the setup code are read in this process only, typed with typeSecret (no locator fill in a call log) and never put
// in an assertion message. Traces, screenshots and videos are off (playwright.config.ts); the line below keeps the page's aria
// snapshot, which could show a typed value, out of a failed test's error-context.md.
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'

// As accessibility.spec.ts: the WCAG 2.2 A and AA axe rules and the target size (SC 2.5.8), on every step of the wizard.
const tags = [...new Set(axe.getRules().flatMap(rule => rule.tags))].filter(tag => /^wcag(2|21|22)(a|aa)$/.test(tag))

async function scan(page: Page, label: string): Promise<void> {
  const result = await new AxeBuilder({ page }).withTags(tags).analyze()
  const failures = result.violations.map(v => ({ id: v.id, targets: v.nodes.map(node => node.target) }))
  expect.soft(failures, `axe: ${label}`).toEqual([])
  expect.soft(await undersizedControls(page), `target size (WCAG 2.5.8): ${label}`).toEqual([])
}

const password = () => appHostSecret('dev-user-password', 'E2E_PASSWORD')
const clientSecret = () => appHostSecret('oidc-client-secret', 'E2E_OIDC_CLIENT_SECRET')

test.describe.serial('setup wizard', () => {
  let context: BrowserContext
  let page: Page
  let authority: string
  const hook = (name: string) => page.locator(`[data-test="${name}"]`)
  const heading = () => hook('step-heading')

  /**
   * Starts the test sign-in and signs in as `user` in its window at Keycloak; the window closes itself. Keycloak's session cookies
   * are cleared first, so its form always asks (the setup session's cookie, on /bff/setup, stays).
   */
  async function testSignIn(user: string): Promise<void> {
    await context.clearCookies({ name: /^(KEYCLOAK_|KC_|AUTH_SESSION_ID)/ })
    const opened = page.waitForEvent('popup')
    await page.getByRole('button', { name: 'Test sign-in' }).click()
    const popup = await opened
    await popup.waitForURL(/\/realms\/fors\//)
    await popup.locator('#username').fill(user)
    await typeSecret(popup.locator('#password'), password())
    const closed = popup.waitForEvent('close', { timeout: 60_000 })
    await popup.locator('#kc-login').click()
    await closed
    await expect(hook('test-pending')).toHaveCount(0, { timeout: 30_000 })
  }

  /** Types the client secret as a new one (the field appears with that choice). */
  async function typeClientSecret(value: string): Promise<void> {
    await hook('secret-choice').getByText('Type a new one').click()
    await typeSecret(page.getByLabel('New client secret'), value)
  }

  test.beforeAll(async ({ browser }) => {
    context = await browser.newContext()
    page = await context.newPage()
    const user = await (await page.request.get('/bff/user')).json()
    test.skip(user.setup?.open !== true, 'Setup is not open on this stack (/bff/user setup.open is false): start a fresh one with Setup__Try=true.')
    authority = keycloakAuthority()
  })

  test.afterAll(async () => { await context?.close() })

  test('before a session: the sign-in page offers setup only, and a wrong code is refused with the focus back on the field', async () => {
    await page.goto('/')
    await expect(hook('setup-open')).toContainText('Sign-in is not set up yet')
    await expect(hook('sign-in')).toHaveCount(0)
    await scan(page, 'the sign-in page while setup is open')
    await hook('set-up').click()
    await page.waitForURL(url => url.pathname === '/setup')
    await expect(heading()).toHaveText('Setup code')
    await scan(page, 'step 1: setup code')

    // Well formed (26 Crockford base32 characters), but not the code. The throttle's 429 is covered by the unit tests.
    const code = page.getByRole('textbox', { name: 'Setup code' })
    await code.fill('00000-00000-00000-00000-000000')
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(hook('code-step')).toContainText('That is not the setup code')
    await expect(code).toBeFocused()
  })

  test('the code in lower case without dashes opens the provider step with the redirect URIs, and a reload resumes past the code', async () => {
    let code: string | null = null
    await expect.poll(() => (code = setupCode()) !== null, { message: 'a setup code in the admin API’s output', timeout: 60_000 }).toBe(true)
    await typeSecret(page.getByRole('textbox', { name: 'Setup code' }), code!.toLowerCase().replaceAll('-', ''))
    await page.getByRole('button', { name: 'Continue' }).click()
    await expect(heading()).toHaveText('Identity provider')
    await expect(heading()).toBeFocused()
    await expect(hook('redirect-sign-in')).toContainText(/https:\/\/\S+\/signin-oidc/)
    await expect(hook('redirect-sign-out')).toContainText(/https:\/\/\S+\/signout-callback-oidc/)
    await expect(hook('setup-ends')).toContainText('Setup closes at')
    await scan(page, 'step 2: identity provider')

    await page.reload()
    await expect(heading()).toHaveText('Identity provider')
    await hook('next').click()
    await expect(heading()).toHaveText('Sign-in values')
  })

  test('a wrong client secret fails the test with its reason, and Save stays disabled', async () => {
    await page.getByRole('textbox', { name: 'Provider address (authority)' }).fill(authority)
    await typeClientSecret('not-the-client-secret')
    await testSignIn('gateway-admin')
    await expect(hook('test-failed')).toBeVisible()
    await scan(page, 'step 3: a failed test')
    await hook('next').click()
    await expect(heading()).toHaveText('Save')
    await expect(hook('save')).toHaveAttribute('aria-disabled', 'true')
    await hook('back').click()
  })

  test('a person who is not a gateway admin passes the test, but Save stays disabled', async () => {
    await typeClientSecret(clientSecret())
    await testSignIn('viewer')
    await expect(hook('test-passed')).toBeVisible()
    await expect(hook('is-admin')).toContainText('is not a gateway admin')
    await expect(hook('not-admin-help')).toContainText('gateway-admin')
    await scan(page, 'step 3: a passed test by someone who is not a gateway admin')
    await hook('next').click()
    await expect(hook('save')).toHaveAttribute('aria-disabled', 'true')
    await hook('back').click()
  })

  test('a gateway admin passes; a changed value makes the test stale until it is changed back; Save closes setup and the sign-in works', async () => {
    await testSignIn('gateway-admin')
    await expect(hook('test-passed')).toBeVisible()
    await expect(hook('is-admin')).toContainText('is a gateway admin')
    await expect(hook('test-stale')).toHaveCount(0)

    const provider = page.getByRole('textbox', { name: 'Provider address (authority)' })
    await provider.fill(`${authority}x`)
    await expect(hook('test-stale')).toBeVisible()
    await provider.fill(authority)
    await expect(hook('test-stale')).toHaveCount(0)

    await hook('next').click()
    await expect(heading()).toHaveText('Save')
    await expect(hook('save-authority')).toContainText(authority)
    await expect(hook('save-person')).toContainText('Gateway Administrator')
    await expect(hook('save')).not.toHaveAttribute('aria-disabled', 'true')
    await scan(page, 'step 4: save')
    await hook('save').click()

    // Saving sends the browser to /bff/login; Keycloak still has the tested person's session, so it comes back signed in.
    await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible({ timeout: 60_000 })
    const user = await (await page.request.get('/bff/user')).json()
    expect(user.roles).toContain('gateway-admin')
    expect(user.signIn.configured).toBe(true)
    expect(user.setup.open).toBe(false)

    // Setup is closed for everyone now.
    expect((await page.request.get('/bff/setup/state')).status()).toBe(404)
    await page.goto('/settings')
    await expect(page.locator('#sign-in-h')).toBeVisible()
  })
})
