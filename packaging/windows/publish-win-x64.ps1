# Publica o WindowProbe como aplicacao self-contained win-x64 para o instalador.
# Somente build: nao altera firewall, servicos, registro nem executa como admin.
# Windows: powershell -ExecutionPolicy Bypass -File packaging\windows\publish-win-x64.ps1
# Compativel com Windows PowerShell 5.1 (sem sintaxe exclusiva do PowerShell 7).
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # Vazio = le packaging/VERSION (fonte unica de versao).
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$packagingDir = $PSScriptRoot                          # packaging/windows
$repoDir      = Split-Path -Parent (Split-Path -Parent $packagingDir)
$project      = Join-Path $repoDir 'windows/WindowProbe/WindowProbe.csproj'
$versionFile  = Join-Path (Split-Path -Parent $packagingDir) 'VERSION'
$outDir       = Join-Path $packagingDir (Join-Path 'publish' $Runtime)

if (-not $Version) {
    if (-not (Test-Path -LiteralPath $versionFile)) {
        throw "arquivo VERSION ausente: $versionFile"
    }
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "versao fora do formato x.y.z: '$Version'"
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "projeto nao encontrado: $project"
}

# dotnet: prefere o PATH; senao o SDK .NET 10 local documentado no README.
$dotnetExe = $null
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCmd) {
    $dotnetExe = $dotnetCmd.Source
} else {
    $localDotnet = Join-Path $repoDir '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) {
        $dotnetExe = $localDotnet
    } else {
        throw 'dotnet nao encontrado no PATH nem em .tools/dotnet. Instale o .NET 10 SDK.'
    }
}

Write-Host "== dotnet publish $Configuration/$Runtime (versao $Version)"
$dotnetArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $outDir,
    "-p:Version=$Version",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=none',
    '-p:DebugSymbols=false'
)
& $dotnetExe @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falhou com codigo $LASTEXITCODE"
}

$exe = Join-Path $outDir 'WindowProbe.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "saida esperada ausente: $exe"
}
Write-Host "ok: $exe"
Write-Host "nota: binario sem assinatura de codigo; o instalador apenas copia os arquivos."
