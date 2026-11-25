<template>
  <div class="space-y-7">
    <div class="flex flex-wrap items-end justify-between gap-5">
      <div>
        <p class="eyebrow mb-2">
          Copilot usage
        </p>

        <h1 class="page-heading">
          Your Copilot, in perspective.
        </h1>

        <p class="mt-2 text-muted-foreground">
          Measured token usage from Visual Studio, VS Code, and the shared Copilot runtime.
        </p>
      </div>

      <Button
        variant="outline"
        :disabled="loading"
        @click="refresh"
      >
        <RefreshCw
          :size="15"
          :class="{ 'animate-spin': loading }"
        />Refresh
      </Button>
    </div>

    <div class="flex flex-wrap items-center justify-between gap-4">
      <div
        class="inline-flex rounded-lg border bg-card p-1"
        aria-label="Copilot period mode"
      >
        <button
          v-for="option in ['calendar', 'rolling']"
          :key="option"
          class="rounded-md px-4 py-2 text-sm font-medium capitalize"
          :class="mode === option ? 'bg-secondary text-foreground shadow-sm' : 'text-muted-foreground'"
          :aria-pressed="mode === option"
          @click="mode = option"
        >
          {{ option }}
        </button>
      </div>

      <div class="flex flex-wrap gap-3">
        <select
          v-model="client"
          aria-label="Filter Copilot by client"
        >
          <option value="">
            All clients
          </option>

          <option
            v-for="item in filters.clients"
            :key="item"
            :value="item"
          >
            {{ copilotClientName(item) }}
          </option>
        </select>

        <select
          v-model="model"
          aria-label="Filter Copilot by model"
        >
          <option value="">
            All models
          </option>

          <option
            v-for="item in filters.models"
            :key="item"
            :value="item"
          >
            {{ item }}
          </option>
        </select>

        <input
          v-model.lazy="timezone"
          aria-label="Copilot reporting timezone"
          class="h-10 w-44 rounded-md border bg-card px-3 text-sm"
          list="copilot-timezones"
        />

        <datalist id="copilot-timezones">
          <option>
            America/New_York
          </option>

          <option>
            America/Los_Angeles
          </option>

          <option>
            Europe/London
          </option>

          <option>
            UTC
          </option>
        </datalist>
      </div>
    </div>

    <p
      v-if="error"
      role="alert"
      class="error"
    >
      {{ error }}
    </p>

    <p
      v-if="loading && !report"
      role="status"
      class="subtle"
    >
      Loading Copilot usage…
    </p>

    <template v-if="report">
      <CopilotIndexNotice
        :status="report.index"
        :timezone="report.timezone"
      />

      <div
        v-if="hasMeasuredUsage"
        class="grid gap-5 md:grid-cols-3"
      >
        <UsageCard
          v-for="period in report.periods"
          :key="period.label"
          :period="period"
        />
      </div>

      <section
        v-else
        class="panel p-6"
        aria-label="Copilot usage unavailable"
      >
        <h2 class="text-lg font-semibold">
          {{ report.index.initialScanComplete ? 'No measured usage available' : 'Copilot usage is not ready yet' }}
        </h2>

        <p class="subtle mt-2">
          {{ report.index.initialScanComplete ? 'No measured calls match these filters. This does not mean no tokens were used.' : 'Waiting for the source scan before displaying totals.' }}
        </p>

        <RouterLink
          to="/settings"
          class="mt-3 inline-block text-sm text-primary underline"
        >Manage Copilot sources</RouterLink>
      </section>

      <section
        class="panel p-5 sm:p-6"
        aria-labelledby="copilot-trend-title"
      >
        <div class="mb-6 flex flex-wrap items-start justify-between gap-4">
          <div>
            <h2
              id="copilot-trend-title"
              class="text-lg font-semibold"
            >
              Copilot usage over time
            </h2>

            <p class="subtle mt-1">
              Daily totals from observed calls in the selected timezone.
            </p>
          </div>

          <div class="inline-flex rounded-lg bg-muted p-1">
            <button
              v-for="item in (['tokens', 'cost'] as const)"
              :key="item"
              class="rounded-md px-3 py-1.5 text-sm"
              :class="metric === item ? 'bg-card font-medium shadow-sm' : 'text-muted-foreground'"
              :aria-pressed="metric === item"
              @click="metric = item"
            >
              {{ item === 'tokens' ? 'Tokens' : 'Cost (USD)' }}
            </button>
          </div>
        </div>

        <div class="mb-5 flex flex-wrap items-center gap-3 text-sm">
          <CalendarDays
            :size="16"
            class="text-muted-foreground"
          />

          <label
            for="copilot-trend-from"
            class="sr-only"
          >
            Copilot trend start date
          </label>

          <input
            id="copilot-trend-from"
            v-model="range.from"
            type="date"
            class="rounded-md border bg-background px-3 py-1.5"
            :max="range.to"
          />

          <span class="text-muted-foreground">to</span>

          <label
            for="copilot-trend-to"
            class="sr-only"
          >
            Copilot trend end date
          </label>

          <input
            id="copilot-trend-to"
            v-model="range.to"
            type="date"
            class="rounded-md border bg-background px-3 py-1.5"
            :min="range.from"
          />
        </div>

        <UsageChart
          v-if="hasMeasuredUsage"
          :buckets="report.buckets"
          :metric="metric"
        />

        <p
          v-else
          class="py-12 text-center text-sm text-muted-foreground"
        >
          The chart will appear when measured usage is available.
        </p>
      </section>

      <section
        v-if="hasMeasuredUsage && latestTokens"
        class="panel p-6"
        aria-labelledby="copilot-breakdown-title"
      >
        <h2
          id="copilot-breakdown-title"
          class="text-lg font-semibold"
        >
          Reported token breakdown
        </h2>

        <p class="subtle mt-1">
          {{ report.periods.at(-1)?.label }}. Cache tokens are portions of input, and reasoning tokens are included in output.
        </p>

        <dl class="mt-5 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <dt class="subtle">
              Input
            </dt>

            <dd class="tabular mt-1 text-xl font-semibold">
              {{ number(latestTokens.inputTokens) }}
            </dd>
          </div>

          <div>
            <dt class="subtle">
              Known cache reads
            </dt>

            <dd class="tabular mt-1 text-xl font-semibold">
              {{ cacheValue(latestTokens.cachedInputTokens, report.unknownCacheReadRecords) }}
            </dd>
          </div>

          <div>
            <dt class="subtle">
              Known cache writes
            </dt>

            <dd class="tabular mt-1 text-xl font-semibold">
              {{ cacheValue(latestTokens.cacheWriteInputTokens, report.unknownCacheWriteRecords) }}
            </dd>
          </div>

          <div>
            <dt class="subtle">
              Output
            </dt>

            <dd class="tabular mt-1 text-xl font-semibold">
              {{ number(latestTokens.outputTokens) }}
            </dd>
          </div>
        </dl>

        <p
          v-if="report.unknownCacheReadRecords || report.unknownCacheWriteRecords"
          class="mt-4 text-xs text-amber-700 dark:text-amber-400"
        >
          Across matching records, {{ number(report.unknownCacheReadRecords) }} calls omit cache reads and {{ number(report.unknownCacheWriteRecords) }} omit cache writes. Unknown values are excluded from the reported breakdown.
        </p>
      </section>

      <section
        class="rounded-lg border p-4 text-xs leading-relaxed text-muted-foreground"
        aria-label="Copilot pricing assumptions"
      >
        <p>
          Estimated API-equivalent cost (USD), using catalog {{ report.pricingVersion }}. These estimates are separate from your Copilot bill, AI Credits, premium requests, and subscription allowances. Missing rates remain unpriced.
        </p>

        <ul
          v-if="report.pricingAssumptions.length"
          class="mt-3 list-disc space-y-1 pl-5"
        >
          <li
            v-for="assumption in report.pricingAssumptions"
            :key="assumption"
          >
            {{ assumption }}
          </li>
        </ul>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { CalendarDays, RefreshCw } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import UsageCard from '@/components/UsageCard.vue'
