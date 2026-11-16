<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { problemMessage, retryAfterSeconds } from '@/utils/problem'

// Step 1: the setup code gives the setup session (a cookie). The code lives in this component only and goes nowhere but the
// request body: never into storage or an address.
const emit = defineEmits<{ (e: 'started'): void; (e: 'closed'): void }>()

/** When the admin API gives no Retry-After: its attempt limit is per minute. */
const DEFAULT_WAIT_SECONDS = 60

const code = ref('')
const error = ref<string | null>(null)
const sending = ref(false)
const waitUntil = ref(0)
const announced = ref('')
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | undefined
const waitSeconds = computed(() => Math.max(0, Math.ceil((waitUntil.value - now.value) / 1000)))

const waitText = (seconds: number) => `Too many attempts. Try again in ${seconds} ${seconds === 1 ? 'second' : 'seconds'}.`

function startWait(seconds: number): void {
  announced.value = waitText(seconds)
  waitUntil.value = Date.now() + seconds * 1000
  now.value = Date.now()
  clearInterval(clock)
  clock = setInterval(() => {
    now.value = Date.now()
    if (waitSeconds.value > 0) return
    clearInterval(clock)
    announced.value = ''
  }, 1000)
}
onBeforeUnmount(() => clearInterval(clock))

/** The error is read with the field, which gets the focus (the forms' pattern). */
function showError(message: string): void {
  error.value = message
  void nextTick(() => document.getElementById('setup-code')?.focus())
}

async function submit(): Promise<void> {
  if (sending.value || waitSeconds.value > 0) return
  const value = code.value.trim()
  if (!value) {
    showError('Enter the setup code.')
    return
  }
  sending.value = true
  error.value = null
  try {
    await api.setup.session(value)
    code.value = ''
    emit('started')
  } catch (e) {
    if (isApiError(e) && e.status === 404) {
      emit('closed')
      return
    }
    if (isApiError(e) && e.status === 429) startWait(retryAfterSeconds(e) ?? DEFAULT_WAIT_SECONDS)
    else if (isApiError(e) && e.problem?.code === 'setup_code_invalid') showError('That is not the setup code. Check it in the admin API’s log and try again.')
    else showError(problemMessage(e))
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <form class="grid gap-5" novalidate data-test="code-step" @submit.prevent="submit">
    <div class="grid gap-2 text-fg-2">
      <p>
        The admin API writes the setup code to its output when setup opens, with the time setup closes (one hour later). The line
        starts with <code class="font-mono text-small">adminapi: setup mode is open until</code>. Find it in the admin API’s log:
      </p>
      <ul class="grid list-disc gap-1 pl-5">
        <li><span class="font-medium text-fg">Portainer:</span> the log of the <code class="font-mono text-small">adminapi</code> container</li>
        <li><span class="font-medium text-fg">Docker Compose:</span> <code class="font-mono text-small">docker compose logs adminapi</code></li>
        <li><span class="font-medium text-fg">Kubernetes:</span> <code class="font-mono text-small">kubectl logs deploy/adminapi</code></li>
      </ul>
    </div>
    <TextField
      id="setup-code" v-model="code" label="Setup code" hint="Upper or lower case, with or without dashes and spaces." :error="error" mono name="setup-code"
      autocomplete="off"
    />
    <!-- The countdown is announced once, not every second. -->
    <p v-if="waitSeconds > 0" class="text-small text-warn" data-test="code-wait" aria-hidden="true">{{ waitText(waitSeconds) }}</p>
    <p role="status" class="sr-only">{{ announced }}</p>
    <div>
      <UiButton variant="primary" type="submit" :loading="sending" :disabled="waitSeconds > 0" data-test="code-submit">Continue</UiButton>
    </div>
  </form>
</template>
