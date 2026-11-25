<template>
  <div class="space-y-6">
    <div class="flex flex-wrap items-end justify-between gap-4">
      <div>
        <p class="eyebrow mb-2">
          Workspace preferences
        </p>

        <h1 class="page-heading">
          Make it yours.
        </h1>

        <p class="mt-2 text-muted-foreground">
          Manage sources, pricing assumptions, and your Codex connection.
        </p>
      </div>

      <Button
        :disabled="busy || !draft"
        @click="save"
      >
        <Save :size="16" />{{ busy ? 'Saving…' : 'Save settings' }}
      </Button>
    </div>

    <p
      v-if="error || statusError"
      role="alert"
      class="error"
    >
      {{ error || statusError }}
    </p>

    <p
      v-if="notice"
      role="status"
      class="flex items-center gap-2 rounded-lg border border-primary/30 bg-accent p-4 text-sm text-accent-foreground"
    >
      <Check :size="17" />{{ notice }}
    </p>

    <p
      v-if="!draft && !error"
      role="status"
      class="subtle"
    >
      Loading settings…
    </p>

    <template v-if="draft">
      <section class="panel p-6">
        <h2 class="text-lg font-semibold">
          Sources & reporting
        </h2>

        <p class="subtle mt-1">
          Source files are read-only. Each directory is scanned recursively for session logs.
        </p>

        <div class="mt-5 grid gap-5 md:grid-cols-[2fr_1fr]">
          <div class="field">
            <label for="source-directories">Source directories · one per line</label>

            <textarea
              id="source-directories"
              v-model="sources"
              rows="4"
              class="mono rounded-md border bg-background p-3 text-sm"
              spellcheck="false"
            />
          </div>

          <div class="field">
            <label for="reporting-timezone">Reporting timezone</label>

            <Input
              id="reporting-timezone"
              v-model="draft.timezone"
            />

            <p class="text-xs leading-relaxed text-muted-foreground">
              Calendar days and weeks follow this timezone, including daylight saving. Weeks start on Monday.
            </p>
          </div>
        </div>
      </section>

      <section class="panel p-6">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h2 class="text-lg font-semibold">
              Pricing catalog
            </h2>

            <p class="subtle mt-1">
              USD per million tokens. Blank write rates mean unknown.
            </p>
          </div>

          <Button
            variant="outline"
            size="sm"
            @click="addRate"
          >
            <Plus :size="15" />Add rate
          </Button>
        </div>

        <div class="my-5 grid gap-4 sm:grid-cols-2">
          <div class="field">
            <label for="pricing-version">Catalog version</label>

            <Input
              id="pricing-version"
              v-model="draft.pricing.version"
            />
          </div>

          <div class="field">
            <label for="pricing-source">Reference URL</label>

            <Input
              id="pricing-source"
              v-model="draft.pricing.sourceUrl"
            />
          </div>
        </div>

        <div class="overflow-x-auto rounded-lg border">
          <table class="w-full text-left text-xs">
            <caption class="sr-only">
              Editable model prices per million tokens
            </caption>

            <thead class="border-b bg-muted">
              <tr>
                <th class="p-3">
                  Model
                </th>

                <th class="p-3">
                  Tier
                </th>

                <th class="p-3">
                  Min input
                </th>

                <th class="p-3">
                  Max input
                </th>

                <th class="p-3">
                  Input $
                </th>

                <th class="p-3">
                  Cached $
                </th>

                <th class="p-3">
                  Output $
                </th>

                <th class="p-3">
                  5m write $
                </th>

                <th class="p-3">
                  1h write $
                </th>

                <th class="p-3">
                  30m write $
                </th>

                <th class="p-3">
                  12h write $
                </th>

                <th>
                  <span class="sr-only">Actions</span>
                </th>
              </tr>
            </thead>

            <tbody>
              <tr
                v-for="(rate, index) in draft.pricing.rates"
                :key="index"
                class="border-b"
              >
                <td class="p-2">
                  <Input
                    v-model="rate.model"
                    :aria-label="`Model ${index + 1}`"
                    class="min-w-40 text-xs"
                  />
                </td>

                <td class="p-2">
                  <select
                    v-model="rate.tier"
                    :aria-label="`Tier ${index + 1}`"
                    class="h-9 text-xs"
                  >
                    <option>
                      standard
                    </option>

                    <option>
                      fast
                    </option>

                    <option>
                      ultrafast
                    </option>

                    <option>
                      flex
                    </option>

                    <option>
                      batch
                    </option>
                  </select>
                </td>

                <td class="p-2">
                  <Input
                    v-model.number="rate.minInputTokens"
                    type="number"
                    min="0"
                    :aria-label="`Minimum input ${index + 1}`"
                    class="w-28 text-xs"
                  />
                </td>

                <td class="p-2">
                  <input
                    :value="rate.maxInputTokens"
                    type="number"
                    min="0"
                    :aria-label="`Maximum input ${index + 1}`"
                    class="h-9 w-28 rounded-md border bg-transparent px-3"
                    placeholder="No limit"
                    @input="rate.maxInputTokens = ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value)"
                  />
                </td>

                <td
                  v-for="field in (['inputPerMillion', 'cachedInputPerMillion', 'outputPerMillion'] as const)"
                  :key="field"
                  class="p-2"
                >
                  <Input
                    v-model.number="rate[field]"
                    type="number"
                    min="0"
                    step="any"
                    :aria-label="`${field} ${index + 1}`"
                    class="w-24 text-xs"
                  />
                </td>

                <td
                  v-for="field in (['cacheWrite5mPerMillion', 'cacheWrite1hPerMillion', 'cacheWrite30mPerMillion', 'cacheWrite12hPerMillion'] as const)"
                  :key="field"
                  class="p-2"
                >
                  <input
                    :value="rate[field]"
                    type="number"
                    min="0"
                    step="any"
                    :aria-label="`${field} ${index + 1}`"
                    class="h-9 w-24 rounded-md border bg-transparent px-3"
                    placeholder="Unknown"
                    @input="rate[field] = ($event.target as HTMLInputElement).value === '' ? null : Number(($event.target as HTMLInputElement).value)"
                  />
                </td>

                <td class="p-2">
                  <Button
                    variant="ghost"
                    size="icon"
                    :aria-label="`Remove rate ${index + 1}`"
                    @click="draft.pricing.rates.splice(index, 1)"
                  >
                    <Trash2 :size="15" />
                  </Button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <p class="mt-4 text-xs leading-relaxed text-muted-foreground">
          Context bands use input tokens per request. Cached input and cache writes are portions of input; reasoning is already included in output. Changes recalculate historical estimates under this catalog.
        </p>

        <div class="mt-6 grid gap-5 lg:grid-cols-3">
          <div class="field">
            <label for="model-aliases">Model mappings</label>

            <textarea
              id="model-aliases"
              v-model="aliases"
              rows="4"
              class="mono rounded-md border bg-background p-3 text-xs"
              placeholder="logged-model = priced-model"
            />

            <p class="text-xs text-muted-foreground">
              Map an exact logged name to a priced model.
            </p>
          </div>

          <div class="field">
            <label for="tier-overrides">Tier overrides by model</label>

            <textarea
              id="tier-overrides"
              v-model="tiers"
              rows="4"
              class="mono rounded-md border bg-background p-3 text-xs"
              placeholder="model-name = standard"
            />

            <p class="text-xs text-muted-foreground">
              Use when the log has no usable speed tier.
            </p>
          </div>

          <div class="field">
            <label for="cache-lifetimes">Cache-write lifetime by model</label>

            <textarea
              id="cache-lifetimes"
              v-model="lifetimes"
              rows="4"
              class="mono rounded-md border bg-background p-3 text-xs"
              placeholder="model-name = 30m"
            />

            <p class="text-xs text-muted-foreground">
              Explicitly choose 5m, 1h, 30m, or 12h for unknown write lifetimes.
            </p>
          </div>
        </div>
      </section>
    </template>

    <section
      class="panel p-6"
      aria-labelledby="copilot-settings-title"
    >
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2
            id="copilot-settings-title"
            class="text-lg font-semibold"
          >
            Copilot sources
          </h2>

          <p class="subtle mt-1">
            Configure Visual Studio, VS Code, and shared Copilot usage independently of Codex sources.
          </p>
        </div>

        <Button
          :disabled="copilotBusy || !copilotDraft"
          @click="saveCopilot"
        >
          <Save :size="15" />{{ copilotBusy ? 'Saving Copilot sources…' : 'Save Copilot sources' }}
        </Button>
      </div>

      <p
        v-if="copilotError || copilotStatusError"
        role="alert"
        class="error mt-4"
      >
        {{ copilotError || copilotStatusError }}
      </p>

      <p
        v-if="copilotNotice"
        role="status"
        class="mt-4 text-sm text-primary"
      >
        {{ copilotNotice }}
      </p>

      <template v-if="copilotDraft">
        <label class="mt-5 flex items-start gap-3 text-sm">
          <input
            v-model="copilotDraft.autoDiscover"
            type="checkbox"
            class="mt-1"
          />

          <span>Automatically discover local Copilot sources</span>
        </label>

        <p class="mt-2 text-xs leading-relaxed text-muted-foreground">
          Only measured usage records are imported. Shared runtime records may also include Copilot CLI activity; they stay labeled Shared Copilot / CLI when the editor is unknown.
        </p>

        <div
          v-for="(source, sourceIndex) in copilotDraft.sources"
          :key="sourceIndex"
          class="mt-4 grid items-end gap-3 rounded-lg border p-3 sm:grid-cols-[10rem_1fr_auto_auto]"
        >
          <div class="field">
            <label :for="`copilot-source-kind-${sourceIndex}`">Source type</label>

            <select
              :id="`copilot-source-kind-${sourceIndex}`"
              v-model="source.kind"
              :aria-label="`Copilot source type ${sourceIndex + 1}`"
            >
              <option value="visual-studio">
                Visual Studio
              </option>

              <option value="vscode">
                VS Code
              </option>

              <option value="shared">
                Shared Copilot / CLI
              </option>
            </select>
          </div>

          <div class="field min-w-0">
            <label :for="`copilot-source-path-${sourceIndex}`">Source directory</label>

            <Input
              :id="`copilot-source-path-${sourceIndex}`"
              v-model="source.path"
              :aria-label="`Copilot source directory ${sourceIndex + 1}`"
              class="mono text-xs"
            />
          </div>

          <label class="flex h-9 items-center gap-2 text-xs">
            <input
              v-model="source.enabled"
              type="checkbox"
              :aria-label="`Enable Copilot source ${sourceIndex + 1}`"
            />

            <span>Enabled</span>
          </label>

          <Button
            variant="ghost"
            size="icon"
            :aria-label="`Remove Copilot source ${sourceIndex + 1}`"
            @click="copilotDraft.sources.splice(sourceIndex, 1)"
          >
            <Trash2 :size="15" />
          </Button>
        </div>

        <div class="mt-4 flex flex-wrap gap-3">
          <Button
            variant="outline"
            size="sm"
            @click="copilotDraft.sources.push({ kind: 'shared', path: '', enabled: true })"
          >
            <Plus :size="15" />Add Copilot source
          </Button>

          <Button
            variant="outline"
            size="sm"
            :disabled="copilotRescanning"
            @click="rescanCopilot"
          >
            <RefreshCw
              :size="15"
              :class="{ 'animate-spin': copilotRescanning }"
            />Rescan Copilot sources
          </Button>
        </div>
      </template>

      <p
        v-else-if="!copilotError"
        role="status"
        class="subtle mt-4"
      >
        Loading Copilot sources…
      </p>

      <div
        v-if="copilotStatus"
        class="mt-5"
      >
        <CopilotIndexNotice
          :status="copilotStatus"
          :timezone="reportingTimezone"
        />
      </div>

      <p class="mt-4 text-xs leading-relaxed text-muted-foreground">
        For VS Code local chat, enable <code class="break-all">github.copilot.chat.agentDebugLog.fileLogging.enabled</code> to record future calls. Newer VS Code agent-host sessions may appear under Shared Copilot / CLI instead. Older chat history and Visual Studio diagnostic logs may omit usage.
      </p>
    </section>

    <section class="panel p-6">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <div>
          <h2 class="text-lg font-semibold">
            Index health
          </h2>

          <p class="subtle mt-1">
            The viewer and MCP server share one local index.
          </p>
        </div>

        <Button
          variant="outline"
          :disabled="rebuilding"
          @click="rebuild"
        >
          <RefreshCw
            :size="15"
            :class="{ 'animate-spin': rebuilding }"
          />Rebuild index
        </Button>
      </div>

      <template v-if="status">
        <div class="mt-5">
          <IndexNotice :status="status" />
        </div>

        <dl class="my-5 grid gap-4 sm:grid-cols-3">
          <div>
            <dt class="subtle">
              Sessions
            </dt>

            <dd class="mt-1 font-medium">
              {{ number(status.sessionCount) }}
            </dd>
          </div>

          <div>
            <dt class="subtle">
              Events
            </dt>

            <dd class="mt-1 font-medium">
              {{ number(status.eventCount) }}
            </dd>
          </div>

          <div>
            <dt class="subtle">
              Last successful scan
            </dt>

            <dd class="mt-1 text-sm font-medium">
              {{ status.lastSuccessfulScanUtc ? dateTime(status.lastSuccessfulScanUtc, reportingTimezone) : 'Not yet completed' }}
            </dd>
          </div>
        </dl>

        <ul class="divide-y rounded-lg border">
          <li
            v-for="source in status.sources"
            :key="source.path"
            class="flex flex-wrap justify-between gap-2 p-3 text-sm"
          >
            <span class="mono break-all text-xs">{{ source.path }}</span>

            <span :class="source.available ? 'text-primary' : 'text-amber-600'">{{ source.available ? `${source.fileCount} files` : 'Unavailable' }}</span>
          </li>
        </ul>
      </template>
    </section>

    <section class="panel p-6">
      <h2 class="text-lg font-semibold">
        Connect Codex
      </h2>

      <p class="subtle mt-1">
        Codex starts the read-only MCP server on demand, even when the viewer is closed.
      </p>

      <div class="my-5 grid gap-4 lg:grid-cols-2">
        <div class="field">
          <label for="mcp-executable">Published MCP executable · absolute path</label>

          <Input
            id="mcp-executable"
            v-model="executable"
            class="mono text-xs"
          />
        </div>

        <div class="field">
          <label for="mcp-data-dir">Data directory · optional</label>

          <Input
            id="mcp-data-dir"
            v-model="dataDirectory"
            class="mono text-xs"
            placeholder="Default: local app data / Turnglass"
          />
        </div>
      </div>

      <p class="mb-3 text-sm text-muted-foreground">
        Set the executable path to your published build, then add this to your Codex MCP configuration.
      </p>

      <pre class="mono overflow-auto rounded-lg border bg-background p-4 text-xs leading-6">{{ config }}</pre>

      <Button
        variant="outline"
        class="mt-4"
        @click="copyConfiguration"
      >
        <Check
          v-if="copied"
          :size="15"
        />

        <Copy
          v-else
          :size="15"
        />{{ copied ? 'Copied configuration' : 'Copy configuration' }}
      </Button>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Check, Copy, Plus, RefreshCw, Save, Trash2 } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import IndexNotice from '@/components/IndexNotice.vue'
