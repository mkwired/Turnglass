<template>
  <article
    class="panel min-w-0 p-5 sm:p-6"
    :aria-label="`Insight: ${insight.statement}`"
  >
    <div class="mb-3 flex flex-wrap items-center gap-2 text-xs">
      <label
        v-if="active"
        class="mr-2 flex items-center gap-2"
      >
        <input
          type="checkbox"
          :checked="selected"
          :disabled="busy || !!draft || !!relation"
          :aria-label="`Select ${insight.statement}`"
          @change="emit('select', ($event.target as HTMLInputElement).checked)"
        />Select
      </label>

      <span class="rounded-md bg-accent px-2 py-1 capitalize text-accent-foreground">{{ insight.category }}</span>

      <span class="rounded-md bg-muted px-2 py-1 capitalize">{{ insight.status }}</span>

      <span class="capitalize">{{ insight.basis }}</span>

      <span class="ml-auto text-muted-foreground">Revision {{ insight.revision }}</span>
    </div>

    <h2 class="text-lg font-semibold leading-relaxed">
      {{ insight.statement }}
    </h2>

    <p class="mt-2 break-all text-xs text-muted-foreground">
      <span class="capitalize">{{ insight.scope }}</span>

      <span v-if="insight.project"> · {{ insight.project }}</span>

      <span v-if="insight.sessionId"> · {{ insight.sessionId }}</span>
    </p>

    <p class="mt-3 text-sm leading-6">
      <span class="font-medium">Rationale:</span> {{ insight.rationale }}
    </p>

    <section
      class="mt-5 space-y-3"
      aria-label="Original evidence"
    >
      <p class="eyebrow">
        Original evidence
      </p>

      <div
        v-for="(evidence, i) in insight.evidence"
        :key="`${evidence.sessionId}:${evidence.eventId}:${i}`"
        class="rounded-lg border bg-muted/30 p-3"
      >
        <div class="mb-2 flex flex-wrap items-center justify-between gap-2 text-xs">
          <RouterLink
            :to="{ path: '/sessions', query: { session: evidence.sessionId, event: evidence.eventId } }"
            class="break-all font-medium text-primary underline"
          >Open event {{ evidence.ordinal }}</RouterLink>

          <span
            class="rounded border px-2 py-1 capitalize"
            :class="evidence.availability === 'available' ? 'text-primary' : 'text-amber-600'"
          >{{ evidence.availability }}</span>
        </div>

        <blockquote class="whitespace-pre-wrap break-words text-sm leading-6">{{ evidence.quote }}</blockquote>

        <p
          v-if="evidence.availability !== 'available'"
          class="mt-2 text-xs text-muted-foreground"
        >
          The original quote is retained; the source is {{ evidence.availability }}.
        </p>
      </div>
    </section>

    <details class="mt-4 text-sm">
      <summary class="text-muted-foreground">
        Status history · {{ insight.history.length }} revisions
      </summary>

      <ol class="mt-3 space-y-3">
        <li
          v-for="entry in insight.history"
          :key="entry.revision"
          class="border-l-2 pl-3"
        >
          <p>
            <strong class="capitalize">{{ entry.action }}</strong> · Revision {{ entry.revision }} · {{ dateTime(entry.timestamp) }}
          </p>

          <p class="mt-1">
            {{ entry.statement }}
          </p>

          <p
            v-if="entry.note"
            class="mt-1 text-muted-foreground"
          >
            {{ entry.note }}
          </p>
        </li>
      </ol>
    </details>

    <form
      v-if="draft"
      class="mt-5 space-y-4 rounded-lg border p-4"
      aria-label="Edit insight"
      @submit.prevent="act('edit')"
    >
      <label class="field">
        Statement<textarea
          v-model="draft.statement"
          aria-label="Insight statement"
          required
          rows="3"
          class="rounded-md border bg-background p-3"
        />
      </label>

      <div class="grid gap-3 sm:grid-cols-3">
        <label class="field">
          Category<select
            v-model="draft.category"
            aria-label="Insight category"
          >
            <option
              v-for="category in ['preference', 'lesson', 'decision', 'workflow']"
              :key="category"
            >
              {{ category }}
            </option>
          </select>
        </label>

        <label class="field">
          Scope<select
            v-model="draft.scope"
            aria-label="Insight scope"
          >
            <option
              v-for="scope in ['global', 'project', 'session']"
              :key="scope"
            >
              {{ scope }}
            </option>
          </select>
        </label>

        <label class="field">
          Basis<select
            v-model="draft.basis"
            aria-label="Insight basis"
          >
            <option>explicit</option>

            <option>inferred</option>
          </select>
        </label>
      </div>

      <label
        v-if="draft.scope !== 'global'"
        class="field"
      >
        Project<input
          v-model="draft.project"
          aria-label="Insight project"
          :required="draft.scope === 'project'"
          class="h-10 rounded-md border bg-background px-3"
        />
      </label>

      <label
        v-if="draft.scope === 'session'"
        class="field"
      >
        Session ID<input
          v-model="draft.sessionId"
          aria-label="Insight session ID"
          required
          class="h-10 rounded-md border bg-background px-3"
        />
      </label>

      <label class="field">
        Rationale<textarea
          v-model="draft.rationale"
          aria-label="Insight rationale"
          required
          rows="2"
          class="rounded-md border bg-background p-3"
        />
      </label>

      <p class="subtle">
        Original evidence is preserved. {{ insight.status === 'approved' ? 'Saving explicitly updates this approved insight.' : 'Saving keeps this insight in the inbox.' }}
      </p>

      <div class="flex gap-2">
        <Button
          type="submit"
          :disabled="busy || !valid"
        >
          Save changes
        </Button>

        <Button
          type="button"
          variant="outline"
          :disabled="busy"
          @click="cancel"
        >
          Cancel edit
        </Button>
      </div>
    </form>

    <div
      v-else-if="relation"
      class="mt-5 space-y-3 rounded-lg border p-4"
    >
      <p class="font-medium">
        {{ relation === 'merge' ? 'Merge into another insight' : 'Choose the approved replacement' }}
      </p>

      <p class="subtle">
        {{ relation === 'merge' ? 'Combine evidence into the target and mark this insight superseded. Both must have the same scope.' : 'Mark this insight superseded. The selected approved insight becomes its replacement.' }}
      </p>

      <form
        class="flex gap-2"
        @submit.prevent="findTargets"
      >
        <Input
          v-model="targetQuery"
          aria-label="Search target insights"
          placeholder="Search matching insights…"
        />

        <Button
          type="submit"
          variant="outline"
          :disabled="targetBusy"
        >
          Find targets
        </Button>
      </form>

      <select
        v-model="targetId"
        aria-label="Target insight"
        class="w-full"
      >
        <option value="">
          Select a target…
        </option>

        <option
          v-for="item in targets"
          :key="item.id"
          :value="item.id"
        >
          {{ item.statement }} ({{ item.status }}, revision {{ item.revision }})
        </option>
      </select>

      <p
        v-if="targetBusy"
        role="status"
        class="subtle"
      >
        Loading targets…
      </p>

      <p
        v-else-if="!targets.length"
        class="subtle"
      >
        No matching targets. Try a different search or approve a replacement first.
      </p>

      <p
        v-if="targetError"
        role="alert"
        class="error"
      >
        {{ targetError }}
      </p>

      <div class="flex gap-2">
        <Button
          :disabled="busy || !target"
          @click="act(relation)"
        >
          Confirm {{ relation }}
        </Button>

        <Button
          variant="outline"
          :disabled="busy"
          @click="cancel"
        >
          Cancel
        </Button>
      </div>
    </div>

    <div
      v-else-if="active"
      class="mt-5 flex flex-wrap gap-2"
    >
      <Button
        v-if="insight.status === 'draft'"
        :disabled="busy"
        @click="act('approve')"
      >
        Approve
      </Button>

      <Button
        variant="outline"
        :disabled="busy"
        @click="edit"
      >
        Edit
      </Button>

      <Button
        variant="outline"
        :disabled="busy"
        @click="act('dismiss')"
      >
        Dismiss
      </Button>

      <Button
        variant="ghost"
        :disabled="busy"
        @click="relate('merge')"
      >
        Merge
      </Button>

      <Button
        variant="ghost"
        :disabled="busy"
        @click="relate('supersede')"
      >
        Supersede
      </Button>
    </div>
  </article>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { knowledge } from '@/api/knowledge'
