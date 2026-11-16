<script setup lang="ts">
import type { SetupState } from '@/api/types'
import CodeBlock from '@/components/keys/CodeBlock.vue'

// Step 2: what to set up at the identity provider before the sign-in values: the client and the addresses it must allow. The
// test sign-in uses the same redirect URI, so nothing else is registered.
defineProps<{ state: SetupState }>()
</script>

<template>
  <div class="grid gap-6" data-test="provider-step">
    <div class="grid gap-2 text-fg-2">
      <p>At your identity provider (Keycloak, Entra ID, AD FS), create or open the client for the admin console:</p>
      <ul class="grid list-disc gap-1 pl-5">
        <li>An OpenID Connect client with the authorization code flow. The admin API uses PKCE, so a public client works; a confidential client also needs its client secret in the next step.</li>
        <li>Its tokens carry the person’s roles in a role claim (by default <code class="font-mono text-small">roles</code>), or their groups in a group claim (by default <code class="font-mono text-small">groups</code>).</li>
        <li>Allow the two addresses below.</li>
      </ul>
    </div>
    <div class="grid gap-4">
      <CodeBlock :code="state.redirectUris.signIn" label="Redirect URI (sign-in)" data-test="redirect-sign-in" />
      <CodeBlock :code="state.redirectUris.signOutCallback" label="Post-logout redirect URI" data-test="redirect-sign-out" />
    </div>
    <section aria-labelledby="setup-admins-heading" class="grid gap-2">
      <h3 id="setup-admins-heading" class="text-heading">Who can save</h3>
      <p class="text-fg-2">
        Saving needs a test sign-in by a person who comes out as a gateway admin: with the role
        <code class="font-mono text-small">gateway-admin</code> in the role claim<template v-if="state.gatewayAdminGroups.length">, or in one of the groups the deployment maps to it</template>.
      </p>
      <p v-if="state.gatewayAdminGroups.length" class="text-fg-2" data-test="admin-groups">
        Gateway admin groups: <span class="font-mono text-small">{{ state.gatewayAdminGroups.join(', ') }}</span>
      </p>
      <p v-else class="text-small text-fg-3" data-test="no-admin-groups">The deployment maps no groups to gateway-admin (<code class="font-mono">Oidc:RoleGroups:gateway-admin</code>).</p>
    </section>
  </div>
</template>
