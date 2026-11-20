import { describe, expect, it } from 'vitest'
import type { SignInSecretStatus } from '@/api/types'
import { setupTest, signInSettings } from '@/test/fixtures'
import { canSave, completeRequestOf, isStale, newForm, secretChoices, secretOf, testRequestOf, valuesOf } from './setupValues'

const AUTHORITY = 'https://idp.example.org/realms/fors'
const stored: SignInSecretStatus = { set: true, source: 'wizard', setAt: '2026-11-01T08:00:00Z', expiresOn: '2027-11-01', unreadable: false, locked: false }
const deployed: SignInSecretStatus = { set: true, source: 'deployment', setAt: null, expiresOn: null, unreadable: false, locked: true }

describe('the setup form', () => {
  it('shows a deployment value, fills in a value set in setup and leaves a default empty', () => {
    const form = newForm(signInSettings({
      authority: { value: AUTHORITY, source: 'deployment', locked: true },
      clientId: { value: 'gateway-admin-ui', source: 'wizard', locked: false },
      extraScopes: { value: ['groups'], source: 'wizard', locked: false },
    }))
    expect(form.authority).toBe(AUTHORITY)
    expect(form.clientId).toBe('gateway-admin-ui')
    expect(form.roleClaim).toBe('')
    expect(form.extraScopes).toBe('groups')
    expect(form.clientSecret).toBe('')
  })

  it('takes the tested values on a reload, so the test still counts', () => {
    const test = setupTest({ values: { ...setupTest().values!, roleClaim: 'app-roles' } })
    const signIn = signInSettings()
    const form = newForm(signIn, test)
    expect(form.authority).toBe(AUTHORITY)
    expect(form.roleClaim).toBe('app-roles')
    expect(isStale(test, form, signIn, false)).toBe(false)
  })

  it('sends null for a locked value and for an empty one, and splits the scopes', () => {
    const signIn = signInSettings({ clientId: { value: 'fors-admin', source: 'deployment', locked: true } })
    const form = { ...newForm(signIn), authority: `  ${AUTHORITY} `, clientId: 'typed-over', groupClaim: ' ', extraScopes: 'groups, offline_access groups' }
    expect(valuesOf(form, signIn)).toEqual({
      authority: AUTHORITY,
      clientId: null,
      roleClaim: null,
      departmentClaim: null,
      teamClaim: null,
      groupClaim: null,
      extraScopes: ['groups', 'offline_access'],
      clientSecretExpiresOn: null,
    })
  })
})

describe('the client secret', () => {
  it('offers keeping a stored secret only when one is stored and readable, and nothing when the deployment sets it', () => {
    expect(secretChoices(stored)).toEqual(['keep', 'public', 'typed'])
    expect(secretChoices(signInSettings().clientSecret)).toEqual(['public', 'typed'])
    expect(secretChoices({ ...stored, unreadable: true })).toEqual(['public', 'typed'])
    expect(secretChoices(deployed)).toEqual([])
  })

  it('maps keep, public client and typed to null, "" and the typed value', () => {
    const signIn = signInSettings({ clientSecret: stored })
    const form = newForm(signIn)
    expect(form.secretChoice).toBe('keep')
    expect(secretOf(form, stored)).toBeNull()
    expect(secretOf({ ...form, secretChoice: 'public' }, stored)).toBe('')
    expect(secretOf({ ...form, secretChoice: 'typed', clientSecret: 's3cret' }, stored)).toBe('s3cret')
    expect(secretOf({ ...form, secretChoice: 'typed', clientSecret: 's3cret' }, deployed)).toBeNull()
    expect(testRequestOf({ ...form, secretChoice: 'typed', clientSecret: 's3cret' }, signIn)).toMatchObject({ clientSecret: 's3cret', clientSecretExpiresOn: '2027-11-01' })
  })

  it('sends the expiry date only with a secret kept in setup', () => {
    const signIn = signInSettings({ clientSecret: stored })
    const form = newForm(signIn)
    expect(valuesOf(form, signIn).clientSecretExpiresOn).toBe('2027-11-01')
    expect(valuesOf({ ...form, secretChoice: 'public' }, signIn).clientSecretExpiresOn).toBeNull()
    expect(valuesOf({ ...form, clientSecretExpiresOn: '2027-11-01' }, signInSettings({ clientSecret: deployed })).clientSecretExpiresOn).toBeNull()
  })

  it('starts from the choice the latest test was made with', () => {
    expect(newForm(signInSettings({ clientSecret: stored }), setupTest({ clientSecret: 'removed' })).secretChoice).toBe('public')
    expect(newForm(signInSettings(), setupTest({ clientSecret: 'typed' })).secretChoice).toBe('typed')
    expect(newForm(signInSettings({ clientSecret: { ...stored, unreadable: true } })).secretChoice).toBe('typed')
  })
})

describe('when Save is offered', () => {
  const signIn = signInSettings()

  it('needs a passed test whose person is a gateway admin, with the values unchanged', () => {
    const test = setupTest()
    const form = newForm(signIn, test)
    expect(canSave(test, isStale(test, form, signIn, false))).toBe(true)
    expect(canSave(test, isStale(test, { ...form, clientId: 'other' }, signIn, false))).toBe(false)
    expect(canSave(test, isStale(test, form, signIn, true))).toBe(false)
    const notAdmin = setupTest({ result: { ...setupTest().result!, isGatewayAdmin: false } })
    expect(canSave(notAdmin, false)).toBe(false)
    expect(canSave(setupTest({ status: 'failed', reason: 'token_refused', result: null }), false)).toBe(false)
    expect(canSave(setupTest({ status: 'pending', result: null }), false)).toBe(false)
    expect(canSave(null, false)).toBe(false)
  })

  it('does not count spaces or a repeated scope as a change', () => {
    const test = setupTest({ values: { ...setupTest().values!, extraScopes: ['groups'] } })
    const form = { ...newForm(signIn, test), authority: ` ${AUTHORITY}  `, extraScopes: 'groups groups' }
    expect(isStale(test, form, signIn, false)).toBe(false)
  })

  it('completes with the tested values and the test id, never a secret', () => {
    const test = setupTest()
    expect(canSave(test, false)).toBe(true)
    if (!canSave(test, false)) return
    const request = completeRequestOf(test)
    expect(request).toEqual({ ...test.values, testId: 't1' })
    expect(request).not.toHaveProperty('clientSecret')
  })
})
