<script setup lang="ts">
import { computed } from 'vue'
import { useAuthStore } from '@/stores/auth'
import { SHELL } from '@/utils/labels'
import DetailRow from '@/components/ui/DetailRow.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'

/**
 * *Your access* from the user menu (#127): the signed-in person's roles, departments and teams, and where each came
 * from. Everything here is from the session; it changes at the identity provider and shows after the next sign-in.
 */
defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()
const auth = useAuthStore()

const ROLE: Record<string, { label: string; description: string }> = {
  'gateway-admin': { label: 'Gateway admin', description: 'Everything, in every department' },
  'department-admin': { label: 'Department admin', description: 'Teams, keys and budgets in your departments' },
  viewer: { label: 'Viewer', description: 'Usage, PII incidents and the catalogue in your departments' },
  'team-admin': { label: 'Team admin', description: 'The keys of your teams' },
}
const roles = computed(() => auth.roles.map((r) => ({ key: r, ...(ROLE[r] ?? { label: r, description: '' }) })))
const departments = computed(() => {
  const sources = new Map((auth.user?.departmentSources ?? []).map((s) => [s.code, s.source]))
  return auth.departmentCodes.map((code) => ({
    code,
    source: sources.get(code) === 'group' ? 'A member group of the department' : 'The department claim of your sign-in',
  }))
})
const teams = computed(() => auth.user?.teamCodes ?? [])
</script>

<template>
  <UiDrawer :open="open" :title="SHELL.yourAccess" description="Your roles, departments and teams, and where they come from." @update:open="emit('update:open', $event)">
    <div class="flex flex-col gap-6" data-test="your-access">
      <p class="m-0 text-fg-2">
        From your sign-in at the identity provider. A change there shows here after you sign in again.
      </p>
      <section aria-labelledby="access-roles-h">
        <h3 id="access-roles-h" class="mb-2 text-heading">Roles</h3>
        <dl v-if="roles.length" class="m-0">
          <DetailRow v-for="r in roles" :key="r.key" :label="r.label">
            <span class="text-fg-2">{{ r.description }}</span>
          </DetailRow>
        </dl>
        <p v-else class="m-0 text-fg-3">No role: you can only see the catalogue.</p>
      </section>
      <section aria-labelledby="access-departments-h">
        <h3 id="access-departments-h" class="mb-2 text-heading">Departments</h3>
        <p v-if="auth.isGatewayAdmin" class="m-0 text-fg-2">Every department, as a gateway admin.</p>
        <dl v-else-if="departments.length" class="m-0">
          <DetailRow v-for="d in departments" :key="d.code" :label="d.code">
            <span class="text-fg-2">{{ d.source }}</span>
          </DetailRow>
        </dl>
        <p v-else class="m-0 text-fg-3">None</p>
      </section>
      <section v-if="teams.length" aria-labelledby="access-teams-h">
        <h3 id="access-teams-h" class="mb-2 text-heading">Teams</h3>
        <ul class="m-0 flex list-none flex-wrap gap-2 p-0">
          <li v-for="t in teams" :key="t" class="rounded-chip bg-fill-2 px-2 py-0.5 font-mono text-small">{{ t }}</li>
        </ul>
      </section>
    </div>
  </UiDrawer>
</template>
