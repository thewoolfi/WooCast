# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2025 WooCast contributors
#
# Подготовка окружения разработки WooCast.
#
# Скрипт ничего не скачивает: он только находит уже установленные компоненты
# и выставляет переменные для текущей сессии PowerShell.
#
#   . .\tools\dev-env.ps1          # применить к текущей сессии
#   .\tools\dev-env.ps1 -Check     # показать, что найдено

[CmdletBinding()]
param(
    # Каталог с локально установленным тулчейном (dotnet, JDK, Android SDK, Gradle).
    [string] $ToolchainRoot = "$env:LOCALAPPDATA\WooCast\sdk",

    # Только показать состояние окружения, ничего не менять.
    [switch] $Check
)

$ErrorActionPreference = 'Stop'

function Find-FirstDirectory {
    param([string] $Path, [string] $Filter)

    if (-not (Test-Path $Path)) { return $null }
    $item = Get-ChildItem -Path $Path -Directory -Filter $Filter -ErrorAction SilentlyContinue |
        Select-Object -First 1

    # Никакого $item?.FullName: в PowerShell 5.1 это выражение молча разбирается
    # как переменная с именем «item?» и всегда даёт $null.
    if (-not $item) { return $null }
    return $item.FullName
}

# ---- .NET ------------------------------------------------------------------
$dotnetRoot = Join-Path $ToolchainRoot 'dotnet'
$dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
if (-not (Test-Path $dotnetExe)) {
    $found = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($found) {
        $dotnetExe = $found.Source
        $dotnetRoot = Split-Path $dotnetExe -Parent
    }
    else {
        $dotnetExe = $null
    }
}

# ---- JDK -------------------------------------------------------------------
$javaHome = Find-FirstDirectory (Join-Path $ToolchainRoot 'jdk') 'jdk-17*'
if (-not $javaHome) { $javaHome = Find-FirstDirectory (Join-Path $ToolchainRoot 'jdk21') 'jdk-21*' }
if (-not $javaHome) { $javaHome = $env:JAVA_HOME }

# ---- Android SDK -----------------------------------------------------------
$androidHome = Join-Path $ToolchainRoot 'android-sdk'
if (-not (Test-Path (Join-Path $androidHome 'platform-tools'))) { $androidHome = $env:ANDROID_HOME }

# ---- Gradle ----------------------------------------------------------------
$gradleBin = Get-ChildItem -Path (Join-Path $ToolchainRoot 'gradle') -Directory -Filter 'gradle-*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Select-Object -First 1
$gradleHome = $null
if ($gradleBin) { $gradleHome = $gradleBin.FullName }

if ($Check) {
    $ffmpegExe = Join-Path $env:LOCALAPPDATA 'WooCast\vendor\ffmpeg\bin\ffmpeg.exe'
    if (-not (Test-Path $ffmpegExe)) {
        $fromPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if ($fromPath) { $ffmpegExe = $fromPath.Source }
        else { $ffmpegExe = 'не найден — запустите tools/fetch-ffmpeg.ps1' }
    }

    $dotnetText = 'не найден'
    if ($dotnetExe) { $dotnetText = $dotnetExe }

    $javaText = 'не найден'
    if ($javaHome) { $javaText = $javaHome }

    $androidText = 'не найден'
    if ($androidHome) { $androidText = $androidHome }

    $gradleText = 'не найден (используется gradlew)'
    if ($gradleHome) { $gradleText = Join-Path $gradleHome 'bin\gradle.bat' }

    Write-Host ''
    Write-Host '  Окружение WooCast' -ForegroundColor Magenta
    Write-Host "  dotnet          $dotnetText"
    Write-Host "  JAVA_HOME       $javaText"
    Write-Host "  ANDROID_HOME    $androidText"
    Write-Host "  gradle          $gradleText"
    Write-Host "  ffmpeg          $ffmpegExe"
    Write-Host ''
    Write-Host '  Уборка:  powershell -File tools\clean.ps1 -Report' -ForegroundColor DarkGray
    Write-Host ''
    return
}

if ($dotnetRoot) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}

if ($javaHome) { $env:JAVA_HOME = $javaHome }
if ($androidHome) {
    $env:ANDROID_HOME = $androidHome
    $env:ANDROID_SDK_ROOT = $androidHome
    $platformTools = Join-Path $androidHome 'platform-tools'
    if (Test-Path $platformTools) { $env:PATH = "$platformTools;$env:PATH" }
}
if ($gradleHome) { $env:PATH = "$(Join-Path $gradleHome 'bin');$env:PATH" }

$ffmpeg = Join-Path $env:LOCALAPPDATA 'WooCast\vendor\ffmpeg\bin'
if (Test-Path $ffmpeg) { $env:PATH = "$ffmpeg;$env:PATH" }

Write-Host ''
Write-Host '  Окружение WooCast применено' -ForegroundColor Green
Write-Host "    dotnet   $dotnetRoot"
Write-Host "    java     $javaHome"
Write-Host "    android  $androidHome"
if ($gradleHome) { Write-Host "    gradle   $gradleHome" }
Write-Host ''
Write-Host '  Сборка:  dotnet build WooCast.sln'
Write-Host '  Запуск:  dotnet run --project src/WooCast.Desktop'
Write-Host '  APK:     cd android; .\gradlew.bat assembleDebug'
Write-Host ''
