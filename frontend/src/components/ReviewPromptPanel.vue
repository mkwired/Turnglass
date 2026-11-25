<template>
  <details
    class="panel p-5"
    aria-label="Review with Codex"
  >
    <summary class="font-medium">
      Review with Codex
    </summary>

    <p class="subtle mt-3">
      Use your connected Codex session to review bounded source evidence and submit drafts. The default range is the last 30 days. You approve findings here.
    </p>

    <div class="mt-4 grid gap-3 sm:grid-cols-3">
      <label class="field">
        Project scope<Input
          v-model="scope"
          aria-label="Review project scope"
          placeholder="All projects"
        />
      </label>

      <label class="field">
        From (UTC)<input
          v-model="from"
          aria-label="Review from date"
          type="date"
          class="h-10 rounded-md border bg-card px-3"
        />
      </label>

      <label class="field">
        Through (UTC)<input
          v-model="to"
          aria-label="Review through date"
          type="date"
          class="h-10 rounded-md border bg-card px-3"
        />
      </label>
    </div>

    <Button
      class="mt-4"
      :disabled="busy"
      @click="copyPrompt"
    >
      {{ busy ? 'Preparing prompt…' : 'Copy Codex review prompt' }}
    </Button>

    <p
      v-if="message"
      role="status"
      class="mt-3 text-sm text-primary"
    >
      {{ message }}
    </p>

    <p
      v-if="error"
      role="alert"
      class="error mt-3"
    >
      {{ error }}
    </p>

    <details
      v-if="prompt"
      class="mt-4 text-sm"
    >
      <summary>
        Prompt preview
      </summary>

      <pre class="mt-3 max-h-80 overflow-auto rounded-lg bg-muted p-4 text-xs">{{ prompt }}</pre>
    </details>
  </details>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { knowledge } from '@/api/knowledge'
import { messageOf } from '@/lib/format'

const props = defineProps<{ project: string }>()
const scope = ref(props.project)
const from = ref(new Date(Date.now() - 29 * 86400000).toISOString().slice(0, 10))
const to = ref(new Date().toISOString().slice(0, 10))
const prompt = ref('')
const busy = ref(false)
const message = ref('')
const error = ref('')

async function copyPrompt() {
  busy.value = true; error.value = ''; message.value = ''
  try {
    if (!from.value || !to.value || from.value > to.value) throw new Error('Choose a valid date range with the start on or before the end.')
    const result = await knowledge.prompt(scope.value)
    const end = new Date(`${to.value}T00:00:00Z`); end.setUTCDate(end.getUTCDate() + 1)
    prompt.value = `${result.text}\n\nReview scope for this run (replaces any default date range): from ${from.value}T00:00:00.000Z inclusive, to ${end.toISOString()} exclusive (UTC). ${scope.value ? `Project: ${scope.value}.` : 'All projects.'}\nKeep this review within ${result.runCharacterLimit.toLocaleString('en-US')} characters across packets; each packet is limited to ${result.packetCharacterLimit.toLocaleString('en-US')} characters.`
    await navigator.clipboard.writeText(prompt.value)
    message.value = 'Copied review prompt. Paste it into Codex with the Turnglass MCP connection enabled.'
  } catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}
</script>
