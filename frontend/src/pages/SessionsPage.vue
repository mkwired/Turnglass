<template>
  <div class="space-y-6">
    <div>
      <p class="eyebrow mb-2">
        Codex sessions
      </p>

      <h1 class="page-heading">
        Every session tells a story.
      </h1>

      <p class="mt-2 text-muted-foreground">
        Explore conversations, tool activity, and the work behind your usage.
      </p>
    </div>

    <div class="panel flex flex-wrap gap-3 p-4">
      <div class="relative min-w-52 flex-1">
        <Search
          :size="16"
          class="absolute left-3 top-3 text-muted-foreground"
        />

        <Input
          v-model="query"
          aria-label="Search sessions"
          placeholder="Search session titles and projects…"
          class="pl-9"
        />
      </div>

      <select
        v-model="project"
        aria-label="Filter sessions by project"
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
        aria-label="Filter sessions by model"
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
        v-model="from"
        type="date"
        aria-label="Sessions from date"
        class="h-10 rounded-md border bg-card px-3 text-sm"
      />

      <input
        v-model="to"
        type="date"
        aria-label="Sessions to date"
        class="h-10 rounded-md border bg-card px-3 text-sm"
      />

      <Button
        variant="ghost"
        @click="resetFilters"
      >
        Reset
      </Button>
    </div>

    <p
      v-if="error"
      role="alert"
      class="error"
    >
      {{ error }}
    </p>

    <IndexNotice
      v-if="page"
      :status="page.index"
    />

    <p
      v-if="loading && !page"
      role="status"
      class="subtle"
    >
      Loading sessions…
    </p>

    <div
      v-if="page && !rows.length"
      class="empty"
    >
      <Folder
        :size="28"
        class="mx-auto mb-3 opacity-50"
      />

      <h2 class="font-medium text-foreground">
        No sessions found
      </h2>

      <p class="mt-2 text-sm">
        Try different filters or check your source directories in Settings.
      </p>
    </div>

    <section
      v-if="rows.length"
      class="panel overflow-x-auto"
      aria-label="Session list"
    >
      <table class="w-full text-left text-sm">
        <thead class="border-b bg-muted/40 text-xs text-muted-foreground">
          <tr>
            <th class="min-w-72 px-5 py-4 font-medium">
              SESSION / PROJECT
            </th>

            <th class="px-5 py-4 font-medium">
              MODEL
            </th>

            <th class="whitespace-nowrap px-5 py-4 text-right font-medium">
              OWN TOKENS
            </th>

            <th class="whitespace-nowrap px-5 py-4 text-right font-medium">
              OWN EST. COST
            </th>

            <th class="px-5 py-4 font-medium">
              UPDATED
            </th>
          </tr>
        </thead>

        <tbody>
          <template
            v-for="row in roots"
            :key="row.session.id"
          >
            <tr class="border-b transition-colors hover:bg-muted/40">
              <td class="px-5 py-4">
                <div class="flex items-start gap-2">
                  <button
                    v-if="row.childCount"
                    class="mt-1 text-muted-foreground"
                    :aria-label="`Toggle subagents for ${row.session.title}`"
                    :aria-expanded="!!expanded[row.session.id]"
                    @click="expand(row)"
                  >
                    <ChevronDown
                      v-if="expanded[row.session.id]"
                      :size="16"
                    />

                    <ChevronRight
                      v-else
                      :size="16"
                    />
                  </button>

                  <button
                    class="text-left"
                    @click="openSession(row.session.id)"
                  >
                    <span class="block font-medium hover:text-primary">{{ row.session.title }}</span>

                    <span class="mt-1 block text-xs text-muted-foreground">{{ shortProject(row.session.project) }}<span v-if="row.childCount"> · {{ row.childCount }} subagents</span>

                      <span v-if="row.session.parentId"> · Child session</span></span>
                  </button>
                </div>
              </td>

              <td class="px-5 py-4">
                <span class="whitespace-nowrap rounded-md bg-muted px-2 py-1 text-xs">{{ row.session.model ?? 'Unknown' }}</span>
              </td>

              <td class="tabular px-5 py-4 text-right font-medium">
                {{ compact(row.ownUsage.tokens.totalTokens) }}
              </td>

              <td class="tabular px-5 py-4 text-right">
                {{ dollars(row.ownUsage.knownCostUsd) }}<span
                  v-if="!row.ownUsage.isPricingComplete"
                  class="block text-xs text-amber-600"
                >Incomplete</span>
              </td>

              <td class="whitespace-nowrap px-5 py-4 text-xs text-muted-foreground">
                {{ dateTime(row.session.updatedAt, timezone) }}
              </td>
            </tr>

            <tr
              v-for="child in expanded[row.session.id] ?? []"
              :key="child.session.id"
              class="border-b bg-muted/30"
            >
              <td class="py-3 pl-12 pr-5">
                <button
                  class="text-left hover:text-primary"
                  @click="openSession(child.session.id)"
                >
                  <span class="mr-2 text-muted-foreground">↳</span>{{ child.session.title }}
                </button>
              </td>

              <td class="px-5 text-xs text-muted-foreground">
                {{ child.session.model ?? 'Unknown' }}
              </td>

              <td class="tabular px-5 text-right">
                {{ compact(child.ownUsage.tokens.totalTokens) }}
              </td>

              <td class="tabular px-5 text-right">
                {{ dollars(child.ownUsage.knownCostUsd) }}<span
                  v-if="!child.ownUsage.isPricingComplete"
                  class="block text-xs text-amber-600"
                >Incomplete</span>
              </td>

              <td class="px-5 text-xs text-muted-foreground">
                Subagent
              </td>
            </tr>
          </template>
        </tbody>
      </table>

      <div class="flex items-center justify-between p-4">
        <span class="subtle">{{ rows.length }} sessions loaded</span>

        <Button
          v-if="page?.nextCursor"
          variant="outline"
          :disabled="loading"
          @click="refresh(true)"
        >
          Load more sessions
        </Button>
      </div>
    </section>

    <section
      v-if="selectedId"
      class="panel p-5 sm:p-6"
      aria-label="Session detail"
    >
      <div class="mb-5 flex justify-between gap-3">
        <div>
          <p class="eyebrow mb-2">
            Session detail
          </p>

          <h2 class="text-xl font-semibold">
            {{ selected?.detail.session.title ?? 'Loading session…' }}
          </h2>

          <p class="mono mt-2 break-all text-xs text-muted-foreground">
            {{ selectedId }}
          </p>
        </div>

        <Button
          variant="ghost"
          size="icon"
          aria-label="Close session detail"
          @click="closeSession"
        >
          <X :size="18" />
        </Button>
      </div>

      <p
        v-if="detailError"
        role="alert"
        class="error mb-4"
      >
        {{ detailError }}
      </p>

      <template v-if="selected">
        <div class="mb-6 grid gap-4 rounded-lg bg-muted/50 p-4 sm:grid-cols-2">
          <div>
            <p class="subtle">
              This session
            </p>

            <p class="mt-1 font-medium">
              {{ compact(selected.detail.ownUsage.tokens.totalTokens) }} tokens · {{ dollars(selected.detail.ownUsage.knownCostUsd) }} <span
                v-if="!selected.detail.ownUsage.isPricingComplete"
                class="text-xs text-amber-600"
              >incomplete</span>
            </p>
          </div>

          <div>
            <p class="subtle">
              Including descendants
            </p>

            <p class="mt-1 font-medium">
              {{ compact(selected.detail.familyUsage.tokens.totalTokens) }} tokens · {{ dollars(selected.detail.familyUsage.knownCostUsd) }} <span
                v-if="!selected.detail.familyUsage.isPricingComplete"
                class="text-xs text-amber-600"
              >incomplete</span>
            </p>
          </div>
        </div>

        <form
          class="mb-5 flex gap-2"
          @submit.prevent="searchLogs()"
        >
          <Input
            v-model="logQuery"
            aria-label="Search within session"
            placeholder="Search messages and tool output…"
          />

          <Button
            variant="outline"
            :disabled="detailBusy"
            type="submit"
          >
            <Search :size="15" />Search logs
          </Button>
        </form>

        <p
          v-if="searchResults !== null"
          class="subtle mb-4"
        >
          {{ searchResults.length }} matching events <button
            class="ml-2 text-primary underline"
            @click="searchResults = null; logQuery = ''"
          >
            Clear search
          </button>
        </p>

        <div
          v-if="selectedEventId"
          class="mb-4 rounded-lg border border-primary/30 bg-accent/30 p-4 text-sm"
        >
          <p>
            Showing the linked evidence event with nearby context.<span v-if="contextTruncated"> Context is limited; read raw records for complete content.</span>
          </p>

          <Button
            variant="outline"
            size="sm"
            class="mt-2"
            @click="openSession(selectedId)"
          >
            Show full transcript
          </Button>
        </div>

        <div class="space-y-4">
          <div
            v-for="event in searchResults ?? events"
            :id="`event-${event.id}`"
            :key="event.id"
            :tabindex="event.id === selectedEventId ? -1 : undefined"
            :class="event.id === selectedEventId ? 'rounded-lg ring-2 ring-primary ring-offset-2 ring-offset-background' : ''"
          >
            <p
              v-if="event.id === selectedEventId"
              class="mb-2 px-2 pt-2 text-xs font-semibold text-primary"
            >
              Linked evidence event
            </p>

            <TranscriptEntry
              :event="event"
              :timezone="timezone"
            />
          </div>
        </div>

        <p
          v-if="!(searchResults ?? events).length && !detailBusy"
          class="empty"
        >
          No events to display.
        </p>

        <Button
          v-if="searchResults !== null ? searchCursor : eventsCursor"
          class="mt-5"
          variant="outline"
          :disabled="detailBusy"
          @click="searchResults !== null ? searchLogs(true) : moreEvents()"
        >
          Load more events
        </Button>
      </template>

      <p
        v-if="detailBusy"
        role="status"
        class="subtle mt-4"
      >
        Loading transcript…
      </p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ChevronDown, ChevronRight, Folder, Search, X } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import TranscriptEntry from '@/components/TranscriptEntry.vue'