import UsageChart from '@/components/UsageChart.vue'
import CopilotIndexNotice from '@/components/CopilotIndexNotice.vue'
import { api } from '@/api/client'
import { copilotApi, copilotClientName, type CopilotFilterOptions, type CopilotUsageReport } from '@/api/copilot'
import { useLoader } from '@/composables/useLoader'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { dateBounds, dateRange, number } from '@/lib/format'

const route = useRoute()
const router = useRouter()
const mode = ref(localStorage.getItem('copilot-period-mode') === 'rolling' ? 'rolling' : 'calendar')
const timezone = ref('America/New_York')
const client = ref(String(route.query.client ?? ''))
const model = ref(String(route.query.model ?? ''))
const range = ref(dateRange())
const metric = ref<'tokens' | 'cost'>('tokens')
const filters = ref<CopilotFilterOptions>({ clients: ['visual-studio', 'vscode', 'shared'], models: [] })
const { data: report, error, loading, load } = useLoader<CopilotUsageReport>()
const hasMeasuredUsage = computed(() => (report.value?.matchingRecordCount ?? 0) > 0)
const latestTokens = computed(() => report.value?.periods.at(-1)?.totals.tokens)
const cacheValue = (value: number, unknownRecords: number) => value === 0 && unknownRecords > 0 ? '0 known · incomplete' : number(value)
const refresh = () => load(async () => {
  const result = await copilotApi.usage({ mode: mode.value, timezone: timezone.value, client: client.value, model: model.value, ...dateBounds(range.value.from, range.value.to, timezone.value) })
  try { filters.value = await copilotApi.filters() } catch { /* Keep the last available filters. */ }
  return result
})
useAutoRefresh(refresh)
onMounted(async () => {
  try {
    const settings = await api.settings()
    timezone.value = settings.timezone
    range.value = dateRange(30, settings.timezone)
  } catch { /* The usage request displays connection errors. */ }
})
watch([mode, timezone, client, model, range], () => {
  localStorage.setItem('copilot-period-mode', mode.value)
  void router.replace({ query: { client: client.value || undefined, model: model.value || undefined } })
  void refresh()
}, { deep: true })
</script>
