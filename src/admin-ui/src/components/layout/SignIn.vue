<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useBrandingStore } from '@/stores/branding'
import AppIcon from '@/components/ui/AppIcon.vue'
import UiButton from '@/components/ui/UiButton.vue'
import BrandMark from './BrandMark.vue'

// Setup mode: while setup is open the first action is the wizard; without a usable identity provider there is no sign-in to
// start (/bff/login would answer 409), so the page says what to do instead.
defineProps<{ failed?: boolean }>()
const auth = useAuthStore()
const branding = useBrandingStore()
const route = useRoute()
const router = useRouter()
/** The page to return to after signing in, without the failed sign-in's marker. */
const returnUrl = computed(() => {
  const query = Object.fromEntries(Object.entries(route.query).filter(([key]) => key !== 'signin'))
  return router.resolve({ path: route.path, query, hash: route.hash }).fullPath
})
const href = computed(() => auth.loginUrl(returnUrl.value))
const expired = computed(() => auth.user !== null && !auth.user.isAuthenticated && !!auth.user.name)
/** The admin API sends a sign-in it could not complete to `/?signin=failed`. */
const signInFailed = computed(() => route.query.signin === 'failed')
const setupOpen = computed(() => auth.user?.setup?.open === true)
/** Without an answer from /bff/user (`failed`) the sign-in button stays. */
const configured = computed(() => auth.user?.signIn?.configured !== false)
const setupHref = computed(() => router.resolve({ name: 'setup' }).href)

function openSetup(event: MouseEvent): void {
  event.preventDefault()
  void router.push({ name: 'setup' })
}
</script>

<template>
  <main id="main" tabindex="-1" class="flex min-h-screen items-center justify-center p-4 outline-none">
    <div class="material-overlay flex w-full max-w-md flex-col gap-5 rounded-dialog p-8">
      <BrandMark size="lg" />
      <div>
        <h1 id="page-title" tabindex="-1" class="text-title-2 text-fg outline-none">Sign in to AI Gateway</h1>
        <p class="mt-1 text-body text-fg-2">
          {{ branding.organisationName ? `Admin console for ${branding.organisationName}.` : 'Admin console.' }} Use your organisation account.
        </p>
      </div>
      <p v-if="failed" role="alert" class="flex gap-2 text-small text-danger"><AppIcon name="error" /> Could not check your session. Try again.</p>
      <p v-else-if="signInFailed" role="alert" class="flex gap-2 text-small text-danger" data-test="sign-in-failed">
        <AppIcon name="error" />
        <span>The sign-in could not be completed: it was interrupted or expired, the identity provider refused it, or the sign-in settings changed meanwhile. Try again.</span>
      </p>
      <p v-else-if="expired" role="status" class="flex gap-2 text-small text-fg-2"><AppIcon name="info" /> Your session has expired. Sign in again.</p>
      <template v-if="setupOpen">
        <p class="flex gap-2 text-small text-fg-2" data-test="setup-open">
          <AppIcon name="info" />
          <span>{{ configured ? 'Setup mode is open: the sign-in can be changed with the setup code from the admin API’s log.' : 'Sign-in is not set up yet. Set it up with the setup code from the admin API’s log.' }}</span>
        </p>
        <UiButton variant="primary" size="lg" icon="settings" :href="setupHref" data-test="set-up" @click="openSetup">Set up sign-in</UiButton>
        <UiButton v-if="configured" size="lg" icon="login" :href="href" data-test="sign-in">Sign in</UiButton>
      </template>
      <p v-else-if="!configured" role="status" class="flex gap-2 text-small text-fg-2" data-test="not-configured">
        <AppIcon name="info" />
        <span>
          Sign-in is not set up. The deployment must set the identity provider (<code class="font-mono">UME_OIDC_AUTHORITY</code>), or open setup mode again:
          give <code class="font-mono">UME_SETUP_REOPEN</code> a new value and restart the admin API.
        </span>
      </p>
      <UiButton v-else variant="primary" size="lg" icon="login" :href="href" data-test="sign-in">Sign in</UiButton>
    </div>
  </main>
</template>
