import type {
  AttachmentPolicy,
  IntegrationRole,
  BudgetPeriod,
  ContentGuardPolicy,
  BudgetScope,
  CircuitState,
  DataPermission,
  DataResidency,
  HealthStatus,
  KeyStatus,
  ModelKind,
  PiiAction,
  PiiCategory,
  PiiPolicy,
  PriceMode,
  ProviderCapability,
  ProviderStatus,
  RequestOutcome,
  RoutingScope,
  SignInValueSource,
} from '@/api/types'

export type Tone = 'ok' | 'warn' | 'danger' | 'neutral' | 'accent'

export const KEY_STATUS: Record<KeyStatus, { label: string; tone: Tone }> = {
  Active: { label: 'Active', tone: 'ok' },
  InGracePeriod: { label: 'Grace period', tone: 'warn' },
  Expired: { label: 'Expired', tone: 'neutral' },
  Revoked: { label: 'Revoked', tone: 'danger' },
  Disabled: { label: 'Disabled', tone: 'neutral' },
}

/** What each Data API permission gives access to (docs/data-access.md). */
export const DATA_PERMISSION: Record<DataPermission, { label: string; description: string }> = {
  'usage.aggregate': {
    label: 'Usage totals',
    description: 'Totals per day or month by department, model, provider and residency. No key or team rows; small departments are grouped. For management reporting.',
  },
  'usage.detail': {
    label: 'Usage records',
    description: 'One row per request with key, team, department, model, tokens, cost, latency and outcome. For BI and chargeback.',
  },
  'security.read': {
    label: 'Security data',
    description: 'Refused keys, requests with a PII action or policy refusal, the admin audit log and every key’s status. For the security team’s SIEM.',
  },
  'catalog.read': {
    label: 'Catalogue',
    description: 'Departments, teams, keys (name and prefix only), providers, models, prices and budgets, for joining the other data.',
  },
}

export const HEALTH_STATUS: Record<HealthStatus, { label: string; tone: Tone }> = {
  Healthy: { label: 'Healthy', tone: 'ok' },
  Degraded: { label: 'Degraded', tone: 'warn' },
  Unhealthy: { label: 'Down', tone: 'danger' },
}

/** The provider status computed by the admin API (`GET /api/ops/health`). */
export const PROVIDER_STATUS: Record<ProviderStatus, { label: string; tone: Tone }> = {
  Healthy: { label: 'Healthy', tone: 'ok' },
  Degraded: { label: 'Degraded', tone: 'warn' },
  Unhealthy: { label: 'Down', tone: 'danger' },
  CircuitOpen: { label: 'Circuit open', tone: 'danger' },
  NoRecentTraffic: { label: 'No recent traffic', tone: 'neutral' },
  Disabled: { label: 'Disabled', tone: 'neutral' },
}

export const CIRCUIT_LABEL: Record<CircuitState, { label: string; tone: Tone }> = {
  Closed: { label: 'Healthy', tone: 'ok' },
  Open: { label: 'Circuit open', tone: 'danger' },
}

export const RESIDENCY: Record<DataResidency, { label: string; glyph: string; cls: string; description: string }> = {
  OnPrem: { label: 'On-prem', glyph: '■', cls: 'text-res-onprem', description: 'Runs in the municipality’s own data centre.' },
  Eu: { label: 'EU', glyph: '●', cls: 'text-res-eu', description: 'Hosted by a provider inside the EU/EEA.' },
  External: { label: 'External', glyph: '◆', cls: 'text-res-ext', description: 'Hosted outside the EU/EEA.' },
}

export const PII_LABEL: Record<PiiPolicy, string> = {
  Off: 'Off',
  Allow: 'Allow',
  Redact: 'Redact',
  Block: 'Block',
  RerouteToOnPrem: 'On-prem',
}

export const PII_CATEGORY_LABEL: Record<PiiCategory, string> = {
  Personnummer: 'Personal identity number',
  Samordningsnummer: 'Coordination number',
  Email: 'Email address',
  Phone: 'Phone number',
  Iban: 'IBAN',
}

