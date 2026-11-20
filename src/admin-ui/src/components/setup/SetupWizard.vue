<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { SetupState } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { formatTime } from '@/utils/format'
import { setupCompleteReasonText, setupTestReasonText } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import SetupCodeStep from './SetupCodeStep.vue'
import SetupProviderStep from './SetupProviderStep.vue'
import SetupSaveStep from './SetupSaveStep.vue'
import SetupSignInStep from './SetupSignInStep.vue'
import { canSave, completeRequestOf, isStale, newForm, testRequestOf, valuesOf, type SetupForm, type SignInFieldName } from './setupValues'
import { listenForTestDone, navigation, openTestWindow } from './testWindow'

// The setup wizard (docs/admin-ui.md, "Setup wizard"). The setup session is a cookie; the code and a typed client secret live
// in component state only, never in storage, an address or a log. A 401 (the session ended) goes back to the code and keeps the
// typed values except the secret; a 404 means setup is closed.
type StepKey = 'code' | 'provider' | 'signIn' | 'save'
const STEPS: { key: StepKey; label: string }[] = [
  { key: 'code', label: 'Setup code' },
  { key: 'provider', label: 'Identity provider' },
  { key: 'signIn', label: 'Sign-in values' },
  { key: 'save', label: 'Save' },
]
const indexOf = (key: StepKey) => STEPS.findIndex((s) => s.key === key)

/** `closed`: setup was closed when the page opened; `closedMeanwhile`: it closed while the wizard ran; `ended`: its hour ran out. */
type Phase = 'loading' | 'wizard' | 'closed' | 'closedMeanwhile' | 'ended' | 'done'
const phase = ref<Phase>('loading')
const stepIndex = ref(0)
const current = computed(() => STEPS[stepIndex.value]!)
const state = ref<SetupState | null>(null)
const form = ref<SetupForm | null>(null)
/** Why the wizard is back at the code, or a failed read. */
const notice = ref<string | null>(null)
/** The secret's choice or value changed since the latest test started (the secret is not among the test's values). */
const secretEdited = ref(false)
const testing = ref(false)
const testError = ref<string | null>(null)
const blockedUrl = ref<string | null>(null)
const cancelledTestId = ref<string | null>(null)
const saving = ref(false)
const saveError = ref<string | null>(null)
const signInUrl = ref<string | null>(null)
const now = ref(Date.now())
const errors = useFormErrors<SignInFieldName>({
  authority: 'setup-authority',
  clientId: 'setup-clientId',
  clientSecret: 'setup-clientSecret',
  roleClaim: 'setup-roleClaim',
  groupClaim: 'setup-groupClaim',
  departmentClaim: 'setup-departmentClaim',
  teamClaim: 'setup-teamClaim',
  extraScopes: 'setup-extraScopes',
})
let popup: Window | null = null

const test = computed(() => {
  const latest = state.value?.latestTest ?? null
  return latest && latest.testId !== cancelledTestId.value ? latest : null
})
const stale = computed(() => !!form.value && !!state.value && isStale(test.value, form.value, state.value.signIn, secretEdited.value))
const savable = computed(() => canSave(test.value, stale.value))
const savedValues = computed(() => {
  const t = test.value
  if (canSave(t, stale.value)) return t.values
  return form.value && state.value ? valuesOf(form.value, state.value.signIn) : null
})
const minutesLeft = computed(() => (state.value ? Math.max(0, Math.ceil((Date.parse(state.value.endsAt) - now.value) / 60_000)) : 0))

function reachable(index: number): boolean {
  return index === 0 ? !state.value : !!state.value
}

function goTo(index: number): void {
  if (index < 0 || index >= STEPS.length || !reachable(index)) return
  stepIndex.value = index
}

function focus(id: string): void {
  void nextTick(() => document.getElementById(id)?.focus())
}

watch(stepIndex, () => focus('setup-step-heading'))

function closePopup(): void {
  if (popup && !popup.closed) popup.close()
  popup = null
}

/** Back to the code with `message`: the session is gone; the typed values stay, the secret does not. */
function backToCode(message: string): void {
  state.value = null
  notice.value = message
  testing.value = false
  blockedUrl.value = null
  cancelledTestId.value = null
  closePopup()
  if (form.value) form.value.clientSecret = ''
  if (stepIndex.value === 0) focus('setup-step-heading')
  stepIndex.value = 0
}

/** Leaves the wizard for one of its end states; nothing typed is kept. */
function end(next: Exclude<Phase, 'loading' | 'wizard'>): void {
  phase.value = next
  state.value = null
  form.value = null
  closePopup()
  focus('setup-end-heading')
}

const SESSION_ENDED = 'Your setup session has ended. Enter the setup code again. The values you typed are kept, but not the client secret.'

/** A 401 (no setup session) goes back to the code, a 404 (setup closed) ends the wizard. Whether `error` was one of them. */
function lost(error: unknown): boolean {
  if (!isApiError(error)) return false
  if (error.status === 404) {
    end(state.value ? 'closedMeanwhile' : 'closed')
    return true
  }
  if (error.status === 401) {
    backToCode(SESSION_ENDED)
    return true
  }
  return false
}

