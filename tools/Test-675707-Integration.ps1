# SPDX-License-Identifier: GPL-3.0-or-later
# #675/#707 の統合回帰テスト。既定では入力フックやインストール処理を実行しない。
param(
    [switch]$RunFocusE2E,
    [ValidateRange(1, 100)][int]$FocusIterations = 6,
    [switch]$BuildTip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$report = Join-Path ([System.IO.Path]::GetTempPath()) ("meltype-675707-tests-" + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $report -Force | Out-Null
Write-Host "Results: $report"

function Run-Step([string]$name, [string[]]$arguments) {
    Write-Host "=== $name ==="
    $log = Join-Path $report ($name + '.txt')
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "$name failed: exit code $LASTEXITCODE; log=$log" }
}

Push-Location $root
try {
    Run-Step 'build' @('build', 'Meltype.sln', '-c', 'Release')
    Run-Step 'core-tests' @('run', '--project', 'src/Meltype.Core.Tests', '-c', 'Release', '--no-build')
    Run-Step 'windows-tests' @('run', '--project', 'src/Meltype.Tests', '-c', 'Release', '--no-build')
    # テストランナーの部分実行も検証する (JUnit ではなく独自 [Test] ランナー)。
    Run-Step 'focus-guards' @('run', '--project', 'src/Meltype.Tests', '-c', 'Release', '--no-build', '--', 'FocusInspector_')
    Run-Step 'ime-guards' @('run', '--project', 'src/Meltype.Tests', '-c', 'Release', '--no-build', '--', 'AsyncIme_')
    if ($BuildTip) {
        Write-Host '=== Build Native TIP ==='
        & (Join-Path $root 'native/tip/build.ps1') -Configuration Release 2>&1 |
            Tee-Object -FilePath (Join-Path $report 'native-tip.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Native TIP build failed' }
    }
    if ($RunFocusE2E) {
        Write-Warning 'Focus E2E moves the mouse and foreground window. Do not type or use the mouse during this test.'
        for ($i = 1; $i -le $FocusIterations; $i++) {
            Run-Step ("focus-e2e-{0:D2}" -f $i) @('run', '--project', 'src/Meltype.Tests', '-c', 'Release', '--no-build', '--', '--focus-e2e')
        }
    }
    Write-Host 'All requested automated steps completed. This does not validate manual TSF/IME/DPI or installer scenarios.'
    Write-Host "Logs: $report"
}
finally {
    Pop-Location
}
