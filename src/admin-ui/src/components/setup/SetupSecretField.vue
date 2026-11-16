<script setup lang="ts">
import { computed } from 'vue'
import type { SignInSecretStatus } from '@/api/types'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import TextField from '@/components/ui/TextField.vue'
import { formatDateTime } from '@/utils/format'
import { secretChoices, type SecretChoice, type SetupForm } from './setupValues'

// The client secret: write-only, never prefilled, never shown. Keep the stored one, test as a public client (no secret; saving
// removes a stored one), or type a new one. A deployment's secret locks the field. The optional expiry date is a reminder
// that belongs to a secret kept in setup.
const props = defineProps<{ secret: SignInSecretStatus; error?: string }>()
const form = defineModel<SetupForm>({ required: true })

const LABELS: Record<SecretChoice, string> = { keep: 'Keep the stored one', public: 'No secret (public client)', typed: 'Type a new one' }
const options = computed(() => secretChoices(props.secret).map((value) => ({ value, label: LABELS[value] })))

const status = computed(() => {
  const s = props.secret
  if (s.unreadable) return 'A secret is stored, but the admin API can no longer read it. Type it again, or choose a public client.'
  if (s.source === 'wizard' && s.set) return `A secret is stored${s.setAt ? ` (set ${formatDateTime(s.setAt)})` : ''}. It is never shown.`
  return 'No secret is stored.'
})
const choiceHint = computed(() => ({
  keep: 'The test uses the stored secret. It is sent only to the provider and client it was stored for.',
  public: props.secret.set ? 'The test runs without a secret, and saving removes the stored one.' : 'The test runs without a secret.',
  typed: 'Used for the test and saved, encrypted, when you save. It is never shown again.',
})[form.value.secretChoice])
</script>

<template>
  <div class="flex flex-col gap-1.5" data-test="secret">
    <template v-if="secret.locked">
      <span class="font-medium">Client secret</span>
      <p class="text-small text-fg-3" data-test="secret-locked">Set by the deployment. The test uses it, and only with the deployment’s provider address.</p>
    </template>
    <template v-else>
      <span class="font-medium" aria-hidden="true">Client secret</span>
      <SegmentedControl v-model="form.secretChoice" mode="radio" label="Client secret" :options="options" data-test="secret-choice" />
      <span class="text-small text-fg-3" data-test="secret-status">{{ status }}</span>
      <span class="text-small text-fg-3">{{ choiceHint }}</span>
    </template>
  </div>
  <TextField
    v-if="!secret.locked && form.secretChoice === 'typed'" id="setup-clientSecret" v-model="form.clientSecret" type="password" autocomplete="new-password"
    label="New client secret" :error="error"
  />
  <TextField
    v-if="!secret.locked && form.secretChoice !== 'public'" id="setup-clientSecretExpiresOn" v-model="form.clientSecretExpiresOn" type="date"
    label="Secret expires on (optional)" hint="A reminder of when to renew it; nothing enforces it."
  />
</template>
