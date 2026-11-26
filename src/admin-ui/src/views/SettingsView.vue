<script setup lang="ts">
import { nextTick, provide, reactive, ref } from 'vue'
import { useUnsavedGuard } from '@/composables/useUnsavedGuard'
import PageHeader from '@/components/ui/PageHeader.vue'
import { CARD_HEADING_LEVEL } from '@/components/ui/UiCard.vue'
import BrandingCard from '@/components/settings/BrandingCard.vue'
import RequestLogCard from '@/components/settings/RequestLogCard.vue'
import ModelPricesCard from '@/components/settings/ModelPricesCard.vue'
import RetentionCard from '@/components/settings/RetentionCard.vue'
import SignInCard from '@/components/settings/SignInCard.vue'
import TrustedProxiesCard from '@/components/settings/TrustedProxiesCard.vue'
import VersionCard from '@/components/settings/VersionCard.vue'

// Each card with fields saves through its own save bar (layout rule 7); leaving the page with any of them unsaved asks once.
const unsaved = reactive({ proxies: false, retention: false, branding: false })
useUnsavedGuard(() => Object.values(unsaved).some(Boolean))

// Rule 4: the cards are grouped by the question people come with, under h2 headings, so the cards' own headings are h3.
provide(CARD_HEADING_LEVEL, 3)
const SECTIONS = [
  { id: 'settings-network', label: 'Network' },
  { id: 'settings-data', label: 'Data' },
  { id: 'settings-prices', label: 'Prices' },
  { id: 'settings-console', label: 'This console' },
] as const
const picked = ref('')

/** Moves to a section and its heading, as a skip link does, without changing the address. */
async function go(id: string): Promise<void> {
  const heading = document.getElementById(`${id}-h`)
  if (!heading) return
  heading.scrollIntoView({ block: 'start' })
  heading.focus({ preventScroll: true })
  await nextTick()
  picked.value = ''
}
</script>

<template>
  <PageHeader title="Settings" description="Gateway-wide settings. Only gateway admins can change them." />

  <nav aria-label="Settings sections" class="mb-8 max-md:mb-6" data-test="settings-sections">
    <ul class="flex flex-wrap gap-x-5 gap-y-2 max-md:hidden">
      <li v-for="s in SECTIONS" :key="s.id">
        <a :href="`#${s.id}`" class="text-small font-medium text-accent-ink underline" @click.prevent="go(s.id)">{{ s.label }}</a>
      </li>
    </ul>
    <div class="flex items-center gap-2 md:hidden">
      <label for="settings-section" class="text-small font-medium text-fg-2">Section</label>
      <select id="settings-section" v-model="picked" class="field-input max-w-64 max-md:h-11" @change="go(picked)">
        <option value="" disabled>Go to…</option>
        <option v-for="s in SECTIONS" :key="s.id" :value="s.id">{{ s.label }}</option>
      </select>
    </div>
  </nav>

  <section :id="SECTIONS[0].id" aria-labelledby="settings-network-h" class="mb-10 flex scroll-mt-6 flex-col gap-6">
    <h2 id="settings-network-h" tabindex="-1" class="text-title-2 outline-none">Network</h2>
    <TrustedProxiesCard @dirty="unsaved.proxies = $event" />
  </section>

  <section :id="SECTIONS[1].id" aria-labelledby="settings-data-h" class="mb-10 flex scroll-mt-6 flex-col gap-6">
    <h2 id="settings-data-h" tabindex="-1" class="text-title-2 outline-none">Data</h2>
    <RetentionCard @dirty="unsaved.retention = $event" />
    <RequestLogCard />
  </section>

  <section :id="SECTIONS[2].id" aria-labelledby="settings-prices-h" class="mb-10 flex scroll-mt-6 flex-col gap-6">
    <h2 id="settings-prices-h" tabindex="-1" class="text-title-2 outline-none">Prices</h2>
    <ModelPricesCard />
  </section>

  <section :id="SECTIONS[3].id" aria-labelledby="settings-console-h" class="flex scroll-mt-6 flex-col gap-6">
    <h2 id="settings-console-h" tabindex="-1" class="text-title-2 outline-none">This console</h2>
    <BrandingCard @dirty="unsaved.branding = $event" />
    <SignInCard />
    <VersionCard />
  </section>
</template>
