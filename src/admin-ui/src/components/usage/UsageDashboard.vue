<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { type GatewayEndpointName, type RequestOutcome, type UsageMetrics, type UsageMetricsBucket, type UsageMetricsQuery } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useProvidersStore } from '@/stores/providers'
import { useUsageStore } from '@/stores/usage'
import { periodRange } from '@/utils/period'
import { requestsLink, type RequestsOutcomeFilter } from '@/utils/requestsLink'
import {
  formatCompact,
  formatDate,
  formatMs,
  formatNumber,
  formatRatio,
  formatShortDate,
  formatTime,
  formatTokensPerSecond,
  formatUsdCost,
  toIsoDate,
} from '@/utils/format'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CacheGaugeCard from './CacheGaugeCard.vue'
import MetricChartCard from './MetricChartCard.vue'
import type { ChartSeries } from './TimeSeriesChart.vue'

/** The filters the Usage page keeps in the address for both of its tabs (one set; see UsageView). */
export interface UsageFilters {
  period: string
  from: string
  to: string
  departmentId: string
  teamId: string
  keyId: string
  model: string
  provider: string
  outcome: string
  endpoint: string
}

const DAY_MS = 86_400_000
/** Rolling periods, ending now; *This month* and *Custom…* are whole days (periodRange). */
const PERIOD_DAYS: Record<string, number> = { '24h': 1, '7d': 7, '30d': 30, '90d': 90 }

const props = defineProps<{ filters: UsageFilters }>()
const usage = useUsageStore()
const providers = useProvidersStore()
const auth = useAuthStore()

function range(): { from: string; to: string } {
  const { period, from, to } = props.filters
  const days = PERIOD_DAYS[period]
  if (days) {
    const now = Date.now()
    return { from: new Date(now - days * DAY_MS).toISOString(), to: new Date(now).toISOString() }
  }
  const r = periodRange(period, from, to)
  return { from: new Date(`${r.from}T00:00:00`).toISOString(), to: new Date(`${r.to}T23:59:59.999`).toISOString() }
}

function load(): void {
  const f = props.filters
  const query: UsageMetricsQuery = {
    ...range(),
    ...(f.departmentId && { departmentId: f.departmentId }),
    ...(f.teamId && { teamId: f.teamId }),
    ...(f.keyId && { keyId: f.keyId }),
    ...(f.model && { model: f.model }),
    ...(f.provider && { provider: f.provider }),
    ...(f.outcome && { outcome: f.outcome as RequestOutcome }),
    ...(f.endpoint && { endpoint: f.endpoint as GatewayEndpointName }),
  }
  void usage.loadMetrics(query)
}

// The filters are the page's, in the address: any change (a chip, a link, back or forward) reloads.
watch(() => ({ ...props.filters }), load)

onMounted(() => {
  if (auth.isGatewayAdmin && !providers.loaded) void providers.load()
  load()
})

// ---- chart data ------------------------------------------------------------------------------------------

const data = computed<UsageMetrics | null>(() => usage.metrics)
const slots = computed(() => data.value?.buckets.map((b) => new Date(b.start).getTime()) ?? [])
const formatSlot = (ms: number) =>
  data.value?.bucket === 'hour' ? `${formatShortDate(new Date(ms))} ${formatTime(new Date(ms))}` : formatShortDate(new Date(ms))
const pick = (f: (b: UsageMetricsBucket) => number | null): (number | null)[] => data.value?.buckets.map(f) ?? []

