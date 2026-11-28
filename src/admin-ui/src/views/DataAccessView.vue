<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { CreateDataApiKeyResponse, DataApiKey, DataPermission } from '@/api/types'
import { useDataAccessStore } from '@/stores/dataAccess'
import { useUiStore } from '@/stores/ui'
import { DATA_PERMISSION, DOCS, MENU, plural } from '@/utils/labels'
import { formatDate, formatRelative, startOfMonthIso, todayIso } from '@/utils/format'
import DataApiKeyFormDrawer from '@/components/dataAccess/DataApiKeyFormDrawer.vue'
import CodeBlock from '@/components/keys/CodeBlock.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import KeyStatusBadge from '@/components/ui/KeyStatusBadge.vue'
import MenuButton from '@/components/ui/MenuButton.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SecretDialog from '@/components/ui/SecretDialog.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

const store = useDataAccessStore()
const ui = useUiStore()

const formOpen = ref(false)
const editing = ref<DataApiKey | null>(null)
const target = ref<DataApiKey | null>(null)
const revokeOpen = ref(false)
const deleteOpen = ref(false)
const secretOpen = ref(false)
// Kept in memory only and cleared as soon as the dialog closes.
const secret = ref('')
const createdPermissions = ref<DataPermission[]>([])

const columns: Column[] = [
  { key: 'key', label: 'Key' },
  { key: 'permissions', label: 'Data access' },
  { key: 'status', label: 'Status' },
  { key: 'dates', label: 'Created / expires' },
  { key: 'lastUsed', label: 'Last used' },
  { key: 'actions', label: 'Actions', hideLabel: true, align: 'right' },
]

const summary = computed(() => {
  const active = store.items.filter((k) => k.status === 'Active').length
  return `${plural(store.items.length, 'key')}, ${active} active. Keys give reporting and security integrations read-only access to the Data API.`
})

const baseUrl = computed(() => store.settings?.dataApiUrl ?? 'https://data-api.example.se')

/** An example request for the first permission the new key has. */
const examplePath = computed(() => {
  const first = createdPermissions.value[0] ?? 'catalog.read'
  switch (first) {
    case 'usage.aggregate':
      return `/v1/usage/aggregate?from=${startOfMonthIso()}&to=${todayIso()}&granularity=day`
    case 'usage.detail':
      return '/v1/usage/records?limit=100'
    case 'security.read':
      return '/v1/security/audit?limit=100'
    default:
      return '/v1/catalog/departments'
  }
})
// The key is read from an environment variable so it does not end up in shell history.
const curlExample = computed(() => `curl -H "Authorization: Bearer $FORS_DATA_KEY" \\\n  "${baseUrl.value}${examplePath.value}"`)
const powershellExample = computed(
  () => `Invoke-RestMethod "${baseUrl.value}${examplePath.value}" \`\n  -Headers @{ 'X-Api-Key' = $env:FORS_DATA_KEY }`,
)

function openCreate(): void {
  editing.value = null
  formOpen.value = true
}

function openEdit(key: DataApiKey): void {
  editing.value = key
  formOpen.value = true
}

function confirmRevoke(key: DataApiKey): void {
  target.value = key
  revokeOpen.value = true
}

function confirmDelete(key: DataApiKey): void {
  target.value = key
  deleteOpen.value = true
}

function onCreated(result: CreateDataApiKeyResponse): void {
  createdPermissions.value = result.key.permissions
  secret.value = result.secret
  secretOpen.value = true
}

function onSaved(key: DataApiKey): void {
  ui.notify(`${key.name} saved`)
}

async function revoke(): Promise<void> {
  if (!target.value) return
  const key = await store.revoke(target.value.id)
  ui.notify(`${key.name} revoked`)
}

async function remove(): Promise<void> {
  if (!target.value) return
  const name = target.value.name
  await store.remove(target.value.id)
  ui.notify(`${name} deleted`)
}

watch(secretOpen, (open) => {
  if (!open) secret.value = ''
})

onMounted(() => {
  void store.load()
})
onBeforeUnmount(() => {
  secret.value = ''
})
</script>

