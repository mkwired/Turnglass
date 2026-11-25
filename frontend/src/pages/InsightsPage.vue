<template>
  <div class="space-y-6">
    <div>
      <p class="eyebrow mb-2">
        Evidence-backed knowledge
      </p>

      <h1 class="page-heading">
        Keep what you learn.
      </h1>

      <p class="mt-2 text-muted-foreground">
        Review preferences, lessons, decisions, and workflows from your sessions. Every insight keeps its original evidence.
      </p>
    </div>

    <div
      v-if="progress"
      class="grid grid-cols-2 gap-3 lg:grid-cols-4"
      aria-label="Review progress"
    >
      <div class="panel p-4">
        <p class="subtle">
          Inbox
        </p>

        <p class="mt-1 text-2xl font-semibold">
          {{ progress.drafts }}
        </p>
      </div>

      <div class="panel p-4">
        <p class="subtle">
          Approved
        </p>

        <p class="mt-1 text-2xl font-semibold">
          {{ progress.approved }}
        </p>
      </div>

      <div class="panel p-4">
        <p class="subtle">
          Sessions reviewed
        </p>

        <p class="mt-1 text-2xl font-semibold">
          {{ progress.reviewedSessions }}
        </p>
      </div>

      <div class="panel p-4">
        <p class="subtle">
          Characters reviewed
        </p>

        <p class="mt-1 text-2xl font-semibold">
          {{ progress.reviewedCharacters.toLocaleString() }}
        </p>
      </div>
    </div>

    <ReviewPromptPanel :project="project" />

    <div class="panel p-4">
      <div
        class="mb-4 flex flex-wrap gap-2"
        aria-label="Insight status"
      >
        <Button
          :variant="status === 'draft' ? 'default' : 'outline'"
          :disabled="!!editing.size"
          @click="status = 'draft'"
        >
          Inbox
        </Button>

        <Button
          :variant="status === 'approved' ? 'default' : 'outline'"
          :disabled="!!editing.size"
          @click="status = 'approved'"
        >
          Approved
        </Button>

        <select
          v-model="status"
          aria-label="Filter insight status"
          :disabled="!!editing.size"
        >
          <option value="draft">
            Inbox drafts
          </option>

          <option value="approved">
            Approved
          </option>

          <option value="dismissed">
            Dismissed
          </option>

          <option value="superseded">
            Superseded
          </option>
        </select>
      </div>

      <div class="flex flex-wrap gap-3">
        <Input
          v-model="query"
          aria-label="Search insights"
          placeholder="Search statements and rationale…"
          class="min-w-48 flex-1"
          :disabled="!!editing.size"
        />

        <Input
          v-model="project"
          aria-label="Filter insights by project"
          placeholder="All projects"
          class="min-w-48 flex-1"
          :disabled="!!editing.size"
        />

        <select
          v-model="category"
          aria-label="Filter insight category"
          :disabled="!!editing.size"
        >
          <option value="">
            All categories
          </option>

          <option
            v-for="item in ['preference', 'lesson', 'decision', 'workflow']"
            :key="item"
          >
            {{ item }}
          </option>
        </select>

        <Button
          variant="outline"
          :disabled="loading || busy || !!editing.size"
          @click="reload"
        >
          Reload
        </Button>
      </div>
    </div>

    <div class="flex flex-wrap justify-between gap-2 text-xs text-muted-foreground">
      <span>{{ paused ? 'Auto-refresh paused while editing or selecting.' : 'Refreshes every 5 seconds while this page is active.' }}<span v-if="freshAt"> Updated {{ dateTime(freshAt) }}.</span></span>

      <span v-if="progress">{{ progress.lastReviewedAt ? `Last review ${dateTime(progress.lastReviewedAt)}` : 'No sessions reviewed yet.' }}</span>
    </div>

    <p
      v-if="error"
      role="alert"
      class="error"
    >
      {{ error }}
    </p>

    <p
      v-if="message"
      role="status"
      class="text-sm text-primary"
    >
      {{ message }}
    </p>

    <section
      v-if="selected.length"
      class="panel flex flex-wrap items-center gap-3 p-4"
      aria-label="Selected insights"
    >
      <span class="text-sm font-medium">{{ selected.length }} selected</span>

      <Button
        v-if="selectedDrafts.length"
        :disabled="busy || !!editing.size"
        @click="approveSelected"
      >
        Approve {{ selectedDrafts.length }} selected
      </Button>

      <template v-if="selectedApproved.length">
        <select
          v-model="format"
          aria-label="Export format"
          @change="exported = undefined"
        >
          <option value="markdown">
            Markdown
          </option>

          <option value="json">
            JSON
          </option>
        </select>

        <Button
          variant="outline"
          :disabled="busy || !!editing.size"
          @click="previewExport"
        >
          Preview export ({{ selectedApproved.length }})
        </Button>
      </template>

      <Button
        variant="ghost"
        :disabled="busy"
        @click="clearSelection"
      >
        Clear selection
      </Button>
    </section>

    <section
      v-if="exported"
      class="panel p-5"
      aria-label="Export preview"
    >
      <h2 class="font-semibold">
        Export preview
      </h2>

      <p class="subtle mt-2">
        {{ exported.fileName }} · Selected approved insights only
      </p>

      <pre
        class="mono mt-4 max-h-96 overflow-auto rounded-lg bg-muted p-4 text-xs"
        tabindex="0"
      >{{ exported.content }}</pre>

      <div class="mt-4 flex gap-2">
        <Button
          variant="outline"
          @click="copyExport"
        >
          Copy export
        </Button>

        <Button @click="downloadExport">
          Download export
        </Button>
      </div>
    </section>

    <p
      v-if="loading && !loaded"
      role="status"
      class="subtle"
    >
      Loading insights…
    </p>

    <div
      v-if="loaded && !items.length"
      class="empty"
    >
      <h2 class="font-medium text-foreground">
        {{ status === 'draft' ? 'Your inbox is clear' : 'No insights found' }}
      </h2>

      <p class="mt-2 text-sm">
        {{ status === 'draft' ? 'Copy a Codex review prompt to discover evidence-backed drafts, or adjust your filters.' : 'Try different filters or approve an insight from the inbox.' }}
      </p>
    </div>

    <div class="space-y-4">
      <InsightCard
        v-for="item in items"
        :key="item.id"
        :insight="item"
        :selected="selectedIds.has(item.id)"
        :busy="busy || (!!editing.size && !editing.has(item.id))"
        :apply="apply"
        @select="toggle(item, $event)"
        @editing="editState(item.id, $event)"
      />
    </div>

    <div
      v-if="loaded"
      class="flex items-center justify-between"
    >
      <span class="subtle">{{ items.length }} insights loaded</span>

      <Button
        v-if="nextCursor"
        variant="outline"
        :disabled="loading || busy || !!editing.size"
        @click="refresh(true)"
      >
        Load more insights
      </Button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import InsightCard from '@/components/InsightCard.vue'