/** Models keep the same colour in the cost and the requests chart; colours go by name, "other models" is grey. */
const modelColors = computed(() => {
  const named = (data.value?.models ?? []).filter((m) => m.model !== null).map((m) => m.model!).sort((a, b) => a.localeCompare(b))
  return new Map(named.map((name, i) => [name, `var(--series-${i + 1})`]))
})
function modelSeries(values: (m: UsageMetrics['models'][number]) => number[]): ChartSeries[] {
  return (data.value?.models ?? []).map((m) => ({
    id: m.model ?? '__other',
    name: m.model ?? 'Other models',
    color: m.model === null ? 'var(--border-input)' : (modelColors.value.get(m.model) ?? 'var(--border-input)'),
    values: values(m),
  }))
}
const costSeries = computed(() => modelSeries((m) => m.costUsdPerBucket))
const modelRequestSeries = computed(() => modelSeries((m) => m.requestsPerBucket))

const requestSeries = computed<ChartSeries[]>(() => [
  { id: 'success', name: 'Success', color: 'var(--ok)', values: pick((b) => b.successes) },
  { id: 'provider', name: 'Provider error', color: 'var(--danger)', values: pick((b) => b.providerErrors) },
  { id: 'refused', name: 'Refused', color: 'var(--warn)', values: pick((b) => b.refused) },
  { id: 'cancelled', name: 'Cancelled', color: 'var(--border-input)', values: pick((b) => b.cancelled) },
])
const tokenSeries = computed<ChartSeries[]>(() => [
  { id: 'input', name: 'Input', color: 'var(--series-1)', values: pick((b) => b.inputTokens - b.cachedInputTokens) },
  { id: 'cached', name: 'Cached input', color: 'var(--series-3)', values: pick((b) => b.cachedInputTokens) },
  { id: 'output', name: 'Output', color: 'var(--series-2)', values: pick((b) => b.outputTokens) },
])

/** Percentiles are one ordered measure: one hue from light to full; the average stands apart in its own hue. */
const shade = (percent: number) => `color-mix(in oklab, var(--chart) ${percent}%, var(--surface))`
function latencySeries(stats: (b: UsageMetricsBucket) => UsageMetricsBucket['latency']): ChartSeries[] {
  return [
    { id: 'avg', name: 'Avg', color: 'var(--series-2)', values: pick((b) => stats(b).avg) },
    { id: 'p50', name: 'P50', color: shade(55), values: pick((b) => stats(b).p50) },
    { id: 'p95', name: 'P95', color: shade(78), values: pick((b) => stats(b).p95) },
    { id: 'p99', name: 'P99', color: shade(100), values: pick((b) => stats(b).p99) },
  ]
}
const latency = computed(() => latencySeries((b) => b.latency))
const overhead = computed(() => latencySeries((b) => b.overhead))
const firstTokenSeries = computed<ChartSeries[]>(() => [
  { id: 'avg', name: 'Avg', color: 'var(--series-2)', values: pick((b) => b.firstTokenAvgMs) },
  { id: 'p95', name: 'P95', color: shade(100), values: pick((b) => b.firstTokenP95Ms) },
])
const throughputSeries = computed<ChartSeries[]>(() => [{ id: 'tps', name: 'Output tokens per second', color: 'var(--series-3)', values: pick((b) => b.tokensPerSecond) }])

const totals = computed(() => data.value?.totals ?? null)
const errorShare = computed(() => {
  const t = totals.value
  if (!t || t.requests === 0) return undefined
  return `${formatRatio((t.providerErrors + t.refused) / t.requests)} errors · ${formatRatio(t.fallbacks / t.requests)} fell back`
})
/** The failed requests behind the Requests card, with the dashboard's period and filters (not the endpoint: the list has no such filter). */
const failedRequests = computed(() => {
  const d = data.value
  const f = props.filters
  if (!d || d.totals.requests === d.totals.successes || f.outcome === 'Success') return null
  return requestsLink({
    from: toIsoDate(new Date(d.from)),
    to: toIsoDate(new Date(d.to)),
    departmentId: f.departmentId,
    teamId: f.teamId,
    keyId: f.keyId,
    model: f.model,
    provider: f.provider,
    outcome: (f.outcome || 'errors') as RequestsOutcomeFilter,
  })
})
const modelCount = computed(() => {
  const named = data.value?.models.filter((m) => m.model !== null).length ?? 0
  return data.value?.models.some((m) => m.model === null) ? `top ${named} models and others` : `${named} ${named === 1 ? 'model' : 'models'}`
})
const percentiles = (s: { p95: number | null; p99?: number | null } | undefined) => (s?.p95 == null ? undefined : `P95 ${formatMs(s.p95)}`)
</script>

