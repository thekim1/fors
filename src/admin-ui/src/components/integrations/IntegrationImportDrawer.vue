<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { ImportedKeyState, IntegrationImportResult, IntegrationPreset } from '@/api/types'
import { problemMessage } from '@/utils/problem'
import AppIcon from '@/components/ui/AppIcon.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import type { Tone } from '@/utils/labels'

/**
 * Adds an integration with all its credential keys in one step, from a manifest (docs/integrations.md): pasted, or
 * started from a preset and edited. A preview shows what would be created before anything is; the import answers each
 * new key's secret once, which the parent shows.
 */
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'imported', result: IntegrationImportResult): void
}>()

const presets = ref<IntegrationPreset[]>([])
const presetId = ref('')
const manifest = ref('')
const preview = ref<IntegrationImportResult | null>(null)
/** The manifest text the preview is of; editing it asks for a new preview before importing. */
const previewed = ref<string | null>(null)
const errors = ref<string[]>([])
const busy = ref(false)

const presetOptions = computed(() => [{ value: '', label: 'None, paste a manifest' }, ...presets.value.map((p) => ({ value: p.id, label: p.title }))])
const selectedPreset = computed(() => presets.value.find((p) => p.id === presetId.value) ?? null)
const current = computed(() => preview.value !== null && previewed.value === manifest.value)
const creates = computed(() => !!preview.value && (preview.value.createdIntegration || preview.value.keys.some((k) => k.state === 'Created')))

const outcome = computed(() => {
  const p = preview.value
  if (!p) return ''
  if (!p.createdIntegration) return 'exists; only missing keys are created.'
  // The warnings below say which team and how to move it.
  if (p.defaultOwner) return p.createdTeam ? 'will be created in a new team named after it, as the manifest names no owner.' : 'will be created in the existing team with the code made from its name, as the manifest names no owner.'
  if (p.createdDepartment) return 'will be created, with a new owner team in a new department.'
  return p.createdTeam ? 'will be created, with a new owner team.' : 'will be created.'
})

const STATE: Record<ImportedKeyState, { label: string; tone: Tone }> = {
  Created: { label: 'Will be created', tone: 'accent' },
  Existing: { label: 'Exists, unchanged', tone: 'neutral' },
  Revoked: { label: 'Revoked, not created again', tone: 'warn' },
}

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    presetId.value = ''
    manifest.value = ''
    preview.value = null
    previewed.value = null
    errors.value = []
    try {
      presets.value = await api.integrations.presets()
    } catch {
      presets.value = [] // pasting still works
    }
  },
  { immediate: true },
)

watch(presetId, (id) => {
  const preset = presets.value.find((p) => p.id === id)
  if (preset) manifest.value = preset.manifest
})

function fail(error: unknown): void {
  const listed = isApiError(error) ? (error.fieldErrors.manifest ?? error.fieldErrors.Manifest) : undefined
  errors.value = listed && listed.length > 0 ? listed : [problemMessage(error)]
}

async function runPreview(): Promise<void> {
  if (busy.value) return
  errors.value = []
  if (!manifest.value.trim()) {
    errors.value = ['Paste a manifest or choose a preset.']
    return
  }
  busy.value = true
  const text = manifest.value
  try {
    preview.value = await api.integrations.previewImport(text)
    previewed.value = text
  } catch (e) {
    preview.value = null
    fail(e)
  } finally {
    busy.value = false
  }
}

async function runImport(): Promise<void> {
  if (busy.value || !current.value) return
  busy.value = true
  try {
    const result = await api.integrations.import(manifest.value)
    emit('update:open', false)
    emit('imported', result)
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDrawer
    :open="open"
    wide
    title="Add from manifest"
    description="Creates an integration and all its credential keys in one step. Only what is missing is created; existing keys are never changed."
    @update:open="emit('update:open', $event)"
  >
    <div class="flex flex-col gap-6">
      <SelectField
        id="import-preset"
        v-model="presetId"
        label="Start from a preset"
        :options="presetOptions"
        :hint="selectedPreset?.description ?? 'Presets are manifests installed with the admin API. Edit the manifest below before you preview it, for example the owner team or an address list.'"
      />
      <TextAreaField
        id="import-manifest"
        v-model="manifest"
        label="Manifest (JSON)"
        mono
        :rows="16"
        :error="errors.length === 1 ? errors[0] : null"
        hint="The format of docs/integrations.md, “Provisioning from a manifest”."
      />
      <div v-if="errors.length > 1" role="alert" class="flex flex-col gap-1">
        <InlineError v-for="message in errors" :key="message" :message="message" />
      </div>

      <section v-if="preview" aria-labelledby="import-preview-title" class="flex flex-col gap-3" :class="current ? '' : 'opacity-60'">
        <h3 id="import-preview-title" class="text-body font-semibold">Preview{{ current ? '' : ' (out of date: preview again)' }}</h3>
        <p class="text-small text-fg-2"><span class="font-medium">{{ preview.integrationName }}</span> {{ outcome }}</p>
        <ul class="flex flex-col divide-y divide-border rounded-control border border-border">
          <li v-for="key in preview.keys" :key="key.name" class="flex items-center justify-between gap-3 px-3 py-2">
            <span class="font-mono text-small">{{ key.name }}<span v-if="key.prefix" class="text-fg-3"> · {{ key.prefix }}…</span></span>
            <UiBadge :tone="STATE[key.state].tone" :label="STATE[key.state].label" />
          </li>
        </ul>
        <ul v-if="preview.warnings.length > 0" class="flex flex-col gap-2">
          <li v-for="warning in preview.warnings" :key="warning" class="flex items-start gap-2 text-small text-fg-2">
            <AppIcon name="warning" :size="18" class="mt-px shrink-0 text-warn" />
            <span>{{ warning }}</span>
          </li>
        </ul>
      </section>
    </div>
    <template #footer>
      <UiButton icon="visibility" :loading="busy && !current" @click="runPreview">Preview</UiButton>
      <UiButton variant="primary" icon="upload" :disabled="!current || !creates" :loading="busy && current" @click="runImport">Create</UiButton>
      <UiButton :disabled="busy" @click="emit('update:open', false)">Cancel</UiButton>
      <span v-if="!current || !creates" class="text-small text-fg-3">{{ !current ? 'Preview the manifest to create it.' : 'Nothing to create: everything exists.' }}</span>
    </template>
  </UiDrawer>
</template>
