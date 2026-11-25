import { queryString, request, type Query } from './client'
import type { Schema } from './types'

export type CopilotSettings = Schema<'CopilotSettings'>
export type CopilotIndexStatus = Schema<'CopilotIndexStatus'>
export type CopilotUsageReport = Schema<'CopilotUsageReport'>
export type CopilotFilterOptions = Schema<'CopilotFilterOptions'>

export const copilotClientName = (client: string) => ({
  'visual-studio': 'Visual Studio',
  vscode: 'VS Code',
  shared: 'Shared Copilot / CLI',
}[client] ?? client)

export const copilotApi = {
  usage: (query: Query) => request<CopilotUsageReport>(`/copilot/usage${queryString(query)}`),
  status: () => request<CopilotIndexStatus>('/copilot/index'),
  filters: () => request<CopilotFilterOptions>('/copilot/filters'),
  settings: () => request<CopilotSettings>('/copilot/settings'),
  saveSettings: (settings: CopilotSettings) => request<CopilotSettings>('/copilot/settings', { method: 'PUT', body: JSON.stringify(settings) }),
  rescan: () => request<void>('/copilot/index/rescan', { method: 'POST' }),
}
