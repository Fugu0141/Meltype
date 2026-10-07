# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe が見つかりません。Visual Studio Build Tools (C++) が必要です。' }
$install = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $install) { throw 'Visual Studio / Build Tools が見つかりません。' }
$msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
$project = Join-Path $PSScriptRoot 'meltype_input_hook.vcxproj'
& $msbuild $project /m /p:Configuration=$Configuration /p:Platform=x64 /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw "input hook DLL のビルドに失敗しました (exit code $LASTEXITCODE)。" }
$dll = Join-Path $PSScriptRoot "bin\x64\$Configuration\meltype_input_hook.dll"
if (-not (Test-Path $dll)) { throw "DLL が生成されませんでした: $dll" }
Write-Host "input hook DLL: $dll"
