<script setup lang="ts">
import { computed } from 'vue'
import type { SetupTest } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { setupTestReasonText } from '@/utils/labels'
import { TEST_WINDOW_NAME } from './testWindow'

// The test sign-in's result: who signed in, what the token carried, and above all whether that person comes out as a gateway
// admin, with the claims that were read. `blockedUrl`: the browser blocked the window, so the person opens it from a link.
const props = defineProps<{ test: SetupTest | null; stale: boolean; gatewayAdminGroups: string[]; blockedUrl: string | null }>()
defineEmits<{ (e: 'cancel'): void }>()

const result = computed(() => props.test?.result ?? null)
const list = (values: string[]) => (values.length ? values.join(', ') : 'None')
</script>

<template>
  <div aria-live="polite" class="grid gap-3" data-test="test-panel">
    <template v-if="test?.status === 'pending'">
      <div v-if="blockedUrl" class="grid gap-2 rounded-control bg-warn-soft p-3 text-small text-warn" data-test="popup-blocked">
        <p class="flex items-center gap-2 font-medium"><AppIcon name="warning" :size="18" filled />The browser blocked the sign-in window.</p>
        <a :href="blockedUrl" :target="TEST_WINDOW_NAME" class="font-medium text-fg underline">Open the sign-in window<span class="sr-only"> (opens in a new window)</span></a>
      </div>
      <div class="flex flex-wrap items-center gap-3" data-test="test-pending">
        <p role="status" class="flex items-center gap-2 text-fg-2"><AppIcon name="progress_activity" class="animate-spin" />Waiting for the sign-in window. Sign in there as yourself.</p>
        <UiButton variant="quiet" size="sm" data-test="test-cancel" @click="$emit('cancel')">Cancel and start over</UiButton>
      </div>
    </template>
    <InlineError
      v-else-if="test?.status === 'incomplete'" tone="warn" message="The test sign-in did not finish: the window was closed or the test expired after 10 minutes. Test again."
      data-test="test-incomplete"
    />
    <template v-else-if="test">
      <InlineError v-if="stale" tone="warn" message="You changed values after this test. Test again before saving." data-test="test-stale" />
      <p v-if="test.status === 'passed'" class="flex items-center gap-2 font-medium text-ok" data-test="test-passed"><AppIcon name="check_circle" filled />The test sign-in worked</p>
      <InlineError v-else title="The test sign-in failed" :message="setupTestReasonText(test.reason)" data-test="test-failed" />
      <template v-if="result">
        <p
          class="flex items-start gap-2 rounded-control p-3 font-medium"
          :class="result.isGatewayAdmin ? 'bg-ok-soft text-ok' : 'bg-danger-soft text-danger'"
          data-test="is-admin"
        >
          <AppIcon :name="result.isGatewayAdmin ? 'check_circle' : 'block'" :size="18" filled class="mt-px" />
          {{ result.isGatewayAdmin ? `${result.name ?? result.subject} is a gateway admin.` : `${result.name ?? result.subject} is not a gateway admin, so saving is refused.` }}
        </p>
        <div v-if="!result.isGatewayAdmin" class="grid gap-1 text-small" data-test="not-admin-help">
          <p>
            Give this person the role <code class="font-mono">gateway-admin</code> at the provider, in the claim
            <code class="font-mono">{{ result.claimsRead.role }}</code>, or check the role claim under Claims and scopes. Then test again.
          </p>
          <p v-if="gatewayAdminGroups.length">
            Or add them to a group the deployment maps to gateway-admin (<span class="font-mono">{{ gatewayAdminGroups.join(', ') }}</span>), sent in the claim
            <code class="font-mono">{{ result.claimsRead.group }}</code>.
          </p>
        </div>
        <dl class="m-0" data-test="test-result">
          <DetailRow label="Name">{{ result.name ?? 'None' }}</DetailRow>
          <DetailRow label="Subject"><span class="font-mono text-small">{{ result.subject }}</span></DetailRow>
          <DetailRow label="Roles">
            {{ list(result.roles) }}
            <span class="block text-small text-fg-3" data-test="claims-read">From the role claim “{{ result.claimsRead.role }}” and the deployment’s role groups.</span>
          </DetailRow>
          <DetailRow label="Groups">
            {{ list(result.groups) }}
            <span v-if="result.groupCount > result.groups.length" class="text-small text-fg-3" data-test="groups-capped">({{ result.groups.length }} of {{ result.groupCount }} shown)</span>
            <span class="block text-small text-fg-3">From the group claim “{{ result.claimsRead.group }}”.</span>
          </DetailRow>
          <DetailRow label="Departments">{{ list(result.departmentCodes) }}</DetailRow>
          <DetailRow label="Teams">{{ list(result.teamCodes) }}</DetailRow>
        </dl>
      </template>
    </template>
  </div>
</template>
