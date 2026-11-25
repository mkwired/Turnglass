# Turnglass

A local dashboard for inspecting Codex sessions, Copilot token usage, and estimated API-equivalent costs, available in a browser or a Windows desktop client with an independently launchable MCP server. Built with Vue 3, TypeScript, shadcn-vue, C#/.NET 10, SQLite, and Aspire; the desktop host uses WinUI 3 and WebView2.

## Start developing

Prerequisites: Windows, .NET 10 SDK (the repository also permits SDK 11), Node.js 24, npm, and the Aspire CLI **17.0.0** bundle. The application itself targets .NET 10. SQLite runs locally without a database container.

```powershell
dotnet restore Turnglass.Managed.slnf --locked-mode
npm --prefix frontend ci
./scripts/dev.ps1
```

Open the frontend resource URL in the Aspire dashboard. Aspire starts the API, frontend, health checks, and local telemetry. The AppHost uses Aspire 17's path-based `AddDotnetProject` registration; the frontend waits for the API's `/health` check before starting. Build `Turnglass.Managed.slnf` to compile the web/MCP projects and tests, since building the AppHost alone no longer builds the API. Open `Turnglass.slnx` in Visual Studio to see all projects, including the desktop client, native adapter, and desktop tests. The desktop build requires the C++ tools described below. For development without Aspire, run these in separate terminals:

```powershell
dotnet run --project src/Turnglass.Web -- --data-dir .local/data
npm --prefix frontend run dev
```

The standalone API uses `http://127.0.0.1:5278`; Vite uses `http://127.0.0.1:5173`. Vite proxies `/api` to the API; `API_URL` can override its destination. The web, desktop, and MCP hosts use `--data-dir`, then `TURNGLASS_DATA_DIR`, then the default local data directory. Use an absolute directory when sharing state between launchers with different working directories.

## Publish and run

```powershell
./scripts/publish.ps1
./artifacts/publish/web/Turnglass.Web.exe
```

Open `http://127.0.0.1:5278`. Publishing includes the compiled frontend and separately produces `artifacts/publish/mcp/Turnglass.Mcp.exe`. Published applications require the .NET 10 runtime (ASP.NET Core runtime for the web host).

The publish script clears and recreates only the selected output directory's `web` and `mcp` children before publishing, so old binaries do not remain in the distribution. The output defaults to the repository's `artifacts/publish` directory and must be below the repository's `artifacts` directory. Stop hosts running from the selected output directories before publishing. Keep source logs and application data outside these output directories.

To publish alongside hosts still using the default package, run `./scripts/publish.ps1 -OutputDirectory artifacts/publish/turnglass`; the separate package's executables will be in `artifacts/publish/turnglass/web` and `artifacts/publish/turnglass/mcp`.

The default data directory is `%LOCALAPPDATA%\Turnglass`. It contains the SQLite index, settings, pricing catalog, importer lock, and a separate `knowledge.db` for curated insights and review progress. Rebuilding the session index preserves this knowledge database. Use a local disk, and keep this directory private: it contains session text and evidence excerpts. The web application binds to loopback and validates browser origins. No OpenAI API key is needed.

## Windows desktop client