/** What happened to a request with PII, by the key's policy. */
export const PII_ACTION_LABEL: Record<PiiAction, { label: string; tone: Tone }> = {
  Allow: { label: 'Detected, sent on', tone: 'neutral' },
  Redact: { label: 'Redacted', tone: 'ok' },
  Block: { label: 'Blocked', tone: 'warn' },
  RerouteToOnPrem: { label: 'Sent on-prem', tone: 'ok' },
}

export const PII_HINT: Record<PiiPolicy, string> = {
  Off: 'No PII check. Use only for public data.',
  Allow: 'PII is detected and logged as a category, then sent on.',
  Redact: 'Detected PII is masked before it leaves the gateway.',
  Block: 'Requests containing PII are rejected with pii_blocked.',
  RerouteToOnPrem: 'Requests with PII are sent to on-prem models only.',
}

export const CONTENT_GUARD_LABEL: Record<ContentGuardPolicy, string> = { Off: 'Off', Record: 'Record', Block: 'Block' }

export const CONTENT_GUARD_HINT: Record<ContentGuardPolicy, string> = {
  Off: 'No content check.',
  Record: 'Prompt-injection rules and the on-prem guard model check requests; findings are recorded, requests sent on.',
  Block: 'Requests the content guard flags are refused with content_blocked.',
}

export const ATTACHMENT_LABEL: Record<AttachmentPolicy, string> = {
  Allowed: 'All files',
  ImagesOnly: 'Images only',
  None: 'Text only',
}

export const ATTACHMENT_HINT: Record<AttachmentPolicy, string> = {
  Allowed: 'Images, documents, audio and video are sent on. The PII check cannot read file contents.',
  ImagesOnly: 'Images are sent on; documents, audio, video and file references are rejected with attachment_not_allowed.',
  None: 'Any attached file is rejected with attachment_not_allowed. Use for keys handling sensitive data.',
}

/** Shown with a restrictive attachment policy: the check stops files, not text a client extracted from a file. */
export const ATTACHMENT_LIMIT_NOTE =
  'A safeguard, not a guarantee: some chat apps read a file themselves and paste its text into the message. That text is no longer a file, so it gets through. The PII policy still checks it.'

/** Key settings that let requests send the conversation beyond the provider (tool_not_allowed / storage_not_allowed when off). */
export const PROVIDER_TOOLS_HINT =
  'Web search, file search, code interpreter, MCP servers and other tools the provider runs itself. They send the conversation to other services or the internet. Off: only function tools.'
export const PROVIDER_STORAGE_HINT =
  'Lets requests ask the provider to keep the conversation (store, background responses, conversations). Off: refused, and Responses API requests are sent with store: false.'

export const REQUIRE_MAX_RESIDENCY_HINT =
  'Every request must send the x-fors-max-residency header (OnPrem, Eu or External), or it is refused. The header can only narrow the residencies above.'

/** A key's part in an integration (docs/integrations.md). */
export const INTEGRATION_ROLE: Record<IntegrationRole, { label: string; description: string }> = {
  None: { label: 'None', description: 'An ordinary key that an application presents itself.' },
  Assigned: {
    label: 'Assigned',
    description:
      'For one chatbot or knowledge base of the integration. It has no secret: the integration uses it by reference, and its usage, limits and budgets are this key’s and this team’s.',
  },
  Credential: {
    label: 'Credential',
    description: 'What the integration’s own services sign in with. Belongs to the integration’s owner team; only gateway admins change it.',
  },
}

export const INTEGRATION_SCOPE: Record<string, { label: string; description: string } | undefined> = {
  'usage.read': { label: 'Read usage', description: 'Usage and budget status of the integration’s own keys (Integration API).' },
  'organisation.read': {
    label: 'Read the organisation',
    description: 'Every department and team in the key’s context (/v1/fors/context), not only those of the integration’s keys.',
  },
}

/** Shown on a key assigned to an integration: one question in the platform is usually several requests on the key. */
export const ASSIGNED_KEY_HINT =
  'The integration may make several requests on this key for one question (for example rewriting the question, searching, ranking and answering). ' +
  'Allow every model it uses for them, embedding and rerank models included, and set requests per minute for that many requests per question.'

