# SPDX-License-Identifier: MIT
# Copyright (c) 2025 WooCast contributors
#
# Уборка за WooCast: освобождает место, занятое сборкой и тулчейном.
#
#   pwsh -File tools/clean.ps1              # только артефакты сборки (безопасно)
#   pwsh -File tools/clean.ps1 -Report      # показать, что и сколько занимает
#   pwsh -File tools/clean.ps1 -Build -Nuget       # + кэш пакетов NuGet
#   pwsh -File tools/clean.ps1 -Build -Toolchain   # + весь тулчейн (потребует повторной установки)
#
# Тулчейн WooCast лежит в %LOCALAPPDATA%\WooCast и содержит JDK, .NET SDK, Android SDK
# и ffmpeg. Он не входит в репозиторий и удаляется целиком без следа.

[CmdletBinding()]
param(
    # Удалить артефакты сборки: bin/obj проектов и android/app/build.
    [switch] $Build,

    # Удалить кэш пакетов NuGet (при следующей сборке скачается заново).
    [switch] $Nuget,

    # Удалить весь тулчейн %LOCALAPPDATA%\WooCast (JDK, .NET, Android SDK, ffmpeg).
    [switch] $Toolchain,

    # Только показать размеры, ничего не удалять.
    [switch] $Report
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# Имена переменных не должны совпадать с именами switch-параметров ($Build, $Nuget,
# $Toolchain): PowerShell не различает регистр, и присваивание строки в такую
# переменную падает с ошибкой преобразования в SwitchParameter.
$toolchainPath = Join-Path $env:LOCALAPPDATA 'WooCast'
$nugetPath = Join-Path $env:USERPROFILE '.nuget\packages'

function Get-SizeMb([string] $path) {
    if (-not (Test-Path $path)) { return 0 }
    $item = Get-Item $path -Force
    $bytes = if ($item.PSIsContainer) {
        (Get-ChildItem $path -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
    }
    else { $item.Length }

    if ($null -eq $bytes) { return 0 }
    return [math]::Round($bytes / 1MB, 1)
}

function Remove-Target([string] $path, [string] $label) {
    if (-not (Test-Path $path)) { return 0 }

    $mb = Get-SizeMb $path
    Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue

    if (Test-Path $path) {
        Write-Host ("  не удалось   {0,9:N1} МБ  {1}" -f $mb, $label) -ForegroundColor Red
        return 0
    }

    Write-Host ("  удалено      {0,9:N1} МБ  {1}" -f $mb, $label) -ForegroundColor DarkGray
    return $mb
}

function Show-Targets {
    Write-Host ''
    Write-Host '  Что занимает место' -ForegroundColor Magenta
    Write-Host ''

    $buildTargets = Get-BuildTargets
    $buildSize = 0
    foreach ($t in $buildTargets) { $buildSize += Get-SizeMb $t.Path }
    Write-Host ("  {0,9:N1} МБ  артефакты сборки (удаляются ключом -Build)" -f $buildSize)

    Write-Host ("  {0,9:N1} МБ  кэш NuGet (ключ -Nuget)" -f (Get-SizeMb $nugetPath))
    Write-Host ("  {0,9:N1} МБ  тулчейн: {1} (ключ -Toolchain)" -f (Get-SizeMb $toolchainPath), $toolchainPath)

    if (Test-Path $toolchainPath) {
        Write-Host ''
        Write-Host '  Состав тулчейна:' -ForegroundColor DarkGray
        Get-ChildItem $toolchainPath -Force -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host ("    {0,9:N1} МБ  {1}" -f (Get-SizeMb $_.FullName), $_.Name) -ForegroundColor DarkGray
        }
    }

    Write-Host ''
}

function Get-BuildTargets {
    $targets = @()

    foreach ($project in Get-ChildItem (Join-Path $repoRoot 'src') -Directory -ErrorAction SilentlyContinue) {
        $targets += [pscustomobject]@{ Path = Join-Path $project.FullName 'bin'; Label = "src\$($project.Name)\bin" }
        $targets += [pscustomobject]@{ Path = Join-Path $project.FullName 'obj'; Label = "src\$($project.Name)\obj" }
    }

    $android = Join-Path $repoRoot 'android'
    $targets += [pscustomobject]@{ Path = Join-Path $android 'app\build'; Label = 'android\app\build' }
    $targets += [pscustomobject]@{ Path = Join-Path $android 'build'; Label = 'android\build' }
    $targets += [pscustomobject]@{ Path = Join-Path $android '.gradle'; Label = 'android\.gradle' }

    return $targets | Where-Object { Test-Path $_.Path }
}

Write-Host ''
Write-Host '  WooCast — уборка' -ForegroundColor Magenta

if ($Report) {
    Show-Targets
    return
}

if (-not ($Build -or $Nuget -or $Toolchain)) {
    # По умолчанию убираем только артефакты сборки — это всегда безопасно.
    $Build = $true
}

$freed = 0

if ($Build) {
    Write-Host ''
    Write-Host '  Артефакты сборки' -ForegroundColor Cyan

    foreach ($target in Get-BuildTargets) {
        $freed += Remove-Target $target.Path $target.Label
    }

    $dist = Join-Path $repoRoot 'dist'
    if (Test-Path $dist) {
        Write-Host ("  оставлено    {0,9:N1} МБ  dist (готовые сборки: exe и APK)" -f (Get-SizeMb $dist)) -ForegroundColor DarkGray
    }
}

if ($Nuget) {
    Write-Host ''
    Write-Host '  Кэш NuGet' -ForegroundColor Cyan
    $freed += Remove-Target $nugetPath 'кэш пакетов NuGet'
    Write-Host '  Следующая сборка скачает пакеты заново.' -ForegroundColor DarkGray
}

if ($Toolchain) {
    Write-Host ''
    Write-Host '  Тулчейн' -ForegroundColor Cyan
    Write-Host '  Внимание: после этого для сборки потребуется заново установить' -ForegroundColor Yellow
    Write-Host '  .NET SDK, JDK, Android SDK и ffmpeg (см. README).' -ForegroundColor Yellow
    $freed += Remove-Target $toolchainPath $toolchainPath
}

Write-Host ''
Write-Host ("  Освобождено: {0:N2} ГБ" -f ($freed / 1024)) -ForegroundColor Green
Get-PSDrive C | ForEach-Object {
    Write-Host ("  Свободно на C: {0:N1} ГБ" -f ($_.Free / 1GB)) -ForegroundColor Green
}
Write-Host ''
