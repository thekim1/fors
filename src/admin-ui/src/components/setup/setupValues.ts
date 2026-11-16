import type { SetupCompleteRequest, SetupTest, SetupValuesRequest, SignInSecretStatus, SignInSettings, SignInWizardValues } from '@/api/types'

/**
 * The setup wizard's form of the admin console's sign-in, what it sends, and when a test still stands for it. Only what is
 * set in the wizard is sent: a field left empty (the default applies) or set by the deployment is sent as null.
 */

export const TEXT_FIELDS = ['authority', 'clientId', 'roleClaim', 'departmentClaim', 'teamClaim', 'groupClaim'] as const
export type TextFieldName = (typeof TEXT_FIELDS)[number]
/** The fields a test can refuse (validation problems name them). */
export type SignInFieldName = TextFieldName | 'extraScopes' | 'clientSecret'

/**
 * What the test does with the client secret: keep the stored one (sent as null), test without one, a public client (""), or
 * test with the one typed. The secret is never prefilled and lives nowhere but the form.
 */
export type SecretChoice = 'keep' | 'public' | 'typed'

export interface SetupForm {
  authority: string
  clientId: string
  roleClaim: string
  departmentClaim: string
  teamClaim: string
  groupClaim: string
  /** Separated by spaces (or commas). */
  extraScopes: string
  secretChoice: SecretChoice
  clientSecret: string
  /** `yyyy-mm-dd`, or empty. */
  clientSecretExpiresOn: string
}

/** A stored secret the test can keep: one set in setup that the key ring can still read. */
export function canKeepSecret(secret: SignInSecretStatus): boolean {
  return secret.source === 'wizard' && secret.set && !secret.unreadable
}

/** The choices the secret field offers; none when the deployment sets the secret. */
export function secretChoices(secret: SignInSecretStatus): SecretChoice[] {
  if (secret.locked) return []
  return canKeepSecret(secret) ? ['keep', 'public', 'typed'] : ['public', 'typed']
}

function choiceOf(secret: SignInSecretStatus, tested: SetupTest | null): SecretChoice {
  const keep = canKeepSecret(secret)
  switch (tested?.clientSecret) {
    case 'typed':
      return 'typed'
    case 'removed':
    case 'none':
      return 'public'
    case 'stored':
      return keep ? 'keep' : 'typed'
    default:
      return keep ? 'keep' : secret.unreadable ? 'typed' : 'public'
  }
}

/**
 * The form for `signIn` (`GET /bff/setup/state`): a deployment's value is shown (locked), a value set in setup is filled in, a
 * default is left empty. With a `tested` test (a reload), the values that test was made with are filled in instead, so the test
 * still counts.
 */
export function newForm(signIn: SignInSettings, tested: SetupTest | null = null): SetupForm {
  const values = tested?.values ?? null
  const text = (field: TextFieldName): string => {
    const live = signIn[field]
    if (live.locked) return live.value ?? ''
    if (values) return values[field] ?? ''
    return live.source === 'wizard' ? (live.value ?? '') : ''
  }
  const scopes = signIn.extraScopes.locked ? signIn.extraScopes.value
    : values ? (values.extraScopes ?? [])
    : signIn.extraScopes.source === 'wizard' ? signIn.extraScopes.value : []
  return {
    authority: text('authority'),
    clientId: text('clientId'),
    roleClaim: text('roleClaim'),
    departmentClaim: text('departmentClaim'),
    teamClaim: text('teamClaim'),
    groupClaim: text('groupClaim'),
    extraScopes: scopes.join(' '),
    secretChoice: choiceOf(signIn.clientSecret, tested),
    clientSecret: '',
    clientSecretExpiresOn: values ? (values.clientSecretExpiresOn ?? '') : (signIn.clientSecret.expiresOn ?? ''),
  }
}

function clean(value: string | null | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed ? trimmed : null
}

function scopesOf(text: string): string[] | null {
  const scopes = [...new Set(text.split(/[\s,]+/).filter(Boolean))]
  return scopes.length ? scopes : null
}

/** The expiry date belongs to a secret kept in setup: none for a deployment's secret or a public client. */
function expiryOf(form: SetupForm, secret: SignInSecretStatus): string | null {
  return secret.locked || form.secretChoice === 'public' ? null : clean(form.clientSecretExpiresOn)
}

/** The values the form sends, without the secret: null for a locked value and for an empty one. */
export function valuesOf(form: SetupForm, signIn: SignInSettings): SignInWizardValues {
  const text = (field: TextFieldName) => (signIn[field].locked ? null : clean(form[field]))
  return {
    authority: text('authority'),
    clientId: text('clientId'),
    roleClaim: text('roleClaim'),
    departmentClaim: text('departmentClaim'),
    teamClaim: text('teamClaim'),
    groupClaim: text('groupClaim'),
    extraScopes: signIn.extraScopes.locked ? null : scopesOf(form.extraScopes),
    clientSecretExpiresOn: expiryOf(form, signIn.clientSecret),
  }
}

/** The secret as `POST /bff/setup/test` takes it: null keeps the stored one (or uses the deployment's), "" tests without one, else the typed one. */
export function secretOf(form: SetupForm, secret: SignInSecretStatus): string | null {
  if (secret.locked) return null
  switch (form.secretChoice) {
    case 'keep':
      return null
    case 'public':
      return ''
    case 'typed':
      return form.clientSecret
  }
}

export function testRequestOf(form: SetupForm, signIn: SignInSettings): SetupValuesRequest {
  return { ...valuesOf(form, signIn), clientSecret: secretOf(form, signIn.clientSecret) }
}

/** As the API's `SignInWizardValues.Normalized()`: trimmed, empty as null, scopes without repeats. */
export function normalized(values: SignInWizardValues): SignInWizardValues {
  const scopes = [...new Set((values.extraScopes ?? []).map(clean).filter((s): s is string => s !== null))]
  return {
    authority: clean(values.authority),
    clientId: clean(values.clientId),
    roleClaim: clean(values.roleClaim),
    departmentClaim: clean(values.departmentClaim),
    teamClaim: clean(values.teamClaim),
    groupClaim: clean(values.groupClaim),
    extraScopes: scopes.length ? scopes : null,
    clientSecretExpiresOn: clean(values.clientSecretExpiresOn),
  }
}

/** `POST /bff/setup/complete` takes the tested values exactly, as the test reports them, and the test's id (never the secret). */
export function completeRequestOf(test: SetupTest & { values: SignInWizardValues }): SetupCompleteRequest {
  return { ...normalized(test.values), testId: test.testId }
}

export function sameValues(a: SignInWizardValues, b: SignInWizardValues): boolean {
  return JSON.stringify(normalized(a)) === JSON.stringify(normalized(b))
}

/**
 * Whether a finished test no longer stands for the form: a value differs from the tested ones, or the secret's choice or value
 * changed since the test started (`secretEdited`; the secret is not among the test's values).
 */
export function isStale(test: SetupTest | null, form: SetupForm, signIn: SignInSettings, secretEdited: boolean): boolean {
  if (!test || test.status === 'pending') return false
  return !test.values || secretEdited || !sameValues(valuesOf(form, signIn), test.values)
}

/** Save is offered for a passed test whose person comes out as a gateway admin, and that still stands for the form. */
export function canSave(test: SetupTest | null, stale: boolean): test is SetupTest & { values: SignInWizardValues } {
  return !!test && test.status === 'passed' && test.result?.isGatewayAdmin === true && !!test.values && !stale
}
