#Requires -Version 5.1
<#
.SYNOPSIS
    Сборка готовых к запуску версий WooCast: приложения для Windows и приставки для Android TV.

.DESCRIPTION
    Собирает самодостаточный WooCast.exe (не требует установленного .NET) и, при указании
    -Android, APK для приставки. Результат складывается в dist/.

    ffmpeg копируется в поставку всегда: без него приложение не работает, а на чужом
    компьютере его может не оказаться. Отказаться можно ключом -NoFfmpeg — тогда сборка
    станет меньше примерно на 160 МБ, но будет полагаться на установленный ffmpeg.

.PARAMETER Android
    Собрать также APK для Android TV. Требует JDK и Android SDK (tools/dev-env.ps1).

.PARAMETER NoFfmpeg
    Не копировать ffmpeg: сборка уменьшится, но станет зависимой от чужой установки.

.PARAMETER Version
    Версия в именах файлов. По умолчанию 0.1.0.

.EXAMPLE
    powershell -File tools\publish.ps1 -Android

.EXAMPLE
    powershell -File tools\publish.ps1 -NoFfmpeg
#>
[CmdletBinding()]
param(
    [switch]$Android,
    [switch]$NoFfmpeg,
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "  $text" -ForegroundColor Cyan
}

function Write-Ok([string]$text) {
    Write-Host "  [+] $text" -ForegroundColor Green
}

function Write-Note([string]$text) {
    Write-Host "      $text" -ForegroundColor DarkGray
}

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return '{0:N2} ГБ' -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return '{0:N1} МБ' -f ($bytes / 1MB) }
    return '{0:N0} КБ' -f ($bytes / 1KB)
}

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$desktopOut = Join-Path $dist "WooCast-Desktop-$Version"
$apkOut = Join-Path $dist "WooCast-TV-$Version.apk"

Write-Host ''
Write-Host '  WooCast — сборка готовых версий' -ForegroundColor White
Write-Note "Репозиторий: $root"

# --- .NET -------------------------------------------------------------------

Write-Step 'Приложение для Windows'

$dotnet = $null
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'WooCast\sdk\dotnet\dotnet.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)

foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { $dotnet = $candidate; break }
}

if (-not $dotnet) {
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $dotnet = $onPath.Source }
}

if (-not $dotnet) {
    throw 'Не найден dotnet. Запустите tools/dev-env.ps1 или установите .NET SDK 10.'
}

Write-Note "dotnet: $dotnet"

if (Test-Path $desktopOut) {
    Remove-Item $desktopOut -Recurse -Force
}

$publishArgs = @(
    'publish', (Join-Path $root 'src\WooCast.Desktop\WooCast.Desktop.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    # Символы от нативных библиотек Skia весят около 100 МБ и в поставке не нужны.
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $desktopOut
)

& $dotnet @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Сборка приложения завершилась с кодом $LASTEXITCODE" }

# Символы из нативных пакетов копируются помимо DebugType — убираем их отдельно.
Get-ChildItem -Path $desktopOut -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $desktopOut 'WooCast.exe'
if (-not (Test-Path $exe)) { throw "Не найден собранный WooCast.exe в $desktopOut" }

Write-Ok "WooCast.exe — $(Format-Size (Get-Item $exe).Length)"

# --- ffmpeg -----------------------------------------------------------------
#
# ffmpeg кладётся в поставку по умолчанию: без него приложение не работает вовсе,
# а на чужом компьютере его может не оказаться. Отказаться можно ключом -NoFfmpeg.

if (-not $NoFfmpeg) {
    $ffmpegSource = Join-Path $env:LOCALAPPDATA 'WooCast\vendor\ffmpeg\bin\ffmpeg.exe'

    if (-not (Test-Path $ffmpegSource)) {
        Write-Host '  [!] ffmpeg не найден — запустите tools/fetch-ffmpeg.ps1' -ForegroundColor Yellow
        Write-Note 'сборка получилась без ffmpeg и на чужом компьютере не заработает'
    }
    else {
        $ffmpegTarget = Join-Path $desktopOut 'ffmpeg'
        New-Item -ItemType Directory -Force -Path $ffmpegTarget | Out-Null
        Copy-Item $ffmpegSource -Destination $ffmpegTarget -Force

        Write-Ok "ffmpeg.exe — $(Format-Size (Get-Item (Join-Path $ffmpegTarget 'ffmpeg.exe')).Length)"
    }
}
else {
    Write-Note 'ffmpeg не копируется по ключу -NoFfmpeg'
    Write-Note 'на чужом компьютере приложение без него не запустит трансляцию'
}

# --- установщик драйвера геймпада -------------------------------------------
#
# Сам драйвер устанавливается в систему и рядом с программой лежать не может,
# но установщик — может: на другом компьютере не придётся ничего искать.
# Нужен только для управления с контроллера.

$vigemSource = Join-Path $env:LOCALAPPDATA 'WooCast\vendor\vigem\ViGEmBus_setup.exe'

if (Test-Path $vigemSource) {
    $vigemTarget = Join-Path $desktopOut 'driver'
    New-Item -ItemType Directory -Force -Path $vigemTarget | Out-Null
    Copy-Item $vigemSource -Destination (Join-Path $vigemTarget 'ViGEmBus_setup.exe') -Force

    Write-Ok "ViGEmBus_setup.exe — $(Format-Size (Get-Item $vigemSource).Length)"
}
else {
    Write-Note 'установщик драйвера геймпада не найден — запустите tools/fetch-vigem.ps1'
}

# --- установщик ---------------------------------------------------------------
#
# Установщик собирается Inno Setup: он ставит всё сразу — приложение, ffmpeg,
# установщик драйвера и приложение для приставки. Если Inno Setup не установлен,
# это не ошибка: остаётся обычная папка поставки.

$iscc = Get-ChildItem @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) -ErrorAction SilentlyContinue | Select-Object -First 1

