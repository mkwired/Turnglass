<template>
  <div
    v-if="!status.isUsageComplete || status.warnings.length || status.sources.some(source => !source.available)"
    class="flex gap-3 rounded-lg border border-amber-500/25 bg-amber-500/5 p-4 text-sm"
    role="status"
  >
    <LoaderCircle
      v-if="!status.initialIndexComplete"
      :size="18"
      class="mt-0.5 shrink-0 animate-spin text-amber-600"
    />

    <CircleAlert
      v-else
      :size="18"
      class="mt-0.5 shrink-0 text-amber-600"
    />

    <div class="min-w-0">
      <p
        v-if="!status.initialIndexComplete"
        class="font-medium"
      >
        Indexing your history. These totals are partial.
      </p>

      <p
        v-else
        class="font-medium"
      >
        Some session data needs attention.
      </p>

      <p
        v-if="!status.isUsageComplete"
        class="mt-1 text-xs text-amber-700 dark:text-amber-400"
      >
        Usage accounting is incomplete.
      </p>

      <ul class="mt-1 space-y-1 text-muted-foreground">
        <li
          v-for="warning in status.warnings"
          :key="warning"
        >
          {{ warning }}
        </li>

        <li
          v-for="source in status.sources.filter(source => !source.available)"
          :key="source.path"
          class="break-all"
        >
          {{ source.path }}: {{ source.error ?? 'Source unavailable; retained data may be stale.' }}
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import { CircleAlert, LoaderCircle } from '@lucide/vue'
import type { IndexStatus } from '@/api/types'
defineProps<{ status: IndexStatus }>()
</script>
