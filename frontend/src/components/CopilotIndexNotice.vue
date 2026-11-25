<template>
  <section
    class="rounded-lg border bg-card p-4 text-sm"
    aria-label="Copilot source coverage"
  >
    <p
      class="font-medium"
      role="status"
    >
      {{ status.isIndexing ? 'Scanning Copilot sources…' : status.initialScanComplete ? 'Copilot source scan complete' : 'Waiting for the first Copilot scan…' }}
    </p>

    <p class="mt-2 text-muted-foreground">
      {{ number(status.recordCount) }} measured calls indexed.
      {{ status.lastSuccessfulScanUtc ? `Last synchronized ${dateTime(status.lastSuccessfulScanUtc, timezone)}.` : 'No fully successful scan yet.' }}
    </p>

    <p class="mt-2 text-xs leading-relaxed text-muted-foreground">
      Totals cover retained local usage records. Missing or disabled logs leave gaps; a completed scan does not mean complete account usage. Shared Copilot / CLI can include VS Code agent-host sessions whose editor is not identified in the usage record.
    </p>

    <p
      v-if="status.observedFrom && status.observedTo"
      class="mt-2 text-xs text-muted-foreground"
    >
      Observed records: {{ dateTime(status.observedFrom, timezone) }} – {{ dateTime(status.observedTo, timezone) }}.
    </p>

    <p
      v-if="status.skippedRecords || status.ambiguousRecords"
      class="mt-2 text-amber-700 dark:text-amber-400"
    >
      {{ number(status.skippedRecords) }} records skipped · {{ number(status.ambiguousRecords) }} ambiguous records excluded.
    </p>

    <ul
      v-if="status.warnings.length"
      class="mt-3 list-disc space-y-1 pl-5 text-amber-700 dark:text-amber-400"
    >
      <li
        v-for="warning in status.warnings"
        :key="warning"
      >
        {{ warning }}
      </li>
    </ul>

    <details class="mt-3 border-t pt-3">
      <summary class="w-fit text-muted-foreground">
        Source availability
      </summary>

      <ul
        v-if="status.sources.length"
        class="mt-3 divide-y"
      >
        <li
          v-for="source in status.sources"
          :key="`${source.kind}:${source.path}`"
          class="py-3"
        >
          <div class="flex flex-wrap justify-between gap-2">
            <span class="font-medium">{{ copilotClientName(source.kind) }}</span>

            <span :class="source.enabled && !source.available ? 'text-amber-700 dark:text-amber-400' : 'text-muted-foreground'">{{ !source.enabled ? 'Disabled' : source.available ? `${number(source.fileCount)} files` : 'Unavailable' }}</span>
          </div>

          <p class="mono mt-1 break-all text-xs text-muted-foreground">
            {{ source.path }}
          </p>

          <p
            v-if="source.error"
            class="mt-1 text-xs text-amber-700 dark:text-amber-400"
          >
            {{ source.error }}
          </p>
        </li>
      </ul>

      <p
        v-else
        class="mt-3 text-muted-foreground"
      >
        No Copilot sources are configured or discovered.
      </p>
    </details>
  </section>
</template>

<script setup lang="ts">
import { copilotClientName, type CopilotIndexStatus } from '@/api/copilot'
import { dateTime, number } from '@/lib/format'

defineProps<{ status: CopilotIndexStatus; timezone: string }>()
</script>
