# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro

# -Version を付けると、公開するリリースの名前 (dist\Meltype-<版>-windows.zip) にする (GitHub Actions がタグから付ける)。
param([string]$Version = '')

$ErrorActionPreference = 'Stop'

# 協力者に渡すテスト版の zip を作る: dist\Meltype-test-<日付>.zip
# 中身は ビルド済みの app フォルダー (.NET ランタイム同梱) + Install.cmd / Uninstall.cmd + README.txt。
#
# .NET の同梱: この環境は NuGet が使えないので自己完結ビルド (--self-contained) の代わりに、
# この PC にインストール済みの .NET ランタイムを app\dotnet にコピーし、Meltype.exe がそこを使うようにする
# (AppHostDotNetSearch=AppRelative: .NET 9 以降の apphost の機能)。協力者の PC に .NET は不要。

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'Meltype'
$app = Join-Path $stage 'app'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$zip = if ($Version) { Join-Path $dist "Meltype-$Version-windows.zip" } else { Join-Path $dist "Meltype-test-$stamp.zip" }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# UI Automation で見えない独自入力欄 (Unity Editor #111) を検出する x64 hook DLL。
& (Join-Path $root 'native\inputhook\Build-InputHook.ps1') -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "input hook DLL のビルドに失敗しました (exit code $LASTEXITCODE)。" }

dotnet publish (Join-Path $root 'src\Meltype\Meltype.csproj') -c Release -o $app -p:DebugType=none `
    -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=dotnet
if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました (exit code $LASTEXITCODE)。" }

# Mozc の変換ヘルパー (native\mozc\Build-MozcHelper.ps1 で作ったもの) を同梱する。無ければ Microsoft IME だけで動く。
$mozcBin = Join-Path $root 'native\mozc\bin'
if (Test-Path -LiteralPath (Join-Path $mozcBin 'meltype_mozc_helper.exe')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $app 'mozc') | Out-Null
    Copy-Item -Path (Join-Path $mozcBin '*') -Destination (Join-Path $app 'mozc') -Force
    Write-Host 'Mozc の変換ヘルパーを同梱しました。'
}
else {
    Write-Warning 'Mozc の変換ヘルパーがありません (native\mozc\Build-MozcHelper.ps1)。Microsoft IME だけで変換します。'
}

# Meltype が使うランタイムの版 (runtimeconfig.json に書かれている) と同じものを、インストール済みの .NET から探してコピーする。
$config = Get-Content -Raw (Join-Path $app 'Meltype.runtimeconfig.json') | ConvertFrom-Json
$frameworks = @($config.runtimeOptions.frameworks) + @($config.runtimeOptions.framework) | Where-Object { $_ }
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$runtime = Join-Path $app 'dotnet'
$version = $null
foreach ($framework in $frameworks) {
    $major = ($framework.version -split '\.')[0..1] -join '.'
    $installed = Get-ChildItem (Join-Path $dotnetRoot "shared\$($framework.name)") -Directory |
        Where-Object { $_.Name -like "$major.*" } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if (-not $installed) { throw "$($framework.name) $major がインストールされていません。" }
    Copy-Item -LiteralPath $installed.FullName -Destination (Join-Path $runtime "shared\$($framework.name)\$($installed.Name)") -Recurse
    if ($framework.name -eq 'Microsoft.NETCore.App') { $version = $installed.Name }
}
$fxr = Join-Path $dotnetRoot "host\fxr\$version"
if (-not (Test-Path -LiteralPath $fxr)) { throw "hostfxr $version が見つかりません。" }
Copy-Item -LiteralPath $fxr -Destination (Join-Path $runtime "host\fxr\$version") -Recurse
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination $runtime -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination $runtime -ErrorAction SilentlyContinue

# スマホからも受け取れる大きさ (30MB 未満) にするため、ランタイムから Meltype が使わないものを 2 段階で削る。
#   1. 参照をたどって削る: Meltype.dll が実際に使う型からたどって必要なアセンブリだけ残す (Meltype.Tests の --runtime-closure)。
#      ネイティブの DLL は、デバッグ用 (mscordaccore, mscordbi, DiaSymReader, createdump) と WPF の描画用を削る。
#   2. 実際に読み込まれたかで削る: 1 の状態で自己診断を走らせ、読み込まれなかった大きなアセンブリ (512KB 超) を削る
#      (XML・ネットワーク・暗号などは WinForms から参照されているが、Meltype の使い方では読み込まれない)。
#   どちらの後も自己診断を走らせ、削りすぎていないことを確かめる。
# フレームワークの .deps.json は、一覧にあるファイルが無いと起動できないので、残したファイルだけの一覧に書き直す
# (.deps.json 自体を消すと、そのフレームワークが無いものとして扱われる)。

function Update-DepsJson {