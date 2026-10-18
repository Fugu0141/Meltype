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

# Windows PowerShell 5.1 はネイティブ標準エラーを 2>&1 で取り込むと、
# ErrorActionPreference=Stop により終了コード0でも停止することがある。
# stdout/stderrをプロセス側でファイルに分け、終了コードだけで成功判定する。
function Run-Step([string]$name, [string[]]$arguments) {
    Write-Host "=== $name ==="
    $log = Join-Path $report ($name + '.stdout.txt')
    $errLog = Join-Path $report ($name + '.stderr.txt')
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errLog
    if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | ForEach-Object { Write-Host $_ } }
    if (Test-Path -LiteralPath $errLog) { Get-Content -LiteralPath $errLog | ForEach-Object { Write-Host $_ } }
    if ($process.ExitCode -ne 0) {
        throw "$name failed: exit code $($process.ExitCode); stdout=$log; stderr=$errLog"
    }
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
        $tipBuild = Join-Path $root 'native/tip/build.ps1'
        $tipLog = Join-Path $report 'native-tip.txt'
        # PS5.1 の NativeCommandError を回避し、TIP ビルドも終了コードで判定する。
        # -File のパスだけを明示的に引用符で囲む (スペース入りのチェックアウト先にも対応)。
        $tipErrorLog = Join-Path $report 'native-tip.stderr.txt'
        $tipArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $tipBuild + '"'), '-Configuration', 'Release')
        $tipResult = Start-Process -FilePath (Get-Command powershell.exe -ErrorAction Stop).Source `
            -ArgumentList $tipArgs -NoNewWindow -Wait -PassThru -RedirectStandardOutput $tipLog `
            -RedirectStandardError $tipErrorLog
        if (Test-Path -LiteralPath $tipLog) { Get-Content -LiteralPath $tipLog | ForEach-Object { Write-Host $_ } }
        if (Test-Path -LiteralPath $tipErrorLog) { Get-Content -LiteralPath $tipErrorLog | ForEach-Object { Write-Host $_ } }
        if ($tipResult.ExitCode -ne 0) {
            throw "Native TIP build failed: exit code $($tipResult.ExitCode); stdout=$tipLog; stderr=$tipErrorLog"
        }
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