The desktop client hosts the same Vue interface in WinUI 3/WebView2 and calls the backend services through a generated native host-object bridge. It does not start an HTTP server. Release publishing uses Native AOT; the portable x64 folder includes its .NET and Windows App SDK dependencies and requires the installed [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

Building requires Windows PowerShell 5.1 or PowerShell 7, .NET 10 SDK, Node.js 24, Visual Studio C++ Build Tools with MSVC v143 or a newer compatible toolset, and Windows SDK **10.0.26100.0**. From the repository root:

```powershell
./scripts/build-desktop.ps1 -Configuration Debug
./scripts/publish-desktop.ps1
./artifacts/publish/desktop/win-x64/Turnglass.Desktop.exe
```

Copy the entire `artifacts/publish/desktop/win-x64` folder when distributing it. Settings, Codex indexes, Copilot history, and approved insights use the same data directory as the web and MCP hosts; pass `--data-dir` to select another directory. Startup diagnostics are written to `<data-dir>/desktop-errors.log` when that directory is available.

Run `./scripts/verify-desktop.ps1` for Native AOT smoke checks and acceptance tests against the published WebView2 application. `-SkipBuild` reuses an existing desktop publish. See [desktop architecture, prerequisites, and verification](docs/desktop.md) for details. The desktop solution and Windows CI workflow are separate from the existing web/MCP solution.

## Upgrading from Codex Session Viewer

Turnglass uses new executable names, the MCP configuration key `turnglass`, and the environment variables `TURNGLASS_DATA_DIR` and `TURNGLASS_COPILOT_AUTO_DISCOVER`. The old `CODEX_SESSION_VIEWER_DATA_DIR` and `CODEX_SESSION_VIEWER_COPILOT_AUTO_DISCOVER` variables are ignored; there are no compatibility aliases. `CODEX_HOME` still identifies the Codex log source and keeps its existing meaning.

Existing data is not moved or imported automatically from `%LOCALAPPDATA%\CodexSessionViewer`. Stop the old web and MCP hosts and back up that entire directory before upgrading. To keep using its index, settings, approved insights, and retained Copilot usage, pass the same existing directory explicitly to both new hosts:

```powershell
./artifacts/publish/web/Turnglass.Web.exe --data-dir "$env:LOCALAPPDATA\CodexSessionViewer"
```

Replace the old MCP entry with `[mcp_servers.turnglass]`, point `command` at `Turnglass.Mcp.exe`, and set `args = ['--data-dir', 'C:\Users\YOUR_NAME\AppData\Local\CodexSessionViewer']`. Use your actual absolute path. The desktop executable accepts the same `--data-dir` override. Omitting the override starts with the separate `%LOCALAPPDATA%\Turnglass` directory. Restart all hosts together; do not run old and new versions against the same data directory.

## Connect Codex through MCP

Publish first, then add an entry to your Codex configuration using your actual absolute paths:

```toml
[mcp_servers.turnglass]
command = 'D:\source\turnglass\artifacts\publish\mcp\Turnglass.Mcp.exe'
args = ['--data-dir', 'C:\Users\YOUR_NAME\AppData\Local\Turnglass']
startup_timeout_sec = 20
tool_timeout_sec = 60
```

Settings also provides a copyable configuration. Codex launches the stdio server itself; the web and desktop apps may be closed. The server writes protocol traffic to stdout and diagnostics to stderr. The apps and multiple MCP processes can share one data directory safely.

Session logs remain read-only. Available retrieval tools:

| Tool | Purpose |
| --- | --- |
| `list_sessions` | Filter and page through sessions and their usage |
| `get_session` | Inspect metadata, child sessions, and own/family totals |
| `read_session_events` | Read chronological messages and tool activity |
| `read_log_record` | Retrieve raw JSON records in bounded chunks |
| `search_session_logs` | Search indexed text and retrieve matching event IDs |
| `get_usage` | Query calendar/rolling totals and daily trends |
| `get_index_status` | Check freshness, completion, warnings, and source availability |
| `get_copilot_usage` | Query recorded Copilot tokens and estimated costs, independently of Codex |
| `get_copilot_index_status` | Check Copilot source availability, observed coverage, and skipped records |
| `get_review_packet` | Retrieve a bounded packet of new content for an incremental review |
| `get_event_context` | Read nearby events around a specific piece of evidence |
| `search_insights` | Retrieve approved global and matching project knowledge |

`submit_review` saves proposed findings as drafts and records review progress atomically. It is the only tool that writes findings; it cannot approve them, change source logs, or modify instruction files. Retrieving a review packet may save its snapshot for reliable continuation but does not mark its content reviewed.

For example: “Use turnglass to find my highest-token sessions this week, inspect their tool calls, and suggest ways to reduce repeated work.” Tools accept indexed IDs, not arbitrary filesystem paths. Follow pagination cursors and raw-record offsets to retrieve additional content. Read `initialIndexComplete`, `isUsageComplete`, warnings, and pricing completeness before drawing conclusions.

## Copilot usage

Open **Copilot** for daily, weekly, and monthly recorded tokens and API-equivalent USD estimates. Calendar and rolling periods use the same reporting timezone as Codex. Filter by model or client: **Visual Studio**, **VS Code**, or **Shared Copilot / CLI**. Shared SDK records may include CLI or other clients; the viewer only attributes them to an editor when source or request identity provides evidence. Codex totals, sessions, transcripts, and Insights remain separate.

Automatic discovery reads these local sources without changing them:

| Source | Default location on Windows |
| --- | --- |
| Visual Studio SDK chat | `%LOCALAPPDATA%\Microsoft\VisualStudio\CopilotCli\session-state` |
| Shared Copilot SDK | `%USERPROFILE%\.copilot\session-state` |
| VS Code | `%APPDATA%\Code\User` workspace/global/profile `GitHub.copilot-chat\debug-logs` directories |
| VS Code Insiders | The corresponding `%APPDATA%\Code - Insiders\User` directories |

In **Settings → Copilot sources**, enable automatic discovery or add explicit source directories. SDK sources can point at `session-state`, its parent, or an individual session directory. VS Code sources can point at `User`, `workspaceStorage`, `globalStorage`, an extension's `debug-logs` directory, or an individual debug session. A disabled explicit source overrides automatic discovery for that same kind and path. Removing or disabling a source stops collection and retains its recorded history.

Visual Studio uses its SDK-based chat experience (opt-in in 18.11, enabled by default in 18.12); the session directory is created when that experience persists a session. The path was verified against the installed 18.12 integration. Legacy `.vs` chat histories contain model capacity fields rather than complete token consumption and are not imported. [Microsoft's SDK chat announcement](https://devblogs.microsoft.com/visualstudio/copilot-in-visual-studio-just-got-a-major-upgrade/)

For VS Code local chat, enable `github.copilot.chat.agentDebugLog.fileLogging.enabled` in settings and reload the window if needed. This captures future model calls; ordinary chat history and diagnostic output cannot reconstruct missing past usage. The viewer reads `llm_request` records from parent and child debug logs. [VS Code debug documentation](https://code.visualstudio.com/docs/agents/agent-troubleshooting/chat-debug-view), [logger implementation](https://github.com/microsoft/vscode/blob/main/extensions/copilot/src/extension/chat/vscode-node/chatDebugFileLoggerService.ts)

VS Code's newer agent-host sessions can instead write SDK usage to `%USERPROFILE%\.copilot\session-state`. Automatic discovery includes this location. These calls appear under **Shared Copilot / CLI** unless a matching editor record identifies their origin; select **All clients** to include them. Enabling debug logging does not guarantee that an agent-host session also produces `llm_request` records.

SDK sources use per-call `session.usage_record` events, including rotated `events.N.jsonl` archives. Stable accounting IDs deduplicate copied history; matching response IDs and session families prevent SDK calls mirrored in VS Code logs from being added again. Model capacities, context snapshots, quota percentages, premium requests, and unverified aggregate records are never converted into token usage. Only usage metadata is retained, not prompts, responses, or tool output.

Source scanning runs approximately every five seconds. **Rescan Copilot sources** reparses available logs without erasing history. A completed scan describes ingestion, not complete account coverage: log retention, disabled logging, missing counters, or older formats can leave gaps. The page distinguishes absent measurements from a period with no recorded calls and reports skipped or ambiguous records. Known cache counts are input subsets; unknown cache counters are not observed zeros.

Copilot history lives in **`copilot-usage.db`**, independently of the Codex index and `knowledge.db`. Back it up when moving or backing up the data directory: retained usage may no longer exist in rotated editor logs. Codex rebuilds never clear this ledger. Web, desktop, and MCP hosts can collect safely through the separate Copilot importer lock. For isolated test hosts, set `TURNGLASS_COPILOT_AUTO_DISCOVER=false` before first initialization; saved source settings subsequently take precedence.

Copilot REST endpoints are `/api/copilot/usage`, `/index`, `/filters`, `/settings` (GET/PUT), and `/index/rescan` (POST), relative to `/api/copilot`. Usage accepts `mode`, `timezone`, `client`, `model`, `from`, and `to`. Client values are `visual-studio`, `vscode`, and `shared`; omit the filter for all clients. MCP exposes the same report and source status through the two read-only Copilot tools.

To validate live capture, complete a small chat using each editor's supported experience and compare the resulting input/output counts with its per-call usage log. Select **All clients** when checking VS Code agent-host sessions, since they may be attributed to the shared runtime. Repeat Rescan and confirm totals do not increase. A missing source remains unavailable until actual usage records exist; synthetic fixtures alone do not certify an editor version's emitted format.

## Review preferences and lessons

Open **Insights** and copy the review prompt into Codex. Choose a project and date range, or start with the last 30 days. Codex reads compact review packets, investigates relevant messages and tool outcomes, and submits candidate findings to the inbox. Reviews are on demand and use the existing Codex connection; the viewer does not call a separate model API.

Each packet is limited to 24,000 characters. The prompt limits a review run to 120,000 characters and asks Codex to report remaining work and its continuation cursor. These are character budgets, not exact token counts. Progress advances only after a successful submission, including an explicitly empty findings list. Repeated submissions with the same idempotency key do not duplicate findings. New log content remains eligible for later review; changed source history is flagged for re-review.

The inbox distinguishes preferences, lessons, decisions, and workflows. Findings retain their scope (global, project, or session), explicit/inferred basis, supporting excerpts, and review history. One-time instructions remain narrowly scoped. Original user statements can support preferences; copied subagent history, injected environment text, assistant suggestions, and unavailable reasoning do not establish user preferences. Lessons should describe an observable problem, resolution, and applicable conditions.

Use evidence links to open the exact transcript event and neighboring messages. Missing or changed evidence is flagged. Approve, edit, dismiss, merge duplicates, or explicitly supersede outdated guidance. Conflicting findings remain visible until you resolve them. Batch approval is available for selected candidates.

Approved findings are available through `search_insights`. For example: “Before working on this project, retrieve relevant approved preferences and lessons from turnglass, then follow the current task instructions.” Historical findings are reference material; they do not override current instructions.

Select approved findings to preview and export Markdown guidance or JSON. Downloads and copied text do not modify `AGENTS.md` or other instruction files. Review the scope and wording before explicitly incorporating guidance into those files. Preserve `knowledge.db` when moving your data directory: curated approvals cannot be reconstructed from the source logs alone.

## Data and accounting

The first launch discovers `CODEX_HOME`, or `~/.codex`, and reads its `sessions` and `archived_sessions` directories. Add or change source directories in Settings. Source logs are never modified. The application processes complete JSONL lines, periodically reconciles changes, and checkpoints committed batches. It retains the existing index for unavailable sources; successful scans reconcile deletions and moves. Rebuild regenerates the index from the configured sources.

Modern per-response token records are authoritative and deduplicated. Cumulative counters and compaction copies are not added to them. Older logs use validated cumulative deltas; ambiguous inherited history, counter resets, and format transitions generate explicit warnings. Parent metadata copied into a child log does not replace its identity. Global totals count each usage record once; family totals include descendants and should not be added to overlapping parent totals.

Calendar summaries use local midnight, Monday-start weeks, and month boundaries. The default timezone is `America/New_York`. Rolling windows are exactly 24 hours, 7 days, and 30 days, including across daylight-saving changes. Usage belongs to its response timestamp. Project/model filters apply to summary cards; chart date filters control the chart range.

The transcript includes messages, available recorded reasoning, tool calls/results, and compaction summaries. Encrypted reasoning is marked unavailable. Unknown raw record types remain inspectable. Large records are paged/chunked; log content is escaped and treated as data.

## Pricing

Costs are **estimated API-equivalent USD values**, not a bill or subscription allowance. The selected pricing snapshot also values historical usage; editing rates recalculates estimates without reimporting logs. Rates originate from [OpenAI pricing](https://developers.openai.com/api/docs/pricing) and the linked model documentation.

The initial catalog includes GPT-6 Astra, GPT-6.2 Astra, and GPT-6.2 Sol, with Standard/Fast/Batch/Ultrafast and short/long context bands. Its date is November 21, 2026; GPT-6.2 Ultrafast uses the promotional snapshot. Update the catalog when rates change. Other models remain unpriced until an explicit rate or mapping is supplied.

Ordinary input is input minus cached reads and cache writes. Reasoning is already part of output. Service tiers and per-request context thresholds affect prices. Unknown model/tier/context or cache-write retention produces an incomplete cost, displaying a known subtotal and unpriced counts instead of silently reporting zero. Decimal arithmetic preserves precision until display.

Copilot uses this same editable catalog. Missing service tiers use a visibly stated standard-API assumption; input with missing cache detail uses a labeled uncached-input scenario. Recorded cache counts take precedence. Known cache writes with unknown retention remain unpriced until a supported lifetime is recorded or explicitly configured. This is not an upper-bound estimate, a Copilot bill, AI Credits, or a subscription allowance; the SDK's `cost` field is not treated as USD. Cache-write rates and model overrides support `5m`, `1h`, `30m`, and `12h`.

A one-time update adds missing standard model/tier combinations for the following observed Copilot models. It preserves all existing custom bands and does not re-add prices users later remove. The snapshot is November 25, 2026; all values below are USD per million tokens.

| Model | Input | Cached input | Output | 5m writes | 1h writes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Claude Haiku 4.5 | 1 | 0.10 | 5 | 1.25 | 2 |
| Claude Sonnet 4.5 / 4.6 | 3 | 0.30 | 15 | 3.75 | 6 |
| Claude Opus 4.6 | 5 | 0.50 | 25 | 6.25 | 10 |
| GPT-5.3-Codex | 1.75 | 0.175 | 14 | — | — |
| GPT-5.4, up to 272K input | 2.50 | 0.25 | 15 | — | — |
| GPT-5.4, over 272K input | 5 | 0.50 | 22.50 | — | — |
| GPT-6 Luna, up to 272K input | 0.10 | 0.01 | 0.50 | — | — |
| GPT-6 Luna, over 272K input | 0.20 | 0.02 | 0.75 | — | — |
| MAI-Code-1.1-Flash | 0.20 | 0.02 | 1.20 | — | — |

GPT-6 Luna also includes 30m/12h cache-write rates: 0.125/0.25 up to 272K input and 0.25/0.50 above that threshold.

Sources: [Anthropic standard API pricing](https://platform.claude.com/docs/en/about-claude/pricing), [GPT-5.4 pricing](https://developers.openai.com/api/docs/models/gpt-5.4), [OpenAI text-token pricing](https://developers.openai.com/api/docs/pricing#text-tokens), [GPT-6 Luna context bands](https://developers.openai.com/api/docs/models/gpt-6-luna), and [GitHub published model rates](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing). MAI uses GitHub's published reference rate. Verified Haiku `claude-haiku-4-5-20251001` and GPT-5.4 `gpt-5.4-2026-03-05` response IDs receive explicit aliases, preserving existing user mappings. Estimates use the selected catalog snapshot and per-call input bands, excluding discounts, regional premiums, tool fees, and subscription terms. Unknown models stay unpriced until configured.

## Verify

```powershell
./scripts/verify.ps1
```

This restores locked dependencies, builds with analyzers, checks `.editorconfig` formatting, runs xUnit v3/Microsoft Testing Platform tests, enforces at least **80% backend line coverage**, lints and builds/type-checks Vue, enforces **80% frontend line and branch coverage**, and runs Playwright acceptance tests with synthetic logs. `-SkipBrowser` omits only browser acceptance tests.

Vue components use `<template>`, `<script>`, then `<style>` block order. ESLint also enforces template readability with one attribute per line, two-space indentation, blank lines between sibling elements, and line breaks around block content and multiline tag brackets. Literal content in `pre`, `textarea`, and evidence `blockquote` elements keeps its whitespace; inline text also retains meaningful spacing. These rules apply to all Vue files, including the shadcn components, during local verification and Windows CI. Run `npm --prefix frontend run lint` to check or `npm --prefix frontend run lint:fix` to fix the order and formatting automatically.

Backend coverage includes authored Core, Infrastructure, Web, and MCP code; Aspire scaffolding, tests, and generated code are excluded. Frontend coverage includes application pages/components/composables, excluding untouched shadcn primitives and generated API contracts. Reports are written under `artifacts/coverage/backend` and `frontend/coverage`. Windows CI runs the same checks and publishes application artifacts.

REST contracts are available at `/openapi/v1.json`. With the API running, regenerate the checked-in frontend contracts using `npm --prefix frontend run generate:api`.

## Layout

| Project | Responsibility |
| --- | --- |
| Core | Contracts, token accounting, pricing, periods, validation |
| Infrastructure | SQLite, full-text search, JSONL parsing, checkpoints, indexing |
| Web | Local REST API, OpenAPI, published Vue assets |
| Desktop | WinUI 3/WebView2 host, Native AOT publishing, local assets and lifecycle |
| DesktopBridge / DesktopDispatchAdapter | C#/WinRT contract and generated native WebView2 host-object adapter |
| Mcp | Read-only log access, draft review submission, and approved insight retrieval |
| AppHost / ServiceDefaults | Aspire orchestration, health, telemetry |
| frontend | Vue dashboard, session explorer, settings, browser tests |
| tests | xUnit v3 unit, integration, HTTP, and MCP process tests |

Restart all hosts together when upgrading. A host rejects an index schema newer than it understands. If an old process was forcibly stopped, its operating-system lock is released automatically; the persistent lock file itself is not evidence of an active importer.
