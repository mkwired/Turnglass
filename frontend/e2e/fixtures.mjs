import { mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import path from 'node:path'

export async function createFixture(outputDirectory) {
  await mkdir(outputDirectory, { recursive: true })
  const runtime = await mkdtemp(path.join(outputDirectory, 'run-'))
  const codex = path.join(runtime, 'codex')
  const sessions = path.join(codex, 'sessions')
  await mkdir(sessions, { recursive: true })
  await mkdir(path.join(codex, 'archived_sessions'), { recursive: true })
  const now = new Date()
  const title = 'Build the session viewer'
  const id = 'synthetic-session-one'
  const rows = []
  function add(type, payload, timestamp = now.toISOString()) {
    rows.push({ timestamp, ordinal: rows.length + 1, type, payload })
  }
  add('session_meta', { id, title, cwd: 'D:\\projects\\session-viewer' })
  add('turn_context', { model: 'gpt-6-astra', turn_id: 'turn-1' })
  add('event_msg', { type: 'thread_settings_applied', thread_id: id, thread_settings: { model: 'gpt-6-astra', service_tier: 'default' } })
  add('response_item', { type: 'message', role: 'user', content: [{ type: 'input_text', text: 'Please build the session viewer. <img src=x onerror="window.xssDetected=true">' }] })
  add('response_item', { type: 'message', role: 'assistant', content: [{ type: 'output_text', text: 'I will inspect the parser and verify the token accounting.' }] })
  add('response_item', { type: 'function_call', name: 'run_tests', arguments: '{"project":"parser"}', call_id: 'call-1' })
  add('response_item', { type: 'function_call_output', output: 'All parser tests passed.', call_id: 'call-1' })
  add('response_item', { type: 'message', role: 'user', content: [{ type: 'input_text', text: 'For this project, use concise summaries and preserve .editorconfig.' }] })
  for (let day = 29; day >= 0; day--) {
    const timestamp = new Date(now.getTime() - day * 86400000 - 60000).toISOString()
    const input = 20000 + ((day * 31) % 17) * 9000
    const output = 5000 + ((day * 7) % 11) * 1000
    add('token_usage_record', { thread_id: id, turn_id: 'turn-1', response_id: `response-${day}`, usage: { input_tokens: input, cached_input_tokens: Math.floor(input * 0.72), cache_write_input_tokens: 0, output_tokens: output, reasoning_output_tokens: 2000, total_tokens: input + output } }, timestamp)
  }
  await writeFile(path.join(sessions, 'rollout-synthetic.jsonl'), rows.map(row => JSON.stringify(row)).join('\n') + '\n')
  await writeFile(path.join(codex, 'session_index.jsonl'), JSON.stringify({ id, thread_name: title, updated_at: now.toISOString() }) + '\n')
  await mkdir(path.join(runtime, 'empty-source'))
  const copilotRoot = path.join(runtime, 'copilot', 'session-state')
  const vscodeRoot = path.join(runtime, 'vscode', 'debug-logs')
  const copilotSessionId = 'synthetic-copilot-sdk'
  const copilotTimestamp = new Date(now.getTime() - 120000).toISOString()
  await mkdir(path.join(copilotRoot, copilotSessionId), { recursive: true })
  await mkdir(path.join(vscodeRoot, copilotSessionId), { recursive: true })
  await mkdir(path.join(vscodeRoot, 'synthetic-vscode-local'), { recursive: true })
  const copilotRecords = [
    { type: 'session.start', id: 'sdk-start', timestamp: copilotTimestamp, parentId: null, data: { sessionId: copilotSessionId, producer: 'copilot-agent', version: 1 } },
    { type: 'session.usage_record', id: 'sdk-usage', timestamp: copilotTimestamp, parentId: 'sdk-start', data: { usage: { model: 'gpt-6-astra', inputTokens: 10000, outputTokens: 2000, cacheReadTokens: 4000, cacheWriteTokens: 0, reasoningTokens: 500, apiCallId: 'synthetic-model-call', accounting: { sourceSessionId: copilotSessionId, usageId: 'synthetic-model-call', sequence: 1 } } } },
  ]
  await writeFile(path.join(copilotRoot, copilotSessionId, 'events.jsonl'), copilotRecords.map(row => JSON.stringify(row)).join('\n') + '\n')
  const debugCall = (sid, spanId, responseId, inputTokens, outputTokens, cachedTokens) => ({ v: 1, ts: Date.parse(copilotTimestamp), dur: 1000, sid, type: 'llm_request', name: 'chat:gpt-6-astra', spanId, status: 'ok', attrs: { model: 'gpt-6-astra', responseId, inputTokens, outputTokens, cachedTokens } })
  await writeFile(path.join(vscodeRoot, copilotSessionId, 'main.jsonl'), JSON.stringify(debugCall(copilotSessionId, 'sdk-mirror-span', 'synthetic-model-call', 10000, 2000, 4000)) + '\n')
  await writeFile(path.join(vscodeRoot, 'synthetic-vscode-local', 'main.jsonl'), JSON.stringify(debugCall('synthetic-vscode-local', 'vscode-span', 'synthetic-vscode-call', 2000, 300, 0)) + '\n')
  return { runtime, codex, copilotRoot, vscodeRoot, data: path.join(runtime, 'data'), emptySource: path.join(runtime, 'empty-source') }
}
