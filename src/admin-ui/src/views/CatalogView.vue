<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { MODEL_KINDS, type ModelKind } from '@/api/types'
import { ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { useCatalogStore } from '@/stores/catalog'
import { CAPABILITY_LABEL, CAPABILITY_SHORT, FILTERS, KIND_LABEL, featureLabel } from '@/utils/labels'
import { formatUsdCost } from '@/utils/format'
import CodeBlock from '@/components/keys/CodeBlock.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'

const catalog = useCatalogStore()
onMounted(() => {
  void catalog.load()
})

/** The website's guide for application developers (it replaced the Getting started page). */
const guideUrl = 'https://thekim1.github.io/fors/start/'
const baseUrl = computed(() => (catalog.catalog?.gatewayBaseUrl ?? '').replace(/\/+$/, '') + '/v1')
const allRoutes = computed(() => catalog.catalog?.routes ?? [])
const allModels = computed(() => catalog.catalog?.models ?? [])

// Layout audit step 10 (rules 2 and 10): a search and a Kind chip over both tables, kept in the address (?q=&kind=).
const filterRow = ref<HTMLFormElement | null>(null)
const { filters, active, clear } = useQueryFilters({ q: '', kind: '' }, { kind: ONE_OF(MODEL_KINDS) })
const matches = (row: { name: string; description?: string | null; kind: ModelKind }): boolean => {
  if (filters.kind && row.kind !== filters.kind) return false
  const q = filters.q.trim().toLowerCase()
  return !q || row.name.toLowerCase().includes(q) || (row.description ?? '').toLowerCase().includes(q)
}
const routes = computed(() => allRoutes.value.filter(matches))
const models = computed(() => allModels.value.filter(matches))
/** Only the kinds the catalogue has, each with its count of aliases and models. */
const kindOptions = computed(() =>
  MODEL_KINDS.filter((k) => allRoutes.value.some((r) => r.kind === k) || allModels.value.some((m) => m.kind === k)).map((k) => ({
    value: k,
    label: `${KIND_LABEL[k]} (${allRoutes.value.filter((r) => r.kind === k).length + allModels.value.filter((m) => m.kind === k).length})`,
  })),
)
const clearFilters = () => clear(filterRow.value)

const routeColumns: Column[] = [
  { key: 'name', label: 'Alias' },
  { key: 'residency', label: 'Residency' },
  { key: 'capabilities', label: 'Capabilities' },
  { key: 'input', label: 'Input, USD per 1M', align: 'right' },
  { key: 'output', label: 'Output, USD per 1M', align: 'right' },
]
const modelColumns: Column[] = [
  { key: 'name', label: 'Model' },
  { key: 'residency', label: 'Residency' },
  { key: 'kind', label: 'Kind' },
  { key: 'input', label: 'Input, USD per 1M', align: 'right' },
  { key: 'output', label: 'Output, USD per 1M', align: 'right' },
]

const price = (value: number | null): string => (value === null ? '—' : formatUsdCost(value))
</script>

<template>
  <PageHeader title="Catalogue" description="Model aliases and models you can use. Prices are estimates in USD per million tokens." />
  <AsyncState :loading="catalog.loading" :error="catalog.error" :empty="!catalog.catalog" empty-text="The catalogue is empty." @retry="catalog.load(true)">
    <div class="flex flex-col gap-8">
      <section aria-labelledby="catalog-connect" class="flex flex-col gap-3">
        <h2 id="catalog-connect" class="sr-only">Connect an application</h2>
        <CodeBlock :code="baseUrl" label="Gateway base URL" />
        <p class="text-small text-fg-2">
          Use the base URL and a key from your team with any OpenAI or Anthropic SDK.
          <a class="text-accent-ink underline" :href="guideUrl" target="_blank" rel="noopener">Getting started guide<AppIcon name="open_in_new" :size="16" class="ml-1 align-text-bottom" /><span class="sr-only"> (opens in a new tab)</span></a>
        </p>
      </section>
      <form ref="filterRow" class="flex flex-wrap items-center gap-2" role="search" aria-label="Filter the catalogue" data-test="filters" @submit.prevent>
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="catalog-search" class="sr-only">Search aliases and models</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="catalog-search" v-model="filters.q" type="search" autocomplete="off" placeholder="Search by name or description" class="field-input w-full !pl-10" />
        </div>
        <FilterChip v-model="filters.kind" label="Kind" :options="kindOptions" any="All kinds" />
        <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clearFilters">{{ FILTERS.clear }}</UiButton>
      </form>

      <section aria-labelledby="catalog-routes" class="flex flex-col gap-3">
        <h2 id="catalog-routes" class="text-heading">Model aliases</h2>
        <DataTable :columns="routeColumns" :rows="routes" :row-key="(r) => r.name" caption="Model aliases" :clearable="active.length > 0" :empty-text="active.length ? 'No aliases match your filters.' : 'No aliases available.'" @clear="clearFilters">
          <template #cell-name="{ row }">
            <span class="block font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.description" class="block text-caption text-fg-3">{{ row.description }}</span>
            <span class="block text-caption text-fg-3">{{ KIND_LABEL[row.kind] }}</span>
          </template>
          <template #cell-residency="{ row }">
            <span class="flex flex-wrap gap-x-2"><ResidencyLabel v-for="r in row.residencies" :key="r" :residency="r" /></span>
          </template>
          <!-- Rule 5: short chips, the full name for assistive technology and as the title. -->
          <template #cell-capabilities="{ row }">
            <ul v-if="row.capabilities.length" class="m-0 flex list-none flex-wrap gap-1 p-0" :aria-label="`Capabilities of ${row.name}`">
              <li v-for="c in row.capabilities" :key="c"><UiBadge :dot="false" :title="CAPABILITY_LABEL[c]"><span aria-hidden="true">{{ CAPABILITY_SHORT[c] }}</span><span class="sr-only">{{ CAPABILITY_LABEL[c] }}</span></UiBadge></li>
            </ul>
            <span v-else class="text-fg-3">—</span>
          </template>
          <template #cell-input="{ row }">{{ price(row.inputUsdPerMillion) }}</template>
          <template #cell-output="{ row }">{{ price(row.outputUsdPerMillion) }}</template>
        </DataTable>
      </section>

      <section aria-labelledby="catalog-models" class="flex flex-col gap-3">
        <h2 id="catalog-models" class="text-heading">Models</h2>
        <DataTable :columns="modelColumns" :rows="models" :row-key="(m) => m.name" caption="Models" :clearable="active.length > 0" :empty-text="active.length ? 'No models match your filters.' : 'No models available.'" @clear="clearFilters">
          <template #cell-name="{ row }">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.description" class="block max-w-md text-small text-fg-2">{{ row.description }}</span>
            <span v-if="row.features?.length" class="block text-caption text-fg-3">{{ row.features.map(featureLabel).join(' · ') }}</span>
          </template>
          <template #cell-residency="{ row }"><ResidencyLabel :residency="row.residency" /></template>
          <template #cell-kind="{ row }">{{ KIND_LABEL[row.kind] }}</template>
          <template #cell-input="{ row }">{{ price(row.inputUsdPerMillion) }}</template>
          <template #cell-output="{ row }">{{ price(row.outputUsdPerMillion) }}</template>
        </DataTable>
      </section>
    </div>
  </AsyncState>
</template>
