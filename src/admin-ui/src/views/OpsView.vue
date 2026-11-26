<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { OpsProviderHealth } from '@/api/types'
import { useOpsStore } from '@/stores/ops'
import { useUiStore } from '@/stores/ui'
import { HEALTH_STATUS, PROVIDER_STATUS } from '@/utils/labels'
import { formatDateTime, formatNumber } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SummaryTile from '@/components/ui/SummaryTile.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import ConfigDialog from '@/components/ops/ConfigDialog.vue'
import ProviderCircuitTable from '@/components/ops/ProviderCircuitTable.vue'

const ops = useOpsStore()
const ui = useUiStore()

const circuitTarget = ref<OpsProviderHealth | null>(null)
const circuitOpen = ref(false)
const cacheOpen = ref(false)
const configOpen = ref(false)

const health = computed(() => ops.health)
const writer = computed(() => health.value?.usageWriter ?? null)
const isOpening = computed(() => circuitTarget.value?.circuitState !== 'Open')

// Layout rule 9: four tiles answer "is it working?"; a tile says in text what needs attention (rule 8).
const unwell = computed(() => health.value?.components.filter((c) => c.status !== 'Healthy') ?? [])
const allHealthy = computed(() => unwell.value.length === 0)
const componentsAttention = computed(() =>
  unwell.value.map((c) => `${c.name}: ${HEALTH_STATUS[c.status].label.toLowerCase()}`).join(', ') || null,
)
const writerAttention = computed(() => {
  if (!writer.value) return 'Status unavailable'
  const failures = writer.value.consecutiveFailures
  return failures > 0 ? `${formatNumber(failures)} ${failures === 1 ? 'write' : 'writes'} failed in a row` : null
})
const ATTENTION_STATUSES = new Set(['Degraded', 'Unhealthy', 'CircuitOpen'])
const troubled = computed(() => health.value?.providers.filter((p) => ATTENTION_STATUSES.has(p.status)) ?? [])
const providersAttention = computed(() =>
  troubled.value.map((p) => `${p.name}: ${PROVIDER_STATUS[p.status].label.toLowerCase()}`).join(', ') || null,
)

function reviewCircuit(provider: OpsProviderHealth): void {
  circuitTarget.value = provider
  circuitOpen.value = true
}

async function toggleCircuit(): Promise<void> {
  const provider = circuitTarget.value
  if (!provider) return
  const next = provider.circuitState === 'Open' ? 'Closed' : 'Open'
  await ops.setCircuit(provider.id, next)
  ui.notify(`Circuit ${next === 'Open' ? 'opened' : 'closed'} for ${provider.name}.`)
}

async function invalidateCache(): Promise<void> {
  await ops.invalidateKeyCache()
  ui.notify('Key cache invalidated.')
}

onMounted(() => {
  void ops.loadHealth()
})
</script>