if ($iscc) {
    Write-Step 'Установщик'

    $issPath = Join-Path $root 'installer\WooCast.iss'

    # Вывод компилятора сохраняется: при неудаче причина должна быть видна,
    # а не спрятана за кодом возврата. В этот раз ошибка выглядела как «код 2»
    # без объяснения, и причину пришлось искать заново.
    $isccLog = Join-Path $env:TEMP 'woocast-installer-build.log'
    & $iscc.FullName "/DAppVersion=$Version" $issPath *> $isccLog

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [!] Не удалось собрать установщик (код $LASTEXITCODE)" -ForegroundColor Yellow
        Get-Content $isccLog -Tail 12 | ForEach-Object { Write-Note $_ }
    }
    else {
        $setup = Join-Path $dist "WooCast-Setup-$Version.exe"
        if (Test-Path $setup) {
            Write-Ok "WooCast-Setup-$Version.exe — $(Format-Size (Get-Item $setup).Length)"
        }
    }
}
else {
    Write-Note 'Inno Setup не найден — установщик не собирается, остаётся папка поставки'
}

# --- сопроводительный файл ---------------------------------------------------

$readme = @"
WooCast для Windows — версия $Version
=====================================

Запуск
------
Запустите WooCast.exe. Установка не требуется: среда .NET уже внутри.
Windows 10 или 11, 64-разрядная.

Что нужно для работы
--------------------
Ничего устанавливать не нужно: среда .NET и ffmpeg уже в этой папке.

  WooCast.exe             — приложение (запускать этот файл)
  ffmpeg\ffmpeg.exe       — кодировщик видео, берётся из папки рядом с приложением
  WooCast-TV-<версия>.apk — приложение для приставки или телефона
  driver\       — установщик ViGEmBus, нужен только для управления с контроллера
  README.txt         — этот файл

Папку можно целиком скопировать на другой компьютер или на флешку: пути нигде
не прописаны, всё ищется рядом с приложением. ffmpeg также ищется в переменной
окружения WOOCAST_FFMPEG, в %LOCALAPPDATA%\WooCast\vendor\ffmpeg\bin и в PATH —
но при наличии папки ffmpeg рядом этого не требуется.

На другом компьютере ничего искать не придётся. Единственное, что устанавливается
в систему, — драйвер виртуального геймпада: его установщик лежит в папке
«драйвер геймпада». Нужен он только для управления с контроллера; без него
трансляция, звук и захват окна работают как обычно.

Как пользоваться
----------------
1. Включите приставку — она найдётся сама (или введите адрес вручную).
2. Выберите источник: весь экран, конкретный монитор или отдельное окно.
3. Нажмите «Транслировать».

Настройки запоминаются в %APPDATA%\WooCast\settings.json — отдельно для каждого
пользователя, поэтому переносу папки не мешают.

