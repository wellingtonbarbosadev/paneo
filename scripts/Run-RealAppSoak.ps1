#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:USERPROFILE 'remoteDesktop-dev'
if (-not (Test-Path (Join-Path $root '.remoteDesktop-workspace'))) { throw 'Marcador ausente.' }
$source = Join-Path $root ('artifacts/soak-source-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $source -Force | Out-Null
Expand-Archive (Join-Path $root 'soak-source.zip') -DestinationPath $source -Force
$binary = Join-Path $root ('artifacts/soak-bin-' + [Guid]::NewGuid().ToString('N'))
& dotnet build (Join-Path $source 'windows/WindowProbe/WindowProbe.csproj') -c Release --nologo -o $binary
if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }
$out = Join-Path $root 'artifacts/real-app-soak'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $out /inheritance:r /grant:r "${account}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'ACL falhou.' }
foreach ($file in @('session.json','host-result.json','client-done')) {
    $path = Join-Path $out $file
    if (Test-Path $path) { Remove-Item $path }
}
$exe = Join-Path $binary 'WindowProbe.exe'
$name = 'RemoteDesktop-Transient-' + [Guid]::NewGuid().ToString('N')
$action = New-ScheduledTaskAction -Execute $exe -Argument ('--real-app-soak "' + $out + '"') -WorkingDirectory $root
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 34)
try {
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings | Out-Null
    Start-ScheduledTask -TaskName $name
    $deadline = (Get-Date).AddMinutes(33)
    $report = Join-Path $out 'host-result.json'
    while ((Get-Date) -lt $deadline -and -not (Test-Path $report)) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path $report)) { throw 'Timeout sem relatorio.' }
    Start-Sleep -Seconds 2
    Get-Content $report -Raw
} finally {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    $session = Join-Path $out 'session.json'
    if (Test-Path $session) { Remove-Item $session }
}
