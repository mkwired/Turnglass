<template>
  <div class="space-y-7">
    <div class="flex flex-wrap items-end justify-between gap-5">
      <div>
        <p class="eyebrow mb-2">
          Usage intelligence
        </p>

        <h1 class="page-heading">
          Your Codex, at a glance.
        </h1>

        <p class="mt-2 text-muted-foreground">
          Understand where your tokens go and what they’re worth.
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
        aria-label="Period mode"
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
          v-model="project"
          aria-label="Filter by project"
        >
          <option value="">
            All projects
          </option>

          <option
            v-for="item in filters.projects"
            :key="item"
            :value="item"
          >
            {{ shortProject(item) }}
          </option>
        </select>

        <select
          v-model="model"
          aria-label="Filter by model"
        >
          <option value="">
            All models
          </option>

          <option
            v-for="item in filters.models"
            :key="item"
          >
            {{ item }}
          </option>
        </select>

        <input
          v-model.lazy="timezone"
          aria-label="Reporting timezone"
          class="h-10 w-44 rounded-md border bg-card px-3 text-sm"
          list="timezones"
        />

        <datalist id="timezones">
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
      Loading usage…
    </p>

    <template v-if="report">
      <IndexNotice :status="report.index" />

      <div class="grid gap-5 md:grid-cols-3">
        <UsageCard
          v-for="period in report.periods"
          :key="period.label"
          :period="period"
        />
      </div>

      <section
        class="panel p-5 sm:p-6"
        aria-labelledby="trend-title"
      >
        <div class="mb-7 flex flex-wrap items-start justify-between gap-4">
          <div>
            <h2
              id="trend-title"
              class="text-lg font-semibold"
            >
              Usage over time
            </h2>

            <p class="subtle mt-1">
              Daily activity across your selected sessions.
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
            for="trend-from"
            class="sr-only"
          >
            Trend start date
          </label>

          <input
            id="trend-from"
            v-model="range.from"
            type="date"
            class="rounded-md border bg-background px-3 py-1.5"
            :max="range.to"
          />

          <span class="text-muted-foreground">to</span>

          <label
            for="trend-to"
            class="sr-only"
          >
            Trend end date
          </label>

          <input
            id="trend-to"
            v-model="range.to"
            type="date"
            class="rounded-md border bg-background px-3 py-1.5"
            :min="range.from"
          />
        </div>

        <UsageChart
          :buckets="report.buckets"
          :metric="metric"
        />
      </section>

      <div class="grid gap-5 md:grid-cols-[1fr_1fr]">
        <section class="panel flex items-start gap-4 p-5">
          <span class="rounded-lg bg-accent p-2.5 text-primary"><ArrowDownToLine :size="20" /></span>

          <div class="flex-1">
            <h2 class="text-sm font-medium">
              Cache efficiency
            </h2>

            <p class="mt-1 text-2xl font-semibold tabular">
              {{ cachedRatio }}<span class="ml-1 text-base font-normal text-muted-foreground">% of input cached</span>
            </p>

            <div class="mt-3 h-1.5 overflow-hidden rounded-full bg-muted">
              <div
                class="h-full rounded-full bg-primary"
                :style="{ width: `${cachedRatio}%` }"
              />
            </div>

            <p class="mt-2 text-xs text-muted-foreground">
              Cached tokens are already included in input totals.
            </p>
          </div>
        </section>

        <section class="panel p-5">
          <h2 class="text-sm font-medium">
            Your local index
          </h2>

          <p class="mt-2 text-2xl font-semibold tabular">
            {{ compact(report.index.sessionCount) }} <span class="text-base font-normal text-muted-foreground">sessions</span>

            <span class="mx-3 text-border">/</span>

            {{ compact(report.index.eventCount) }} <span class="text-base font-normal text-muted-foreground">events</span>
          </p>

          <p class="mt-3 text-xs text-muted-foreground">
            {{ report.index.lastSuccessfulScanUtc ? `Last synchronized ${dateTime(report.index.lastSuccessfulScanUtc, report.timezone)}` : 'Waiting for the first scan.' }}
          </p>
        </section>
      </div>

      <p class="text-xs leading-relaxed text-muted-foreground">
        Estimated API-equivalent cost (USD). Pricing catalog: {{ report.pricingVersion }}. Subscription charges and tool fees are not included. Unknown rates remain unpriced.
      </p>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowDownToLine, CalendarDays, RefreshCw } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import UsageCard from '@/components/UsageCard.vue'
import UsageChart from '@/components/UsageChart.vue'
import IndexNotice from '@/components/IndexNotice.vue'
import { api } from '@/api/client'
import type { FilterOptions, UsageReport } from '@/api/types'
import { useLoader } from '@/composables/useLoader'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { compact, dateBounds, dateRange, dateTime, shortProject } from '@/lib/format'

const route = useRoute()
const router = useRouter()
const mode = ref(localStorage.getItem('period-mode') === 'rolling' ? 'rolling' : 'calendar')
const timezone = ref('America/New_York')
const project = ref(String(route.query.project ?? ''))
const model = ref(String(route.query.model ?? ''))
const range = ref(dateRange())
const metric = ref<'tokens' | 'cost'>('tokens')
const filters = ref<FilterOptions>({ projects: [], models: [] })
const { data: report, error, loading, load } = useLoader<UsageReport>()
const cachedRatio = computed(() => {
  const totals = report.value?.periods.at(-1)?.totals.tokens
  return totals?.inputTokens ? Math.round(totals.cachedInputTokens / totals.inputTokens * 100) : 0
})
const refresh = () => load(() => api.usage({ mode: mode.value, timezone: timezone.value, model: model.value, project: project.value, ...dateBounds(range.value.from, range.value.to, timezone.value) }))
useAutoRefresh(refresh)
onMounted(async () => {
  try {
    const [options, settings] = await Promise.all([api.filters(), api.settings()])
    filters.value = options
    timezone.value = settings.timezone
    range.value = dateRange(30, settings.timezone)
  } catch { /* The report request displays connection errors. */ }
})
watch([mode, timezone, project, model, range], () => {
  localStorage.setItem('period-mode', mode.value)
  void router.replace({ query: { project: project.value || undefined, model: model.value || undefined } })
  void refresh()
}, { deep: true })
</script>
