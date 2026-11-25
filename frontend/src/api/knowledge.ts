import { queryString, request, type Query } from './client'
import type { EventContext, Insight, InsightAction, InsightExport, InsightPage, ReviewProgress, ReviewPrompt } from './knowledge-types'

export const knowledge = {
  insights: (query: Query) => request<InsightPage>(`/insights${queryString(query)}`),
  insight: (id: string) => request<Insight>(`/insights/${encodeURIComponent(id)}`),
  review: (actions: InsightAction[]) => request<Insight[]>('/insights/review', { method: 'POST', body: JSON.stringify({ actions }) }),
  export: (ids: string[], format: string) => request<InsightExport>('/insights/export', { method: 'POST', body: JSON.stringify({ ids, format }) }),
  progress: () => request<ReviewProgress>('/reviews/progress'),
  prompt: (project: string) => request<ReviewPrompt>(`/reviews/prompt${queryString({ project })}`),
  context: (sessionId: string, eventId: string) => request<EventContext>(`/sessions/${encodeURIComponent(sessionId)}/events/${encodeURIComponent(eventId)}/context?before=3&after=3&maxCharacters=24000`),
}
