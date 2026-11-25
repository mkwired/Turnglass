param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipRestore,
    [switch]$SkipBridgeBuild,
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The desktop dispatch adapter must be built on Windows.' }
$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).ProviderPath
$projectDirectory = Join-Path $repositoryRoot 'src/Turnglass.DesktopDispatchAdapter'
$project = Join-Path $projectDirectory 'Turnglass.DesktopDispatchAdapter.vcxproj'

function Invoke-AdapterCommand {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

if (-not $MSBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -eq 0 -and $installation) {
            $MSBuildPath = Join-Path ($installation | Select-Object -First 1) 'MSBuild/Current/Bin/MSBuild.exe'
        }
    }
    # Standalone Build Tools installations can remain usable without a vswhere registration.
    if (-not $MSBuildPath) {
        $candidate = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe'
        if (Test-Path -LiteralPath $candidate) { $MSBuildPath = $candidate }
    }
}
if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'Install Visual Studio C++ Build Tools with MSVC v143 and Windows SDK 10.0.26100.0, or supply -MSBuildPath.'
}

Push-Location $repositoryRoot
try {
    if (-not $SkipBridgeBuild) {
        $bridgeProject = 'src/Turnglass.DesktopBridge/Turnglass.DesktopBridge.csproj'
        if (-not $SkipRestore) {
            Invoke-AdapterCommand dotnet @('restore', $bridgeProject, '--locked-mode')
        }
        Invoke-AdapterCommand dotnet @('build', $bridgeProject, '-c', $Configuration, '--no-restore')
    }
    $arguments = @($project, "/p:Configuration=$Configuration", '/p:Platform=x64', '/nologo', '/verbosity:quiet')
    if (-not $SkipRestore) {
        Invoke-AdapterCommand $MSBuildPath ($arguments + @('/t:Restore', '/p:RestorePackagesConfig=true'))
    }

    # packages.config fixes versions; this companion file also pins each downloaded archive.
    $expectedHashes = Get-Content -LiteralPath (Join-Path $projectDirectory 'packages.sha512.json') -Raw | ConvertFrom-Json -AsHashtable
    [xml]$packageConfiguration = Get-Content -LiteralPath (Join-Path $projectDirectory 'packages.config') -Raw
    $packages = @($packageConfiguration.packages.package)
    if ($expectedHashes.Count -ne $packages.Count) { throw 'Native package versions and SHA-512 pins differ.' }
    foreach ($package in $packages) {
        $identity = "$($package.id).$($package.version)"
        $archive = Join-Path $projectDirectory "obj/packages/$identity/$identity.nupkg"
        if (-not $expectedHashes.ContainsKey($identity) -or -not (Test-Path -LiteralPath $archive -PathType Leaf)) {
            throw "Missing restored native package or SHA-512 pin: $identity. Run without -SkipRestore."
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expectedHashes[$identity]) {
            throw "The restored native package does not match its SHA-512 pin: $identity."
        }
    }

    Invoke-AdapterCommand $MSBuildPath ($arguments + '/t:Build')
    $outputDirectory = Join-Path $projectDirectory "bin/x64/$Configuration"
    foreach ($file in @('Turnglass.DesktopDispatchAdapter.dll', 'TurnglassDesktopDispatchAdapter.winmd')) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory $file) -PathType Leaf)) {
            throw "Native adapter build did not produce $file."
        }
    }
    Write-Output "Built the desktop dispatch adapter in $outputDirectory."
}
finally { Pop-Location }