import ReviewPromptPanel from '@/components/ReviewPromptPanel.vue'
import { knowledge } from '@/api/knowledge'
import type { Insight, InsightAction, InsightExport, ReviewProgress } from '@/api/knowledge-types'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { dateTime, messageOf } from '@/lib/format'

const route = useRoute()
const router = useRouter()
const query = ref(String(route.query.query ?? ''))
const project = ref(String(route.query.project ?? ''))
const category = ref(String(route.query.category ?? ''))
const status = ref(String(route.query.status ?? 'draft'))
const items = ref<Insight[]>([])
const nextCursor = ref<string | null>(null)
const selected = ref<Insight[]>([])
const editing = ref(new Set<string>())
const progress = ref<ReviewProgress>()
const loaded = ref(false)
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const message = ref('')
const freshAt = ref('')
const exported = ref<InsightExport>()
const format = ref('markdown')
let pages = 1
let requestVersion = 0
let filterTimer: ReturnType<typeof setTimeout>
const paused = computed(() => editing.value.size > 0 || selected.value.length > 0 || busy.value)
const selectedIds = computed(() => new Set(selected.value.map(item => item.id)))
const selectedDrafts = computed(() => selected.value.filter(item => item.status === 'draft'))
const selectedApproved = computed(() => selected.value.filter(item => item.status === 'approved'))

