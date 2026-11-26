<script setup lang="ts">
import type { KeyStatus } from '@/api/types'
import { KEY_STATUS } from '@/utils/labels'
import UiBadge from './UiBadge.vue'

defineProps<{ status: KeyStatus }>()
</script>

<!-- Layout rule 8: Active is the normal case, plain text; a badge only for a state that needs attention. -->
<template>
  <span v-if="status === 'Active'" class="text-small text-fg-2">{{ KEY_STATUS[status].label }}</span>
  <UiBadge v-else :tone="KEY_STATUS[status].tone" :label="KEY_STATUS[status].label" />
</template>
