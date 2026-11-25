import type { components } from './generated'

// OpenAPI describes optional request constructor arguments and computed properties.
// ASP.NET responses always serialize these properties, including explicit nulls.
type Response<T> = T extends (infer Item)[] ? Response<Item>[]
  : T extends object ? { [Key in keyof T]-?: Response<T[Key]> }
    : Exclude<T, undefined>
export type Schema<Name extends keyof components['schemas']> = Response<components['schemas'][Name]>

export type TokenUsage = Schema<'TokenUsage'>
export type UsageTotals = Schema<'UsageTotals'>
export type SourceStatus = Schema<'SourceStatus'>
export type IndexStatus = Schema<'IndexStatus'>
export type PeriodUsage = Schema<'PeriodUsage'>
export type UsageBucket = Schema<'UsageBucket'>
export type UsageReport = Schema<'UsageReport'>
export type SessionRecord = Schema<'SessionRecord'>
export type SessionView = Schema<'SessionView'>
export type SessionDetail = Schema<'SessionDetail'>
export type EventView = Schema<'EventView'>
export type LogChunk = Schema<'LogChunk'>
export type PageResult<T> = Omit<Schema<'PageResultOfEventView'>, 'items'> & { items: T[] }
export type FilterOptions = Schema<'FilterOptions'>
export type PriceRate = Schema<'PriceRate'>
export type PricingCatalog = Schema<'PricingCatalog'>
export type ViewerSettings = Schema<'ViewerSettings'>
