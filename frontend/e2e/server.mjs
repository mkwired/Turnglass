import { mkdir, writeFile } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { createFixture } from './fixtures.mjs'

const frontend = path.resolve(fileURLToPath(new URL('..', import.meta.url)))
const repository = path.dirname(frontend)
const paths = await createFixture(path.join(frontend, 'test-results', 'runtime'))
const { codex, data } = paths
await mkdir(path.join(frontend, 'test-results'), { recursive: true })
await writeFile(path.join(frontend, 'test-results', 'fixture-paths.json'), JSON.stringify(paths))
const dll = path.join(repository, 'src', 'Turnglass.Web', 'bin', process.env.E2E_CONFIGURATION ?? 'Release', 'net10.0', 'Turnglass.Web.dll')
if (!existsSync(dll)) throw new Error('Build the .NET solution in Release before running browser tests.')
const port = process.env.E2E_PORT ?? '5390'
const server = spawn('dotnet', [dll, '--data-dir', data, '--webroot', path.join(frontend, 'dist'), '--urls', `http://127.0.0.1:${port}`], {
  cwd: repository,
  env: { ...process.env, CODEX_HOME: codex, TURNGLASS_COPILOT_AUTO_DISCOVER: 'false', ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
  stdio: 'inherit',
  windowsHide: true,
})
server.on('error', error => { console.error(error); process.exit(1) })
server.on('exit', code => process.exit(code ?? 0))
process.on('SIGINT', () => server.kill())
process.on('SIGTERM', () => server.kill())
