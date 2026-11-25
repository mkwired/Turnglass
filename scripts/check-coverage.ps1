param(
    [Parameter(Mandatory)][string]$ReportPath,
    [double]$Minimum = 80
)
$ErrorActionPreference = 'Stop'
[xml]$report = Get-Content -LiteralPath $ReportPath -Raw
$covered = [double]$report.coverage.'lines-covered'
$valid = [double]$report.coverage.'lines-valid'
if ($valid -le 0) {
    throw 'Coverage report contains no production lines.'
}
$requiredModules = @('Core', 'Infrastructure', 'Web', 'Mcp')
$packageNames = @($report.coverage.packages.package | ForEach-Object { $_.name })
foreach ($module in $requiredModules) {
    if (-not ($packageNames | Where-Object { $_ -match "Turnglass\.$module(?:$|\.)" })) {
        throw "Coverage report is missing the $module production assembly."
    }
}
$percent = $covered / $valid * 100
Write-Output ('Backend line coverage: {0:N2}% ({1}/{2})' -f $percent, $covered, $valid)
if ($percent -lt $Minimum) {
    throw "Backend coverage is below the $Minimum% minimum."
}
