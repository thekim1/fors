import type { RoutingRule, SetupState, SetupTest, SignInSettings } from '@/api/types'

let counter = 0

/** A valid, enabled global routing rule; override what the test cares about. */
export function rule(overrides: Partial<RoutingRule> = {}): RoutingRule {
  counter += 1
  return {
    id: `r${counter}`,
    name: `Rule ${counter}`,
    description: null,
    isEnabled: true,
    priority: counter * 10,
    scope: 'Global',
    isTemplate: false,
    scopeIds: [],
    owners: [],
    isOrphaned: false,
    condition: '',
    chain: false,
    targets: [{ model: 'ume/chat-advanced', weight: 1 }],
    fallbacks: [],
    missingModels: [],
    validationErrors: [],
    createdAt: '2026-10-09T08:00:00Z',
    updatedAt: '2026-10-09T08:00:00Z',
    ...overrides,
  }
}

/** Stub for the drawer/dialog wrappers: renders the slots inline when open, so tests need no native modal dialog. */
export const inlineOverlay = {
  props: ['open', 'title'],
  template: '<div v-if="open"><h2>{{ title }}</h2><slot name="description" /><slot /><slot name="actions" /><slot name="footer" /></div>',
}

/** Sign-in values with nothing set: every value its default, no authority, no secret. */
export function signInSettings(overrides: Partial<SignInSettings> = {}): SignInSettings {
  const value = <T>(v: T) => ({ value: v, source: 'default' as const, locked: false })
  return {
    authority: value<string | null>(null),
    clientId: value('ume-admin'),
    clientSecret: { set: false, source: 'default', setAt: null, expiresOn: null, unreadable: false, locked: false },
    roleClaim: value('roles'),
    departmentClaim: value('departmentCodes'),
    teamClaim: value('teamCodes'),
    groupClaim: value('groups'),
    extraScopes: value<string[]>([]),
    updatedAt: null,
    ...overrides,
  }
}

/** A passed test by a gateway admin, with the authority https://idp.example.org/realms/ume and a typed secret. */
export function setupTest(overrides: Partial<SetupTest> = {}): SetupTest {
  return {
    testId: 't1',
    status: 'passed',
    reason: null,
    startedAt: '2026-11-16T08:00:00Z',
    expiresAt: '2026-11-16T08:10:00Z',
    values: {
      authority: 'https://idp.example.org/realms/ume',
      clientId: null,
      roleClaim: null,
      departmentClaim: null,
      teamClaim: null,
      groupClaim: null,
      extraScopes: null,
      clientSecretExpiresOn: null,
    },
    clientSecret: 'typed',
    result: {
      subject: 'sub-1',
      name: 'Anna Andersson',
      roles: ['gateway-admin'],
      groups: [],
      groupCount: 0,
      departmentCodes: [],
      teamCodes: [],
      isGatewayAdmin: true,
      claimsRead: { role: 'roles', group: 'groups', department: 'departmentCodes', team: 'teamCodes' },
    },
    ...overrides,
  }
}

/** A first run's setup state: setup closes 50 minutes after `now`, the session 25 minutes after it. */
export function setupState(overrides: Partial<SetupState> = {}, now = Date.now()): SetupState {
  return {
    kind: 'first_run',
    endsAt: new Date(now + 50 * 60_000).toISOString(),
    sessionExpiresAt: new Date(now + 25 * 60_000).toISOString(),
    signIn: signInSettings(),
    redirectUris: { signIn: 'https://admin.example.org/signin-oidc', signOutCallback: 'https://admin.example.org/signout-callback-oidc' },
    gatewayAdminGroups: [],
    latestTest: null,
    ...overrides,
  }
}