import CopilotIndexNotice from '@/components/CopilotIndexNotice.vue'
import { api } from '@/api/client'
import { copilotApi, type CopilotSettings, type CopilotIndexStatus } from '@/api/copilot'
import type { IndexStatus, ViewerSettings } from '@/api/types'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { useLoader } from '@/composables/useLoader'
import { dateTime, messageOf, number } from '@/lib/format'
import { formatMappings, mcpConfiguration, parseMappings, validateSettings } from '@/lib/settings'

const draft = ref<ViewerSettings>()
const sources = ref('')
const aliases = ref('')
const tiers = ref('')
const lifetimes = ref('')
const error = ref('')
const notice = ref('')
const busy = ref(false)
const rebuilding = ref(false)
const executable = ref('C:\\path\\to\\publish\\mcp\\Turnglass.Mcp.exe')
const dataDirectory = ref('')
const copied = ref(false)
const reportingTimezone = ref('America/New_York')
const copilotDraft = ref<CopilotSettings>()
const copilotError = ref('')
const copilotNotice = ref('')
const copilotBusy = ref(false)
const copilotRescanning = ref(false)
const { data: copilotStatus, load: loadCopilotStatus, error: copilotStatusError } = useLoader<CopilotIndexStatus>()
const { data: status, load, error: statusError } = useLoader<IndexStatus>()
const config = computed(() => mcpConfiguration(executable.value, dataDirectory.value))
useAutoRefresh(() => load(api.status))
useAutoRefresh(() => loadCopilotStatus(copilotApi.status))

