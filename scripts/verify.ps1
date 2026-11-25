param([switch]$SkipBrowser)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}
Push-Location $repositoryRoot
try {
    Invoke-Checked dotnet @('restore', 'Turnglass.Managed.slnf', '--locked-mode')
    Invoke-Checked dotnet @('build', 'Turnglass.Managed.slnf', '-c', 'Release', '--no-restore')
    Invoke-Checked dotnet @('format', 'Turnglass.Managed.slnf', '--no-restore', '--verify-no-changes', '--severity', 'info')
    $coverageDirectory = Join-Path $repositoryRoot 'artifacts/coverage/backend'
    New-Item -ItemType Directory -Force -Path $coverageDirectory | Out-Null
    Invoke-Checked dotnet @('test', '--project', 'tests/Turnglass.Tests/Turnglass.Tests.csproj', '-c', 'Release', '--no-build', '--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', 'coverage.cobertura.xml', '--coverage-settings', (Join-Path $repositoryRoot 'tests/coverage.config'), '--results-directory', $coverageDirectory)
    & (Join-Path $PSScriptRoot 'check-coverage.ps1') -ReportPath (Join-Path $coverageDirectory 'coverage.cobertura.xml')
    Push-Location (Join-Path $repositoryRoot 'frontend')
    try {
        Invoke-Checked npm @('ci')
        Invoke-Checked npm @('run', 'lint')
        Invoke-Checked npm @('run', 'build')
        Invoke-Checked npm @('run', 'test:coverage')
        if (-not $SkipBrowser) {
            Invoke-Checked npx @('playwright', 'install', 'chromium')
            Invoke-Checked npm @('run', 'test:e2e')
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}
