# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2025 WooCast contributors
#
# Загрузка ffmpeg для WooCast.
#
#   pwsh -File tools/fetch-ffmpeg.ps1
#   pwsh -File tools/fetch-ffmpeg.ps1 -Destination .\vendor
#
# По умолчанию сборка кладётся в %LOCALAPPDATA%\WooCast\vendor\ffmpeg — так она
# не попадает в репозиторий и не синхронизируется облачными папками.
#
# Нужна сборка с фильтром ddagrab (Desktop Duplication API) — её содержат
# сборки BtbN начиная с ffmpeg 6. Скрипт проверяет это после распаковки.

[CmdletBinding()]
param(
    # Куда распаковать. По умолчанию — кэш установщика в LOCALAPPDATA.
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'WooCast\vendor'),

    # Перекачать, даже если ffmpeg уже на месте.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
$target = Join-Path $Destination 'ffmpeg'
$exe = Join-Path $target 'bin\ffmpeg.exe'

function Write-Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  OK  $text" -ForegroundColor Green }
function Write-Warn2($text) { Write-Host "  !   $text" -ForegroundColor Yellow }

Write-Host ''
Write-Host '  WooCast — подготовка ffmpeg' -ForegroundColor Magenta
Write-Host ''

if ((Test-Path $exe) -and -not $Force) {
    Write-Ok "ffmpeg уже установлен: $exe"
}
else {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $zip = Join-Path $Destination 'ffmpeg-download.zip'

    Write-Step "Скачиваю $url"
    curl.exe -L --fail --progress-bar -o $zip $url
    if ($LASTEXITCODE -ne 0) { throw "Не удалось скачать ffmpeg (код $LASTEXITCODE)" }

    Write-Step 'Распаковываю…'
    $staging = Join-Path $Destination 'ffmpeg-extract'
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $zip -DestinationPath $staging -Force

    $inner = Get-ChildItem $staging -Directory | Select-Object -First 1
    if (-not $inner) { throw 'Архив ffmpeg имеет неожиданную структуру' }

    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item $inner.FullName $target

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue

    Write-Ok "Распаковано в $target"
}

# ---- Проверка возможностей -------------------------------------------------

if (-not (Test-Path $exe)) { throw "ffmpeg не найден по пути $exe" }

$version = (& $exe -hide_banner -version 2>&1 | Select-Object -First 1)
Write-Ok $version

$filters = & $exe -hide_banner -filters 2>&1
if ($filters -match 'ddagrab') {
    Write-Ok 'ddagrab доступен — захват экрана через Desktop Duplication (GPU)'
}
else {
    Write-Warn2 'ddagrab не найден: захват пойдёт через gdigrab (медленнее, выше нагрузка на CPU)'
}

$encoders = & $exe -hide_banner -encoders 2>&1
$found = @()
foreach ($name in 'h264_nvenc', 'h264_qsv', 'h264_amf', 'libx264') {
    if ($encoders -match $name) { $found += $name }
}

if ($found.Count -gt 0) {
    Write-Ok "Кодировщики в сборке: $($found -join ', ')"
}
else {
    Write-Warn2 'В сборке нет ни одного кодировщика H.264 — что-то не так'
}

Write-Host ''
Write-Host '  Готово. WooCast найдёт ffmpeg автоматически.' -ForegroundColor Green
Write-Host "  Путь: $exe"
Write-Host ''
Write-Host '  Если ffmpeg лежит в другом месте, укажите переменную окружения:' -ForegroundColor DarkGray
Write-Host '    $env:WOOCAST_FFMPEG = "D:\tools\ffmpeg\bin\ffmpeg.exe"' -ForegroundColor DarkGray
Write-Host ''