onMounted(async () => {
  try { copilotDraft.value = structuredClone(await copilotApi.settings()) }
  catch (reason) { copilotError.value = messageOf(reason) }
})

onMounted(async () => {
  try {
    const settings = await api.settings()
    draft.value = structuredClone(settings)
    reportingTimezone.value = settings.timezone
    sources.value = settings.sources.join('\n')
    aliases.value = formatMappings(settings.pricing.modelAliases)
    tiers.value = formatMappings(settings.pricing.tierOverrides)
    lifetimes.value = formatMappings(settings.pricing.cacheWriteLifetimeOverrides)
  } catch (reason) { error.value = messageOf(reason) }
})

async function save() {
  if (!draft.value) return
  error.value = ''; notice.value = ''; busy.value = true
  try {
    const settings: ViewerSettings = {
      ...draft.value,
      sources: sources.value.split('\n').map(line => line.trim()).filter(Boolean),
      pricing: { ...draft.value.pricing, modelAliases: parseMappings(aliases.value), tierOverrides: parseMappings(tiers.value), cacheWriteLifetimeOverrides: parseMappings(lifetimes.value) },
    }
    validateSettings(settings)
    draft.value = await api.saveSettings(settings)
    reportingTimezone.value = draft.value.timezone
    notice.value = 'Settings saved. Estimates now use this pricing catalog.'
    await load(api.status)
  } catch (reason) { error.value = messageOf(reason) }
  finally { busy.value = false }
}

