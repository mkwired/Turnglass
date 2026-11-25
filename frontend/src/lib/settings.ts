import type { ViewerSettings } from '@/api/types'

export function formatMappings(values: Record<string, string>): string {
  return Object.entries(values).map(([key, value]) => `${key} = ${value}`).join('\n')
}

export function parseMappings(text: string): Record<string, string> {
  const result: Record<string, string> = {}
  for (const line of text.split('\n').map(line => line.trim()).filter(Boolean)) {
    const separator = line.indexOf('=')
    const key = line.slice(0, separator).trim()
    const value = line.slice(separator + 1).trim()
    if (separator < 1 || !key || !value) throw new Error('Each mapping must have a name and value: name = value.')
    if (Object.hasOwn(result, key)) throw new Error(`Duplicate mapping: ${key}.`)
    result[key] = value
  }
  return result
}

export function validateSettings(settings: ViewerSettings): void {
  if (!settings.sources.length) throw new Error('Add at least one source directory.')
  try { new Intl.DateTimeFormat('en-US', { timeZone: settings.timezone }) }
  catch { throw new Error('Enter a valid timezone, such as America/New_York or UTC.') }
  if (!settings.pricing.version.trim()) throw new Error('Give the pricing catalog a version or date.')
  for (const rate of settings.pricing.rates) {
    if (!rate.model.trim() || !rate.tier.trim()) throw new Error('Every price row needs a model and tier.')
    const numbers = [rate.minInputTokens, rate.inputPerMillion, rate.cachedInputPerMillion, rate.outputPerMillion, rate.maxInputTokens, rate.cacheWrite5mPerMillion, rate.cacheWrite1hPerMillion, rate.cacheWrite30mPerMillion, rate.cacheWrite12hPerMillion].filter(value => value != null)
    if (numbers.some(value => !Number.isFinite(value) || value < 0)) throw new Error('Rates and token boundaries must be nonnegative numbers.')
    if (rate.maxInputTokens !== null && rate.maxInputTokens < rate.minInputTokens) throw new Error('A context band’s maximum must be at least its minimum.')
  }
}

export function mcpConfiguration(executable: string, dataDirectory: string): string {
  const args = dataDirectory.trim() ? ['--data-dir', dataDirectory.trim()] : []
  return `[mcp_servers.turnglass]\ncommand = ${JSON.stringify(executable)}\nargs = ${JSON.stringify(args)}\nstartup_timeout_sec = 20\ntool_timeout_sec = 60`
}
