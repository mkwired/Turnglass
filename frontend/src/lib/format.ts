export const number = (value: number) => new Intl.NumberFormat('en-US').format(value)
export const compact = (value: number) => new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 2 }).format(value)
export const dollars = (value: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: value > 0 && value < 0.01 ? 6 : 2 }).format(value)

export function dateTime(value: string, timezone = 'America/New_York'): string {
  return new Intl.DateTimeFormat('en-US', { timeZone: timezone, month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(new Date(value))
}

export function dateRange(days = 30, timezone = 'America/New_York'): { from: string; to: string } {
  const to = today(timezone)
  return { from: to.subtract({ days: days - 1 }).toString(), to: to.toString() }
}

export function dateBounds(from: string, to: string, timezone = 'UTC'): { from?: string; to?: string } {
  return {
    from: from ? parseDate(from).toDate(timezone).toISOString() : undefined,
    to: to ? parseDate(to).add({ days: 1 }).toDate(timezone).toISOString() : undefined,
  }
}

export const shortProject = (project: string | null) => project ? project.split(/[\\/]/).filter(Boolean).at(-1) ?? project : 'No project'

export function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : 'Something went wrong. Please retry.'
}
import { parseDate, today } from '@internationalized/date'