function addRate() {
  draft.value?.pricing.rates.push({ model: '', tier: 'standard', minInputTokens: 0, maxInputTokens: null, inputPerMillion: 0, cachedInputPerMillion: 0, outputPerMillion: 0, cacheWrite5mPerMillion: null, cacheWrite1hPerMillion: null, cacheWrite30mPerMillion: null, cacheWrite12hPerMillion: null })
}

async function saveCopilot() {
  if (!copilotDraft.value) return
  copilotError.value = ''; copilotNotice.value = ''; copilotBusy.value = true
  try {
    const settings: CopilotSettings = { autoDiscover: copilotDraft.value.autoDiscover, sources: copilotDraft.value.sources.map(source => ({ ...source, path: source.path.trim() })) }
    if (settings.sources.some(source => !source.path)) throw new Error('Every Copilot source needs a directory.')
    copilotDraft.value = await copilotApi.saveSettings(settings)
    copilotNotice.value = 'Copilot sources saved. The next scan will use these directories.'
    await loadCopilotStatus(copilotApi.status)
  } catch (reason) { copilotError.value = messageOf(reason) }
  finally { copilotBusy.value = false }
}

async function rescanCopilot() {
  copilotError.value = ''; copilotNotice.value = ''; copilotRescanning.value = true
  try {
    await copilotApi.rescan()
    copilotNotice.value = 'Copilot rescan requested.'
    await loadCopilotStatus(copilotApi.status)
  } catch (reason) { copilotError.value = messageOf(reason) }
  finally { copilotRescanning.value = false }
}

async function rebuild() {
  rebuilding.value = true; error.value = ''; notice.value = ''
  try { await api.rebuild(); notice.value = 'Rebuild requested. The index will refresh from your source logs.'; await load(api.status) }
  catch (reason) { error.value = messageOf(reason) }
  finally { rebuilding.value = false }
}

async function copyConfiguration() {
  try { await navigator.clipboard.writeText(config.value); copied.value = true }
  catch { error.value = 'Clipboard access is unavailable. Select and copy the configuration below.' }
}
</script>
