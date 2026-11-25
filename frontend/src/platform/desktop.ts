interface DesktopHost {
  InvokeAsync(request: string): Promise<string>
  Cancel(id: string): Promise<void> | void
}

declare global {
  interface Window {
    __TURNGLASS_DESKTOP__?: { version: number }
    chrome?: { webview?: { hostObjects?: { turnglass?: DesktopHost } } }
  }
}

export const isDesktop = () => window.__TURNGLASS_DESKTOP__ !== undefined

const pending = new Map<string, () => void>()
let listening = false

export async function desktopResponse(path: string, options: RequestInit): Promise<Response> {
  if (window.__TURNGLASS_DESKTOP__?.version !== 1) throw new Error('This desktop connection version is not supported. Update Turnglass and try again.')
  const host = window.chrome?.webview?.hostObjects?.turnglass
  if (!host) throw new Error('The desktop connection is unavailable. Restart Turnglass and try again.')
  const method = (options.method ?? 'GET').toUpperCase()
  if (!['GET', 'POST', 'PUT'].includes(method) || (options.body != null && typeof options.body !== 'string')) throw new Error('Unsupported desktop request.')
  if (options.signal?.aborted) throw new DOMException('The request was canceled.', 'AbortError')
  if (!listening) {
    window.addEventListener('pagehide', () => { for (const cancel of [...pending.values()]) cancel() })
    listening = true
  }
  const id = crypto.randomUUID()
  const raw = await new Promise<string>((resolve, reject) => {
    let settled = false
    const finish = (result: string | undefined, error?: unknown) => {
      if (settled) return
      settled = true
      pending.delete(id)
      options.signal?.removeEventListener('abort', cancel)
      if (error !== undefined) reject(error)
      else resolve(result!)
    }
    const cancel = () => {
      finish(undefined, new DOMException('The request was canceled.', 'AbortError'))
      try { void Promise.resolve(host.Cancel(id)).catch(() => {}) } catch { /* The host may already be closing. */ }
    }
    pending.set(id, cancel)
    options.signal?.addEventListener('abort', cancel, { once: true })
    try {
      const invocation = host.InvokeAsync(JSON.stringify({ version: 1, id, method, path, body: options.body ?? null }))
      void Promise.resolve(invocation).then(value => finish(value), reason => finish(undefined, reason instanceof Error ? reason : new Error('The desktop request failed. Please retry.')))
    } catch (reason) { finish(undefined, reason) }
  })
  let result: unknown
  try { result = JSON.parse(raw) }
  catch { throw new Error('The desktop connection returned an invalid response.') }
  if (!result || typeof result !== 'object' || !('version' in result) || result.version !== 1 || !('id' in result) || result.id !== id || !('status' in result) || !Number.isInteger(result.status) || Number(result.status) < 200 || Number(result.status) > 599 || !('body' in result) || (result.body !== null && typeof result.body !== 'string')) throw new Error('The desktop connection returned an invalid response.')
  return new Response(result.body as string | null, { status: Number(result.status), headers: { 'Content-Type': 'application/json' } })
}
