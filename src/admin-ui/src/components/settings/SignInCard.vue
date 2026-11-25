<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { SignInSettings, SignInValueSource } from '@/api/types'
import { formatDate, formatDateTime } from '@/utils/format'
import { SIGN_IN_SOURCE } from '@/utils/labels'
import AsyncState from '@/components/ui/AsyncState.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiCard from '@/components/ui/UiCard.vue'

// The admin console's sign-in values, read only: they change only in setup mode or in the deployment, never from a session.
const settings = ref<SignInSettings | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    settings.value = await api.settings.signIn()
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}
onMounted(load)

interface Row { key: string; label: string; value: string | null; source: SignInValueSource }

const rows = computed<Row[]>(() => {
  const s = settings.value
  if (!s) return []
  return [
    { key: 'authority', label: 'Provider address', value: s.authority.value, source: s.authority.source },
    { key: 'clientId', label: 'Client ID', value: s.clientId.value, source: s.clientId.source },
    { key: 'roleClaim', label: 'Role claim', value: s.roleClaim.value, source: s.roleClaim.source },
    { key: 'groupClaim', label: 'Group claim', value: s.groupClaim.value, source: s.groupClaim.source },
    { key: 'departmentClaim', label: 'Department claim', value: s.departmentClaim.value, source: s.departmentClaim.source },
    { key: 'teamClaim', label: 'Team claim', value: s.teamClaim.value, source: s.teamClaim.source },
    { key: 'extraScopes', label: 'Extra scopes', value: s.extraScopes.value.join(' ') || null, source: s.extraScopes.source },
  ]
})

const secretText = computed(() => {
  const secret = settings.value?.clientSecret
  if (!secret) return ''
  if (secret.unreadable) return 'Stored, but the admin API cannot read it: sign-in is refused until it is set again'
  if (!secret.set) return 'None (public client)'
  if (secret.source === 'deployment') return 'Set'
  let text = 'Set'
  if (secret.setAt) text += ` on ${formatDateTime(secret.setAt)}`
  if (secret.expiresOn) text += `, expires ${formatDate(secret.expiresOn)}`
  return text
})
</script>

<template>
  <UiCard title="Sign-in" meta="Identity provider" heading-id="sign-in-h" class="max-w-3xl">
    <AsyncState :loading="loading" :error="loadError" :empty="!settings" @retry="load">
      <template v-if="settings">
        <dl class="m-0" data-test="sign-in-values">
          <DetailRow v-for="r in rows" :key="r.key" :label="r.label">
            <span class="flex flex-wrap items-center gap-2" :data-test="`sign-in-${r.key}`">
              <span v-if="r.value" class="break-all font-mono text-small">{{ r.value }}</span>
              <span v-else class="text-fg-3">Not set</span>
              <UiBadge :tone="SIGN_IN_SOURCE[r.source].tone" :label="SIGN_IN_SOURCE[r.source].label" :dot="false" />
            </span>
          </DetailRow>
          <DetailRow label="Client secret">
            <span class="flex flex-wrap items-center gap-2" data-test="sign-in-clientSecret">
              <span :class="{ 'text-danger': settings.clientSecret.unreadable }">{{ secretText }}</span>
              <UiBadge
                v-if="settings.clientSecret.set" :tone="SIGN_IN_SOURCE[settings.clientSecret.source].tone"
                :label="SIGN_IN_SOURCE[settings.clientSecret.source].label" :dot="false"
              />
            </span>
          </DetailRow>
        </dl>
        <p class="mb-0 mt-3 text-small text-fg-3">
          <template v-if="settings.updatedAt">Last changed in setup {{ formatDateTime(settings.updatedAt) }}. </template>
          These values change only in setup mode, never here. To open it again, give <code class="font-mono">Setup:Reopen</code>
          (<code class="font-mono">FORS_SETUP_REOPEN</code>) a new value, for example today’s date, and restart the admin API: setup opens for an hour,
          with a setup code in the admin API’s log, and the current sign-in keeps working meanwhile. A value set by the deployment
          (<code class="font-mono">Oidc:*</code>) always wins and is changed there.
        </p>
      </template>
    </AsyncState>
  </UiCard>
</template>
