# Multiplayer online — verificação ponta a ponta

## Bloqueio atual

O jogo não entra em play mode nem gera build. O commit `e2189b0` ("Point YARG.Core submodule to fork") moveu o
submódulo `YARG.Core` de `3beb94e` (junho de 2026) para `8ae7718` (outubro de 2026) sem atualizar o código do
jogo. Por isso, `Assets/Script/Audio/Bass/*` tem 19 erros de compilação (CS0534/CS0115/CS0508: membros abstratos
novos de `AudioManager`, `StemMixer`, `SampleChannel`, `MicDevice` e `MetronomeSampleChannel`).

O problema não vem desta branch: o `master` também não compila. Fora do Bass, a compilação no Unity 6000.3.5f2
não tem nenhum erro, e isso inclui todo o código de multiplayer.

Os itens de duas instâncias só podem ser feitos depois que o jogo for atualizado para a `YARG.Core` do fork (ou o
submódulo voltar para `3beb94e`).

## Passo manual pendente

- [ ] Botão "Multiplayer Online" no menu principal (editor da Unity), ligado a `MainMenu.Multiplayer` e com
      rótulo `Menu.Multiplayer.Title`.

## Step 1 — suíte do núcleo

- [x] 2026-10-30 — `dotnet test Tests/YARG.Multiplayer.Tests` → 108/108 aprovados.

## Step 2 — duas instâncias em localhost

Pendente (bloqueado). Para cada item, anotar a data e o resultado.

1. [ ] Entrar no lobby; versão diferente é recusada com mensagem.
2. [ ] Host escolhe uma música que o cliente já tem → vai direto para "tem a música".
3. [ ] Host escolhe uma pasta ini que o cliente não tem → progresso até 100% e a música aparece na biblioteca do cliente.
4. [ ] Mesmo teste com um `.sng`.
5. [ ] Música CON → toast de erro no host.
6. [ ] Começar → as duas músicas iniciam juntas (diferença perceptível < 50 ms).
7. [ ] A pista remota mostra os acertos e erros reais, ~200 ms atrasada.
8. [ ] Score e multiplicador de banda e uníssono de star power funcionam.
9. [ ] Tela de resultados com os stats de cada um.
10. [ ] Pause duplo no cliente sai da partida e o host continua com o cliente como Left.
11. [ ] Fechar o host → o cliente volta ao menu com aviso.
12. [ ] Com "Salvar músicas" desligado, a música baixada some ao sair da sessão.

Também conferir que não houve regressão offline: Quick Play solo, multiplayer local com 2 perfis, replay e
"tocar com replay".

## Step 3 — duas máquinas via Tailscale

Pendente (bloqueado). Repetir os itens 1, 3, 6, 7 e 11 usando o IP `100.x.y.z` do host.