/** Shown on a credential key's residencies: they bound the keys it acts as. */
export const CREDENTIAL_RESIDENCY_HINT =
  'Also an upper bound on every key this key is used for: a request made as an assigned key reaches only residencies both keys allow.'

/** A scope's label, or the scope itself for one this console does not know yet. */
export function integrationScopeLabel(scope: string): string {
  return INTEGRATION_SCOPE[scope]?.label ?? scope
}

export const SCOPE_LABEL: Record<BudgetScope, string> = { Department: 'Department', Team: 'Team', VirtualKey: 'Key' }
export const ROUTING_SCOPE_LABEL: Record<RoutingScope, string> = { VirtualKey: 'Key', Team: 'Team', Department: 'Department', Global: 'Global' }
export const ROUTING_SCOPE_PLURAL: Record<RoutingScope, string> = { VirtualKey: 'Keys', Team: 'Teams', Department: 'Departments', Global: 'Everyone' }
export const ROUTING_SCOPE_HEADING: Record<RoutingScope, string> = {
  VirtualKey: 'Key rules',
  Team: 'Team rules',
  Department: 'Department rules',
  Global: 'Global rules',
}
export const ROUTING_SCOPE_HINT: Record<RoutingScope, string> = {
  VirtualKey: 'Apply to the chosen keys (and the keys that replace them when they are rotated).',
  Team: 'Apply to every key in the chosen teams.',
  Department: 'Apply to every key in the chosen departments.',
  Global: 'Apply to every request.',
}
export const PERIOD_LABEL: Record<BudgetPeriod, string> = {
  Hourly: 'Hourly',
  Daily: 'Daily',
  Weekly: 'Weekly',
  Monthly: 'Monthly',
  Quarterly: 'Quarterly',
  Yearly: 'Yearly',
}
/** Where a model's price comes from (Model.priceMode). */
export const PRICE_MODE_LABEL: Record<PriceMode, string> = {
  List: 'models.dev',
  Unlisted: 'Set by hand (not in models.dev)',
  Manual: 'Set by hand',
}

export const KIND_LABEL: Record<ModelKind, string> = {
  Chat: 'Chat',
  Embedding: 'Embedding',
  Transcription: 'Speech to text',
  Realtime: 'Realtime (live)',
  SpeechTranslation: 'Live interpreting',
  Image: 'Image generation',
  Rerank: 'Rerank',
}

export const PROVIDER_TYPE_LABEL: Record<import('@/api/types').ProviderType, string> = {
  OpenAI: 'OpenAI',
  AzureOpenAI: 'Azure OpenAI',
  AzureAIFoundry: 'Azure AI Foundry',
  Anthropic: 'Anthropic',
  Ollama: 'Ollama',
  OllamaCloud: 'Ollama Cloud',
  OpenAICompatible: 'OpenAI-compatible',
}

/**
 * What each provider type serves (ProviderTypes.cs in the domain; keep the two in step). Null: an OpenAI-compatible server,
 * where the admin chooses the endpoints.
 */
export const TYPE_CAPABILITIES: Record<import('@/api/types').ProviderType, import('@/api/types').ProviderCapability[] | null> = {
  OpenAI: ['ChatCompletions', 'Streaming', 'Embeddings', 'Responses', 'AudioTranscriptions', 'Realtime', 'Images'],
  AzureOpenAI: ['ChatCompletions', 'Streaming', 'Embeddings', 'Responses', 'AudioTranscriptions', 'Realtime', 'Images'],
  AzureAIFoundry: ['ChatCompletions', 'Streaming', 'Embeddings', 'Responses', 'AudioTranscriptions', 'Realtime', 'Images'],
  Anthropic: ['ChatCompletions', 'Streaming', 'AnthropicMessages'],
  Ollama: ['ChatCompletions', 'Streaming', 'Embeddings'],
  OllamaCloud: ['ChatCompletions', 'Streaming'],
  OpenAICompatible: null,
}

export const AUTH_MODE_LABEL: Record<import('@/api/types').ProviderAuthMode, string> = {
  None: 'No authentication',
  Bearer: 'Bearer token',
  ApiKeyHeader: 'API key header',
  XApiKeyHeader: 'X-API-Key header',
}

