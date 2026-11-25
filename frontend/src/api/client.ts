import type { EventView, FilterOptions, IndexStatus, LogChunk, PageResult, SessionDetail, SessionView, UsageReport, ViewerSettings } from './types'
import { desktopResponse, isDesktop } from '@/platform/desktop'

export type Query = Record<string, string | number | undefined | null>

export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = isDesktop()
    ? await desktopResponse(path, options)
    : await fetch(`/api${path}`, { ...options, headers: { 'Content-Type': 'application/json', ...options.headers } })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status}). Please retry.`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export function queryString(query: Query): string {
  const entries = Object.entries(query).filter(([, value]) => value !== undefined && value !== null && value !== '')
  const encoded = new URLSearchParams(entries.map(([key, value]) => [key, String(value)])).toString()
  return encoded ? `?${encoded}` : ''
}

const segment = encodeURIComponent
export const api = {
  usage: (query: Query) => request<UsageReport>(`/usage${queryString(query)}`),
  sessions: (query: Query) => request<PageResult<SessionView>>(`/sessions${queryString(query)}`),
  session: (id: string) => request<SessionDetail>(`/sessions/${segment(id)}`),
  events: (id: string, cursor?: string | null) => request<PageResult<EventView>>(`/sessions/${segment(id)}/events${queryString({ cursor })}`),
  record: (sessionId: string, id: string, offset = 0) => request<LogChunk>(`/sessions/${segment(sessionId)}/records/${segment(id)}${queryString({ offset })}`),
  search: (query: string, sessionId?: string, cursor?: string | null) => request<PageResult<EventView>>(`/search${queryString({ query, sessionId, cursor })}`),
  filters: () => request<FilterOptions>('/filters'),
  status: () => request<IndexStatus>('/index'),
  settings: () => request<ViewerSettings>('/settings'),
  saveSettings: (settings: ViewerSettings) => request<ViewerSettings>('/settings', { method: 'PUT', body: JSON.stringify(settings) }),
  rebuild: () => request<void>('/index/rebuild', { method: 'POST' }),
}
