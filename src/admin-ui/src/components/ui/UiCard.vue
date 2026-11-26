<script lang="ts">
import type { InjectionKey } from 'vue'

/** The level of the cards' headings below a page that groups its cards under h2 headings of its own (Settings: 3). */
export const CARD_HEADING_LEVEL: InjectionKey<2 | 3> = Symbol('cardHeadingLevel')
</script>

<script setup lang="ts">
import { inject } from 'vue'

const level = inject(CARD_HEADING_LEVEL, 2)
withDefaults(defineProps<{ title?: string; meta?: string; padding?: 'md' | 'lg' | 'none'; as?: string; headingId?: string }>(), {
  padding: 'md',
  as: 'section',
})
</script>

<template>
  <component :is="as" class="material rounded-card" :class="{ 'p-5': padding === 'md', 'p-6': padding === 'lg' }" :aria-labelledby="title ? headingId : undefined">
    <div v-if="title || $slots.actions" class="mb-4 flex flex-wrap items-center justify-between gap-4" :class="{ 'px-5 pt-5 !mb-3': padding === 'none' }">
      <component :is="`h${level}`" :id="headingId" class="text-heading">{{ title }}</component>
      <span v-if="meta" class="text-small text-fg-3">{{ meta }}</span>
      <slot name="actions" />
    </div>
    <slot />
  </component>
</template>
