<script setup lang="ts">
import { ref, watch } from 'vue'
import { useCopy } from '@/composables/useCopy'
import AppIcon from '@/components/ui/AppIcon.vue'
import CheckField from '@/components/ui/CheckField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'

/** Several new keys' secrets at once, shown only this time (like SecretDialog for one key). */
const props = defineProps<{ open: boolean; secrets: { name: string; secret: string }[] }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'done'): void }>()

const stored = ref(false)
const copiedName = ref<string | null>(null)
const { copied, copy } = useCopy()

watch(
  () => props.open,
  (open) => {
    if (open) stored.value = false
  },
)

async function copyOne(name: string, secret: string): Promise<void> {
  if (await copy(secret)) copiedName.value = name
}

function done(): void {
  if (!stored.value) return
  emit('done')
  emit('update:open', false)
}

function onOpenChange(value: boolean): void {
  if (!value && !stored.value) return
  emit('update:open', value)
}
</script>

<template>
  <UiDialog
    :open="open"
    title="Copy the secrets now"
    description="This is the only time they're shown. Each is what one of the integration's services signs in with. If you lose one, rotate that key."
    icon="key"
    tone="accent"
    wide
    :dismissible="stored"
    @update:open="onOpenChange"
  >
    <ul class="flex flex-col gap-3">
      <li v-for="s in secrets" :key="s.name">
        <span class="mb-1 block text-small font-medium">{{ s.name }}</span>
        <div class="flex items-center gap-2 rounded-control bg-sunken p-3">
          <code class="min-w-0 flex-1 break-all font-mono text-small" data-testid="secret">{{ s.secret }}</code>
          <UiButton size="sm" :icon="copied && copiedName === s.name ? 'check' : 'content_copy'" @click="copyOne(s.name, s.secret)">
            {{ copied && copiedName === s.name ? 'Copied' : 'Copy' }}<span class="sr-only"> {{ s.name }}</span>
          </UiButton>
        </div>
      </li>
    </ul>
    <span class="sr-only" role="status">{{ copied && copiedName ? `Secret of ${copiedName} copied to clipboard` : '' }}</span>
    <CheckField v-model="stored" label="I've stored the secrets somewhere safe" />
    <p v-if="!stored" class="-mt-2 flex items-center gap-1 text-small text-fg-3"><AppIcon name="info" :size="16" />Tick the box to enable Done.</p>
    <template #actions>
      <UiButton variant="primary" :disabled="!stored" @click="done">Done</UiButton>
    </template>
  </UiDialog>
</template>
