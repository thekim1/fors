<script setup lang="ts">
import { computed, onMounted, ref, toRef, watch } from 'vue'
import { useRoute, useRouter, type LocationQueryRaw } from 'vue-router'
import type { Model, Provider } from '@/api/types'
import { ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { useTabQuery } from '@/composables/useTabQuery'
import { useSortedPage } from '@/composables/useTableView'
import ModelFormDrawer from '@/components/providers/ModelFormDrawer.vue'
import ProviderFormDrawer from '@/components/providers/ProviderFormDrawer.vue'
import { formatAudioPrice } from '@/components/providers/priceDraft'
import AsyncState from '@/components/ui/AsyncState.vue'
import AppIcon from '@/components/ui/AppIcon.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import Pagination from '@/components/ui/Pagination.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import ResidencyLabel from '@/components/ui/ResidencyLabel.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useModelsStore } from '@/stores/models'
import { useProvidersStore } from '@/stores/providers'
import { formatNumber, formatUsd } from '@/utils/format'
import { FILTERS, KIND_LABEL, MODEL_FEATURES, PRICE_MODE_LABEL, PROVIDER_TYPE_LABEL, RESIDENCY, featureLabel, plural } from '@/utils/labels'

const providers = useProvidersStore()
const models = useModelsStore()

const route = useRoute()
const router = useRouter()
// Layout rule 6: Providers and Models are tabs in the address (?tab=models). An older link's ?show=models opens Models.
const segment = useTabQuery(['providers', 'models'] as const)
if (route.query.show === 'models') {
  const query: LocationQueryRaw = { ...route.query, tab: 'models' }
  delete query.show
  void router.replace({ query })
}
const providerDrawer = ref(false)
const modelDrawer = ref(false)
const providerId = ref<string | null>(null)
const modelId = ref<string | null>(null)

const editingProvider = computed(() => providers.items.find((p) => p.id === providerId.value) ?? null)
const editingModel = computed(() => models.items.find((m) => m.id === modelId.value) ?? null)
const noProviders = computed(() => providers.items.length === 0)

const tabs = computed(() => [
  { value: 'providers' as const, label: `Providers (${providers.items.length})` },
  { value: 'models' as const, label: `Models (${models.items.length})` },
])

const providerColumns: Column[] = [
  { key: 'name', label: 'Provider', sortable: true },
  { key: 'type', label: 'Type', sortable: true },
  { key: 'residency', label: 'Residency', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
  { key: 'models', label: 'Models', align: 'right', sortable: true },
]
const modelColumns: Column[] = [
  { key: 'name', label: 'Model', sortable: true },
  { key: 'provider', label: 'Provider', sortable: true },
  { key: 'kind', label: 'Kind', sortable: true },
  { key: 'features', label: 'Capabilities' },
  { key: 'context', label: 'Context', align: 'right', sortable: true },
  { key: 'price', label: 'USD per 1M tokens (in / out)', align: 'right', sortable: true },
  { key: 'status', label: 'Status', sortable: true },
]

// Layout rule 2: the search and chips, kept in the address (?tab=models&kind=Chat&q=gpt). Search and status are shared by
// the two tabs; the others belong to one of them and are dropped when the other tab opens.
const PRICE_FILTERS = ['priced', 'unpriced', 'List', 'Manual', 'Unlisted']
const { filters, active, clear } = useQueryFilters(
  { q: '', status: '', type: '', residency: '', provider: '', kind: '', capability: '', price: '' },
  { status: ONE_OF(['enabled', 'disabled', 'drained']), price: ONE_OF(PRICE_FILTERS) },
)
const filterRow = ref<HTMLElement | null>(null)
const OWN: Record<'providers' | 'models', string[]> = { providers: ['type', 'residency'], models: ['provider', 'kind', 'capability', 'price'] }
// After the address shows the new tab: a change made before would be written over the tab's own change of the address.
watch(() => route.query.tab, () => {
  const other = segment.value === 'models' ? OWN.providers : OWN.models
  for (const key of other) filters[key as keyof typeof filters] = ''
  if (segment.value === 'models' && filters.status === 'drained') filters.status = ''
})
const ALL = ''
const includes = (query: string, ...values: (string | null | undefined)[]) => values.some((v) => v?.toLowerCase().includes(query))

// Providers: search, filters, sorting
const providerSearch = toRef(filters, 'q')
const providerType = toRef(filters, 'type')
const providerResidency = toRef(filters, 'residency')
const providerStatus = toRef(filters, 'status')

const providerTypeOptions = computed(() =>
  ([...new Set(providers.items.map((p) => p.type))].map((t) => ({ value: t, label: PROVIDER_TYPE_LABEL[t] })).sort((a, b) => a.label.localeCompare(b.label))),
)
const residencyOptions = computed(() =>
  ([...new Set([...providers.items.map((p) => p.residency), ...models.items.map((m) => m.residency)])].map((r) => ({ value: r, label: RESIDENCY[r].label }))),
)
const statusOptions = ([
  { value: 'enabled', label: 'Enabled' },
  { value: 'disabled', label: 'Disabled' },
  { value: 'drained', label: 'Drained' },
])

const filteredProviders = computed(() => {
  const q = providerSearch.value.trim().toLowerCase()
  return providers.items.filter((p) => {
    if (providerType.value !== ALL && p.type !== providerType.value) return false
    if (providerResidency.value !== ALL && p.residency !== providerResidency.value) return false
    if (providerStatus.value === 'enabled' && !p.isEnabled) return false
    if (providerStatus.value === 'disabled' && p.isEnabled) return false
    if (providerStatus.value === 'drained' && !p.isDrained) return false
    return !q || includes(q, p.name, p.displayName, p.baseUrl, PROVIDER_TYPE_LABEL[p.type])
  })
})
const providerView = useSortedPage(
  filteredProviders,
  {
    name: (p) => p.name,
    type: (p) => PROVIDER_TYPE_LABEL[p.type],
    residency: (p) => RESIDENCY[p.residency].label,
    status: (p) => (p.isEnabled ? (p.isDrained ? 1 : 0) : 2),
    models: (p) => p.deploymentCount,
  },
  { key: 'name' },
)
const providerPage = providerView.page
const providersFiltering = computed(() => active.value.length > 0)
const clearProviderFilters = () => clear(filterRow.value)

// Models: search, filters, sorting
const modelSearch = toRef(filters, 'q')
const modelProvider = toRef(filters, 'provider')
const modelKind = toRef(filters, 'kind')
const modelFeature = toRef(filters, 'capability')
const modelStatus = toRef(filters, 'status')
const modelPrice = toRef(filters, 'price')

const modelProviderOptions = computed(() =>
  ([...new Map(models.items.map((m) => [m.providerId, m.providerName])).entries()].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))),
)
const kindOptions = (Object.entries(KIND_LABEL).map(([value, label]) => ({ value, label })))
const featureOptions = computed(() => {
  const present = new Set(models.items.flatMap((m) => m.features))
  const known = MODEL_FEATURES.filter((f) => present.has(f))
  const other = [...present].filter((f) => !(MODEL_FEATURES as readonly string[]).includes(f)).sort()
  return ([...known, ...other].map((f) => ({ value: f, label: featureLabel(f) })))
})
const modelStatusOptions = ([
  { value: 'enabled', label: 'Enabled' },
  { value: 'disabled', label: 'Disabled' },
])
const priceOptions = ([
  { value: 'priced', label: 'Has price' },
  { value: 'unpriced', label: 'No price' },
  { value: 'List', label: 'From models.dev' },
  { value: 'Manual', label: 'Set by hand' },
  { value: 'Unlisted', label: 'Not in models.dev' },
])

