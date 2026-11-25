<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { RetentionSettings } from '@/api/types'
import { useDirty } from '@/composables/useDirty'
import { useFormErrors } from '@/composables/useFormErrors'
import { useUiStore } from '@/stores/ui'
import { formatDate, formatDateTime, formatNumber } from '@/utils/format'
import { problemMessage } from '@/utils/problem'
import { isBlank, parseInteger } from '@/utils/validation'
import AppIcon from '@/components/ui/AppIcon.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SaveBar from '@/components/ui/SaveBar.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'

const MIN_DAYS = 90
const MAX_DAYS = 3650

const emit = defineEmits<{ (e: 'dirty', value: boolean): void }>()
const ui = useUiStore()
const ids = { usageRetentionDays: 'retention-usage', auditRetentionDays: 'retention-audit' } as const
const { errors, validate } = useFormErrors(ids)

const settings = ref<RetentionSettings | null>(null)
const loading = ref(true)
const loadError = ref<unknown>(null)
const usageDays = ref('365')
const auditDays = ref('365')
const auditForever = ref(false)
const pending = ref<{ usage: number; audit: number | null }>({ usage: 365, audit: 365 })
const confirmOpen = ref(false)
const runError = ref<unknown>(null)
const starting = ref(false)
let poll: ReturnType<typeof setTimeout> | undefined

// Layout rule 7. Keeping the audit log for ever and its days are one answer, so they count as one change.
const { changes, dirty, reset } = useDirty(() => ({ usage: usageDays.value.trim(), audit: auditForever.value ? 'forever' : auditDays.value.trim() }))
watch(dirty, (value) => emit('dirty', value))

const consequence = computed(() => {
  const { usage, audit } = pending.value
  const auditText = audit === null ? 'Audit entries are kept for ever.' : `Audit entries older than ${formatNumber(audit)} days are removed.`
  return `From the next run, usage records, refused keys and old budget alerts older than ${formatNumber(usage)} days (whole months) are removed; monthly usage totals are kept. ${auditText} Removed rows cannot be restored except from a backup.`
})

/** The saved settings into the form, as its saved state. */
function fill(value: RetentionSettings): void {
  settings.value = value
  usageDays.value = String(value.usageRetentionDays)
  auditForever.value = value.auditRetentionDays === null
  auditDays.value = String(value.auditRetentionDays ?? 365)
  reset()
}

/** A run's state only: the form keeps what is being edited. */
function update(value: RetentionSettings): void {
  if (dirty.value) settings.value = value
  else fill(value)
}

function discard(): void {
  if (settings.value) fill(settings.value)
  validate({ usageRetentionDays: null, auditRetentionDays: null })
}

