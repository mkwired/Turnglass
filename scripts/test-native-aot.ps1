param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'tests/Turnglass.NativeAotSmoke/Turnglass.NativeAotSmoke.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts/native-aot-smoke'

# The project owns its RID so shared library lockfiles remain portable for the web and MCP hosts.
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw "NativeAOT smoke restore failed with exit code $LASTEXITCODE." }
& dotnet publish $project -c Release --no-restore -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "NativeAOT smoke publish failed with exit code $LASTEXITCODE." }
& (Join-Path $publishDirectory 'Turnglass.NativeAotSmoke.exe')
if ($LASTEXITCODE -ne 0) { throw "NativeAOT smoke failed with exit code $LASTEXITCODE." }
