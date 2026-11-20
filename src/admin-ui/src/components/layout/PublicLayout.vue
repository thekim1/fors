<script setup lang="ts">
import { useBrandingStore } from '@/stores/branding'
import BrandMark from './BrandMark.vue'

/** The frame of the pages open without a session (the setup wizard): the brand and the page, no menu and no session. */
const branding = useBrandingStore()
</script>

<template>
  <div class="min-h-screen p-4 sm:p-8">
    <header class="mx-auto flex w-full max-w-3xl items-center gap-3">
      <BrandMark />
      <span class="min-w-0">
        <span class="block truncate text-body font-semibold text-fg">Fors</span>
        <span v-if="branding.organisationName" class="block truncate text-caption text-fg-3">{{ branding.organisationName }}</span>
      </span>
    </header>
    <main id="main" tabindex="-1" class="mx-auto mt-6 w-full max-w-3xl outline-none">
      <slot />
    </main>
  </div>
</template>
