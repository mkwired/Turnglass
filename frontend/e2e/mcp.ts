import { spawn } from 'node:child_process'
import { readFile } from 'node:fs/promises'
import { createInterface } from 'node:readline'
import path from 'node:path'

export async function connectFixtureMcp(fixture?: { data: string; codex: string }) {
  const paths = fixture ?? JSON.parse(await readFile('test-results/fixture-paths.json', 'utf8')) as { data: string; codex: string }
  const child = spawn('dotnet', [path.resolve('../src/Turnglass.Mcp/bin', process.env.E2E_CONFIGURATION ?? 'Release', 'net10.0/Turnglass.Mcp.dll'), '--data-dir', paths.data], {
    env: { ...process.env, CODEX_HOME: paths.codex, TURNGLASS_COPILOT_AUTO_DISCOVER: 'false', DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
  })
  let sequence = 0
  let diagnostics = ''
  child.stderr.on('data', data => { diagnostics = (diagnostics + String(data)).slice(-4000) })
  const pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>()
  const fail = (error: Error) => {
    for (const item of pending.values()) { clearTimeout(item.timer); item.reject(error) }
    pending.clear()
  }
  child.on('error', fail)
  child.on('exit', code => fail(new Error(`MCP exited ${code}: ${diagnostics}`)))
  const lines = createInterface({ input: child.stdout })
  lines.on('line', line => {
    try {
      const message = JSON.parse(line) as { id?: number; result?: unknown; error?: unknown }
      if (message.id === undefined) return
      const item = pending.get(message.id)
      if (!item) return
      pending.delete(message.id)
      clearTimeout(item.timer)
      if (message.error) item.reject(new Error(JSON.stringify(message.error)))
      else item.resolve(message.result)
    } catch (error) { fail(new Error(`MCP stdout must contain JSON-RPC only: ${String(error)}`)) }
  })
  function request(method: string, params: unknown = {}): Promise<unknown> {
    const id = ++sequence
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error(`MCP timed out: ${method}`)) }, 20000)
      pending.set(id, { resolve, reject, timer })
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n')
    })
  }
  await request('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'insights-browser-acceptance', version: '1.0' } })
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n')
  return {
    async call<T>(name: string, args: unknown = {}): Promise<T> {
      const result = await request('tools/call', { name, arguments: args }) as { structuredContent?: T; isError?: boolean; content: { type: string; text: string }[] }
      if (result.isError) throw new Error(JSON.stringify(result.content))
      return result.structuredContent ?? JSON.parse(result.content.find(item => item.type === 'text')!.text) as T
    },
    async close() {
      const closed = new Promise<void>(resolve => child.once('exit', () => resolve()))
      child.stdin.end()
      const timer = setTimeout(() => child.kill(), 5000)
      await closed
      clearTimeout(timer)
      lines.close()
    },
  }
}
