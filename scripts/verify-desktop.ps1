param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'publish-desktop.ps1') }
& (Join-Path $PSScriptRoot 'test-native-aot.ps1')
& dotnet restore (Join-Path $repositoryRoot 'src/Turnglass.Mcp/Turnglass.Mcp.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'MCP test host restore failed.' }
& dotnet build (Join-Path $repositoryRoot 'src/Turnglass.Mcp/Turnglass.Mcp.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'MCP test host build failed.' }
& dotnet restore (Join-Path $repositoryRoot 'tests/Turnglass.Desktop.Tests/Turnglass.Desktop.Tests.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Desktop unit test restore failed.' }
& dotnet test --project (Join-Path $repositoryRoot 'tests/Turnglass.Desktop.Tests/Turnglass.Desktop.Tests.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Desktop unit tests failed.' }
foreach ($relative in @('src/Turnglass.Desktop', 'src/Turnglass.DesktopBridge', 'tests/Turnglass.Desktop.Tests', 'tests/Turnglass.NativeAotSmoke')) {
    $directory = Join-Path $repositoryRoot $relative
    $project = Join-Path $directory ((Split-Path -Leaf $directory) + '.csproj')
    & dotnet format $project --no-restore --verify-no-changes --include $directory --severity info
    if ($LASTEXITCODE -ne 0) { throw "Desktop formatting check failed for $relative." }
}
Push-Location (Join-Path $repositoryRoot 'frontend')
try {
    & npm run test:desktop
    if ($LASTEXITCODE -ne 0) { throw "Desktop acceptance tests failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
