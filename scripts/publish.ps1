param(
    [switch]$SkipRestore,
    [ValidateNotNullOrEmpty()][string]$OutputDirectory = 'artifacts/publish'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).ProviderPath
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$publishRoot = $publishRoot.TrimEnd([char[]]@('\', '/'))
$artifactsPrefix = $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $publishRoot.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a directory below this repository's artifacts directory."
}
function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}
function Reset-PublishDirectory {
    param([ValidateSet('web', 'mcp')][string]$Name)
    $outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $Name))
    if (-not $outputDirectory.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetDirectoryName($outputDirectory) -ne $publishRoot) {
        throw 'Publish output must stay inside the selected artifacts directory.'
    }
    $directory = $outputDirectory
    while ($true) {
        if (Test-Path -LiteralPath $directory) {
            $item = Get-Item -LiteralPath $directory -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                throw "Publish output must use ordinary directories, without links or junctions: $directory"
            }
        }
        if ([string]::Equals($directory, $artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $directory = [System.IO.Path]::GetDirectoryName($directory)
    }
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    return $outputDirectory
}
Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-Checked dotnet @('restore', 'Turnglass.Managed.slnf', '--locked-mode')
    }
    Push-Location (Join-Path $repositoryRoot 'frontend')
    try {
        if (-not $SkipRestore) {
            Invoke-Checked npm @('ci')
        }
        Invoke-Checked npm @('run', 'build')
    }
    finally {
        Pop-Location
    }
    $webOutput = Reset-PublishDirectory 'web'
    $mcpOutput = Reset-PublishDirectory 'mcp'
    Invoke-Checked dotnet @('publish', 'src/Turnglass.Web', '-c', 'Release', '--no-restore', '-o', $webOutput)
    Invoke-Checked dotnet @('publish', 'src/Turnglass.Mcp', '-c', 'Release', '--no-restore', '-o', $mcpOutput)
    Write-Output "Published Turnglass web and MCP hosts under $publishRoot. Both require the .NET 10 runtime."
}
finally {
    Pop-Location
}