Управление с контроллера
------------------------
Контроллер (например, DualShock 4) подключается по Bluetooth к приставке или
телефону, а управление уходит на компьютер и подаётся в игру как обычный геймпад.
Включите «Управление с приёмника» в настройках и выберите вид геймпада.

Для этого нужен драйвер ViGEmBus. Запустите «driver\ViGEmBus_setup.exe»
и согласитесь с установкой — потребуются права администратора, после установки
может понадобиться перезагрузка.

Скачать заново, если понадобится: https://github.com/nefarius/ViGEmBus/releases
Без драйвера всё остальное работает как обычно.

Звук
----
Передаётся системный звук — то, что играет на компьютере. Захват идёт напрямую
с устройства вывода, поэтому «Стерео микшер» включать не нужно.

Если звук на телевизоре отстаёт или спешит, поправьте его ползунком
«Задержка звука»: положительное значение — звук позже картинки.
Поправка применяется при запуске трансляции.
"@

$readmePath = Join-Path $desktopOut 'README.txt'
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding $true))
Write-Ok 'README.txt'

# --- Android ----------------------------------------------------------------

if ($Android) {
    Write-Step 'Приложение для Android TV'

    $androidRoot = Join-Path $root 'android'

    # Сначала берётся то, что уже настроено в окружении: так сборка работает
    # на серверах GitHub, где JDK и Android SDK стоят в своих местах. Только если
    # окружение пустое, ищется папка проекта, подготовленная tools/dev-env.ps1.
    $javaHome = $null

    if ($env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) {
        $javaHome = $env:JAVA_HOME
    }
    else {
        $found = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WooCast\sdk\jdk') -Directory -Filter 'jdk-17*' -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($found) { $javaHome = $found.FullName }
    }

    if (-not $javaHome) {
        throw 'Не найден JDK 17. Запустите tools/dev-env.ps1.'
    }

    $env:JAVA_HOME = $javaHome

    $androidSdk = $null

    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) {
        if ($candidate -and (Test-Path $candidate)) { $androidSdk = $candidate; break }
    }

    if (-not $androidSdk) {
        $androidSdk = Join-Path $env:LOCALAPPDATA 'WooCast\sdk\android-sdk'
    }

    $env:ANDROID_HOME = $androidSdk
    $env:ANDROID_SDK_ROOT = $androidSdk

    Push-Location $androidRoot
    try {
        # --no-daemon: демон Gradle переживает сборку и держит унаследованный дескриптор
        # вывода, из-за чего вызвавший скрипт не завершается. Для редкой сборки поставки
        # постоянный демон не нужен.
        $log = Join-Path $env:TEMP 'woocast-android-build.log'
        & (Join-Path $androidRoot 'gradlew.bat') assembleDebug --console=plain --no-daemon *> $log

        if ($LASTEXITCODE -ne 0) {
            Get-Content $log -Tail 30 | ForEach-Object { Write-Note $_ }
            throw "Сборка APK завершилась с кодом $LASTEXITCODE (журнал: $log)"
        }
    }
    finally {
        Pop-Location
    }

    $apk = Join-Path $androidRoot 'app\build\outputs\apk\debug\app-debug.apk'
    if (-not (Test-Path $apk)) { throw "Не найден собранный APK: $apk" }

    Copy-Item $apk -Destination $apkOut -Force
    Write-Ok "WooCast-TV-$Version.apk — $(Format-Size (Get-Item $apkOut).Length)"

    # Приложение для приставки кладётся и в папку с программой: так весь продукт
    # лежит в одном месте и переносится целиком, без поиска файлов по разным папкам.
    $apkInFolder = Join-Path $desktopOut "WooCast-TV-$Version.apk"
    Copy-Item $apk -Destination $apkInFolder -Force
    Write-Ok "APK для приставки — в папке с программой"
}

# --- Итог -------------------------------------------------------------------

Write-Step 'Готово'

Get-ChildItem $desktopOut -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($desktopOut.Length + 1)
    Write-Note "$relative — $(Format-Size $_.Length)"
}

if ($Android -and (Test-Path $apkOut)) {
    Write-Note "WooCast-TV-$Version.apk — $(Format-Size (Get-Item $apkOut).Length)"
}

$total = (Get-ChildItem $desktopOut -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host "  Итого: $(Format-Size $total) в $desktopOut" -ForegroundColor White
Write-Host ''
