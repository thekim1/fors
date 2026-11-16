<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { SetupState, SetupTest } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import SetupSecretField from './SetupSecretField.vue'
import SetupTestPanel from './SetupTestPanel.vue'
import type { SetupForm, SignInFieldName, TextFieldName } from './setupValues'

// Step 3: the provider, the client and its secret, the claims and scopes, and the test sign-in. A value the deployment sets is
// shown read-only; every value says where it comes from.
const props = defineProps<{
  state: SetupState
  test: SetupTest | null
  stale: boolean
  testing: boolean
  blockedUrl: string | null
  errors: Partial<Record<SignInFieldName, string>>
  /** Why the test could not start, when no field is to blame. */
  testError: string | null
}>()
const emit = defineEmits<{ (e: 'test'): void; (e: 'cancel'): void }>()
const form = defineModel<SetupForm>({ required: true })

const signIn = computed(() => props.state.signIn)
const LOCKED = 'Set by the deployment; change it there.'

/** A field's hint: where its value comes from, and what an empty field means. */
function hintOf(field: TextFieldName, own?: string): string | undefined {
  const value = signIn.value[field]
  if (value.locked) return LOCKED
  const source = value.source === 'wizard' ? 'Set in setup.' : value.value ? `Leave empty for the default: ${value.value}.` : undefined
  return [source, own].filter(Boolean).join(' ') || undefined
}
const placeholderOf = (field: TextFieldName) => (signIn.value[field].source === 'default' ? (signIn.value[field].value ?? undefined) : undefined)
const scopesHint = computed(() => {
  const scopes = signIn.value.extraScopes
  if (scopes.locked) return LOCKED
  const source = scopes.source === 'wizard' ? 'Set in setup. ' : ''
  return `${source}Separate scopes with spaces. openid, profile and email are always requested; add one when the provider sends the roles or groups only for a scope.`
})

const pending = computed(() => props.test?.status === 'pending')
const claimFields = ['roleClaim', 'groupClaim', 'departmentClaim', 'teamClaim', 'extraScopes'] as const
// Open when a value in it is set here or locked, or has an error, so the person sees it (and the field can take the focus).
const claimsOpen = ref(claimFields.some((field) => signIn.value[field].source !== 'default'))
watch(
  () => claimFields.some((field) => props.errors[field]),
  (error) => {
    if (error) claimsOpen.value = true
  },
)
</script>

<template>
  <form class="grid gap-5" novalidate data-test="sign-in-step" @submit.prevent="emit('test')">
    <TextField
      id="setup-authority" v-model="form.authority" label="Provider address (authority)" :error="errors.authority" :disabled="signIn.authority.locked"
      :hint="hintOf('authority', 'The issuer address, for example https://login.example.org/realms/ume or https://login.microsoftonline.com/<tenant ID>/v2.0.')"
      inputmode="url" mono
    />
    <TextField
      id="setup-clientId" v-model="form.clientId" label="Client ID" :error="errors.clientId" :disabled="signIn.clientId.locked" :hint="hintOf('clientId')"
      :placeholder="placeholderOf('clientId')" mono
    />
    <SetupSecretField v-model="form" :secret="signIn.clientSecret" :error="errors.clientSecret" />
    <details :open="claimsOpen" class="rounded-control bg-surface-2 px-3 py-2" data-test="claims" @toggle="claimsOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="cursor-pointer font-medium">Claims and scopes</summary>
      <p class="mt-1 text-small text-fg-3">Which token claims carry the roles, groups, department codes and team codes. The defaults suit the bundled Keycloak realm.</p>
      <div class="mt-4 grid gap-5 pb-2">
        <TextField
          id="setup-roleClaim" v-model="form.roleClaim" label="Role claim" :error="errors.roleClaim" :disabled="signIn.roleClaim.locked"
          :hint="hintOf('roleClaim', 'A gateway admin has gateway-admin in it.')" :placeholder="placeholderOf('roleClaim')" mono
        />
        <TextField
          id="setup-groupClaim" v-model="form.groupClaim" label="Group claim" :error="errors.groupClaim" :disabled="signIn.groupClaim.locked"
          :hint="hintOf('groupClaim', 'Groups map to roles by the deployment’s role groups, and to departments by their member groups.')"
          :placeholder="placeholderOf('groupClaim')" mono
        />
        <TextField
          id="setup-departmentClaim" v-model="form.departmentClaim" label="Department claim" :error="errors.departmentClaim"
          :disabled="signIn.departmentClaim.locked" :hint="hintOf('departmentClaim')" :placeholder="placeholderOf('departmentClaim')" mono
        />
        <TextField
          id="setup-teamClaim" v-model="form.teamClaim" label="Team claim" :error="errors.teamClaim" :disabled="signIn.teamClaim.locked"
          :hint="hintOf('teamClaim')" :placeholder="placeholderOf('teamClaim')" mono
        />
        <TextField
          id="setup-extraScopes" v-model="form.extraScopes" label="Extra scopes" :error="errors.extraScopes" :disabled="signIn.extraScopes.locked"
          :hint="scopesHint" mono
        />
      </div>
    </details>
    <div class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton variant="primary" type="submit" icon="login" :loading="testing" :disabled="pending" data-test="test">Test sign-in</UiButton>
        <span class="text-small text-fg-3">Opens the provider in a new window. Sign in there with a gateway admin’s account.</span>
      </div>
      <InlineError v-if="testError" :message="testError" data-test="test-error" />
    </div>
    <SetupTestPanel :test="test" :stale="stale" :gateway-admin-groups="state.gatewayAdminGroups" :blocked-url="blockedUrl" @cancel="emit('cancel')" />
  </form>
</template>