import IndexNotice from '@/components/IndexNotice.vue'
import { api } from '@/api/client'
import { knowledge } from '@/api/knowledge'
import type { EventView, FilterOptions, PageResult, SessionDetail, SessionView } from '@/api/types'
import { useLoader } from '@/composables/useLoader'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { compact, dateBounds, dateTime, dollars, messageOf, shortProject } from '@/lib/format'

const route = useRoute()
const router = useRouter()
const query = ref(String(route.query.query ?? ''))
const project = ref(String(route.query.project ?? ''))
const model = ref(String(route.query.model ?? ''))
const from = ref(String(route.query.from ?? ''))
const to = ref(String(route.query.to ?? ''))
const filters = ref<FilterOptions>({ projects: [], models: [] })
const timezone = ref('America/New_York')
let loadedPages = 1
const { data: page, error, loading, load } = useLoader<PageResult<SessionView>>()
const rows = ref<SessionView[]>([])
const expanded = ref<Record<string, SessionView[]>>({})
const selected = ref<SessionDetail>()
const selectedId = ref(String(route.query.session ?? ''))
const selectedEventId = ref(String(route.query.event ?? ''))
const contextTruncated = ref(false)
const events = ref<EventView[]>([])
const eventsCursor = ref<string | null>(null)
const detailError = ref('')
const detailBusy = ref(false)
const logQuery = ref('')
const searchCursor = ref<string | null>(null)
const searchResults = ref<EventView[] | null>(null)
let detailRequest = 0
let filterTimer: ReturnType<typeof setTimeout>
const roots = computed(() => rows.value.filter(row => !row.session.parentId || !rows.value.some(parent => parent.session.id === row.session.parentId)))

