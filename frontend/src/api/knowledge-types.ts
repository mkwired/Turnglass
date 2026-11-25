import type { components } from './generated'
import type { Schema } from './types'

export type EvidenceInput = components['schemas']['EvidenceInput']
export type InsightDraft = components['schemas']['InsightDraft']
export type InsightAction = components['schemas']['InsightAction']
export type InsightEvidence = Schema<'InsightEvidence'>
export type InsightRevision = Schema<'InsightRevision'>
export type Insight = Schema<'Insight'>
export type InsightPage = Schema<'InsightPage'>
export type ReviewProgress = Schema<'ReviewProgress'>
export type ReviewPrompt = Schema<'ReviewPrompt'>
export type InsightExport = Schema<'InsightExport'>
export type EventContext = Schema<'EventContext'>
