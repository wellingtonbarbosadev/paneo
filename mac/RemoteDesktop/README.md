# Cliente Mac experimental

AppKit/Swift, macOS 14+, sem dependências externas. Compilar:

```sh
swift build --package-path mac/RemoteDesktop
swift run --package-path mac/RemoteDesktop RemoteDesktop
```

No Windows, executar WindowProbe com `--host`, escolher a janela, informar o IP
local da interface de rede e iniciar. No Mac, preencher IP, porta, impressão
SHA-256 e token exibidos pelo host. O certificado deve ser conferido por um
canal confiável (por exemplo, olhando o PC). O host preserva o pareamento até
revogação. O Mac pode salvar as credenciais no Chaves após autenticar.

O conteúdo é PNG sobre TLS, com confirmação após desenhar cada imagem. Clique
na imagem para dar foco à entrada. Suporta cliques esquerdo/direito, texto
Unicode com composição pelo layout macOS, arraste, rolagem, setas, Enter, Tab,
Escape, Backspace, Delete, Home/End, PageUp/PageDown, F1–F12 e Control+A–Z.
Command continua reservado aos menus do Mac.
Copiar/colar com Control usa o clipboard do Windows, sem sincronizar o Mac.

Trocar o foco para outra janela desconecta e descarta comandos pendentes.
Voltar ao foco reconecta; interrupções têm até cinco tentativas com espera
crescente. Arquivo → Nova Conexão abre outro visualizador para outra porta
autorizada pelo host. Redimensionar mantém a proporção da imagem, sem
redimensionar a janela Windows. Menus/diálogos não são associados automaticamente.
Teclado físico PT-BR completo ainda exige validação. Nenhuma promessa
de desempenho WebRTC: este transporte é um protótipo de diagnóstico.

Teste automatizado, exclusivamente com a janela sintética de RemoteSmoke:

```sh
RemoteDesktop --remote-smoke /caminho/privado/session.json /caminho/resultado.json
```

O arquivo de sessão contém host, port, fingerprint e token; deve ter permissões
restritas e ser removido depois. O teste recebe 10 imagens, clica no editor e
envia uma frase fixa. Relatório e último PNG não contêm credenciais. O teste
local com servidor sintético é `python3 scripts/test-mac-transport.py`; a prova
Windows real está documentada em `docs/VALIDATION.md`.

`python3 scripts/test-mac-reconnect.py` valida o controlador real após queda TLS
e rejeição de pin alterado, usando fixture local e sem tocar no perfil do Chaves.
Detalhes: [pareamento e controles](../../docs/PAIRING-AND-CONTROLS.md).
