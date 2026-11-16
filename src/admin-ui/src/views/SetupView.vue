<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import SetupTestDone from '@/components/setup/SetupTestDone.vue'
import SetupWizard from '@/components/setup/SetupWizard.vue'

// /setup: the setup wizard, or, at `?test=done`, the end of the test sign-in's window.
const route = useRoute()
const done = computed(() => route.query.test === 'done')
</script>

<template>
  <SetupTestDone v-if="done" />
  <SetupWizard v-else />
</template>
