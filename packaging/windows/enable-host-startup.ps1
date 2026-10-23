# Publica e habilita o host na conta Windows atual. Executar no workspace de
# desenvolvimento marcado, depois de compilar/testar as fontes atualizadas.
# Requer .NET 10 instalado. Não reinicia o PC nem modifica o login do Windows.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:USERPROFILE 'remoteDesktop-dev'
if (-not (Test-Path (Join-Path $root '.remoteDesktop-workspace'))) { throw 'Marcador de workspace ausente.' }
$destination = Join-Path $env:LOCALAPPDATA 'Programs/Paneo'
$marker = Join-Path $destination '.paneo-managed'
if ((Test-Path $destination) -and -not (Test-Path $marker)) { throw 'Destino existente sem marcador; nada foi substituido.' }
if (@(Get-Process WindowProbe -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Feche o host antes de atualizar.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Set-Content -Path $marker -Value 'Paneo runtime-dependent deployment' -Encoding ASCII
& dotnet publish (Join-Path $root 'windows/WindowProbe/WindowProbe.csproj') -c Release -r win-x64 --self-contained false -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publicacao falhou.' }
$exe = Join-Path $destination 'WindowProbe.exe'
# Sem sessao grafica, preparar somente o proximo login. O host valida o
# pareamento ao abrir e permanece visivel/sem compartilhar se ele for invalido.
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$interactive = @(Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { $_.UserName -eq $identity })
if ($interactive.Count -eq 0) {
    if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'RemoteDesktop/state.dat'))) { throw 'Configure primeiro um PIN na janela do host.' }
    $command = '"' + $exe + '" --host --autostart'
    if ($command.Length -gt 260) { throw 'Caminho longo demais para inicializacao.' }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name Paneo -Value $command -PropertyType String -Force | Out-Null
    if ((Get-ItemPropertyValue $runKey -Name Paneo) -ne $command) { throw 'Falha ao verificar registro.' }
    Write-Output 'Inicio no proximo login configurado. Nenhum usuario logado: PIN, listener e captura ainda nao validados nesta instalacao.'
    return
}
# Tarefa temporaria somente para iniciar agora na sessao interativa existente.
$name = 'Paneo-StartOnce-' + [Guid]::NewGuid().ToString('N')
try {
    $action = New-ScheduledTaskAction -Execute $exe -Argument '--host --autostart' -WorkingDirectory $destination
    $principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings | Out-Null
    # Carregar DPAPI/certificado na sessao interativa, inclusive quando chamado via SSH.
    $enableAction = New-ScheduledTaskAction -Execute $exe -Argument '--host --enable-startup' -WorkingDirectory $destination
    Set-ScheduledTask -TaskName $name -Action $enableAction | Out-Null
    Start-ScheduledTask -TaskName $name
    Start-Sleep -Seconds 3
    for ($i = 0; $i -lt 20 -and (Get-ScheduledTask -TaskName $name).State -eq 'Running'; $i++) { Start-Sleep -Milliseconds 500 }
    if ((Get-ScheduledTaskInfo -TaskName $name).LastTaskResult -ne 0) { throw 'Nao foi possivel habilitar inicio automatico; configure o PIN na sessao Windows.' }
    $run = Get-ItemPropertyValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Paneo
    $expected = '"' + $exe + '" --host --autostart'
    if ($run -ne $expected) { throw 'Registro de inicializacao divergente.' }
    Write-Output 'Inicializacao no login registrada e verificada.'
    Set-ScheduledTask -TaskName $name -Action $action | Out-Null
    Start-ScheduledTask -TaskName $name
    Start-Sleep -Seconds 8
    $processes = @(Get-Process WindowProbe -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.SessionId -gt 0 })
    if ($processes.Count -eq 0) { throw 'Host nao iniciou na sessao interativa; confira o estado da tarefa e da sessao.' }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -in @($processes.Id) })
    if ($listeners.Count -eq 0) { throw 'Host aberto mas sem porta em escuta. Confira o estado na janela.' }
    $listeners | Select-Object LocalPort,OwningProcess
    Write-Output 'Host iniciado e compartilhamento em escuta. Reinicio/logoff nao executados.'
} finally {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}