<template>
  <PageHeader title="Health & operations" description="Component status, usage recording and provider circuits.">
    <UiButton icon="refresh" :loading="ops.healthLoading" @click="ops.loadHealth">Refresh</UiButton>
  </PageHeader>

  <AsyncState :loading="ops.healthLoading" :error="ops.healthError" :empty="!health" empty-text="No health data yet." @retry="ops.loadHealth">
    <div v-if="health" class="flex flex-col gap-6">
      <section aria-labelledby="ops-summary-h">
        <h2 id="ops-summary-h" class="sr-only">Summary</h2>
        <div class="grid grid-cols-2 gap-3 md:[grid-template-columns:repeat(auto-fit,minmax(200px,1fr))]" data-testid="ops-tiles">
          <SummaryTile
            title="Components"
            :value="allHealthy ? 'Healthy' : `${unwell.length} of ${health.components.length}`"
            :detail="allHealthy ? `All ${health.components.length} checked` : 'need attention'"
            :attention="componentsAttention"
          />
          <SummaryTile
            title="Usage writer"
            :value="writer ? `${formatNumber(writer.queueDepth)} queued` : 'Unavailable'"
            :quiet="!writer || writer.queueDepth === 0"
            :detail="writer ? (writer.lastWriteAt ? `Last write ${formatDateTime(writer.lastWriteAt, true)}` : 'No writes yet') : undefined"
            :attention="writerAttention"
          />
          <SummaryTile
            title="Providers degraded or down"
            :value="formatNumber(troubled.length)"
            :quiet="troubled.length === 0"
            :detail="`Of ${formatNumber(health.providers.length)}, last hour`"
            :attention="providersAttention"
          />
          <SummaryTile
            title="Version"
            :value="health.versions.gateway ?? health.versions.adminApi"
            :detail="`Admin API ${health.versions.adminApi} · schema ${health.versions.schema}`"
            :attention="health.versions.gateway ? null : 'Gateway version unavailable'"
          />
        </div>
      </section>

      <UiCard title="Components" :meta="`Checked ${formatDateTime(health.checkedAt, true)}`" heading-id="ops-components">
        <details v-if="allHealthy" class="text-small text-fg-2" data-testid="components-details">
          <summary class="cursor-pointer py-1 text-accent-ink">All {{ health.components.length }} components are healthy: details</summary>
          <ul class="mt-2 m-0 list-none p-0">
            <li v-for="component in health.components" :key="component.name" class="flex flex-wrap items-center justify-between gap-3 border-b border-border py-2 last:border-b-0">
              <span class="font-medium text-fg">{{ component.name }}</span>
              <span>{{ HEALTH_STATUS[component.status].label }}</span>
            </li>
          </ul>
        </details>
        <ul v-else class="m-0 list-none p-0">
          <li v-for="component in unwell" :key="component.name" class="flex flex-wrap items-center justify-between gap-3 border-b border-border py-2.5 last:border-b-0">
            <div class="min-w-0">
              <p class="font-medium">{{ component.name }}</p>
              <p v-if="component.description" class="text-small text-fg-2">{{ component.description }}</p>
            </div>
            <UiBadge :tone="HEALTH_STATUS[component.status].tone">{{ HEALTH_STATUS[component.status].label }}</UiBadge>
          </li>
        </ul>
        <p v-if="!allHealthy" class="mt-3 text-small text-fg-3">{{ health.components.length - unwell.length }} other components are healthy.</p>
      </UiCard>

      <UiCard title="Usage writer" heading-id="ops-writer">
        <dl v-if="writer" class="m-0">
          <DetailRow label="Queue">{{ formatNumber(writer.queueDepth) }} of {{ formatNumber(writer.capacity) }}</DetailRow>
          <DetailRow label="In flight">{{ formatNumber(writer.inFlightRecords) }} records</DetailRow>
          <DetailRow label="Last write">{{ writer.lastWriteAt ? formatDateTime(writer.lastWriteAt, true) : 'No writes yet' }}</DetailRow>
          <DetailRow label="Consecutive failures">
            <UiBadge v-if="writer.consecutiveFailures > 0" tone="danger">{{ formatNumber(writer.consecutiveFailures) }}</UiBadge>
            <span v-else class="text-fg-3">0</span>
          </DetailRow>
        </dl>
        <p v-else class="text-fg-2">Usage writer status is unavailable.</p>
      </UiCard>

      <section aria-labelledby="ops-providers" class="flex flex-col gap-3">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h2 id="ops-providers" class="text-heading">Providers</h2>
          <router-link :to="{ name: 'provider-stability' }" class="text-small font-medium text-accent-ink underline">Provider stability over time</router-link>
        </div>
        <ProviderCircuitTable :providers="health.providers" @toggle="reviewCircuit" />
      </section>

      <UiCard title="Maintenance" heading-id="ops-maintenance">
        <div class="flex flex-wrap gap-3">
          <UiButton icon="sync" @click="cacheOpen = true">Invalidate key cache</UiButton>
          <UiButton icon="data_object" @click="configOpen = true">Export or import configuration</UiButton>
        </div>
        <p class="mt-3 text-small text-fg-3">Invalidating the key cache makes the gateway re-read every virtual key on its next request.</p>
      </UiCard>
    </div>
  </AsyncState>

  <ConfirmDialog
    v-model:open="circuitOpen"
    :title="isOpening ? `Open circuit for ${circuitTarget?.name ?? ''}?` : `Close circuit for ${circuitTarget?.name ?? ''}?`"
    :consequence="isOpening ? 'The gateway stops sending requests to this provider and uses fallbacks until the circuit is closed.' : 'The gateway resumes sending requests to this provider.'"
    :confirm-label="isOpening ? 'Open circuit' : 'Close circuit'"
    :danger="isOpening"
    :action="toggleCircuit"
  />
  <ConfirmDialog
    v-model:open="cacheOpen"
    title="Invalidate key cache?"
    consequence="Gateway instances re-read all virtual keys on their next request, which briefly increases database load."
    confirm-label="Invalidate cache"
    :action="invalidateCache"
  />
  <ConfigDialog v-model:open="configOpen" />
</template>