import type { Insight, InsightAction, InsightDraft } from '@/api/knowledge-types'
import { dateTime, messageOf } from '@/lib/format'

const props = defineProps<{ insight: Insight; selected: boolean; busy: boolean; apply: (actions: InsightAction[]) => Promise<boolean> }>()
const emit = defineEmits<{ select: [selected: boolean]; editing: [editing: boolean] }>()
const draft = ref<InsightDraft>()
const relation = ref('')
const targets = ref<Insight[]>([])
const targetId = ref('')
const targetQuery = ref('')
const targetError = ref('')
const targetBusy = ref(false)
const editRevision = ref(props.insight.revision)
const target = computed(() => targets.value.find(item => item.id === targetId.value))
const active = computed(() => props.insight.status === 'draft' || props.insight.status === 'approved')
const valid = computed(() => !!draft.value?.statement.trim() && !!draft.value.rationale.trim() && (draft.value.scope !== 'project' || !!draft.value.project?.trim()) && (draft.value.scope !== 'session' || !!draft.value.sessionId?.trim()))

function edit() {
  const item = props.insight
  editRevision.value = item.revision
  draft.value = { statement: item.statement, category: item.category, scope: item.scope, project: item.project, sessionId: item.sessionId, basis: item.basis, rationale: item.rationale, evidence: item.evidence.map(({ sessionId, eventId, quote }) => ({ sessionId, eventId, quote })), relatedInsightIds: [...item.relatedInsightIds] }
  emit('editing', true)
}
function cancel() { draft.value = undefined; relation.value = ''; emit('editing', false) }
async function act(action: string) {
  const change: InsightAction = { id: props.insight.id, expectedRevision: draft.value || relation.value ? editRevision.value : props.insight.revision, action }
  if (action === 'edit' && draft.value) change.draft = { ...draft.value, project: draft.value.scope === 'global' ? null : draft.value.project, sessionId: draft.value.scope === 'session' ? draft.value.sessionId : null }
  if (action === 'merge' || action === 'supersede') {
    if (!target.value) return
    change.targetId = target.value.id; change.targetRevision = target.value.revision
  }
  if (await props.apply([change])) cancel()
}
async function findTargets() {
  targetBusy.value = true; targetError.value = ''
  try {
    const pages = await Promise.all((relation.value === 'merge' ? ['draft', 'approved'] : ['approved']).map(status => knowledge.insights({ query: targetQuery.value, status, limit: 100 })))
    targets.value = pages.flatMap(page => page.items).filter(item => item.id !== props.insight.id && item.scope === props.insight.scope && item.project === props.insight.project && item.sessionId === props.insight.sessionId)
    targetId.value = ''
  } catch (reason) { targetError.value = messageOf(reason) }
  finally { targetBusy.value = false }
}
function relate(action: string) { editRevision.value = props.insight.revision; relation.value = action; emit('editing', true); void findTargets() }
onUnmounted(() => emit('editing', false))
</script>
