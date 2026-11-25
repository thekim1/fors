<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { PII_CATEGORIES, type PiiAction, type PiiCategory } from '@/api/types'
import { useUsageStore } from '@/stores/usage'
import { useDepartmentsStore } from '@/stores/departments'
import { useTeamsStore } from '@/stores/teams'
import { useKeysStore } from '@/stores/keys'
import { IS_DAY, IS_ID, ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { FILTERS, OUTCOME_LABEL, PII_ACTION_LABEL, PII_CATEGORY_LABEL } from '@/utils/labels'
import { formatDateTime, formatNumber } from '@/utils/format'
import { PERIODS, periodRange } from '@/utils/period'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import Pagination from '@/components/ui/Pagination.vue'
import PeriodPicker from '@/components/ui/PeriodPicker.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

const PAGE_SIZE = 50
const ACTIONS = Object.keys(PII_ACTION_LABEL) as PiiAction[]

const usage = useUsageStore()
const departments = useDepartmentsStore()
const teams = useTeamsStore()
const keys = useKeysStore()
// Layout rule 2: the filters are one row of chips, kept in the address (`?period=7d&category=Email`), applied as they change.
const { filters, active, clear } = useQueryFilters(
  { period: '30d', from: '', to: '', department: '', team: '', key: '', category: '', action: '' },
  {
    period: ONE_OF(PERIODS), from: IS_DAY, to: IS_DAY, department: IS_ID, team: IS_ID, key: IS_ID,
    category: ONE_OF(PII_CATEGORIES), action: ONE_OF(ACTIONS),
  },
)
const filterRow = ref<HTMLElement | null>(null)
const range = computed(() => periodRange(filters.period, filters.from, filters.to))
const page = ref(1)

const departmentOptions = computed(() => departments.items.map((d) => ({ value: d.id, label: d.name })))
const teamOptions = computed(() =>
  teams.items.filter((t) => !filters.department || t.departmentId === filters.department).map((t) => ({ value: t.id, label: t.name })),
)
const keyOptions = computed(() =>
  keys.items
    .filter((k) => (!filters.department || k.departmentId === filters.department) && (!filters.team || k.teamId === filters.team))
    .map((k) => ({ value: k.id, label: k.name })),
)
const categoryOptions = PII_CATEGORIES.map((c) => ({ value: c, label: PII_CATEGORY_LABEL[c] }))
const actionOptions = ACTIONS.map((a) => ({ value: a, label: PII_ACTION_LABEL[a].label }))

// A team or key outside the chosen department (or team) no longer applies.
watch(() => filters.department, () => {
  if (filters.team && !teams.items.some((t) => t.id === filters.team && t.departmentId === filters.department)) filters.team = ''
})
watch(() => [filters.department, filters.team], () => {
  if (filters.key && !keys.items.some((k) => k.id === filters.key && (!filters.team || k.teamId === filters.team) && (!filters.department || k.departmentId === filters.department))) filters.key = ''
})

const report = computed(() => usage.piiIncidents)
const filtered = computed(() => filters.category !== '' || filters.action !== '')
const categoryTiles = computed(() =>
  PII_CATEGORIES.map((c) => {
    const row = report.value?.categories.find((x) => x.category === c)
    return { category: c, requests: row?.requests ?? 0, occurrences: row?.occurrences ?? 0 }
  }),
)
const actionTiles = computed(() => ACTIONS.map((a) => ({ action: a, requests: report.value?.actions.find((x) => x.action === a)?.requests ?? 0 })))

async function load(): Promise<void> {
  await usage.loadPiiIncidents({
    from: new Date(`${range.value.from}T00:00:00`).toISOString(),
    to: new Date(`${range.value.to}T23:59:59.999`).toISOString(),
    ...(filters.department && { departmentId: filters.department }),
    ...(filters.team && { teamId: filters.team }),
    ...(filters.key && { keyId: filters.key }),
    ...(filters.category && { category: filters.category as PiiCategory }),
    ...(filters.action && { action: filters.action as PiiAction }),
    page: page.value,
    pageSize: PAGE_SIZE,
  })
}

// A change of any filter (by a chip, a link, back or forward) starts again at the first page.
watch(() => ({ ...filters }), () => {
  page.value = 1
  void load()
})

function setPage(next: number): void {
  page.value = next
  void load()
}

function clearListFilters(): void {
  filters.category = ''
  filters.action = ''
}

const columns: Column[] = [
  { key: 'timestamp', label: 'Time' },
  { key: 'keyName', label: 'Key' },
  { key: 'categories', label: 'Found' },
  { key: 'action', label: 'Action' },
  { key: 'model', label: 'Model' },
  { key: 'outcome', label: 'Outcome' },
  { key: 'requestId', label: 'Request' },
]

onMounted(() => {
  void departments.load()
  void teams.load()
  void keys.load()
  void load()
})
</script>

<template>
  <PageHeader
    title="PII incidents"
    description="Requests in which the gateway found personal data, and what it did. Only the category and count are stored, never the values."
  />

  <form ref="filterRow" class="mb-6 flex flex-wrap items-center gap-2" role="search" aria-label="Filter incidents" data-test="filters" @submit.prevent>
    <PeriodPicker v-model:period="filters.period" v-model:from="filters.from" v-model:to="filters.to" id-prefix="pii" />
    <FilterChip v-model="filters.department" label="Department" :options="departmentOptions" any="All departments" />
    <FilterChip v-model="filters.team" label="Team" :options="teamOptions" any="All teams" />
    <FilterChip v-model="filters.key" label="Key" :options="keyOptions" any="All keys" />
    <FilterChip v-model="filters.category" label="Category" :options="categoryOptions" any="All categories" />
    <FilterChip v-model="filters.action" label="Action" :options="actionOptions" any="All actions" />
    <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clear(filterRow)">{{ FILTERS.clear }}</UiButton>
    <p class="ml-auto text-small text-fg-2 max-md:basis-full" aria-live="polite" data-test="filter-count">
      <template v-if="report">{{ formatNumber(report.totalIncidents) }} {{ report.totalIncidents === 1 ? 'request' : 'requests' }} with PII</template>
    </p>
  </form>

  <AsyncState :loading="usage.piiIncidentsLoading" :error="usage.piiIncidentsError" :empty="!report" @retry="load">
    <template v-if="report">
      <section aria-labelledby="pii-summary-h" class="mb-8">
        <h2 id="pii-summary-h" class="mb-3 text-heading">Summary</h2>
        <p class="mb-3 text-fg-2">The category and action filters apply to the list below, not to these figures.</p>
        <h3 class="mb-2 text-small font-medium text-fg-2">By category</h3>
        <div class="mb-4 grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(180px,1fr))]" data-testid="category-tiles">
          <MetricTile
            v-for="tile in categoryTiles"
            :key="tile.category"
            :label="PII_CATEGORY_LABEL[tile.category]"
            :value="formatNumber(tile.requests)"
            :context="`${formatNumber(tile.occurrences)} ${tile.occurrences === 1 ? 'value' : 'values'} found`"
          />
        </div>
        <h3 class="mb-2 text-small font-medium text-fg-2">By action</h3>
        <div class="grid gap-3 [grid-template-columns:repeat(auto-fit,minmax(180px,1fr))]" data-testid="action-tiles">
          <MetricTile v-for="tile in actionTiles" :key="tile.action" :label="PII_ACTION_LABEL[tile.action].label" :value="formatNumber(tile.requests)" />
        </div>
      </section>

      <section aria-labelledby="pii-list-h">
        <h2 id="pii-list-h" class="mb-3 text-heading">Incidents</h2>
        <p v-if="report.totalIncidents === 0" class="mb-3 rounded-card bg-surface-2 p-4 text-fg-2" data-testid="pii-explainer">
          PII scanning is set per key. A key with PII policy <strong>Off</strong> is not scanned, so its requests never show up here.
          Choose Allow (detect and record), Redact, Block or On-prem on the key to start recording incidents.
        </p>
        <DataTable
          :columns="columns"
          :rows="report.items"
          :row-key="(r) => r.requestId"
          caption="Requests with PII"
          :clearable="filtered"
          :empty-text="filtered ? 'No incidents match these filters.' : 'No PII found in this period.'"
          @clear="clearListFilters"
        >
          <template #cell-timestamp="{ row }"><span class="tabular">{{ formatDateTime(row.timestamp, true) }}</span></template>
          <template #cell-keyName="{ row }">
            <span class="font-medium">{{ row.keyName }}</span> <span class="font-mono text-caption text-fg-3">{{ row.keyPrefix }}</span>
            <span class="block text-caption text-fg-3"><span lang="sv">{{ row.teamName }}</span> · <span lang="sv">{{ row.departmentName }}</span></span>
          </template>
          <template #cell-categories="{ row }">
            <ul class="flex flex-wrap gap-1">
              <li v-for="f in row.categories" :key="f.category">
                <UiBadge :dot="false">{{ PII_CATEGORY_LABEL[f.category] }} × {{ formatNumber(f.count) }}</UiBadge>
              </li>
            </ul>
          </template>
          <template #cell-action="{ row }"><UiBadge :tone="PII_ACTION_LABEL[row.action].tone" :label="PII_ACTION_LABEL[row.action].label" /></template>
          <template #cell-model="{ row }">
            <span class="font-mono text-small">{{ row.requestedModel }}</span>
            <span class="block text-caption text-fg-3">{{ row.providerName ?? '—' }} · <span class="font-mono">{{ row.endpoint }}</span></span>
          </template>
          <template #cell-outcome="{ row }">
            <UiBadge :tone="OUTCOME_LABEL[row.outcome].tone" :label="OUTCOME_LABEL[row.outcome].label" />
            <span class="ml-2 text-caption text-fg-3">{{ row.statusCode }}</span>
          </template>
          <template #cell-requestId="{ row }">
            <RouterLink :to="{ name: 'usage', query: { request: row.requestId } }" class="font-mono text-small text-accent-ink underline">
              {{ row.requestId }}
            </RouterLink>
          </template>
        </DataTable>
        <Pagination :page="page" :page-size="PAGE_SIZE" :total="report.total" label="Incident pages" @update:page="setPage" />
      </section>
    </template>
  </AsyncState>
</template>
