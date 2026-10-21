# WindowProbe

Protótipo WPF/C#/.NET 10: captura local PNG, captura contínua WGC e host
experimental PNG/TLS com entrada remota autorizada por janela/porta.

## Compilar e executar

```powershell
dotnet build windows/WindowProbe/WindowProbe.csproj -c Release
dotnet run --project windows/WindowProbe -c Release
```

O alvo de API provisório é Windows build 19041+. Isso não é uma declaração de
suporte a todas as versões do Windows: a compatibilidade precisa ser medida e
as condições de suporte do runtime precisam ser consideradas separadamente.

## Uso

Selecione uma janela visível na lista e clique em Capturar selecionada.
Escolha o destino do PNG no diálogo. A escolha é solicitada em cada captura,
incluindo confirmação antes de substituir um arquivo. A janela registra
somente diagnósticos em memória; o único resultado persistido é o PNG pedido.

## Fluxo técnico

- EnumWindows filtra janelas de outros processos, visíveis, não minimizadas e
  não ocultas pelo DWM.
- RoInitialize(MTA) é pareado com RoUninitialize na mesma thread. A thread
  aguarda todo o trabalho terminar, mantendo o apartamento ativo.
- D3D11CreateDevice cria o dispositivo com BGRA; WARP é alternativa explícita
  quando o dispositivo de hardware não estiver disponível.
- IGraphicsCaptureItemInterop.CreateForWindow cria o item da janela escolhida.
- CreateFreeThreaded permite consultar TryGetNextFrame sem callbacks de UI.
- O frame permanece vivo até a cópia para SoftwareBitmap terminar.
- BitmapEncoder salva PNG; using libera frame, sessão, pool e dispositivo.
- Cancelamento/timeout interrompem a operação e produzem diagnóstico. Uma
  mudança de tamanho durante o frame pede nova captura.

Não há tentativa de contornar conteúdo protegido, tela de login ou UAC.
Janelas minimizadas, bloqueio da sessão, HDR, múltiplos monitores e aplicativos
específicos ainda precisam de ensaios próprios. O host usa PNG/TLS experimental.

## Teste de integração

```powershell
# Executar no desktop interativo, não no desktop isolado do SSH:
dotnet run --project windows/WindowProbe -c Release -- --smoke C:\pasta-de-teste\captura.png
```

O teste abre uma janela sintética deste processo, captura somente essa janela,
salva PNG e resultado JSON ao lado, fecha a janela e retorna 0/1. A pasta deve
existir. Não captura janelas ou arquivos pessoais. O teste não valida aplicativos
de terceiros, teclado, mouse ou transporte Mac/Windows.

## Validação

Build Release executado no Mac (.NET 10.0.402) e no PC Windows (.NET 10.0.302),
ambos com zero erros e avisos. Resultados de execução estão em
[docs/VALIDATION.md](../../docs/VALIDATION.md).

## Host experimental por janela

```powershell
WindowProbe.exe --host
```

Escolher janela, IP local de escuta e porta. O botão Autorizar inicia o servidor;
antes disso nenhuma porta de produto fica aberta. O padrão é loopback: para
conectar pelo Mac, informar o IP da interface de rede do PC. Copiar SHA-256 e
token para o cliente por um canal confiável. Parar encerra as sessões; Revogar
pareamento também invalida as credenciais persistidas via DPAPI. Não modifica
firewall nem roteador automaticamente. Outras janelas podem ser autorizadas
em portas diferentes, para conexões independentes no Mac.

Somente a janela selecionada é capturada. Entrada exige que ela obtenha foco;
cliques cobertos por outra janela são rejeitados. Não há suporte a aplicativos
elevados, UAC, login, minimização, associação automática de diálogos ou sessão independente.
O computador local compartilha foco e cursor com o Mac.

`--stream-smoke caminho.json` captura 10 frames sintéticos e redimensiona a
janela durante o teste. `--remote-smoke pasta` é exclusivamente de desenvolvimento:
abre editor sintético, escreve credenciais temporárias em `session.json`, espera
cliente por até três minutos e grava `host-result.json` sem credenciais. A pasta
deve ser privada; o script `Run-InteractiveSmoke.ps1` aplica ACL e remove a sessão.

Transporte de diagnóstico PNG/TLS, limitado a aproximadamente 8 FPS antes de
considerar latência/codificação. WebRTC continua pendente no produto. Consulte
[pareamento e controles](../../docs/PAIRING-AND-CONTROLS.md) para limites e testes.

`--real-app-soak pasta` abre documento descartável no Bloco de Notas e espera
um cliente de teste; `scripts/test-real-app-soak.py` orquestra 30 minutos de
transporte/rolagem. Não equivale a 30 minutos de uso manual AppKit nem à matriz
de foco/DPI. `--input-interaction-smoke caminho.json` testa arraste/liberação
e Unicode em janelas WPF sintéticas, incluindo perda de foco no PC.