const filteredModels = computed(() => {
  const q = modelSearch.value.trim().toLowerCase()
  return models.items.filter((m) => {
    if (modelProvider.value !== ALL && m.providerId !== modelProvider.value) return false
    if (modelKind.value !== ALL && m.kind !== modelKind.value) return false
    if (modelFeature.value !== ALL && !m.features.includes(modelFeature.value)) return false
    if (modelStatus.value === 'enabled' && !m.isEnabled) return false
    if (modelStatus.value === 'disabled' && m.isEnabled) return false
    if (modelPrice.value === 'priced' && !m.currentPrice) return false
    if (modelPrice.value === 'unpriced' && m.currentPrice) return false
    if (['List', 'Manual', 'Unlisted'].includes(modelPrice.value) && m.priceMode !== modelPrice.value) return false
    return !q || includes(q, m.name, m.upstreamModel, m.providerName, ...m.features)
  })
})
const modelView = useSortedPage(
  filteredModels,
  {
    name: (m) => m.name,
    provider: (m) => m.providerName,
    kind: (m) => m.kind,
    context: (m) => m.contextWindow,
    price: (m) => m.currentPrice?.inputPerMillionUsd,
    status: (m) => (m.isEnabled ? 0 : 1),
  },
  { key: 'name' },
)
const modelPage = modelView.page
const modelsFiltering = computed(() => active.value.length > 0)
const clearModelFilters = () => clear(filterRow.value)

