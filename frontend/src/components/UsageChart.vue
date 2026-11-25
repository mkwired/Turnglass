<template>
  <div>
    <div
      v-if="buckets.length"
      class="h-64 w-full"
      role="img"
      :aria-label="`Daily ${metric === 'tokens' ? 'token usage' : 'estimated USD cost'} chart; data table below`"
    >
      <ChartContainer
        :key="metric"
        :config="{ value: { label: metric === 'tokens' ? 'Tokens' : 'USD', color: 'var(--chart-1)' } }"
        class="aspect-auto"
      >
        <VisXYContainer
          :data="points"
          :height="250"
          :duration="0"
          :margin="{ top: 16, right: 16, bottom: 10, left: 8 }"
          :y-domain="[0, undefined]"
        >
          <VisArea
            :x="x"
            :y="y"
            color="var(--chart-1)"
            :opacity="0.12"
          />

          <VisLine
            :x="x"
            :y="y"
            color="var(--chart-1)"
            :line-width="2.5"
          />

          <VisAxis
            type="x"
            :tick-format="xLabel"
            :num-ticks="5"
            :grid-line="false"
            :domain-line="false"
          />

          <VisAxis
            type="y"
            :tick-format="yLabel"
            :num-ticks="4"
            :domain-line="false"
            :tick-line="false"
          />

          <VisCrosshair
            color="var(--chart-1)"
            :template="(datum: { x: number; y: number }) => `${xLabel(datum.x)} · ${yLabel(datum.y)}`"
          />

          <VisTooltip />
        </VisXYContainer>
      </ChartContainer>
    </div>

    <div
      v-else
      class="flex h-64 items-center justify-center text-sm text-muted-foreground"
    >
      No usage recorded in this date range.
    </div>

    <details
      v-if="buckets.length"
      class="mt-4 border-t pt-4 text-sm"
    >
      <summary class="w-fit text-muted-foreground">
        View chart data
      </summary>

      <div class="mt-3 max-h-64 overflow-auto">
        <table class="w-full text-left text-sm">
          <caption class="sr-only">
            Daily token usage and priced USD subtotal
          </caption>

          <thead>
            <tr class="border-b">
              <th class="p-2">
                Date
              </th>

              <th class="p-2">
                Tokens
              </th>

              <th class="p-2">
                Cost (USD)
              </th>

              <th class="p-2">
                Pricing
              </th>
            </tr>
          </thead>

          <tbody>
            <tr
              v-for="bucket in buckets"
              :key="bucket.date"
              class="border-b"
            >
              <td class="p-2">
                {{ bucket.date }}
              </td>

              <td class="tabular p-2">
                {{ number(bucket.totals.tokens.totalTokens) }}
              </td>

              <td class="tabular p-2">
                {{ dollars(bucket.totals.knownCostUsd) }}
              </td>

              <td class="p-2">
                {{ bucket.totals.isPricingComplete ? 'Complete' : 'Incomplete' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { VisXYContainer, VisAxis, VisArea, VisLine, VisCrosshair, VisTooltip } from '@unovis/vue'
import { ChartContainer } from '@/components/ui/chart'
import type { UsageBucket } from '@/api/types'
import { compact, dollars, number } from '@/lib/format'

const props = defineProps<{ buckets: UsageBucket[]; metric: 'tokens' | 'cost' }>()
const points = computed(() => props.buckets.map(bucket => ({ x: new Date(`${bucket.date}T12:00:00Z`).getTime(), y: props.metric === 'tokens' ? bucket.totals.tokens.totalTokens : bucket.totals.knownCostUsd })))
const x = (point: { x: number }) => point.x
const y = (point: { y: number }) => point.y
const xLabel = (value: number) => new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' }).format(value)
const yLabel = (value: number) => props.metric === 'tokens' ? compact(value) : dollars(value)
</script>
