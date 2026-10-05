# SPDX-License-Identifier: GPL-3.0-or-later
param(
    [ValidateSet('Quick','Deep')] [string]$Profile = 'Quick',
    [int]$Seed = 20261005,
    [string]$Output = 'artifacts/test-results-local'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $resultRoot = [IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
    dotnet restore src/Meltype.Core.Tests/Meltype.Core.Tests.csproj --configfile (Join-Path $repo 'nuget.config')
    if ($LASTEXITCODE -ne 0) { throw 'Core test restore failed' }
    dotnet build src/Meltype.Core.Tests/Meltype.Core.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core test build failed' }
    $dll = Join-Path $repo 'src/Meltype.Core.Tests/bin/Release/net10.0/Meltype.Core.Tests.dll'
    dotnet $dll
    if ($LASTEXITCODE -ne 0) { throw 'Existing test gates failed' }
    $corpusCount = if ($Profile -eq 'Deep') {10000} else {1000}
    $fuzzCount = if ($Profile -eq 'Deep') {100000} else {1000}
    $stressCount = if ($Profile -eq 'Deep') {1000000} else {10000}
    foreach ($mode in @('corpus','fuzz','stress','fault','privacy','strings')) {
        $count = switch ($mode) {corpus {$corpusCount} fuzz {$fuzzCount} stress {$stressCount} default {10000}}
        dotnet $dll --validate $mode --seed $Seed --count $count --out $resultRoot
        if ($LASTEXITCODE -ne 0) { throw "$mode runner failed: $LASTEXITCODE" }
    }
    # Corpus is an observation, not a replacement for established Quality thresholds.
    $fuzz = Get-Content -LiteralPath (Join-Path $resultRoot 'fuzz-summary.json') -Raw | ConvertFrom-Json
    $stress = Get-Content -LiteralPath (Join-Path $resultRoot 'stress.json') -Raw | ConvertFrom-Json
    if ($fuzz.failures -gt 0 -or ($stress.lost + $stress.duplicated + $stress.reordered + $stress.exceptions) -gt 0) {
        throw 'New stability/integrity violations: inspect failures.jsonl'
    }
    Write-Host "Results: $resultRoot (fault/corpus observations must be triaged separately)"
} finally { Pop-Location }
