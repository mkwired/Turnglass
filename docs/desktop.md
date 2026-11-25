# Windows desktop client

Turnglass Desktop hosts the existing Vue application in a WinUI 3 window with WebView2. It uses the same Core and Infrastructure services and SQLite databases as the web and MCP hosts. It does not start Kestrel, open an application HTTP listener, or require the web host to be running.

The host uses WinUI 3 to satisfy the requirement for Native AOT publishing. WPF's required trimming support remains disabled in the .NET SDK. ReadyToRun would still depend on the managed runtime and would not meet that requirement. See Microsoft's [trimming compatibility notes](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities#wpf) and [Native AOT overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

## Run a published copy

Use Windows x64 with Windows 10 build 19041 or later and an installed [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). Keep the runtime updated. The desktop package includes the application and Windows App SDK dependencies, so users do not need the .NET SDK or a separately installed .NET runtime. Installing the Edge browser alone does not satisfy the WebView2 Runtime requirement. [Microsoft's runtime distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)

Copy the whole published folder and launch `Turnglass.Desktop.exe`. Its native DLLs, resources, and `wwwroot` assets must remain alongside it. This is an unpackaged portable folder, not an installer or a single-file executable.

The data directory is selected in this order:

1. `--data-dir <directory>`
2. `TURNGLASS_DATA_DIR`
3. `%LOCALAPPDATA%\Turnglass`

For example, from the repository root:

```powershell
./artifacts/publish/desktop/win-x64/Turnglass.Desktop.exe --data-dir 'D:\TurnglassData'
```

Use the same absolute directory for the desktop, web, and MCP hosts to share settings, source configuration, indexes, Copilot usage, and curated insights. Importer locks coordinate concurrent hosts. Upgrade every host before reopening a shared data directory; an older host cannot use a newer schema. The desktop also stores its WebView2 profile below `<data-dir>/WebView2`.

`CODEX_HOME` continues to identify Codex source logs. Copilot discovery and `TURNGLASS_COPILOT_AUTO_DISCOVER` behave as described in the [main README](../README.md#copilot-usage). Source logs are read-only. The Codex index and knowledge database contain transcript text and evidence excerpts; the separate Copilot ledger retains usage metadata only. Keep the data directory private and back it up as a whole. Copying the portable application folder does not move this data.

## Build and publish

Install these build prerequisites on Windows:

- Windows PowerShell 5.1 or PowerShell 7, .NET 10 SDK, and Node.js 24/npm.
- Visual Studio or Build Tools with the C++ desktop tools, **MSVC v143 or a newer compatible toolset** for x64, and **Windows SDK 10.0.26100.0** including its WinRT metadata and tools.
- Evergreen WebView2 Runtime to launch or test the application.

The desktop does not require Aspire. Open `Turnglass.slnx` in Visual Studio for all application projects and tests, or `Turnglass.Desktop.slnx` for the desktop subset. The solutions record the bridge → native adapter → desktop build order, and the main solution maps the desktop projects to x64. The native project requires Visual Studio MSBuild. `dotnet build Turnglass.Managed.slnf` builds the web/MCP projects and tests without evaluating the C++ project. Use the scripts below for a complete desktop build including frontend assets and native package restoration.

Direct project builds also work: `dotnet build src/Turnglass.Desktop/Turnglass.Desktop.csproj` builds Debug by default. The project builds the managed bridge, restores/builds the matching native adapter with Visual Studio MSBuild, and then generates its C#/WinRT projection. No previously built adapter is required, and editor design-time builds skip this native build step. To run the app, first build the frontend with `npm --prefix frontend ci` and `npm --prefix frontend run build`, or use the complete build script below. `RestoreNativeAdapterPackages=false` skips native package restoration when those packages are already present.

```powershell
# Build a debuggable desktop client and its frontend/native dependencies.
./scripts/build-desktop.ps1 -Configuration Debug

# Publish the x64 Native AOT distribution.
./scripts/publish-desktop.ps1
```

Publishing writes `artifacts/publish/desktop/win-x64`. Close an application running from that output directory before publishing again. The publisher recreates only its bounded desktop output; keep application data outside it. A normal Debug build is for development; use the Release publish when validating Native AOT.

Managed packages and frontend dependencies use checked-in lockfiles. The native adapter uses exact versions in `packages.config` plus archive checksums in `packages.sha512.json`; its helper verifies the SHA-512 pins before compiling. Evergreen WebView2 is a machine prerequisite that updates independently of the pinned build packages.

The shared application icon is authored in `frontend/public/turnglass.svg`. Its checked-in `turnglass.ico` contains ten sizes from 16 to 256 pixels and is embedded in the executable and loaded for the window icon; the frontend uses the same mark for its sidebar and favicon. After editing the SVG, regenerate the ICO and PNG with `node scripts/generate-icons.mjs` (frontend dependencies and Playwright Chromium required). Ordinary builds use the checked-in assets.

`scripts/build-desktop-adapter.ps1` can build the bridge and native adapter independently. It finds the C++ MSBuild installation using `vswhere`, supports `-MSBuildPath` for an explicit installation, and accepts `-SkipBridgeBuild` when the matching bridge WinMD already exists. `-SkipRestore` on the build/publish scripts requires dependencies to have been restored previously. Do not pass a global runtime identifier to the managed solution: each executable declares its own RID so shared library lockfiles remain portable.

## Bridge and frontend

WebView2 maps `https://turnglass.example` to the published local `wwwroot` folder. This virtual origin is a local asset mapping, not a network service. Desktop navigation uses hash routes so reloads return to the bundled document.

The frontend sends versioned JSON requests through `window.chrome.webview.hostObjects.turnglass.InvokeAsync`. A C#/WinRT component exposes only `InvokeAsync` and `Cancel`; Microsoft's `wv2winrt` generator creates the C++/WinRT dispatch adapter needed by WebView2. The adapter is generated only for the bridge and its required Windows.Foundation async types. This follows Microsoft's [custom WinRT host-object pattern](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/winrt-from-js).

The platform-neutral dispatcher has an explicit allowlist of frontend operations and calls `IViewerService`, `IKnowledgeService`, and `ICopilotUsageService` directly. Source-generated JSON contexts support strict request validation and Native AOT without reflection serialization. Work runs away from the UI thread, and closing the window cancels bridge requests and stops the hosted indexing services.

Only the bundled document receives the bridge. Frames and unexpected network resources are blocked; user-initiated external HTTP(S) links open in the default browser. Insight exports use a native save dialog. The browser application continues to use its existing HTTP API transport.

The generated native namespace is `TurnglassDesktopDispatchAdapter`, with a flat name because the current generator emits invalid C++ for dotted output namespaces. Its file is `Turnglass.DesktopDispatchAdapter.dll`; the projection metadata is `TurnglassDesktopDispatchAdapter.winmd`. Public JavaScript method names retain their Pascal case.

## Verify and diagnose

```powershell
# Publish, then run the desktop verification sequence.
./scripts/verify-desktop.ps1

# Reuse an already published desktop executable.
./scripts/verify-desktop.ps1 -SkipBuild
```

The verification sequence builds the MCP helper used by the acceptance tests, runs desktop unit checks, publishes and executes the Native AOT backend smoke application, and drives the real desktop application through Playwright's Chrome DevTools Protocol connection to WebView2. The smoke application exercises SQLite and source-generated JSON. Desktop acceptance tests use synthetic Codex/Copilot sources and isolated data directories, verify reload/navigation, shared MCP insights, clipboard and export flows, and check that the frontend makes no HTTP API requests.

The CDP listener is a test facility enabled by `--desktop-test` and the harness's explicit port setting; it is not an application API endpoint. Normal launches do not enable it. The harness launches and stops its own desktop process and uses the installed WebView2 Runtime, so it does not need a Playwright Chromium download. `TURNGLASS_DESKTOP_EXE` can select another published executable for the harness.

The separate `.github/workflows/desktop.yml` job validates the Windows C++/SDK prerequisites, installs WebView2 only if missing, publishes Native AOT, runs `verify-desktop.ps1 -SkipBuild`, and uploads the desktop package and test reports. It does not replace the existing web/MCP verification workflow.

Startup and shutdown errors are appended to `<data-dir>/desktop-errors.log` when the data directory is available. For a startup failure, check the runtime installation, writable data directory, and complete published folder first. Test reports are under `frontend/playwright-report-desktop` and `frontend/test-results`; fixture data and host diagnostics are below `frontend/test-results/desktop-runtime`. These paths contain only synthetic data when created by the supplied harness.
