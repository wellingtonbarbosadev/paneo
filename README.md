<p align="center">
  <img src="assets/branding/paneo-logo-v2-minimal.png" alt="Logo do Paneo" width="112" />
</p>

<h1 align="center">Paneo</h1>

<p align="center">Seus aplicativos Windows, em janelas no Mac.</p>

<p align="center">
  <strong>Português (Brasil)</strong> · <a href="README.en-US.md" lang="en-US">English (US)</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/macOS-14%2B-222222" alt="Alvo macOS 14 ou posterior" />
  <img src="https://img.shields.io/badge/host-Windows-0078D4" alt="Host Windows" />
  <img src="https://img.shields.io/badge/v%C3%ADdeo-H.264-2563EB" alt="Vídeo H.264" />
  <img src="https://img.shields.io/badge/status-em%20desenvolvimento-64748B" alt="Em desenvolvimento" />
</p>

O Paneo conecta um PC Windows a um cliente nativo para macOS. Escolha uma janela
para trabalhar no Mac, alterne entre aplicativos ou destaque cada um em sua
própria janela. O processamento dos aplicativos continua no PC.

**Projeto em desenvolvimento para uso pessoal.** O transporte atual usa H.264
sobre TLS/TCP, com fallback PNG. As opções de 30 e 60 FPS definem a taxa desejada;
o desempenho efetivo depende do aplicativo, do hardware e da rede.

## Os aplicativos

<p align="center">
  <img src="assets/screenshots/macos-workspace.png" alt="Cliente Paneo no macOS com barra de aplicativos e área de trabalho remota" width="960" />
</p>

<p align="center"><em>Cliente macOS — organização de janelas e acesso aos controles da sessão.</em></p>

| Personalização no Mac | Compartilhamento no Windows |
| --- | --- |
| <img src="assets/screenshots/macos-settings.png" alt="Ajustes do Paneo no macOS com escolhas de tema, posição e densidade da barra" width="480" /> | <img src="assets/screenshots/windows-host.png" alt="Paneo Host no Windows com etapas de PIN, conexão e gerenciamento de sessões" width="480" /> |

*Prévias das interfaces com dados demonstrativos. O endereço exibido no host é
fictício; as imagens não representam uma medição de streaming.*

## O que você pode fazer

- **Escolher seu espaço:** transmitir uma janela ou monitor, alternar pela barra
  de aplicativos e abrir janelas destacadas no Mac.
- **Encontrar aplicativos:** descobrir PCs na rede local, buscar janelas e apps
  publicados pelo host com **⌘K** e manter favoritos por PC.
- **Controlar o Windows:** usar mouse, hover, arraste, rolagem, texto Unicode e
  atalhos comuns de Command convertidos para Control.
- **Ajustar a leitura:** encaixar automaticamente janelas compatíveis na área de
  vídeo e usar a lupa ou **⌘+ / ⌘− / ⌘0** para ajustar o zoom. O host redimensiona
  a janela Windows e restaura sua geometria ao sair ou desconectar.
- **Personalizar o cliente:** temas claro, escuro ou do sistema, cor de destaque,
  barra nas quatro bordas e três densidades. **⌘,** abre Ajustes em popup.
- **Adaptar a sessão:** escolher qualidade, limite de resolução e 30/60 FPS;
  recuperar conexões transitórias sem repetir entradas ou comandos anteriores.

O cliente usa AppKit e VideoToolbox. O visual translúcido acompanha o macOS,
com Liquid Glass nativo no macOS 26+ e alternativa visual nas versões anteriores.

## Começar a usar

Use o cliente Mac e o host Windows compilados a partir deste repositório.
As instruções de build estão abaixo; os pacotes gerados são de desenvolvimento.

1. **No Windows**, abra o **Paneo Host** pelo atalho instalado ou execute
   `WindowProbe.exe --host`. Defina um PIN de 6 a 128 caracteres e clique em
   **Iniciar compartilhamento**. Anote o IP e a porta exibidos (padrão `9443`).
2. **No Mac**, abra `Paneo.app`. Selecione o PC descoberto na rede local ou
   informe seu IP e porta. Digite o PIN na primeira conexão e clique em **Conectar**.
3. **Escolha uma janela** na barra ou use **Apps / ⌘K**. Os próximos acessos usam
   o pareamento salvo; o host Windows permite revogá-lo a qualquer momento.

Para iniciar o host após o login, salve o PIN e marque
**Iniciar e compartilhar automaticamente ao entrar no Windows**. A opção vale
para a conta atual; fechar o host encerra o compartilhamento.

### Pareamento e acesso

