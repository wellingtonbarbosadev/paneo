# Teste local de entrada

Executar em Windows na mesma sessao do aplicativo de teste. Sem rede, servico ou
captura de teclas. Nao elevar para tentar controlar aplicativos protegidos.

```powershell
dotnet run --project windows/InputProbe -- --list
dotnet run --project windows/InputProbe -- --send-test 0xHANDLE_DA_LISTA
```

Abra um Bloco de Notas vazio e use seu identificador hexadecimal da lista.
O teste exige digitar `ENVIAR` no terminal antes de ativar a janela e enviar
`Teste remoto: ação 123.`. Nao envia Enter nem executa comandos.

A verificacao do foco antes de cada caractere reduz erros, mas nao torna a
injecao atomica em relacao a mudancas externas de foco. Nao use o computador
localmente durante o teste. Menus ou dialogos abertos no mesmo processo podem
alterar o destino efetivo da entrada; use somente o editor vazio de teste.

Envio Unicode verifica o transporte local de texto. Nao valida eventos fisicos,
atalhos, IME, mouse, teclado PT-BR completo nem integracao com o Mac.
