<script setup lang="ts">
import { reactive } from 'vue'
import { useUnsavedGuard } from '@/composables/useUnsavedGuard'
import PageHeader from '@/components/ui/PageHeader.vue'
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
</script>

<template>
  <PageHeader title="Settings" description="Gateway-wide settings. Only gateway admins can change them." />

  <TrustedProxiesCard @dirty="unsaved.proxies = $event" />

  <div class="mt-6">
    <RetentionCard @dirty="unsaved.retention = $event" />
  </div>

  <div class="mt-6">
    <ModelPricesCard />
  </div>

  <div class="mt-6">
    <RequestLogCard />
  </div>

  <div class="mt-6">
    <BrandingCard @dirty="unsaved.branding = $event" />
  </div>

  <div class="mt-6">
    <SignInCard />
  </div>

  <div class="mt-6">
    <VersionCard />
  </div>
</template>