O pareamento usa PIN com PBKDF2-HMAC-SHA256 e prova HMAC. O tráfego passa por TLS,
com certificado fixado ao host; credenciais persistidas são protegidas pelo
Chaves do macOS e DPAPI no Windows. O host aplica bloqueio progressivo por IP
para tentativas de autenticação. **Revogar pareamento** encerra os
compartilhamentos, revoga todos os dispositivos pareados e solicita um novo PIN.

### Limites atuais

- O Paneo compartilha a sessão existente do Windows. Mouse e foco são
  compartilhados com quem estiver usando o PC; não há desktop independente.
- O Windows precisa estar ligado e com sessão interativa iniciada. Acesso
  pré-login, desbloqueio, UAC e conteúdo protegido não são suportados.
- Minimizar uma janela pausa seu vídeo; restaurá-la permite retomá-lo. Zoom e
  ajuste automático dependem de janelas redimensionáveis e respeitam seus
  tamanhos mínimos e a área útil do monitor.
- Menus Win32 padrão podem aparecer nos controles do Mac; menus personalizados
  dependem da interação pelo vídeo. Compatibilidade varia por aplicativo.
- Descoberta automática funciona na rede local. Conexão direta pela internet
  sem VPN ou configuração do roteador permanece pendente.
- Compatibilidade ampla, instalação em máquina limpa e 60 FPS sustentados em
  todos os cenários continuam pendentes de validação.

## Compilar a partir do código

Execute os comandos na raiz do repositório, na plataforma indicada.

| Componente | Requisitos de build | Compatibilidade |
| --- | --- | --- |
| Cliente macOS | macOS 14+, Swift 6 e Xcode Command Line Tools | Build para a arquitetura local; não gera binário Universal. |
| Host Windows | .NET 10 SDK; Inno Setup 6.3+ para o instalador | Alvo declarado: Windows 10 build 19041+ x64. Validação realizada em Windows 11 Pro; não equivale a suporte a todas as edições. |

### macOS

```sh
# Compilar o executável
swift build --package-path mac/RemoteDesktop -c release

# Gerar Paneo.app e DMG em packaging/mac/dist/
sh packaging/mac/build-mac.sh
```

O empacotamento exige uma identidade de assinatura chamada `Paneo Development`
no Chaves, ou outra selecionada com `PANEO_CODESIGN_IDENTITY`.
Para um build local com assinatura ad-hoc, selecione explicitamente
`PANEO_CODESIGN_IDENTITY=- sh packaging/mac/build-mac.sh`.
A configuração padrão usa certificado local, sem Developer ID nem notarização;
prompts do Chaves podem reaparecer após recompilar.

### Windows

```powershell
# Compilar o host
dotnet build windows/WindowProbe/WindowProbe.csproj -c Release

# Gerar o instalador em packaging/windows/dist/ (requer Inno Setup)
powershell -ExecutionPolicy Bypass -File packaging/windows/build-installer.ps1
```

O instalador é por usuário, inclui o runtime .NET e não possui assinatura de
código. Ele não configura regras de firewall automaticamente.

### Verificações de desenvolvimento

```sh
# Suítes .NET — execute transporte no Windows para cobrir os caminhos nativos
dotnet run --project tests/TransportTests/TransportTests.csproj -c Release
dotnet run --project tests/AppCatalogTests/AppCatalogTests.csproj -c Release
dotnet run --project tests/VideoQualityChecks/VideoQualityChecks.csproj -c Release

# Interface e preferências — executar no Mac
mac/RemoteDesktop/Scripts/test-customization.sh
mac/RemoteDesktop/Scripts/test-settings.sh
mac/RemoteDesktop/Scripts/test-stream-controls.sh
mac/RemoteDesktop/Scripts/test-zoom-slider.sh
```

Essas verificações cobrem partes do sistema; não substituem uma sessão real
entre Windows e Mac nem testes manuais de teclado, rede e instalação.

## Código e documentação

| Caminho | Conteúdo |
| --- | --- |
| [`mac/RemoteDesktop`](mac/RemoteDesktop) | Cliente Swift/AppKit e harnesses da interface. |
| [`windows/WindowProbe`](windows/WindowProbe) | Host WPF/.NET, captura Windows.Graphics.Capture, encoder e entrada remota. |
| [`tests`](tests) | Suítes de transporte, pareamento, catálogo, vídeo e host. |
| [`packaging`](packaging) | Scripts do bundle Mac e do instalador Windows. |

Detalhes adicionais: [cliente macOS](mac/RemoteDesktop/README.md) ·
[captura e diagnósticos Windows](windows/WindowProbe/README.md).