function apply(next: SetupState): void {
  state.value = next
  form.value ??= newForm(next.signIn, next.latestTest)
}

async function refresh(): Promise<void> {
  if (!state.value) return
  try {
    apply(await api.setup.state())
  } catch (e) {
    // A failed poll is tried again on the next one.
    lost(e)
  }
}

async function onStarted(): Promise<void> {
  notice.value = null
  try {
    apply(await api.setup.state())
    goTo(indexOf('provider'))
  } catch (e) {
    if (!lost(e)) notice.value = problemMessage(e)
  }
}

function checkExpiry(): void {
  if (!state.value) return
  if (now.value >= Date.parse(state.value.endsAt)) end('ended')
  else if (now.value >= Date.parse(state.value.sessionExpiresAt)) backToCode(SESSION_ENDED)
}

const clock = setInterval(() => {
  now.value = Date.now()
  checkExpiry()
}, 1000)

// While a test waits for its window: the done page tells this page, and every 2 s a read of the state catches what it can't.
let poll: ReturnType<typeof setInterval> | undefined
watch(
  () => phase.value === 'wizard' && test.value?.status === 'pending',
  (pending) => {
    clearInterval(poll)
    if (pending) poll = setInterval(() => void refresh(), 2000)
  },
  { immediate: true },
)
const stopListening = listenForTestDone(() => void refresh())

watch(
  () => [form.value?.secretChoice, form.value?.clientSecret] as const,
  (next, previous) => {
    if (next[0] !== undefined && previous[0] !== undefined) secretEdited.value = true
  },
)

onMounted(async () => {
  // Always asked: /bff/user's setup.open may be up to 10 seconds old, and the API's 404 is the answer that counts.
  try {
    apply(await api.setup.state())
    phase.value = 'wizard'
    // A reload with a live session: on at the provider, or at the sign-in values when a test was made.
    stepIndex.value = indexOf(state.value?.latestTest ? 'signIn' : 'provider')
  } catch (e) {
    if (isApiError(e) && e.status === 404) {
      phase.value = 'closed'
      return
    }
    phase.value = 'wizard'
    if (!isApiError(e) || e.status !== 401) notice.value = problemMessage(e)
  }
})

onBeforeUnmount(() => {
  clearInterval(clock)
  clearInterval(poll)
  stopListening()
  closePopup()
})

async function startTest(): Promise<void> {
  const s = state.value
  const f = form.value
  if (!s || !f || testing.value) return
  testError.value = null
  const ok = errors.validate({
    authority: !s.signIn.authority.locked && !f.authority.trim() && 'Enter the provider address.',
    clientSecret: !s.signIn.clientSecret.locked && f.secretChoice === 'typed' && !f.clientSecret.trim() && 'Type the client secret, or choose another option.',
  })
  if (!ok) return
  // Opened by the click itself; the provider's address follows once the test is made.
  closePopup()
  popup = openTestWindow()
  testing.value = true
  blockedUrl.value = null
  try {
    const started = await api.setup.test(testRequestOf(f, s.signIn))
    secretEdited.value = false
    cancelledTestId.value = null
    if (popup && !popup.closed) popup.location.href = started.authorizeUrl
    else blockedUrl.value = started.authorizeUrl
    await refresh()
  } catch (e) {
    closePopup()
    if (lost(e)) return
    if (isApiError(e) && e.problem?.code === 'setup_test_refused') testError.value = setupTestReasonText(e.problem.reason as string | undefined)
    else {
      errors.applyServerError(e)
      testError.value = errors.formError.value
    }
  } finally {
    testing.value = false
  }
}

function cancelTest(): void {
  cancelledTestId.value = test.value?.testId ?? null
  blockedUrl.value = null
  closePopup()
}

async function save(): Promise<void> {
  const t = test.value
  if (saving.value || !canSave(t, stale.value)) return
  saving.value = true
  saveError.value = null
  try {
    const url = (await api.setup.complete(completeRequestOf(t))).signInUrl
    // Saved: the typed secret and the rest of the form are dropped before leaving for the sign-in.
    signInUrl.value = url
    end('done')
    navigation.assign(url)
  } catch (e) {
    if (lost(e)) return
    if (isApiError(e) && e.problem?.code === 'setup_test_required') {
      saveError.value = setupCompleteReasonText(e.problem.reason as string | undefined)
      await refresh()
    } else saveError.value = problemMessage(e)
  } finally {
    saving.value = false
  }
}

function stepDone(index: number): boolean {
  const key = STEPS[index]!.key
  return (key === 'code' && !!state.value) || (key === 'signIn' && savable.value)
}

const END_TEXT: Record<Exclude<Phase, 'loading' | 'wizard' | 'done'>, { title: string; body: string }> = {
  closed: {
    title: 'Setup is closed',
    body: 'Sign-in is set up already, or setup mode’s hour has ended. To open it again, give Setup:Reopen (FORS_SETUP_REOPEN) a new value, for example today’s date, and restart the admin API.',
  },
  closedMeanwhile: {
    title: 'Setup has closed',
    body: 'Someone finished setup elsewhere, or its hour ran out. Nothing was saved from this page.',
  },
  ended: {
    title: 'Setup mode’s hour has ended',
    body: 'Nothing was saved from this page. To open setup again, give Setup:Reopen (FORS_SETUP_REOPEN) a new value, for example today’s date, and restart the admin API. It prints a new setup code.',
  },
}
</script>