async function load(): Promise<void> {
  await Promise.all([providers.load(), models.load()])
}

function openProvider(provider: Provider | null): void {
  providerId.value = provider?.id ?? null
  providerDrawer.value = true
}
function openModel(model: Model | null): void {
  modelId.value = model?.id ?? null
  modelDrawer.value = true
}
function create(): void {
  if (segment.value === 'providers') openProvider(null)
  else openModel(null)
}

onMounted(load)
</script>

<template>
  <PageHeader title="Providers & models" description="Providers are the services that run models. Routes send requests to their models.">
    <span v-if="segment === 'models' && noProviders" id="new-model-reason" class="text-small text-fg-3">Add a provider first.</span>
    <UiButton
      variant="primary"
      icon="add"
      :disabled="segment === 'models' && noProviders"
      :aria-describedby="segment === 'models' && noProviders ? 'new-model-reason' : undefined"
      @click="create"
    >{{ segment === 'providers' ? 'New provider' : 'New model' }}</UiButton>
  </PageHeader>

  <AsyncState :loading="providers.loading || models.loading" :error="providers.error ?? models.error" :empty="!providers.loaded && !models.loaded" @retry="load">
    <div class="flex flex-col gap-4">
      <UiTabs id="providers" v-model="segment" :tabs="tabs" label="Providers and models" />
    </div>
    <div :id="`providers-panel-${segment}`" role="tabpanel" :aria-labelledby="`providers-tab-${segment}`" class="mt-4 flex flex-col gap-4">
      <form v-if="segment === 'providers'" ref="filterRow" class="flex flex-wrap items-center gap-2" role="search" aria-label="Filter providers" data-test="filters" @submit.prevent>
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="provider-search" class="sr-only">Search providers</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="provider-search" v-model="providerSearch" type="search" autocomplete="off" placeholder="Search by name, type or URL" class="field-input w-full !pl-10" />
        </div>
        <FilterChip v-model="providerType" label="Type" :options="providerTypeOptions" any="All types" />
        <FilterChip v-model="providerResidency" label="Residency" :options="residencyOptions" any="All residencies" />
        <FilterChip v-model="providerStatus" label="Status" :options="statusOptions" any="All statuses" />
        <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clearProviderFilters">{{ FILTERS.clear }}</UiButton>
      </form>
      <form v-else ref="filterRow" class="flex flex-wrap items-center gap-2" role="search" aria-label="Filter models" data-test="filters" @submit.prevent>
        <div class="relative min-w-[240px] max-w-sm flex-1">
          <label for="model-search" class="sr-only">Search models</label>
          <AppIcon name="search" class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-fg-3" />
          <input id="model-search" v-model="modelSearch" type="search" autocomplete="off" placeholder="Search by model, upstream name, provider or capability" class="field-input w-full !pl-10" />
        </div>
        <FilterChip v-model="modelProvider" label="Provider" :options="modelProviderOptions" any="All providers" />
        <FilterChip v-model="modelKind" label="Kind" :options="kindOptions" any="All kinds" />
        <FilterChip v-model="modelFeature" label="Capability" :options="featureOptions" any="Any capability" />
        <FilterChip v-model="modelPrice" label="Price" :options="priceOptions" any="Any price" />
        <FilterChip v-model="modelStatus" label="Status" :options="modelStatusOptions" any="All statuses" />
        <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clearModelFilters">{{ FILTERS.clear }}</UiButton>
      </form>

      <DataTable
        v-if="segment === 'providers'"
        :columns="providerColumns"
        :rows="providerView.paged.value"
        :row-key="(p) => p.id"
        caption="Providers"
        clickable
        :clearable="providersFiltering"
        :sort-key="providerView.sortKey.value"
        :sort-dir="providerView.sortDir.value"
        :empty-text="providersFiltering ? 'No providers match your filters.' : 'No providers yet. Add one to start serving models.'"
        @row-click="openProvider"
        @sort="providerView.toggleSort"
        @clear="clearProviderFilters"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span v-if="row.displayName" class="text-caption text-fg-3">{{ row.displayName }}</span>
          </span>
        </template>
        <template #cell-type="{ row }">{{ PROVIDER_TYPE_LABEL[row.type] }}</template>
        <template #cell-residency="{ row }"><ResidencyLabel :residency="row.residency" /></template>
        <template #cell-status="{ row }">
          <span class="flex flex-wrap gap-1.5">
            <span v-if="row.isEnabled" class="text-small text-fg-2">Enabled</span><UiBadge v-else tone="neutral" label="Disabled" />
            <UiBadge v-if="row.isDrained" tone="neutral" label="Drained" />
          </span>
        </template>
        <template #cell-models="{ row }">{{ row.deploymentCount }}</template>
      </DataTable>

      <DataTable
        v-else
        :columns="modelColumns"
        :rows="modelView.paged.value"
        :row-key="(m) => m.id"
        caption="Models"
        clickable
        :clearable="modelsFiltering"
        :sort-key="modelView.sortKey.value"
        :sort-dir="modelView.sortDir.value"
        :empty-text="modelsFiltering ? 'No models match your filters.' : 'No models yet. Add one so routes have something to send requests to.'"
        @row-click="openModel"
        @sort="modelView.toggleSort"
        @clear="clearModelFilters"
      >
        <template #cell-name="{ row }">
          <span class="flex flex-col">
            <span class="font-mono font-medium">{{ row.name }}</span>
            <span class="font-mono text-caption text-fg-3">{{ row.upstreamModel }}</span>
            <span v-if="row.publicName !== row.name" class="text-caption text-fg-3">
              Other providers have this name; clients call <span class="font-mono">{{ row.publicName }}</span>
            </span>
          </span>
        </template>
        <template #cell-provider="{ row }">
          <span class="flex flex-col">
            <span>{{ row.providerName }}</span>
            <ResidencyLabel :residency="row.residency" />
          </span>
        </template>
        <template #cell-kind="{ row }">{{ KIND_LABEL[row.kind] }}</template>
        <template #cell-features="{ row }">
          <span v-if="row.features.length" class="flex flex-wrap gap-1"><UiBadge v-for="f in row.features" :key="f" tone="neutral" :label="featureLabel(f)" /></span>
          <span v-else class="text-fg-3">Unknown</span>
        </template>
        <template #cell-context="{ row }">{{ row.contextWindow == null ? '—' : formatNumber(row.contextWindow) }}</template>
        <template #cell-price="{ row }">
          <span v-if="row.currentPrice?.audioPerMinuteUsd">{{ formatAudioPrice(row.currentPrice.audioPerMinuteUsd) }}</span>
          <span v-else-if="row.currentPrice?.perImageUsd">{{ formatUsd(row.currentPrice.perImageUsd) }} per image</span>
          <span v-else-if="row.currentPrice">{{ formatUsd(row.currentPrice.inputPerMillionUsd) }} / {{ formatUsd(row.currentPrice.outputPerMillionUsd) }}</span>
          <span v-else class="text-fg-3">No price</span>
          <span class="block text-caption text-fg-3">{{ PRICE_MODE_LABEL[row.priceMode] }}</span>
        </template>
        <template #cell-status="{ row }"><span v-if="row.isEnabled" class="text-small text-fg-2">Enabled</span><UiBadge v-else tone="neutral" label="Disabled" /></template>
      </DataTable>

      <Pagination
        v-if="segment === 'providers' && providerView.sorted.value.length > providerView.pageSize"
        v-model:page="providerPage"
        :page-size="providerView.pageSize"
        :total="providerView.sorted.value.length"
        label="Provider pages"
      />
      <Pagination
        v-if="segment === 'models' && modelView.sorted.value.length > modelView.pageSize"
        v-model:page="modelPage"
        :page-size="modelView.pageSize"
        :total="modelView.sorted.value.length"
        label="Model pages"
      />

      <p v-if="segment === 'providers' && providers.items.length > 0" class="text-small text-fg-3">
        {{ plural(providers.items.reduce((n, p) => n + p.deploymentCount, 0), 'model') }} across {{ plural(providers.items.length, 'provider') }}.
        <template v-if="providersFiltering">Showing {{ filteredProviders.length }} of {{ providers.items.length }} providers.</template>
      </p>
      <p v-else-if="segment === 'models' && modelsFiltering && models.items.length > 0" class="text-small text-fg-3">
        Showing {{ filteredModels.length }} of {{ models.items.length }} models.
      </p>
    </div>
  </AsyncState>

  <ProviderFormDrawer v-model:open="providerDrawer" :provider="editingProvider" />
  <ModelFormDrawer v-model:open="modelDrawer" :model="editingModel" :providers="providers.items" />
</template>
