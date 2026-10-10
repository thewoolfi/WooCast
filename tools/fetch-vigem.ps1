<#
.SYNOPSIS
    Скачивает установщик драйвера ViGEmBus в кэш.

.DESCRIPTION
    Драйвер виртуального геймпада нужен для управления с контроллера, подключённого
    к приставке или телефону. Сам драйвер устанавливается в систему и рядом с программой
    лежать не может, но установщик — может: тогда на другом компьютере не придётся
    ничего искать.

    Файл кладётся в кэш (%LOCALAPPDATA%\WooCast\vendor\vigem) и оттуда попадает
    в поставку при сборке. Лицензия ViGEmBus — MIT, распространение разрешено.

.EXAMPLE
    powershell -File tools\fetch-vigem.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$target = Join-Path $env:LOCALAPPDATA 'WooCast\vendor\vigem'
$destination = Join-Path $target 'ViGEmBus_setup.exe'

if ((Test-Path $destination) -and -not $Force) {
    $size = (Get-Item $destination).Length / 1MB
    Write-Host "  Установщик уже в кэше: $destination ($([math]::Round($size, 1)) МБ)" -ForegroundColor Green
    Write-Host '  Перезагрузить: добавьте -Force' -ForegroundColor DarkGray
    exit 0
}

Write-Host '  Ищу последний выпуск ViGEmBus…' -ForegroundColor DarkGray

$release = Invoke-RestMethod `
    -Uri 'https://api.github.com/repos/nefarius/ViGEmBus/releases/latest' `
    -Headers @{ 'User-Agent' = 'WooCast-build' } `
    -TimeoutSec 60

$asset = $release.assets | Where-Object { $_.name -like '*.exe' } | Select-Object -First 1

if (-not $asset) {
    throw "В выпуске $($release.tag_name) не найден установщик"
}

New-Item -ItemType Directory -Force -Path $target | Out-Null

Write-Host "  Скачиваю $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) МБ)…" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $destination -TimeoutSec 600 -UseBasicParsing

$info = Get-Item $destination
Write-Host "  Готово: $destination ($([math]::Round($info.Length / 1MB, 1)) МБ)" -ForegroundColor Green
Write-Host "  Версия драйвера: $($release.tag_name)" -ForegroundColor DarkGray
