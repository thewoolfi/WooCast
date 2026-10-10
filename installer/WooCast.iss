; Установщик WooCast.
;
; Собирается Inno Setup:
;     iscc.exe installer\WooCast.iss
; Версию можно задать снаружи, чтобы не править файл при каждом выпуске:
;     iscc.exe /DAppVersion=0.2.0 installer\WooCast.iss
;
; Ставит всё сразу — приложение, ffmpeg, установщик драйвера геймпада и приложение
; для приставки. Отдельных файлов, которые нужно раскладывать руками, не остаётся.
;
; Установка идёт в папку пользователя, поэтому права администратора не нужны:
; запрос прав появится только при установке драйвера геймпада, и то по желанию —
; это отдельный пункт в мастере.

#define AppName "WooCast"
#define AppPublisher "WooCast contributors"

; Версия по умолчанию: используется, если не передали /DAppVersion.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; Папка поставки собирается скриптом tools/publish.ps1 и называется по версии.
#define SourceDir "..\dist\WooCast-Desktop-" + AppVersion

[Setup]
; Постоянный идентификатор: по нему Windows понимает, что это та же программа,
; и при установке новой версии убирает прежнюю.
AppId={{7F2C4A18-9D63-4B57-9E21-3C8A5F0D6B44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Папка пользователя: установка без прав администратора.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

OutputDir=..\dist
OutputBaseFilename=WooCast-Setup-{#AppVersion}
SetupIconFile=..\assets\icon\woocast.ico
UninstallDisplayIcon={app}\WooCast.exe
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "driver"; Description: "Установить драйвер геймпада (нужен только для управления с контроллера)"; GroupDescription: "Дополнительно:"

[Files]
; Всё содержимое папки поставки, включая ffmpeg и установщик драйвера.
; Приложение для приставки исключено: оно ставится на телефон или телевизор,
; а не на компьютер, и распространяется отдельным файлом. Держать его внутри
; установщика для Windows незачем — это лишние двенадцать мегабайт в каждом
; скачивании.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.apk"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\WooCast.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WooCast.exe"; Tasks: desktopicon

[Run]
; Драйвер ставится отдельным установщиком и только если его отметили: он не нужен
; для трансляции, а права администратора запросит именно он.
Filename: "{app}\driver\ViGEmBus_setup.exe"; Description: "Установить драйвер геймпада"; Tasks: driver; Flags: shellexec waituntilterminated

Filename: "{app}\WooCast.exe"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent
