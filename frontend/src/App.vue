<template>
  <a
    href="#main"
    class="sr-only focus:not-sr-only focus:fixed focus:z-50 focus:bg-card focus:p-4"
    @click.prevent="main?.focus()"
  >Skip to content</a>

  <div class="min-h-screen lg:grid lg:grid-cols-[240px_1fr]">
    <aside class="border-b bg-card lg:fixed lg:inset-y-0 lg:w-60 lg:border-r lg:border-b-0">
      <RouterLink
        to="/"
        class="flex items-center gap-3 px-6 py-7"
        aria-label="Turnglass home"
      >
        <img
          src="/turnglass.svg"
          alt=""
          width="40"
          height="40"
          class="size-10 shrink-0"
        >

        <span><strong class="block text-base font-semibold tracking-tight">Turnglass</strong>

          <span class="text-xs text-muted-foreground">Understand your AI work.</span></span>
      </RouterLink>

      <nav
        aria-label="Main navigation"
        class="flex flex-wrap gap-1 px-3 pb-3 lg:flex-col lg:pt-4"
      >
        <RouterLink
          v-for="link in links"
          :key="link.to"
          :to="link.to"
          :exact-active-class="'!bg-accent !text-accent-foreground'"
          class="flex flex-1 items-center gap-3 rounded-lg px-4 py-3 text-sm font-medium text-muted-foreground transition-colors hover:bg-muted lg:flex-none"
        >
          <component
            :is="link.icon"
            :size="18"
          />

          <span>{{ link.text }}</span>
        </RouterLink>
      </nav>

      <div class="hidden px-6 lg:absolute lg:inset-x-0 lg:bottom-7 lg:block">
        <div class="mb-5 rounded-lg border bg-background p-3">
          <div class="mb-1 flex items-center gap-2 text-sm font-medium">
            <Activity
              :size="14"
              class="text-primary"
            /> Local workspace
          </div>

          <p class="text-xs leading-relaxed text-muted-foreground">
            Your session history stays on this machine.
          </p>
        </div>

        <label class="flex items-center gap-2 text-muted-foreground">
          <Monitor :size="16" />

          <span class="sr-only">Color theme</span>

          <select
            v-model="theme"
            aria-label="Color theme"
            class="h-8 flex-1 border-0 bg-transparent text-xs"
          >
            <option value="auto">System theme</option>

            <option value="light">Light theme</option>

            <option value="dark">Dark theme</option>
          </select>
        </label>
      </div>
    </aside>

    <div class="min-w-0 lg:col-start-2">
      <header class="flex min-h-16 items-center justify-between gap-3 border-b px-5 text-sm lg:px-10">
        <span class="text-muted-foreground">Workspace <span class="mx-2 opacity-40">/</span>

          <span class="font-medium text-foreground">Session analytics</span></span>

        <span class="flex items-center gap-2 text-xs text-muted-foreground"><span
                                                                              class="size-1.5 rounded-full"
                                                                              :class="status?.initialIndexComplete ? 'bg-primary' : 'bg-amber-500'"
                                                                            />

          {{ status?.initialIndexComplete ? 'Index up to date' : 'Index warming up' }}<ArrowUpRight
            :size="14"
            class="hidden sm:block"
          /></span>
      </header>

      <main
        id="main"
        ref="main"
        tabindex="-1"
        class="mx-auto max-w-[1600px] px-5 py-8 lg:px-10 lg:py-10"
      >
        <RouterView />
      </main>

      <footer class="mx-5 mt-8 flex flex-wrap justify-between gap-2 border-t py-5 text-xs text-muted-foreground lg:mx-10">
        <span>Turnglass</span>

        <span>USD values are API-equivalent estimates.</span>
      </footer>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, RouterView } from 'vue-router'
import { useColorMode } from '@vueuse/core'
import { Activity, ArrowUpRight, ChartNoAxesCombined, FolderClock, Lightbulb, Settings2, Monitor, Bot } from '@lucide/vue'
import { api } from '@/api/client'
import { useAutoRefresh } from '@/composables/useAutoRefresh'
import { useLoader } from '@/composables/useLoader'
import type { IndexStatus } from '@/api/types'

const theme = useColorMode({ emitAuto: true })
const main = ref<HTMLElement | null>(null)
const { data: status, load } = useLoader<IndexStatus>()
useAutoRefresh(() => load(api.status))
const links = [
  { to: '/', text: 'Overview', icon: ChartNoAxesCombined },
  { to: '/copilot', text: 'Copilot', icon: Bot },
  { to: '/sessions', text: 'Codex', icon: FolderClock },
  { to: '/insights', text: 'Insights', icon: Lightbulb },
  { to: '/settings', text: 'Settings', icon: Settings2 },
]
</script>