async function refresh(append = false) {
  const cursor = append ? page.value?.nextCursor : undefined
  const filters = { query: query.value, project: project.value, model: model.value, ...dateBounds(from.value, to.value, timezone.value) }
  const ok = await load(async () => {
    let result = await api.sessions({ ...filters, cursor })
    const items = [...result.items]
    if (!append) {
      for (let pageNumber = 1; pageNumber < loadedPages && result.nextCursor; pageNumber++) {
        result = await api.sessions({ ...filters, cursor: result.nextCursor })
        items.push(...result.items)
      }
    }
    return { ...result, items }
  })
  if (ok && page.value) {
    rows.value = [...new Map((append ? [...rows.value, ...page.value.items] : page.value.items).map(row => [row.session.id, row])).values()]
    if (append) loadedPages++
  }
}
useAutoRefresh(() => refresh())
onMounted(async () => {
  try {
    const [options, settings] = await Promise.all([api.filters(), api.settings()])
    filters.value = options
    timezone.value = settings.timezone
    if (from.value || to.value) void refresh()
  } catch { /* List error is visible. */ }
  if (selectedId.value) void openSession(selectedId.value, selectedEventId.value)
})
onUnmounted(() => { clearTimeout(filterTimer); detailRequest++ })
watch([query, project, model, from, to], () => {
  clearTimeout(filterTimer)
  filterTimer = setTimeout(() => {
    loadedPages = 1
    void router.replace({ query: { query: query.value || undefined, project: project.value || undefined, model: model.value || undefined, from: from.value || undefined, to: to.value || undefined, session: selectedId.value || undefined, event: selectedEventId.value || undefined } })
    void refresh()
  }, 250)
})
watch(() => [route.query.session, route.query.event], ([session, event]) => {
  const id = String(session ?? ''); const eventId = String(event ?? '')
  if (id && (id !== selectedId.value || eventId !== selectedEventId.value)) void openSession(id, eventId)
})