export const PARAMETER_PROFILE_LABEL: Record<import('@/api/types').ParameterProfile, string> = {
  Standard: 'Standard',
  OpenAIReasoning: 'OpenAI reasoning',
}

export const CAPABILITY_LABEL: Record<ProviderCapability, string> = {
  ChatCompletions: 'Chat completions',
  Embeddings: 'Embeddings',
  Responses: 'Responses',
  AnthropicMessages: 'Anthropic messages',
  Streaming: 'Streaming',
  AudioTranscriptions: 'Speech to text',
  Realtime: 'Live audio (realtime)',
  Images: 'Image generation and editing',
  Rerank: 'Rerank (Cohere-compatible)',
}

export const OUTCOME_LABEL: Record<RequestOutcome, { label: string; tone: Tone }> = {
  Success: { label: 'Success', tone: 'ok' },
  ProviderError: { label: 'Provider error', tone: 'danger' },
  BudgetExceeded: { label: 'Over budget', tone: 'danger' },
  RateLimited: { label: 'Rate limited', tone: 'warn' },
  PiiBlocked: { label: 'PII blocked', tone: 'warn' },
  Rejected: { label: 'Rejected', tone: 'warn' },
  ClientCancelled: { label: 'Cancelled', tone: 'neutral' },
}

/** Where a sign-in value comes from (Settings › Sign-in and the setup wizard). */
export const SIGN_IN_SOURCE: Record<SignInValueSource, { label: string; tone: Tone }> = {
  deployment: { label: 'Set by the deployment', tone: 'accent' },
  wizard: { label: 'Set in setup', tone: 'ok' },
  default: { label: 'Default', tone: 'neutral' },
}

/**
 * Every reason a setup test sign-in can end with, as the admin API names them: the refusals of `POST /bff/setup/test` and the
 * results of its callback. The source is `SetupTestReasons` in src/Fors.AdminApi/Setup/SetupProvider.cs (without
 * `passed`); keep this list equal to it. SETUP_TEST_REASON has a text for each (labels.spec.ts).
 */
export const SETUP_TEST_REASONS = [
  'authority_invalid',
  'authority_unreachable',
  'tls',
  'redirected',
  'not_discovery',
  'issuer_mismatch',
  'insecure_endpoint',
  'par_required',
  'keys_unavailable',
  'stored_secret_unreadable',
  'provider_error',
  'token_refused',
  'token_endpoint_unreachable',
  'id_token_missing',
  'id_token_invalid',
  'nonce_mismatch',
  'secret_required',
  'deployment_secret_without_authority',
  'browser_mismatch',
  'session_expired',
  'unknown_test',
] as const
export type SetupTestReason = (typeof SETUP_TEST_REASONS)[number]

