<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { announceTestDone } from './testWindow'

// Where the test sign-in's window ends (`/setup?test=done`, whatever happened): it tells the wizard and closes. Without an
// opener (opened from the fallback link in a browser that cut it off) it may not close itself, so it says so.
const orphan = ref(false)

onMounted(() => {
  if (announceTestDone()) window.close()
  else orphan.value = true
})
</script>

<template>
  <section class="material grid gap-2 rounded-card p-6 text-center" aria-labelledby="page-title" data-test="test-done">
    <h1 id="page-title" tabindex="-1" class="text-title-2 outline-none">Test sign-in finished</h1>
    <p class="text-fg-2">The setup wizard shows the result.</p>
    <p v-if="orphan" role="status" class="text-fg-2" data-test="test-done-close">You can close this window and go back to the setup wizard.</p>
  </section>
</template>