async function expand(row: SessionView) {
  if (expanded.value[row.session.id]) { delete expanded.value[row.session.id]; return }
  try { expanded.value[row.session.id] = (await api.session(row.session.id)).children }
  catch (reason) { detailError.value = messageOf(reason) }
}

async function openSession(id: string, eventId = '') {
  const version = ++detailRequest
  selectedId.value = id
  selectedEventId.value = eventId
  contextTruncated.value = false
  selected.value = undefined
  detailBusy.value = true
  detailError.value = ''
  events.value = []
  searchResults.value = null
  logQuery.value = ''
  void router.replace({ query: { ...route.query, session: id, event: eventId || undefined } })
  try {
    const [detail, firstPage] = await Promise.all([api.session(id), eventId ? knowledge.context(id, eventId).then(context => { contextTruncated.value = context.truncated; return { items: context.events, nextCursor: null } }) : api.events(id)])
    if (version !== detailRequest) return
    selected.value = detail
    events.value = firstPage.items
    eventsCursor.value = firstPage.nextCursor
    if (eventId) {
      await nextTick()
      const anchor = document.getElementById(`event-${eventId}`)
      anchor?.scrollIntoView?.({ block: 'center' }); anchor?.focus({ preventScroll: true })
    }
  } catch (reason) { if (version === detailRequest) detailError.value = messageOf(reason) }
  finally { if (version === detailRequest) detailBusy.value = false }
}

function closeSession() {
  detailRequest++
  selectedId.value = ''
  selectedEventId.value = ''
  selected.value = undefined
  void router.replace({ query: { ...route.query, session: undefined, event: undefined } })
}

async function moreEvents() {
  const version = detailRequest
  detailBusy.value = true
  try {
    const result = await api.events(selectedId.value, eventsCursor.value)
    if (version !== detailRequest) return
    events.value.push(...result.items)
    eventsCursor.value = result.nextCursor
  } catch (reason) { if (version === detailRequest) detailError.value = messageOf(reason) }
  finally { if (version === detailRequest) detailBusy.value = false }
}

async function searchLogs(append = false) {
  if (!logQuery.value.trim()) { searchResults.value = null; return }
  detailBusy.value = true
  detailError.value = ''
  const version = detailRequest
  try {
    const result = await api.search(logQuery.value, selectedId.value, append ? searchCursor.value : undefined)
    if (version !== detailRequest) return
    searchResults.value = append ? [...(searchResults.value ?? []), ...result.items] : result.items
    searchCursor.value = result.nextCursor
  } catch (reason) { if (version === detailRequest) detailError.value = messageOf(reason) }
  finally { if (version === detailRequest) detailBusy.value = false }
}

function resetFilters() { query.value = ''; project.value = ''; model.value = ''; from.value = ''; to.value = '' }
</script>
