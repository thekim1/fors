<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { OpsProviderHealth, Route } from '@/api/types'
import PageBreadcrumb from '@/components/layout/PageBreadcrumb.vue'
import FallbackChain from '@/components/routing/FallbackChain.vue'
import ResidencyPaths from '@/components/routing/ResidencyPaths.vue'
import RouteFormDrawer from '@/components/routing/RouteFormDrawer.vue'
import RouteList from '@/components/routing/RouteList.vue'
import { buildTiers } from '@/components/routing/tiers'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import MenuButton from '@/components/ui/MenuButton.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useListDetail } from '@/composables/useListDetail'
import { useModelsStore } from '@/stores/models'
import { useRoutesStore } from '@/stores/routes'
import { useUiStore } from '@/stores/ui'
import { KIND_LABEL, MENU } from '@/utils/labels'

const routes = useRoutesStore()
const models = useModelsStore()
const ui = useUiStore()

const health = ref(new Map<string, OpsProviderHealth>())
const heading = ref<HTMLElement | null>(null)
const drawer = ref(false)
const editing = ref<Route | null>(null)
const deleting = ref(false)

// Layout audit Q1 A: the route is in the address (/routes/<name>); on a phone the list or the route, with a breadcrumb.
const { selected, narrow, detailPage, showList, select: open, listTo } = useListDetail({
  param: 'route',
  items: () => routes.items,
  keyOf: (r) => r.name,
  first: () => routes.items[0],
  ready: () => !routes.loading && routes.loaded,
  focusDetail: () => heading.value?.focus(),
})
const selectedId = computed(() => selected.value?.id ?? null)
const tiers = computed(() => (selected.value ? buildTiers(selected.value) : []))

async function loadHealth(): Promise<void> {
  try {
    const ops = await api.ops.health()
    health.value = new Map(ops.providers.map((p) => [p.name, p]))
  } catch {
    health.value = new Map()
  }
}

async function load(): Promise<void> {
  await Promise.all([routes.load(), models.load(), loadHealth()])
}

function select(id: string): void {
  const route = routes.items.find((r) => r.id === id)
  if (route) open(route.name)
}

function create(): void {
  editing.value = null
  drawer.value = true
}
function edit(): void {
  editing.value = selected.value
  drawer.value = true
}

async function remove(): Promise<void> {
  if (!selected.value) return
  await routes.remove(selected.value.id)
  ui.notify('Route deleted')
}

onMounted(load)
</script>

<template>
  <PageBreadcrumb v-if="detailPage" :items="[{ label: 'Routes', to: listTo }]" />
  <PageHeader v-else title="Routes" description="A route is the model name clients call. It falls back by priority, then splits by weight.">
    <UiButton variant="primary" icon="add" @click="create">New route</UiButton>
  </PageHeader>

  <AsyncState :loading="routes.loading" :error="routes.error" :empty="routes.items.length === 0" empty-text="No routes yet. Create one so clients have a model name to call." @retry="load">
    <div class="grid grid-cols-[minmax(240px,300px)_1fr] items-start gap-6 max-lg:grid-cols-1">
      <RouteList v-if="showList" :routes="routes.items" :selected-id="narrow ? null : selectedId" :health="health" @select="select" />

      <section v-if="selected" :aria-labelledby="detailPage ? 'page-title' : 'route-title'" class="flex min-w-0 flex-col gap-4">
        <div class="material flex flex-col gap-6 rounded-card p-6">
          <div class="flex flex-wrap items-start justify-between gap-4">
            <div class="flex min-w-0 flex-col gap-1">
              <div class="flex flex-wrap items-center gap-2">
                <component :is="detailPage ? 'h1' : 'h2'" :id="detailPage ? 'page-title' : 'route-title'" ref="heading" tabindex="-1" class="break-all font-mono text-title-2 outline-none">{{ selected.name }}</component>
                <UiBadge tone="neutral" :label="KIND_LABEL[selected.kind]" :dot="false" />
                <UiBadge v-if="!selected.isEnabled" tone="warn" label="Disabled" />
              </div>
              <span v-if="selected.description" class="text-fg-2">{{ selected.description }}</span>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              <UiButton icon="edit" @click="edit">Edit route</UiButton>
              <MenuButton :label="MENU.more(selected.name)" :items="[{ key: 'delete', label: 'Delete route…', icon: 'delete', danger: true }]" @select="deleting = true" />
            </div>
          </div>

          <div class="flex flex-col gap-3">
            <h3 class="text-heading">Fallback chain</h3>
            <FallbackChain :tiers="tiers" :health="health" />
            <p class="text-small text-fg-3">Falls back on retryable provider errors. Never falls back after a streamed response has started.</p>
          </div>
        </div>

        <div class="flex flex-col gap-3">
          <div>
            <h3 class="text-heading">Where requests land, by key data residency</h3>
            <p class="text-small text-fg-3">Targets outside a key's allowed residencies are skipped before routing.</p>
          </div>
          <ResidencyPaths :route="selected" />
        </div>
      </section>
    </div>
  </AsyncState>

  <RouteFormDrawer v-model:open="drawer" :route="editing" @saved="select($event.id)" />
  <ConfirmDialog v-model:open="deleting" title="Delete route?" confirm-label="Delete route" danger :action="remove">
    Clients that call <span class="font-mono">{{ selected?.name }}</span> get an error until you create it again.
  </ConfirmDialog>
</template>
