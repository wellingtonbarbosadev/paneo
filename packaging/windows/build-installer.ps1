# Compila o instalador por usuario do remoteDesktop Host com Inno Setup 6.
# Sem administracao: PrivilegesRequired=lowest no .iss. Nao cria firewall,
# servico, tarefa agendada nem inicializacao automatica.
# Windows: powershell -ExecutionPolicy Bypass -File packaging\windows\build-installer.ps1
# Requisitos: .NET 10 SDK (publicacao) e Inno Setup 6.3+ (ISCC.exe).
[CmdletBinding()]
param(
    # Caminho manual do ISCC.exe, se o Inno Setup estiver em local nao padrao.
    [string]$IsccPath = '',

    # Reaproveita packaging\windows\publish\win-x64 ja existente.
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot                       # packaging/windows
$issFile      = Join-Path $packagingDir 'installer\remoteDesktop-host.iss'
$distDir      = Join-Path $packagingDir 'dist'
$versionFile  = Join-Path (Split-Path -Parent $packagingDir) 'VERSION'

if (-not (Test-Path -LiteralPath $issFile)) {
    throw "script do Inno ausente: $issFile"
}
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "arquivo VERSION ausente: $versionFile"
}
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
$issText = Get-Content -LiteralPath $issFile -Raw
if ($issText -notmatch [regex]::Escape("#define MyAppVersion `"$version`"")) {
    throw "#define MyAppVersion do .iss difere de packaging/VERSION ($version). Rode packaging/check-versions.sh."
}

if (-not $SkipPublish) {
    & (Join-Path $packagingDir 'publish-win-x64.ps1')
}

$candidates = @()
if ($IsccPath)        { $candidates += $IsccPath }
if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
if ($env:ProgramFiles)        { $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }
if ($env:LOCALAPPDATA)        { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }

$iscc = $null
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) { $iscc = $candidate; break }
}
if (-not $iscc) {
    throw 'ISCC.exe nao encontrado. Instale o Inno Setup 6.3+ (https://jrsoftware.org/isinfo.php) ou informe -IsccPath.'
}

Write-Host "== ISCC $issFile"
& $iscc $issFile
if ($LASTEXITCODE -ne 0) {
    throw "ISCC falhou com codigo $LASTEXITCODE"
}

$setup = Get-ChildItem -LiteralPath $distDir -Filter 'remoteDesktop-host-*-setup.exe' -ErrorAction SilentlyContinue
if (-not $setup) {
    throw "instalador nao encontrado em $distDir"
}
foreach ($file in $setup) { Write-Host "ok: $($file.FullName)" }
Write-Host 'nota: instalador local sem certificado de assinatura; SmartScreen pode avisar na primeira execucao.'
