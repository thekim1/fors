<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useDebouncedWatch } from '@/composables/useDebouncedWatch'
import { ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import type { AuditEntry } from '@/api/types'
import { useOpsStore } from '@/stores/ops'
import { formatDateTime } from '@/utils/format'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import Pagination from '@/components/ui/Pagination.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import FilterTextChip from '@/components/ui/FilterTextChip.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { FILTERS } from '@/utils/labels'

const PAGE_SIZE = 50
const ENTITY_TYPES = ['Department', 'Team', 'VirtualKey', 'DataApiKey', 'Provider', 'Model', 'Route', 'RoutingRule', 'Budget', 'Settings', 'Config', 'Usage']
const entityOptions = ENTITY_TYPES.map((t) => ({ value: t, label: t }))

const columns: Column[] = [
  { key: 'timestamp', label: 'Time' },
  { key: 'actor', label: 'Actor' },
  { key: 'action', label: 'Action' },
  { key: 'entity', label: 'Entity' },
  { key: 'details', label: 'Details' },
]

const ops = useOpsStore()
const page = ref(1)
// Layout rule 2: Type and Who are chips kept in the address (?type=Team&actor=anna), as Källa's audit log has them.
const { filters, active, clear } = useQueryFilters({ type: '', actor: '' }, { type: ONE_OF(ENTITY_TYPES) })
const filterRow = ref<HTMLElement | null>(null)

const load = () =>
  ops.loadAudit({ page: page.value, pageSize: PAGE_SIZE, entityType: filters.type || undefined, actor: filters.actor.trim() || undefined })

function reload(): void {
  page.value = 1
  void load()
}
// A type applies at once; who is typed, so it waits until the typing stops.
watch(() => filters.type, reload)
useDebouncedWatch(() => filters.actor, reload, 300)

function setPage(next: number): void {
  page.value = next
  void load()
}

function detailsText(details: unknown): string {
  return typeof details === 'string' ? details : JSON.stringify(details, null, 2)
}

/** Where and in which session the entry was made, for the details panel. */
function context(entry: AuditEntry): { label: string; value: string }[] {
  const rows: [string, string | null | undefined][] = [
    ['Roles', [entry.actorRoles, entry.actorDepartments].filter(Boolean).join(' · ')],
    ['Address', entry.sourceAddress],
    ['Session', entry.sessionId],
    ['Request', entry.requestId],
    ['Browser', entry.userAgent],
  ]
  return rows.filter((r): r is [string, string] => !!r[1]).map(([label, value]) => ({ label, value }))
}

onMounted(load)
</script>

<template>
  <PageHeader title="Audit log" description="Who changed what, and when. Metadata only." />

  <form ref="filterRow" class="mb-4 flex flex-wrap items-center gap-2" role="search" aria-label="Filter the audit log" data-test="filters" @submit.prevent>
    <FilterChip v-model="filters.type" label="Type" :options="entityOptions" any="All types" />
    <FilterTextChip v-model="filters.actor" label="Who" any="Anyone" hint="A name, e-mail or sign-in subject" />
    <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clear(filterRow)">{{ FILTERS.clear }}</UiButton>
  </form>

  <AsyncState :loading="ops.auditLoading" :error="ops.auditError" @retry="load">
    <DataTable
      :columns="columns"
      :rows="ops.audit.items"
      :row-key="(e) => e.id"
      caption="Audit log entries"
      :clearable="active.length > 0"
      empty-text="No audit entries match."
      @clear="clear(filterRow)"
    >
      <template #cell-timestamp="{ row }"><span class="tabular">{{ formatDateTime(row.timestamp, true) }}</span></template>
      <template #cell-actor="{ row }">
        <template v-if="row.actorName">{{ row.actorName }}<span class="block font-mono text-small text-fg-3">{{ row.actor }}</span></template>
        <template v-else>{{ row.actor }}</template>
      </template>
      <template #cell-action="{ row }">{{ row.action }}</template>
      <template #cell-entity="{ row }">
        {{ row.entityType }}<span v-if="row.entityId" class="font-mono text-small text-fg-3"> · {{ row.entityId }}</span>
      </template>
      <template #cell-details="{ row }">
        <details v-if="row.details || context(row).length">
          <summary class="cursor-pointer text-accent-ink">Show<span class="sr-only"> details for entry {{ row.id }}</span></summary>
          <dl v-if="context(row).length" class="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-small">
            <template v-for="item in context(row)" :key="item.label">
              <dt class="text-fg-3">{{ item.label }}</dt>
              <dd class="m-0 break-all font-mono">{{ item.value }}</dd>
            </template>
          </dl>
          <pre v-if="row.details" class="mt-2 whitespace-pre-wrap break-words font-mono text-small">{{ detailsText(row.details) }}</pre>
        </details>
        <span v-else class="text-fg-3">—</span>
      </template>
    </DataTable>
    <Pagination :page="page" :page-size="PAGE_SIZE" :total="ops.audit.total" label="Audit log pages" @update:page="setPage" />
  </AsyncState>
</template>