<template>
  <div class="grid gap-6" data-test="setup-wizard">
    <header>
      <h1 id="page-title" tabindex="-1" class="text-title-2 outline-none">Set up sign-in</h1>
      <p class="mt-1 text-fg-2">Connect the admin console to your identity provider. Saving needs a test sign-in by a gateway admin.</p>
      <p v-if="state?.kind === 'reopen'" class="mt-2 text-small text-fg-2" data-test="reopened">Setup was opened again. The current sign-in keeps working until you save.</p>
      <p v-if="state" class="mt-2 text-small text-fg-3" data-test="setup-ends">
        Setup closes at {{ formatTime(state.endsAt) }} (in {{ minutesLeft }} min); this setup session ends at {{ formatTime(state.sessionExpiresAt) }}.
      </p>
    </header>

    <p v-if="phase === 'loading'" role="status" class="text-small text-fg-3">Loading…</p>

    <section v-else-if="phase === 'done'" class="material grid gap-3 rounded-card p-6" aria-labelledby="setup-end-heading" data-test="setup-done">
      <h2 id="setup-end-heading" tabindex="-1" class="text-heading outline-none">Sign-in is set up</h2>
      <p class="text-fg-2">The values are saved and setup is closed. Taking you to the sign-in; use the account you tested with.</p>
      <div><UiButton v-if="signInUrl" variant="primary" size="lg" icon="login" :href="signInUrl" data-test="done-sign-in">Sign in</UiButton></div>
    </section>

    <section v-else-if="phase !== 'wizard'" class="material grid gap-3 rounded-card p-6" aria-labelledby="setup-end-heading" data-test="setup-closed">
      <h2 id="setup-end-heading" tabindex="-1" class="text-heading outline-none">{{ END_TEXT[phase].title }}</h2>
      <p class="text-fg-2">{{ END_TEXT[phase].body }}</p>
      <div><UiButton variant="primary" icon="login" href="/" data-test="to-sign-in">Go to sign-in</UiButton></div>
    </section>

    <template v-else>
      <nav aria-label="Setup steps">
        <ol class="flex flex-wrap gap-2" data-test="stepper">
          <li v-for="(s, index) in STEPS" :key="s.key">
            <button
              type="button" class="inline-flex min-h-8 items-center gap-2 rounded-control px-2 text-small disabled:cursor-not-allowed"
              :class="index === stepIndex ? 'bg-surface font-medium text-fg shadow-1' : 'text-fg-2 hover:text-fg disabled:opacity-60'"
              :aria-current="index === stepIndex ? 'step' : undefined" :disabled="index !== stepIndex && !reachable(index)" :data-test="`stepper-${s.key}`"
              @click="goTo(index)"
            >
              <span class="grid size-6 place-items-center rounded-full bg-sunken text-caption" aria-hidden="true">{{ index + 1 }}</span>
              {{ s.label }}<span v-if="stepDone(index)" class="sr-only"> (done)</span>
            </button>
          </li>
        </ol>
      </nav>

      <section class="material grid gap-5 rounded-card p-6" aria-labelledby="setup-step-heading">
        <div>
          <p class="text-caption text-fg-3">Step {{ stepIndex + 1 }} of {{ STEPS.length }}</p>
          <h2 id="setup-step-heading" tabindex="-1" class="text-title-3 outline-none" data-test="step-heading">{{ current.label }}</h2>
        </div>
        <InlineError v-if="notice" tone="warn" :message="notice" data-test="setup-notice" />
        <SetupCodeStep v-if="current.key === 'code'" @started="onStarted" @closed="end('closed')" />
        <template v-else-if="state && form">
          <SetupProviderStep v-if="current.key === 'provider'" :state="state" />
          <SetupSignInStep
            v-else-if="current.key === 'signIn'" v-model="form" :state="state" :test="test" :stale="stale" :testing="testing" :blocked-url="blockedUrl"
            :errors="errors.errors" :test-error="testError" @test="startTest" @cancel="cancelTest"
          />
          <SetupSaveStep
            v-else-if="current.key === 'save' && savedValues" :state="state" :form="form" :values="savedValues" :test="test" :savable="savable"
            :saving="saving" :error="saveError" @save="save"
          />
          <div class="flex flex-wrap gap-3 border-t border-border pt-4">
            <UiButton v-if="stepIndex > 1" icon="chevron_left" data-test="back" @click="goTo(stepIndex - 1)">Back</UiButton>
            <UiButton v-if="stepIndex < STEPS.length - 1" variant="primary" icon="arrow_forward" data-test="next" @click="goTo(stepIndex + 1)">Next</UiButton>
          </div>
        </template>
      </section>
    </template>
  </div>
</template>
