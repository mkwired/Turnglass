import { createRouter, createWebHashHistory, createWebHistory } from 'vue-router'
import { isDesktop } from '@/platform/desktop'
import OverviewPage from '@/pages/OverviewPage.vue'
import SessionsPage from '@/pages/SessionsPage.vue'
import SettingsPage from '@/pages/SettingsPage.vue'
import InsightsPage from '@/pages/InsightsPage.vue'
import CopilotPage from '@/pages/CopilotPage.vue'

export const routes = [
  { path: '/', component: OverviewPage },
  { path: '/copilot', component: CopilotPage },
  { path: '/sessions', component: SessionsPage },
  { path: '/insights', component: InsightsPage },
  { path: '/settings', component: SettingsPage },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]
export const createAppHistory = () => isDesktop() ? createWebHashHistory() : createWebHistory()
export const router = createRouter({ history: createAppHistory(), routes })
