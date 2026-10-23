#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:USERPROFILE 'remoteDesktop-dev'
if (-not (Test-Path (Join-Path $root '.remoteDesktop-workspace'))) { throw 'Marcador de workspace ausente.' }
$source = Join-Path $root ('artifacts/app-compatibility-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $source -Force | Out-Null
Expand-Archive (Join-Path $root 'application-compatibility-source.zip') -DestinationPath $source -Force
$binary = Join-Path $root ('artifacts/app-compatibility-bin-' + [Guid]::NewGuid().ToString('N'))
& dotnet build (Join-Path $source 'windows/WindowProbe/WindowProbe.csproj') -c Release --nologo -o $binary
if ($LASTEXITCODE -ne 0) { throw 'Build da matriz de compatibilidade falhou.' }
$out = Join-Path $root 'artifacts/application-compatibility'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $out /inheritance:r /grant:r "${account}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível proteger os artefatos temporários.' }
$report = Join-Path $out 'application-compatibility-result.json'
if (Test-Path $report) { Remove-Item $report }
$exe = Join-Path $binary 'WindowProbe.exe'
$name = 'Paneo-AppCompat-' + [Guid]::NewGuid().ToString('N')
$action = New-ScheduledTaskAction -Execute $exe -Argument ('--application-compatibility-smoke "' + $out + '"') -WorkingDirectory $root
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 8)
try {
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $name
    $deadline = (Get-Date).AddMinutes(7)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $report)) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path $report)) { throw 'Timeout sem relatório da matriz.' }
    Get-Content $report -Raw
    if (-not ((Get-Content $report -Raw | ConvertFrom-Json).success)) { throw 'A matriz de compatibilidade reportou falhas.' }
} finally {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}
