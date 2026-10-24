# Cliente Paneo para Mac

Swift 6/AppKit, macOS 14+, sem dependências externas. Compilar:

```sh
swift build --package-path mac/RemoteDesktop
./packaging/mac/build-mac.sh
```

O segundo comando gera `Paneo.app` e DMG com a identidade local configurada.
Consulte [instaladores](../../docs/INSTALLERS.md) para os limites de assinatura.

No Windows, abrir o host, salvar um PIN e iniciar o compartilhamento. No Mac,
selecionar um PC próximo na lista Bonjour ou preencher IP/porta manualmente.
Informar o PIN na primeira conexão; as próximas usam o token do dispositivo
salvo no Chaves. A descoberta não dispensa autenticação e o certificado salvo
continua fixado ao host. A revogação é feita no Windows.

Após conectar, selecionar uma janela ou monitor na barra. **Apps** busca
janelas abertas e aplicativos publicados pelo Windows. A lista distingue
**Abertos agora** e **Aplicativos instalados**; o lançador só aceita IDs do
catálogo do host. **Vídeo** controla qualidade, limite de resolução e 30/60 FPS.
As escolhas valem para a sessão e são reaplicadas após reconectar. A opção
automática usa o RTT; uma qualidade manual não é sobrescrita pelo ajuste automático.

H.264 sobre TLS/TCP v2 é o caminho principal, com PNG de fallback. O limite
de resolução reduz a transmissão sem ampliar imagens menores, mantendo a
proporção; o fallback informa quando não pode aplicá-lo. Janelas abertas atrás
de outras fazem parte do escopo, sem garantia para conteúdo protegido,
programas elevados ou transmissão de janelas minimizadas.

Mouse, hover, arraste, rolagem, composição Unicode e atalhos são encaminhados
ao Windows. A perda de foco libera entrada mantida. Falhas transitórias usam
até cinco tentativas com espera crescente; certificado divergente, autenticação
recusada ou protocolo incompatível interrompem a recuperação. Retomar uma
janela não repete comandos de abertura, arquivos ou entrada antiga.

Temas, barra nas quatro bordas, densidades, favoritos e janelas destacadas
continuam disponíveis. A engrenagem e ⌘, abrem Ajustes em popup modal sobre a janela principal;
Fechar/Esc devolve o foco ao formulário ou workspace. ⌘K abre a busca.

**Visualizar → Aumentar/Diminuir Zoom** muda o tamanho aparente da janela Windows
inteira, incluindo fontes, menus e botões. ⌘+ (ou ⌘=) aumenta, ⌘− diminui e ⌘0
restaura o encaixe padrão (100%). Aumentar pede uma janela menor no PC, com
menos espaço para conteúdo; diminuir pede uma janela maior, com mais espaço.
O vídeo continua encaixado por inteiro, sem recorte para ampliar a imagem.
Usar zoom ativa o ajuste automático somente na janela atual, inclusive nas
destacadas; a preferência salva não muda. A faixa solicitada é de 50% a 300%,
em passos de 10%, limitada pelo tamanho mínimo do aplicativo e pela área útil
do monitor do PC. Janelas fixas, monitores e hosts sem ajuste de janela deixam
o zoom indisponível. A escala volta a 100% ao selecionar outra janela e não é
salva entre execuções. Toda rolagem continua sendo enviada ao Windows.

O botão **Janela** controla a janela Windows selecionada: minimizar,
maximizar/restaurar e solicitar o fechamento normal. Os botões nativos do Mac
continuam controlando apenas o visualizador. Uma janela minimizada mantém seu
card; selecione-o ou use Restaurar para retomar o vídeo.

O **ajuste automático** acompanha a área de vídeo ao selecionar uma janela,
redimensionar o Paneo, mudar a barra ou entrar em tela cheia. O host ajusta o
quadro visível da janela Windows preservando a proporção e respeitando a área
útil do monitor. **Ajustes → Ajustar janelas automaticamente** salva a preferência
neste Mac e aplica a mudança às janelas abertas; o padrão é ligado.
**Janela → Ajustar automaticamente** liga/desliga o ajuste somente para a janela
atual; a marca indica quando está ligado. Desligar, trocar de janela ou desconectar restaura
posição, tamanho e estado anteriores. Uma reconexão reaplica o ajuste se ele
continuar ligado. Janelas fixas, monitores e hosts antigos mantêm a proporção
original; limites de tamanho impostos pelo aplicativo podem deixar margens.

Em **Janela → Menus do aplicativo**, menus Win32 padrão aparecem com submenus,
itens marcados e desabilitados. Aplicativos com menus customizados podem não
oferecer essa integração; o Paneo informa a limitação e mantém o acesso pelo
vídeo. Hosts antigos deixam os novos controles indisponíveis.

## Validação

Na troca de apps, o quadro anterior permanece estável enquanto o novo fluxo
prepara a imagem e a geometria. Uma transição breve revela o vídeo pronto,
respeitando Reduzir Movimento. A entrada remota aguarda essa revelação.
O primeiro ajuste automático é enviado sem o debounce usado durante resize.

```sh
mac/RemoteDesktop/Scripts/test-customization.sh
python3 mac/RemoteDesktop/Scripts/test-app-switch.py
mac/RemoteDesktop/Scripts/test-stream-decoder.sh artifacts/video-media.bin
mac/RemoteDesktop/Scripts/test-settings.sh
mac/RemoteDesktop/Scripts/test-stream-controls.sh
mac/RemoteDesktop/Scripts/test-window-commands.sh
mac/RemoteDesktop/Scripts/test-cursor-interaction.sh
mac/RemoteDesktop/Scripts/test-zoom.sh
python3 scripts/test-mac-transport.py
python3 scripts/test-mac-reconnect.py
```

O teste de decoder requer um arquivo de mídia H.264 com duas sequências de
keyframes; `artifacts/video-media.bin` é uma fixture local, não versionada.
Passe o caminho de uma captura de teste equivalente quando ela não existir.

Os harnesses usam preferências/credenciais sintéticas, sem o pareamento real.
A fixture local não comprova captura nem entrada Windows; essas exigem sessão
no PC. `--remote-smoke` continua disponível para janela sintética autorizada,
com arquivo privado contendo host, port, fingerprint e PIN efêmero. Nunca
imprimir esse arquivo; removê-lo após o teste.

Internet direta gratuita permanece na próxima etapa de [PLAN.md](../../PLAN.md).