<template>
  <div class="flex flex-col gap-4">
    <p v-if="data?.recordsFrom" class="flex items-start gap-2 text-small text-fg-2">
      <AppIcon name="info" :size="18" class="mt-px shrink-0" />
      The charts start on {{ formatDate(data.recordsFrom) }}: older usage is only kept as monthly totals (data retention, see Settings), without timings.
    </p>

    <AsyncState :loading="usage.metricsLoading" :error="usage.metricsError" :empty="!data" @retry="load">
      <div v-if="data && totals" class="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3" :class="{ 'opacity-60 transition-opacity': usage.metricsLoading }" :aria-busy="usage.metricsLoading">
        <MetricChartCard
          title="Cost"
          total-label="Total"
          :total="formatUsdCost(totals.costUsd)"
          :context="modelCount"
          :slots="slots"
          :series="costSeries"
          stacked
          :format="formatUsdCost"
          :format-slot="formatSlot"
          empty-text="No cost in this period."
        />
        <MetricChartCard
          title="Requests"
          total-label="Total"
          :total="formatNumber(totals.requests)"
          :context="errorShare"
          :slots="slots"
          :series="requestSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No requests in this period."
        >
          <template #context>
            {{ errorShare }}
            <router-link v-if="failedRequests" :to="failedRequests" class="ml-1 whitespace-nowrap font-medium text-accent-ink underline">Show failed requests</router-link>
          </template>
        </MetricChartCard>
        <MetricChartCard
          title="Tokens"
          total-label="Total"
          :total="formatCompact(totals.inputTokens + totals.outputTokens)"
          :slots="slots"
          :series="tokenSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No tokens in this period."
        />
        <CacheGaugeCard :rate="totals.cacheHitRate" :cached-tokens="totals.cachedInputTokens" :input-tokens="totals.inputTokens" />
        <MetricChartCard
          title="Requests by model"
          total-label="Total"
          :total="formatNumber(totals.requests)"
          :context="modelCount"
          :slots="slots"
          :series="modelRequestSeries"
          stacked
          :format="formatNumber"
          :format-tick="formatCompact"
          :format-slot="formatSlot"
          empty-text="No requests in this period."
        />
        <MetricChartCard
          title="Latency"
          total-label="Avg"
          :total="formatMs(totals.latency.avg)"
          :context="percentiles(totals.latency)"
          :slots="slots"
          :series="latency"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No answered requests in this period."
        />
        <MetricChartCard
          title="Gateway overhead"
          total-label="Avg"
          :total="formatMs(totals.overhead.avg)"
          :context="percentiles(totals.overhead)"
          :slots="slots"
          :series="overhead"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No timings in this period. The gateway records them for requests made after this dashboard was added."
        />
        <MetricChartCard
          title="Time to first token"
          total-label="Avg"
          :total="formatMs(totals.firstToken.avg)"
          :context="percentiles(totals.firstToken)"
          :slots="slots"
          :series="firstTokenSeries"
          kind="line"
          :format="formatMs"
          :format-slot="formatSlot"
          empty-text="No streamed answers in this period."
        />
        <MetricChartCard
          title="Throughput"
          total-label="Avg"
          :total="formatTokensPerSecond(totals.tokensPerSecond)"
          context="output tokens per second"
          :slots="slots"
          :series="throughputSeries"
          :format="formatTokensPerSecond"
          :format-tick="(v: number) => formatCompact(Math.round(v))"
          :format-slot="formatSlot"
          empty-text="No answers with output in this period."
        />
      </div>
    </AsyncState>
  </div>
</template>
