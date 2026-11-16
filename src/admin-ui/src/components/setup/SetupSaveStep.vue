<script setup lang="ts">
import { computed } from 'vue'
import type { SetupState, SetupTest, SignInValue, SignInValueSource, SignInWizardValues } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { SIGN_IN_SOURCE } from '@/utils/labels'
import { secretOf, type SetupForm, type TextFieldName } from './setupValues'

// Step 4: the values that are saved, each with its source, the tested person, and Save. Save needs a passed test by a
// gateway admin that still stands for the form (`canSave`); the admin API checks the same again.
const props = defineProps<{
  state: SetupState
  form: SetupForm
  /** The values that are saved: the tested ones when Save is possible, else the form's. */
  values: SignInWizardValues
  test: SetupTest | null
  savable: boolean
  saving: boolean
  error: string | null
}>()
defineEmits<{ (e: 'save'): void }>()

const FIELDS: { field: TextFieldName; label: string }[] = [
  { field: 'authority', label: 'Provider address' },
  { field: 'clientId', label: 'Client ID' },
  { field: 'roleClaim', label: 'Role claim' },
  { field: 'groupClaim', label: 'Group claim' },
  { field: 'departmentClaim', label: 'Department claim' },
  { field: 'teamClaim', label: 'Team claim' },
]

interface Row { key: string; label: string; value: string; source: SignInValueSource }

/** A value's row: the deployment's, the one set here, or the default. */
function row(key: string, label: string, own: string | null, live: SignInValue<string | null>): Row {
  if (live.locked) return { key, label, value: live.value ?? '', source: 'deployment' }
  if (own !== null) return { key, label, value: own, source: 'wizard' }
  return { key, label, value: live.source === 'default' ? (live.value ?? '') : '', source: 'default' }
}

const rows = computed<Row[]>(() => {
  const s = props.state.signIn
  const scopes = s.extraScopes
  return [
    ...FIELDS.map(({ field, label }) => row(field, label, props.values[field], s[field])),
    row('extraScopes', 'Extra scopes', props.values.extraScopes?.join(' ') ?? null, { ...scopes, value: scopes.value.join(' ') }),
  ]
})

const SECRET_TEXT = {
  typed: 'The secret typed for the test, stored encrypted',
  stored: 'The stored secret, unchanged',
  removed: 'None (public client); the stored secret is removed',
  none: 'None (public client)',
  deployment: 'Set by the deployment',
} as const

/** What happens to the secret: the test's answer when Save is possible, else what the form would send. */
const secretText = computed(() => {
  const secret = props.state.signIn.clientSecret
  if (secret.locked) return SECRET_TEXT.deployment
  if (props.savable && props.test?.clientSecret) return SECRET_TEXT[props.test.clientSecret]
  const sent = secretOf(props.form, secret)
  if (sent === '') return secret.set ? SECRET_TEXT.removed : SECRET_TEXT.none
  if (sent !== null) return SECRET_TEXT.typed
  return SECRET_TEXT.stored
})
const person = computed(() => props.test?.result ?? null)
</script>

<template>
  <div class="grid gap-6" data-test="save-step">
    <section aria-labelledby="setup-values-heading">
      <h3 id="setup-values-heading" class="text-heading">Values to save</h3>
      <p class="mt-1 text-small text-fg-3">The values the deployment sets are not stored: they keep coming from the deployment.</p>
      <dl class="m-0 mt-2" data-test="save-values">
        <DetailRow v-for="r in rows" :key="r.key" :label="r.label">
          <span class="flex flex-wrap items-center gap-2" :data-test="`save-${r.key}`">
            <span v-if="r.value" class="break-all font-mono text-small">{{ r.value }}</span>
            <UiBadge :tone="SIGN_IN_SOURCE[r.source].tone" :label="r.source === 'wizard' ? 'Set here' : SIGN_IN_SOURCE[r.source].label" :dot="false" />
          </span>
        </DetailRow>
        <DetailRow label="Client secret"><span data-test="save-secret">{{ secretText }}</span></DetailRow>
        <DetailRow v-if="values.clientSecretExpiresOn" label="Secret expires on">{{ values.clientSecretExpiresOn }}</DetailRow>
        <DetailRow label="Tested as">
          <span data-test="save-person">{{ person ? `${person.name ?? person.subject} (${person.subject})` : 'Not tested yet' }}</span>
        </DetailRow>
      </dl>
    </section>
    <div class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton
          variant="primary" size="lg" icon="check" :disabled="!savable" :loading="saving" :aria-describedby="savable ? undefined : 'setup-save-needs'"
          data-test="save" @click="$emit('save')"
        >
          Save and close setup
        </UiButton>
        <span v-if="!savable" id="setup-save-needs" class="text-small text-fg-3">
          Save needs a passed test sign-in by a gateway admin, with the values as they are now. Go back to Sign-in values to test.
        </span>
      </div>
      <p class="text-small text-fg-3">Saving closes setup mode. Afterwards these values change only when setup is opened again.</p>
      <InlineError v-if="error" :message="error" data-test="save-error" />
    </div>
  </div>
</template>
