<script setup lang="ts">
import { computed } from 'vue'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { ROLE } from '@/utils/labels'
import DetailRow from '@/components/ui/DetailRow.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'

/**
 * *Your access* from the user menu (thekim1/fors#127): the roles, departments and teams of the session, from
 * `/bff/user`, and where each came from as far as Fors knows it. A department code's source is recorded
 * (`departmentSources`); a role's is not, since a role claim and a mapped role group give the same claim.
 */
defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

const auth = useAuthStore()

const roles = computed(() =>
  auth.roles.map((code) => ({ code, ...(ROLE[code as Role] ?? { label: code, description: 'Not a role Fors knows; it gives nothing.' }) })),
)
const departments = computed(() => {
  const sources = new Map((auth.user?.departmentSources ?? []).map((s) => [s.code, s.source]))
  return auth.departmentCodes.map((code) => ({
    code,
    source: sources.get(code) === 'group' ? 'a member group of the department' : 'the department claim',
  }))
})
const teams = computed(() => auth.user?.teamCodes ?? [])
</script>

<template>
  <UiDialog :open="open" title="Your access" description="What your sign-in gives you in Fors." @update:open="emit('update:open', $event)">
    <dl class="text-body" data-test="access">
      <DetailRow label="Roles">
        <ul v-if="roles.length" class="grid gap-1.5" data-test="access-roles">
          <li v-for="r in roles" :key="r.code">
            <span class="font-medium">{{ r.label }}</span> <code class="text-small text-fg-3">{{ r.code }}</code>
            <span class="block text-small text-fg-2">{{ r.description }}</span>
          </li>
        </ul>
        <span v-else data-test="access-roles">None: you see the catalogue only.</span>
      </DetailRow>
      <DetailRow label="Departments">
        <ul v-if="departments.length" class="grid gap-1" data-test="access-departments">
          <li v-for="d in departments" :key="d.code"><code>{{ d.code }}</code> <span class="text-small text-fg-2">from {{ d.source }}</span></li>
        </ul>
        <span v-else data-test="access-departments">None</span>
        <span v-if="auth.isGatewayAdmin" class="mt-1 block text-small text-fg-2">As a gateway admin you see every department.</span>
      </DetailRow>
      <DetailRow label="Teams">
        <ul v-if="teams.length" class="grid gap-1" data-test="access-teams">
          <li v-for="t in teams" :key="t"><code>{{ t }}</code> <span class="text-small text-fg-2">from the team claim</span></li>
        </ul>
        <span v-else data-test="access-teams">None</span>
      </DetailRow>
    </dl>
    <p class="text-small text-fg-2" data-test="access-source">
      All of this comes from your sign-in provider when you sign in. A role comes from its role claim or from a group the
      deployment maps to the role; Fors does not record which. A change at the provider applies when you sign in again.
    </p>
    <template #actions>
      <UiButton @click="emit('update:open', false)">Close</UiButton>
    </template>
  </UiDialog>
</template>
