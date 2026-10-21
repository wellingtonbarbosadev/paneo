# remoteDesktop

Projeto pessoal para usar aplicativos de um PC Windows em janelas individuais
no Mac, inclusive pela internet. Os programas continuam executando no PC.

**Estado atual:** protótipo PNG/TLS. O cliente Mac recebeu e
exibiu 10 quadros de uma janela sintética do Windows 11 Pro e enviou clique,
texto Unicode, Ctrl+A e Backspace, com resultado conferido no Windows.
Ainda não é o MVP: faltam testes prolongados/aplicativos reais, WebRTC,
internet e integração completa de menus/diálogos. Pareamento persistente,
reconexão e conexões manuais por janela foram adicionados; consulte as validações.

## Projetos

- `mac/RemoteDesktop`: cliente AppKit com conexão autenticada e visualizador interativo.
- `windows/WindowProbe`: captura WGC e host experimental (`--host`).
- `windows/InputProbe`: teste local de texto Unicode com confirmação explícita.
- `scripts`: diagnóstico, build e autorização opcional de SSH para desenvolvimento.
- [docs/VALIDATION.md](docs/VALIDATION.md): evidências, instruções e matriz de testes.
- [docs/PAIRING-AND-CONTROLS.md](docs/PAIRING-AND-CONTROLS.md): pareamento,
  revogação, arraste, composição de texto e múltiplas conexões.

```sh
sh scripts/check-mac.sh
.tools/dotnet/dotnet build windows/WindowProbe/WindowProbe.csproj -c Release
.tools/dotnet/dotnet build windows/InputProbe/InputProbe.csproj -c Release
```

O SDK local `.tools/dotnet` foi instalado neste workspace e é ignorado pelo Git.
Em outro equipamento, use seu SDK .NET 10. Build cruzado não valida execução Windows.

## Contexto para continuar em outro chat

Abra esta pasta como workspace e peça ao agente:

> Leia AGENTS.md, memory.md e PLAN.md, confira o estado dos arquivos e retome a
> partir do próximo passo registrado na memória.

- [memory.md](memory.md): estado atual, propostas, pendências e histórico de continuidade.
- [PLAN.md](PLAN.md): escopo, arquitetura proposta, etapas e critérios de aceite.
- [AGENTS.md](AGENTS.md): instruções de retomada e regras de delegação.

Para testar manualmente, siga os READMEs de [Windows](windows/WindowProbe/README.md)
e [Mac](mac/RemoteDesktop/README.md). Escolha no PC a janela a compartilhar e
copie os dados de pareamento para o Mac. SSH serve somente ao desenvolvimento;
a conexão dos aplicativos usa diretamente TLS, sem túnel SSH.

Próximo trabalho: sessão prolongada com aplicativo real, foco/DPI, rolagem e
falhas de rede; integração de vídeo WebRTC e validação do fluxo de pareamento.