<template>
  <PageHeader title="Data API access" :description="summary">
    <UiButton variant="primary" icon="add" @click="openCreate">New Data API key</UiButton>
  </PageHeader>

  <!-- Layout audit step 10 (rule 4, "how do I connect?"): the address in a card of its own, as the Catalogue's base URL. -->
  <section v-if="store.settings" aria-labelledby="data-connect" class="mb-6 flex flex-col gap-3" data-test="data-api-address">
    <h2 id="data-connect" class="sr-only">Connect an integration</h2>
    <template v-if="store.settings.dataApiUrl">
      <CodeBlock :code="store.settings.dataApiUrl" label="Data API address" />
      <p class="text-small text-fg-2">
        Use the address with a key below, or with OAuth client credentials from your identity provider
        (<a class="text-accent-ink underline" :href="DOCS.dataAccess" target="_blank" rel="noopener">Data API guide<span class="sr-only"> (opens in a new tab)</span></a>).
        <a class="text-accent-ink underline" :href="`${store.settings.dataApiUrl}/scalar`" target="_blank" rel="noopener noreferrer">API reference<AppIcon name="open_in_new" :size="16" class="ml-1 align-text-bottom" /><span class="sr-only"> (opens in a new tab)</span></a>
      </p>
    </template>
    <p v-else class="flex flex-wrap items-center gap-2 text-small text-fg-2">
      <AppIcon name="info" :size="18" />
      The Data API address is not configured (DataApi:PublicUrl on the admin API).
      <a class="text-accent-ink underline" :href="DOCS.dataAccess" target="_blank" rel="noopener">Data API guide<span class="sr-only"> (opens in a new tab)</span></a>
    </p>
  </section>

  <AsyncState
    :loading="store.loading"
    :error="store.error"
    :empty="store.items.length === 0"
    empty-text="No Data API keys yet. Create one for a BI, reporting or SIEM integration."
    @retry="store.load()"
  >
    <DataTable :columns="columns" :rows="store.items" :row-key="(k) => k.id" caption="Data API keys">
      <template #cell-key="{ row }">
        <span class="block font-medium">{{ row.name }}</span>
        <span class="block font-mono text-caption text-fg-3">{{ row.prefix }}</span>
        <span v-if="row.description" class="block text-caption text-fg-3">{{ row.description }}</span>
      </template>
      <template #cell-permissions="{ row }">
        <span class="flex flex-wrap gap-1">
          <UiBadge v-for="p in row.permissions" :key="p" tone="accent" :dot="false" :label="DATA_PERMISSION[p as DataPermission]?.label ?? p" />
        </span>
      </template>
      <template #cell-status="{ row }"><KeyStatusBadge :status="row.status" /></template>
      <template #cell-dates="{ row }">
        <span class="block">{{ formatDate(row.createdAt) }}</span>
        <span class="block text-caption text-fg-3">{{ row.expiresAt ? `Expires ${formatDate(row.expiresAt)}` : 'No expiry' }}</span>
      </template>
      <template #cell-lastUsed="{ row }"><span :class="row.lastUsedAt ? 'text-fg-2' : 'text-fg-3'">{{ row.lastUsedAt ? formatRelative(row.lastUsedAt) : 'Never used' }}</span></template>
      <template #cell-actions="{ row }">
        <span class="flex flex-wrap justify-end gap-2">
          <UiButton v-if="row.status !== 'Revoked'" size="sm" icon="edit" @click="openEdit(row)">Edit<span class="sr-only"> {{ row.name }}</span></UiButton>
          <!-- Layout rule 1: revoking and deleting are on the row's ⋯ menu, behind their confirmations. -->
          <MenuButton
            size="sm" :label="MENU.more(row.name)"
            :items="[row.status === 'Active' ? { key: 'revoke', label: 'Revoke…', icon: 'block', danger: true } : { key: 'delete', label: MENU.delete, icon: 'delete', danger: true }]"
            @select="$event === 'revoke' ? confirmRevoke(row) : confirmDelete(row)"
          />
        </span>
      </template>
    </DataTable>
  </AsyncState>

  <DataApiKeyFormDrawer v-model:open="formOpen" :existing="editing" @created="onCreated" @saved="onSaved" />
  <ConfirmDialog
    v-model:open="revokeOpen"
    :title="`Revoke ${target?.name ?? 'key'}?`"
    consequence="The Data API refuses the key within about 30 seconds (DataApi:KeyCacheSeconds). This can’t be undone."
    confirm-label="Revoke key"
    danger
    :action="revoke"
  />
  <ConfirmDialog
    v-model:open="deleteOpen"
    :title="`Delete ${target?.name ?? 'key'}?`"
    consequence="The key disappears from this list. Its history stays in the audit log."
    confirm-label="Delete key"
    danger
    :action="remove"
  />
  <SecretDialog
    v-model:open="secretOpen"
    :secret="secret"
    description="This is the only time it’s shown. If you lose it, create a new key and revoke this one."
    @done="ui.notify('Data API key ready to use')"
  >
    <p class="m-0 text-small text-fg-2">Store it in the integration’s secret store, then send it as a bearer token or in the X-Api-Key header:</p>
    <CodeBlock label="curl (FORS_DATA_KEY holds the key)" :code="curlExample" />
    <CodeBlock label="PowerShell" :code="powershellExample" />
  </SecretDialog>
</template>
