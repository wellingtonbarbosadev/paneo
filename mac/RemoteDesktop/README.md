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

```sh
mac/RemoteDesktop/Scripts/test-customization.sh
mac/RemoteDesktop/Scripts/test-settings.sh
mac/RemoteDesktop/Scripts/test-stream-controls.sh
mac/RemoteDesktop/Scripts/test-window-commands.sh
mac/RemoteDesktop/Scripts/test-cursor-interaction.sh
python3 scripts/test-mac-transport.py
python3 scripts/test-mac-reconnect.py
```

Os harnesses usam preferências/credenciais sintéticas, sem o pareamento real.
A fixture local não comprova captura nem entrada Windows; essas exigem sessão
no PC. `--remote-smoke` continua disponível para janela sintética autorizada,
com arquivo privado contendo host, port, fingerprint e PIN efêmero. Nunca
imprimir esse arquivo; removê-lo após o teste.

Internet direta gratuita permanece na próxima etapa de [PLAN.md](../../PLAN.md).
