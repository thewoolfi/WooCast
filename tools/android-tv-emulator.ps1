# SPDX-License-Identifier: MIT
# Copyright (c) 2025 WooCast contributors
#
# Создание и запуск эмулятора Android TV для проверки приёмника без реальной приставки.
#
#   pwsh -File tools/android-tv-emulator.ps1 -Create     # создать AVD (нужен Android SDK)
#   pwsh -File tools/android-tv-emulator.ps1             # запустить эмулятор
#   pwsh -File tools/android-tv-emulator.ps1 -Install    # установить собранный APK
#
# Эмулятор полезен для проверки запуска и интерфейса, но его программный декодер
# заметно слабее аппаратного в реальной приставке: 1080p60 он не тянет.
# Для честной проверки производительности нужна настоящая ТВ-приставка.

[CmdletBinding()]
param(
    [string] $AvdName = 'WooCastTV',
    [string] $SystemImage = 'system-images;android-34;android-tv;x86',
    [string] $DeviceProfile = 'tv_1080p',
    [string] $ToolchainRoot = "$env:LOCALAPPDATA\WooCast\sdk",

    [switch] $Create,
    [switch] $Install,
    [switch] $Headless
)

$ErrorActionPreference = 'Stop'

$sdk = Join-Path $ToolchainRoot 'android-sdk'
$emulator = Join-Path $sdk 'emulator\emulator.exe'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$avdManager = Join-Path $sdk 'cmdline-tools\latest\bin\avdmanager.bat'

$javaHome = Get-ChildItem (Join-Path $ToolchainRoot 'jdk') -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($javaHome) { $env:JAVA_HOME = $javaHome.FullName }
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

if (-not (Test-Path $emulator)) {
    throw "Эмулятор не найден. Установите: sdkmanager `"emulator`" `"$SystemImage`""
}

if ($Create) {
    Write-Host '  Создаю AVD...' -ForegroundColor Cyan
    'no' | & $avdManager create avd -n $AvdName -k $SystemImage -d $DeviceProfile --force
    Write-Host '  Готово.' -ForegroundColor Green
}

if ($Install) {
    $apk = Join-Path $PSScriptRoot '..\android\app\build\outputs\apk\debug\app-debug.apk'
    if (-not (Test-Path $apk)) { throw "APK не найден: $apk. Сначала соберите: cd android; .\gradlew.bat assembleDebug" }

    & $adb install -r $apk
    & $adb shell am start -a android.intent.action.MAIN -c android.intent.category.LEANBACK_LAUNCHER -n app.woocast.tv/.MainActivity

    # Порты приёмника пробрасываются на хост: снаружи эмулятор недоступен напрямую,
    # поэтому подключение с ПК идёт на 127.0.0.1.
    & $adb forward tcp:47811 tcp:47811
    & $adb forward tcp:47812 tcp:47812

    Write-Host ''
    Write-Host '  Подключение с ПК: 127.0.0.1 (порты 47811 и 47812 проброшены)' -ForegroundColor Green
    Write-Host '  Трансляция: woocast send --to 127.0.0.1 --size 1280x720 --fps 30' -ForegroundColor DarkGray
    Write-Host ''
    return
}

$arguments = @(
    '-avd', $AvdName,
    '-no-snapshot',
    '-no-boot-anim',
    '-no-audio',
    '-gpu', 'swiftshader_indirect',
    '-netdelay', 'none',
    '-netspeed', 'full'
)

if ($Headless) { $arguments += '-no-window' }

Write-Host "  Запускаю эмулятор $AvdName..." -ForegroundColor Cyan
& $emulator @arguments
