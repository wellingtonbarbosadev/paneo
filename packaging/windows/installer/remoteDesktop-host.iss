; Paneo Host — instalador POR USUARIO (Inno Setup 6.3+).
;
; Regras deste escopo:
;   * PrivilegesRequired=lowest: instala em {localappdata}, sem administracao.
;   * Nenhuma regra de firewall, servico, tarefa agendada, registro proprio
;     nem inicializacao automatica e criada pelo instalador. O usuario pode
;     habilitar inicio apos login no host; a desinstalacao remove essa opcao.
;   * Versao deve ser igual a packaging/VERSION; confira com
;     packaging/check-versions.sh antes de compilar.
;   * Sem certificado de assinatura: SmartScreen pode avisar na primeira
;     execucao. Isso nao e distribuicao aprovada.
;
; Compilar: powershell -ExecutionPolicy Bypass -File packaging\windows\build-installer.ps1

#define MyAppName "Paneo Host"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Paneo"
#define MyAppExeName "WindowProbe.exe"

[Setup]
AppId={{8B6E1F44-2D97-4A0C-B5E3-9F1A4C7D2E60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\remoteDesktop
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Instalacao por usuario: sem prompt de administracao.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
; Alvo declarado: Windows 10 build 19041+ x64 (mesmo alvo do csproj).
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Paneo-host-{#MyAppVersion}-win-x64-setup
SetupIconFile=../../../assets/branding/paneo.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoTextVersion={#MyAppVersion}.0

[Languages]
; Incluido na instalacao padrao do Inno Setup 6.
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Area de Trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
; Saida do publish self-contained (packaging/windows/publish-win-x64).
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--host"; WorkingDir: "{app}"; Comment: "Compartilhe aplicativos Windows com o Paneo no Mac"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--host"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--host"; WorkingDir: "{app}"; Description: "Iniciar {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--host --disable-startup"; WorkingDir: "{app}"; RunOnceId: "DisablePaneoStartup"; Flags: runhidden skipifdoesntexist
