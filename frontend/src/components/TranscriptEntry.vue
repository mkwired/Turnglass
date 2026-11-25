<template>
  <article
    class="rounded-lg border bg-card p-4"
    :aria-label="`${event.role ?? event.type} event`"
  >
    <div class="mb-3 flex flex-wrap items-center gap-2 text-xs">
      <span class="rounded-md bg-muted px-2 py-1 font-semibold capitalize">{{ event.role ?? event.type }}</span>

      <span
        v-if="event.inherited"
        class="rounded-md bg-accent px-2 py-1 text-accent-foreground"
      >Inherited context</span>

      <time
        :datetime="event.timestamp"
        class="ml-auto text-muted-foreground"
      >
        {{ dateTime(event.timestamp, timezone) }}
      </time>
    </div>

    <p
      v-if="event.encrypted"
      class="flex items-center gap-2 text-sm text-muted-foreground"
    >
      <LockKeyhole :size="15" />Encrypted reasoning is unavailable as plain text.
    </p>

    <pre
      v-else-if="event.role === 'user' || event.role === 'assistant'"
      class="text-sm leading-7"
      style="font-family: inherit"
    >{{ event.text }}</pre>

    <details v-else>
      <summary class="flex items-center gap-2 text-sm font-medium">
        <Wrench :size="15" />{{ event.type }} details
      </summary>

      <pre class="mono mt-3 max-h-96 overflow-auto text-xs leading-6">{{ event.text }}</pre>
    </details>

    <p
      v-if="event.truncated"
      class="mt-2 text-xs text-amber-600"
    >
      Preview limited. Read the raw record for the complete content.
    </p>

    <Button
      v-if="!rawOpen"
      variant="ghost"
      size="sm"
      class="mt-3 text-muted-foreground"
      @click="showRaw"
    >
      <FileJson :size="14" />View raw record
    </Button>

    <div
      v-if="rawOpen"
      class="mt-4 rounded-md bg-background p-3"
    >
      <div class="mb-2 flex justify-between">
        <span class="eyebrow">Raw record</span>

        <button
          class="text-xs text-muted-foreground underline"
          @click="rawOpen = false"
        >
          Hide
        </button>
      </div>

      <pre class="mono max-h-96 overflow-auto text-xs leading-6">{{ raw }}</pre>

      <p
        v-if="error"
        role="alert"
        class="error mt-2"
      >
        {{ error }}
      </p>

      <Button
        v-if="nextOffset !== null"
        size="sm"
        variant="outline"
        class="mt-3"
        :disabled="busy"
        @click="readRaw"
      >
        {{ busy ? 'Loading…' : 'Load more raw text' }}
      </Button>
    </div>
  </article>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { FileJson, LockKeyhole, Wrench } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import type { EventView } from '@/api/types'
import { api } from '@/api/client'
import { dateTime, messageOf } from '@/lib/format'

const props = defineProps<{ event: EventView; timezone?: string }>()
const raw = ref('')
const rawOpen = ref(false)
const nextOffset = ref<number | null>(0)
const busy = ref(false)
const error = ref('')

async function readRaw() {
  rawOpen.value = true
  busy.value = true
  error.value = ''
  try {
    const result = await api.record(props.event.sessionId, props.event.id, nextOffset.value ?? 0)
    raw.value += result.text
    nextOffset.value = result.nextOffset
  } catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}

function showRaw() {
  if (raw.value) rawOpen.value = true
  else void readRaw()
}
</script>
