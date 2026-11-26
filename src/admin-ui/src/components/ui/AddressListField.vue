<script setup lang="ts">
import { computed, ref } from 'vue'
import { addressListError, formatAddressList, parseAddressList } from '@/utils/addresses'
import FormField from './FormField.vue'

/**
 * A list of IP addresses and CIDR networks as chips (layout rule 3, as Källa's host lists): type or paste an entry and
 * press Enter, a comma or a space, or leave the field; several pasted at once become several chips. Each chip has a
 * remove button. The model stays the list one entry per line, so callers parse it as before. An entry that does not look
 * like an address is marked on its chip; the caller's validation says why when saving.
 */
const props = defineProps<{ label: string; modelValue: string; id?: string; hint?: string; error?: string | null; placeholder?: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string): void }>()

const entries = computed(() => parseAddressList(props.modelValue))
const draft = ref('')
const input = ref<HTMLInputElement | null>(null)

function commit(): void {
  const added = parseAddressList(draft.value).filter((e) => !entries.value.includes(e))
  draft.value = ''
  if (added.length) emit('update:modelValue', formatAddressList([...entries.value, ...added]))
}

function onKey(event: KeyboardEvent): void {
  if (event.key === 'Enter' || event.key === ',' || event.key === ' ') {
    // Enter adds the entry rather than submitting the form; with nothing typed it submits as usual.
    if (event.key !== 'Enter' || draft.value.trim()) {
      event.preventDefault()
      commit()
    }
  }
}

function onInput(event: Event): void {
  draft.value = (event.target as HTMLInputElement).value
  // A paste of several entries (lines, commas or spaces) becomes chips at once.
  if (/[\s,]/.test(draft.value.trim())) commit()
}

// An input drops the line breaks of a pasted list, which would run its entries together: read the clipboard instead.
function onPaste(event: ClipboardEvent): void {
  const text = event.clipboardData?.getData('text') ?? ''
  if (!/[\s,]/.test(text.trim())) return
  event.preventDefault()
  draft.value = `${draft.value} ${text}`
  commit()
}

function remove(entry: string): void {
  emit('update:modelValue', formatAddressList(entries.value.filter((e) => e !== entry)))
  input.value?.focus()
}

const invalid = (entry: string) => addressListError([entry]) !== null
</script>

<template>
  <FormField :id="id" v-slot="{ id: fieldId, describedBy, invalid: fieldInvalid }" :label="label" :hint="hint" :error="error">
    <div
      class="field-input !h-auto flex min-h-11 flex-wrap items-center gap-1.5 py-1.5"
      :class="fieldInvalid ? 'shadow-inset-error' : ''"
      data-test="address-list"
    >
      <ul v-if="entries.length" class="contents">
        <li
          v-for="entry in entries"
          :key="entry"
          class="inline-flex max-w-full items-center gap-0.5 rounded-chip bg-sunken py-0.5 pl-2 pr-0.5 font-mono text-small"
          :class="invalid(entry) ? 'shadow-[inset_0_0_0_1px_var(--danger)] text-danger' : ''"
          data-test="address-chip"
        >
          <span class="min-w-0 [overflow-wrap:anywhere]">{{ entry }}</span>
          <span v-if="invalid(entry)" class="sr-only">(not a valid address)</span>
          <button
            type="button"
            class="grid size-6 flex-none place-items-center rounded-chip text-fg-2 hover:bg-hover hover:text-fg"
            :aria-label="`Remove ${entry}`"
            @click="remove(entry)"
          >
            <span aria-hidden="true">×</span>
          </button>
        </li>
      </ul>
      <input
        :id="fieldId"
        ref="input"
        :name="fieldId"
        :value="draft"
        :placeholder="entries.length ? undefined : placeholder"
        spellcheck="false"
        autocapitalize="off"
        autocomplete="off"
        :aria-invalid="fieldInvalid ? 'true' : undefined"
        :aria-describedby="describedBy"
        class="min-w-[12ch] flex-1 rounded-chip bg-transparent px-1 py-1 font-mono text-small"
        @input="onInput"
        @keydown="onKey"
        @paste="onPaste"
        @blur="commit"
      />
    </div>
  </FormField>
</template>
