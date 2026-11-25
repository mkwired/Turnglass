<template>
  <article
    class="panel p-6"
    :aria-label="period.label"
  >
    <div class="flex items-center justify-between">
      <h2 class="text-sm font-medium text-muted-foreground">
        {{ period.label }}
      </h2>

      <span class="rounded-md bg-muted p-2 text-muted-foreground"><Coins :size="16" /></span>
    </div>

    <div class="mt-5 flex items-baseline gap-2">
      <span
        class="tabular text-4xl font-semibold tracking-tight"
        :title="number(period.totals.tokens.totalTokens)"
      >{{ compact(period.totals.tokens.totalTokens) }}</span>

      <span class="text-sm text-muted-foreground">tokens</span>
    </div>

    <div class="mt-3 flex flex-wrap items-center gap-2 text-sm">
      <span class="tabular font-semibold text-primary">{{ dollars(period.totals.knownCostUsd) }}</span>

      <span class="text-muted-foreground">{{ period.totals.isPricingComplete ? 'estimated cost' : 'priced subtotal' }}</span>

      <ArrowUpRight
        :size="14"
        class="ml-auto text-muted-foreground"
      />
    </div>

    <p
      v-if="!period.totals.isPricingComplete"
      class="mt-2 text-xs text-amber-600 dark:text-amber-400"
    >
      {{ compact(period.totals.unpricedTokens) }} tokens unpriced · incomplete estimate
    </p>

    <div class="mt-5 flex justify-between gap-4 border-t pt-4 text-xs text-muted-foreground">
      <span>Input <strong class="ml-1 font-medium text-foreground">{{ compact(period.totals.tokens.inputTokens) }}</strong></span>

      <span>Output <strong class="ml-1 font-medium text-foreground">{{ compact(period.totals.tokens.outputTokens) }}</strong></span>
    </div>
  </article>
</template>

<script setup lang="ts">
import { ArrowUpRight, Coins } from '@lucide/vue'
import type { PeriodUsage } from '@/api/types'
import { compact, dollars, number } from '@/lib/format'
defineProps<{ period: PeriodUsage }>()
</script>
