$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    dotnet run --project src/Turnglass.AppHost
    if ($LASTEXITCODE -ne 0) {
        throw "Aspire exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