async function refresh(append = false, force = false) {
  const version = ++requestVersion
  loading.value = true; error.value = ''
  try {
    const filters = { query: query.value, project: project.value, category: category.value, status: status.value, limit: 30 }
    let result = await knowledge.insights({ ...filters, cursor: append ? nextCursor.value : undefined })
    const rows = [...result.items]
    if (!append) for (let page = 1; page < pages && result.nextCursor; page++) { result = await knowledge.insights({ ...filters, cursor: result.nextCursor }); rows.push(...result.items) }
    const latestProgress = await knowledge.progress()
    if (version !== requestVersion || (!force && paused.value && !append)) return
    items.value = [...new Map((append ? [...items.value, ...rows] : rows).map(item => [item.id, item])).values()]
    nextCursor.value = result.nextCursor; progress.value = latestProgress; loaded.value = true; freshAt.value = new Date().toISOString()
    if (append) pages++
  } catch (reason) { if (version === requestVersion) error.value = messageOf(reason) }
  finally { if (version === requestVersion) loading.value = false }
}
useAutoRefresh(() => { if (!paused.value && !loading.value) void refresh() })
watch([query, project, category, status], () => {
  clearTimeout(filterTimer)
  filterTimer = setTimeout(() => {
    pages = 1
    void router.replace({ query: { query: query.value || undefined, project: project.value || undefined, category: category.value || undefined, status: status.value === 'draft' ? undefined : status.value } })
    void refresh(false, true)
  }, 250)
})
onUnmounted(() => { clearTimeout(filterTimer); requestVersion++ })
function toggle(item: Insight, checked: boolean) { selected.value = checked ? [...selected.value.filter(row => row.id !== item.id), item] : selected.value.filter(row => row.id !== item.id); exported.value = undefined }
function editState(id: string, active: boolean) { if (active) editing.value.add(id); else editing.value.delete(id) }
function clearSelection() { selected.value = []; exported.value = undefined }

async function apply(actions: InsightAction[]): Promise<boolean> {
  busy.value = true; error.value = ''; message.value = ''
  try {
    await knowledge.review(actions)
    clearSelection()
    message.value = `${actions.length} insight${actions.length === 1 ? '' : 's'} updated.`
    await refresh(false, true)
    return true
  } catch (reason) { error.value = `${messageOf(reason)} Your draft and selection are preserved. Reload when ready to review the latest revisions.`; return false }
  finally { busy.value = false }
}
function approveSelected() { return apply(selectedDrafts.value.map(item => ({ id: item.id, expectedRevision: item.revision, action: 'approve' }))) }
async function previewExport() {
  busy.value = true; error.value = ''
  try { exported.value = await knowledge.export(selectedApproved.value.map(item => item.id), format.value) }
  catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}
async function copyExport() {
  try { await navigator.clipboard.writeText(exported.value!.content); message.value = 'Copied export.' }
  catch { error.value = 'Clipboard access is unavailable. Select the preview text or download the file.' }
}
function downloadExport() {
  const value = exported.value!
  const url = URL.createObjectURL(new Blob([value.content], { type: value.contentType }))
  const link = document.createElement('a'); link.href = url; link.download = value.fileName; link.click(); URL.revokeObjectURL(url)
  message.value = 'Download started.'
}
function reload() { clearSelection(); void refresh(false, true) }
</script>