async function load(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    fill(await api.settings.retention())
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

function daysError(value: string): string | null {
  const parsed = parseInteger(value)
  if (isBlank(value) || parsed === null || Number.isNaN(parsed) || parsed < MIN_DAYS || parsed > MAX_DAYS) {
    return `Enter a whole number of days from ${MIN_DAYS} to ${formatNumber(MAX_DAYS)}.`
  }
  return null
}

function review(): void {
  if (!validate({ usageRetentionDays: daysError(usageDays.value), auditRetentionDays: auditForever.value ? null : daysError(auditDays.value) })) return
  pending.value = { usage: parseInteger(usageDays.value)!, audit: auditForever.value ? null : parseInteger(auditDays.value)! }
  confirmOpen.value = true
}

async function save(): Promise<void> {
  fill(await api.settings.updateRetention(pending.value.usage, pending.value.audit))
  ui.notify('Data retention saved')
}

// While a run is going, check its state every few seconds.
function watchRun(): void {
  clearTimeout(poll)
  if (!settings.value?.running) return
  poll = setTimeout(async () => {
    try {
      const wasRunning = settings.value?.running
      update(await api.settings.retention())
      if (wasRunning && !settings.value?.running) ui.notify('Old data removed')
    } catch {
      // The next look at the page shows the state.
    }
    watchRun()
  }, 3000)
}

async function runNow(): Promise<void> {
  runError.value = null
  starting.value = true
  try {
    update({ ...(await api.settings.runRetention()), running: true })
    watchRun()
  } catch (e) {
    runError.value = e
  } finally {
    starting.value = false
  }
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(poll))
</script>

<template>
  <UiCard title="Data retention" meta="Removed nightly" heading-id="retention-h" class="max-w-[680px]">
    <AsyncState :loading="loading" :error="loadError" :empty="!settings" @retry="load">
      <div class="flex flex-col gap-3 text-fg-2">
        <p class="m-0">
          Old request data is removed every night so the database does not keep growing. Before a month’s usage records are removed, the gateway saves
          the month’s totals per key, model and provider, so costs, budgets and monthly reports stay complete.
        </p>
      </div>

      <form class="mt-4 flex flex-col items-start gap-5" novalidate @submit.prevent="review">
        <div class="grid w-full grid-cols-2 gap-4 max-md:grid-cols-1">
          <TextField
            :id="ids.usageRetentionDays"
            v-model="usageDays"
            label="Usage data, days"
            inputmode="numeric"
            short
            :error="errors.usageRetentionDays"
            hint="Usage records, refused keys and budget alerts. Whole months are removed. 90 to 3 650 days."
          />
          <div class="flex flex-col gap-2">
            <TextField
              v-if="!auditForever"
              :id="ids.auditRetentionDays"
              v-model="auditDays"
              label="Audit log, days"
              inputmode="numeric"
              short
              :error="errors.auditRetentionDays"
              hint="Who changed what in the admin UI. 90 to 3 650 days."
            />
            <CheckField v-model="auditForever" label="Keep the audit log for ever" description="Nothing is removed from the audit log." />
          </div>
        </div>

        <details class="w-full text-small text-fg-2">
          <summary class="cursor-pointer py-1 text-accent-ink">What is kept and what is removed</summary>
          <ul class="mt-2 flex list-disc flex-col gap-1.5 pl-5">
            <li><strong>Removed:</strong> each request’s details (time, key, model, tokens, cost, status, PII categories found), refused-key records, and budget alerts of periods that ended over a year ago.</li>
            <li><strong>Kept as monthly totals:</strong> requests, errors, fallbacks, tokens, audio seconds and cost per month, key, team, department, model, provider and endpoint. Budgets and the usage summary, CSV export and Data API monthly figures use them, so yearly budgets stay exact.</li>
            <li>For removed months, the <em>Usage</em> request list and <em>PII incidents</em> are empty, grouping by day shows one row per month, and the Data API daily figures and feeds no longer have those rows. Systems that read the Data API feeds must keep up within the retention period.</li>
            <li>The audit log may be covered by your archiving rules (for example a records management plan). Check before shortening it; it can also be kept for ever.</li>
            <li>Removal cannot be undone except by restoring a backup. Backups still contain removed rows until they expire.</li>
            <li>Shortening the period takes effect at the next run; lengthening it does not bring back rows already removed.</li>
          </ul>
        </details>

        <SaveBar :changes="changes" consequence="You confirm the change first; it applies from the next run" @save="review" @discard="discard" />
      </form>

      <div v-if="settings" class="mt-6 flex flex-col gap-2 border-t border-border pt-4 text-small text-fg-2">
        <p class="m-0">
          <template v-if="settings.usageRetainedFrom">Usage records are kept from {{ formatDate(settings.usageRetainedFrom) }}; earlier months are monthly totals.</template>
          <template v-else>No usage has been removed yet.</template>
          The next run removes usage before {{ formatDate(settings.nextUsageCutoff) }}<template v-if="settings.nextAuditCutoff"> and audit entries before {{ formatDate(settings.nextAuditCutoff) }}</template>.
        </p>
        <p class="m-0">
          <template v-if="settings.running">Running now…</template>
          <template v-else-if="settings.lastRunAt">
            Last run {{ formatDateTime(settings.lastRunAt) }}:
            <template v-if="settings.lastRunError"><span class="text-danger">failed ({{ settings.lastRunError }}).</span> See the admin API log; the next run continues where it stopped.</template>
            <template v-else>{{ formatNumber(settings.lastRunDeleted ?? 0) }} rows removed.</template>
          </template>
          <template v-else>It has not run yet.</template>
          <template v-if="settings.nextRunAt"> Next run {{ formatDateTime(settings.nextRunAt) }}.</template>
          <template v-else> The nightly run is switched off (<code class="font-mono">Retention__Enabled</code>).</template>
        </p>
        <InlineError v-if="runError" :message="problemMessage(runError)" />
        <div>
          <UiButton size="sm" icon="play_arrow" :loading="starting" :disabled="settings.running" @click="runNow">Run now</UiButton>
        </div>
        <p class="m-0 flex items-start gap-2 text-fg-3">
          <AppIcon name="info" :size="16" class="mt-px shrink-0" />
          Runs with the saved settings. Changes are recorded in the audit log.
        </p>
      </div>
    </AsyncState>
  </UiCard>

  <ConfirmDialog v-model:open="confirmOpen" title="Change data retention?" :consequence="consequence" confirm-label="Save retention" :action="save" />
</template>
