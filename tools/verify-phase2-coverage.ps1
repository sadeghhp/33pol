# Verifies line coverage for Phase 2 assemblies (Registry + Proxy).
# Run after: dotnet test 33pol.sln -c Release --collect:"XPlat Code Coverage" --results-directory ./TestResults/phase2-coverage

param(
    [string]$ResultsDirectory = "TestResults",
    [double]$RegistryMinLinePercent = 65,
    [double]$ProxyMinLinePercent = 85
)

$ErrorActionPreference = "Stop"
$files = Get-ChildItem -Path $ResultsDirectory -Recurse -Filter coverage.cobertura.xml
if (-not $files) {
    Write-Error "No coverage.cobertura.xml under $ResultsDirectory. Run dotnet test with --collect:`"XPlat Code Coverage`" first."
}

$best = @{}
foreach ($file in $files) {
    [xml]$doc = Get-Content $file.FullName
    foreach ($pkg in $doc.coverage.packages.package) {
        $name = [string]$pkg.name
        if ($name -notin @("33pol.Registry", "33pol.Proxy")) { continue }
        $line = [double]$pkg.'line-rate' * 100
        if (-not $best.ContainsKey($name) -or $line -gt $best[$name]) {
            $best[$name] = $line
        }
    }
}

$failed = $false
foreach ($entry in $best.GetEnumerator() | Sort-Object Name) {
    $threshold = if ($entry.Key -eq "33pol.Registry") { $RegistryMinLinePercent } else { $ProxyMinLinePercent }
    $ok = $entry.Value -ge $threshold
    $status = if ($ok) { "PASS" } else { "FAIL"; $failed = $true }
    Write-Output ("{0} line coverage {1:N1}% (min {2:N0}%) [{3}]" -f $entry.Key, $entry.Value, $threshold, $status)
}

if ($failed) {
    Write-Error "Phase 2 coverage gate failed. Target guide: 90% per assembly (see docs/implementation-plan/02-testing-strategy.md)."
}
Write-Output "Phase 2 interim coverage gate passed."
