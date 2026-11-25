param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-desktop.ps1') -Configuration Release -Publish -SkipRestore:$SkipRestore
