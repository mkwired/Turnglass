param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipRestore,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).ProviderPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $IsWindows) { throw 'The desktop client must be built on Windows.' }
if ($Publish -and $Configuration -ne 'Release') { throw 'Publish the Native AOT client in Release configuration.' }

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-Checked npm @('--prefix', 'frontend', 'ci')
        # Each executable owns its RID. A global -r changes shared library lockfiles.
        Invoke-Checked dotnet @('restore', 'src/Turnglass.Desktop/Turnglass.Desktop.csproj', '--locked-mode')
    }
    Invoke-Checked npm @('--prefix', 'frontend', 'run', 'build')
    $nativeRestore = if ($SkipRestore) { '-p:RestoreNativeAdapterPackages=false' } else { '-p:RestoreNativeAdapterPackages=true' }
    if (-not $Publish) {
        Invoke-Checked dotnet @('build', 'src/Turnglass.Desktop/Turnglass.Desktop.csproj', '-c', $Configuration, '--no-restore', $nativeRestore)
        return
    }

    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    $output = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish/desktop/win-x64'))
    if (-not $output.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Desktop publish output must remain inside this repository artifacts directory.'
    }
    $directory = $output
    while ($true) {
        if (Test-Path -LiteralPath $directory) {
            $item = Get-Item -LiteralPath $directory -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Desktop publish output must use ordinary directories: $directory"
            }
        }
        if ([string]::Equals($directory, $artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $directory = [IO.Path]::GetDirectoryName($directory)
    }
    $executable = Join-Path $output 'Turnglass.Desktop.exe'
    if (Get-Process -Name 'Turnglass.Desktop' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
        throw 'Close the previously published Turnglass desktop client before publishing.'
    }
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    Invoke-Checked dotnet @('publish', 'src/Turnglass.Desktop/Turnglass.Desktop.csproj', '-c', 'Release', '--no-restore', '-o', $output, $nativeRestore)
    foreach ($required in @('Turnglass.Desktop.exe', 'Turnglass.DesktopDispatchAdapter.dll', 'e_sqlite3.dll', 'wwwroot/index.html', 'Assets/turnglass.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $required))) { throw "Desktop package is missing $required." }
    }
    foreach ($jitFile in @('coreclr.dll', 'clrjit.dll', 'Turnglass.Desktop.dll', 'WinRT.Host.dll', 'WinRT.Host.Shim.dll')) {
        if (Test-Path -LiteralPath (Join-Path $output $jitFile)) { throw "Desktop package unexpectedly contains a managed runtime artifact: $jitFile." }
    }
    Write-Output "Published Native AOT Turnglass to $output. Copy the whole folder; the Evergreen WebView2 Runtime must be installed."
}
finally { Pop-Location }