/** Why a test sign-in could not start or failed: the step that failed and what to do. */
export const SETUP_TEST_REASON: Record<SetupTestReason, string> = {
  authority_invalid:
    'The provider address is not valid. Enter the full https address of the provider’s issuer, without a query or #, for example https://login.example.org/realms/fors.',
  authority_unreachable:
    'The admin API could not reach the provider address. Check the address, and that the admin API can reach the provider through the network and any proxy.',
  tls: 'The admin API does not trust the provider’s certificate. Add the provider’s certificate authority to the admin API’s trusted certificates, or check the address.',
  redirected: 'The provider address answered with a redirect, and the admin API follows none. Enter exactly the address the provider names as its issuer.',
  not_discovery:
    'The provider address does not lead to an OpenID Connect discovery document. Check the address: for Keycloak it ends with /realms/ and the realm, for Entra ID with /v2.0.',
  issuer_mismatch: 'The provider names itself with another address (its issuer). Enter exactly the issuer the provider names, including any trailing slash.',
  insecure_endpoint: 'The provider lists an address that is not https, and the admin console signs in over https only. Fix the provider’s public address.',
  par_required:
    'The provider requires pushed authorization requests, which the admin console’s sign-in does not make. Turn that requirement off for this client at the provider.',
  keys_unavailable: 'The admin API could not fetch the provider’s signing keys. Check that the provider publishes its keys and that the admin API can reach them.',
  stored_secret_unreadable: 'The stored client secret can no longer be read (the Data Protection key ring changed). Type the client secret again.',
  provider_error:
    'The provider sent you back with an error instead of signing you in. Check that the redirect URI is registered for this client, that the client is enabled and that you may use it. Then test again.',
  token_refused:
    'The provider refused the sign-in’s code exchange. Most often the client secret or the client ID is wrong, or the client needs a secret and none was given. Check them and test again.',
  token_endpoint_unreachable:
    'The admin API could not reach the provider’s token address. Check that the admin API can reach the provider through the network and any proxy.',
  id_token_missing: 'The provider sent no ID token. Check that the client is an OpenID Connect client that allows the openid scope.',
  id_token_invalid:
    'The provider’s ID token did not pass the checks of its signature, issuer, audience (the client ID) or lifetime. Check the client ID, and that the clocks of the admin API and the provider are right.',
  nonce_mismatch: 'The provider’s answer did not belong to this test. Test again.',
  secret_required:
    'The provider address or the client ID is not the one the stored client secret belongs to, so that secret is not sent. Type the client secret of this client, or choose a public client.',
  deployment_secret_without_authority:
    'The deployment sets the client secret but not the provider address, so that secret is never sent to an address typed here. Remove the client secret from the deployment and type it here instead.',
  browser_mismatch: 'The sign-in finished in another browser than the one that started the test. Test again, and sign in in the window the test opens.',
  session_expired: 'Your setup session ended before the sign-in finished. Enter the setup code again, then test again.',
  unknown_test: 'The test is unknown, already used or expired. Test again.',
}

/** A test reason's text; a code this console does not know gets a generic text that names it. */
export function setupTestReasonText(reason: string | null | undefined): string {
  return (reason && SETUP_TEST_REASON[reason as SetupTestReason]) || `The test sign-in failed (${reason ?? 'no reason given'}). Check the values and test again.`
}

/**
 * The reasons `POST /bff/setup/complete` refuses to save with (`setup_test_required`): `SetupService.CompleteAsync` in
 * src/Fors.AdminApi/Setup/SetupService.cs. Keep this list equal to it.
 */
export const SETUP_COMPLETE_REASONS = ['unknown_test', 'not_latest', 'not_passed', 'not_gateway_admin', 'values_changed'] as const
export type SetupCompleteReason = (typeof SETUP_COMPLETE_REASONS)[number]

export const SETUP_COMPLETE_REASON: Record<SetupCompleteReason, string> = {
  unknown_test: 'This setup session does not know the test. Test sign-in again, then save.',
  not_latest: 'A newer test sign-in was started. Wait for its result, then save.',
  not_passed: 'The latest test sign-in did not pass. Test again.',
  not_gateway_admin:
    'The person who tested does not come out as a gateway admin. Give them the role gateway-admin at the provider, or a group the deployment maps to it, and test again.',
  values_changed: 'The values differ from the ones tested. Test sign-in again with these values.',
}

export function setupCompleteReasonText(reason: string | null | undefined): string {
  return (reason && SETUP_COMPLETE_REASON[reason as SetupCompleteReason]) || `Saving needs a passed test sign-in by a gateway admin (${reason ?? 'no reason given'}). Test sign-in again.`
}

export const TONE_CLASSES: Record<Tone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  neutral: 'bg-sunken text-fg-2',
  accent: 'bg-accent-soft text-accent-ink',
}

export function plural(count: number, singular: string, pluralForm = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : pluralForm}`
}

/** `allowedModels: []` means every model; `allowedResidencies: []` means any residency. */
export function modelsLabel(models: readonly string[]): string {
  return models.length === 0 ? 'All models' : models.join(', ')
}

export function residenciesLabel(residencies: readonly DataResidency[]): string {
  return residencies.length === 0 ? 'Any residency' : residencies.map((r) => RESIDENCY[r].label).join(', ')
}

/** Known model capability tags, in display order. Providers may report others; those are shown as reported. */
export const MODEL_FEATURES = ['tools', 'vision', 'thinking', 'image generation', 'embedding', 'audio', 'code'] as const

export function featureLabel(feature: string): string {
  return feature.charAt(0).toUpperCase() + feature.slice(1)
}
