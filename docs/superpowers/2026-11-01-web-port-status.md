# Port web — estado em 2026-11-01

Mergeado nos forks (YARG#6, YARG.Core#2) e depois a rodada de testes e lobby em `claude/web-lobby` (abaixo). Spec:
`docs/superpowers/specs/2026-10-31-web-port-design.md`.

## Feito

- **Subprojeto 1 (build web até o menu)**:
  - `SyncAddressables` faz o preload na web.
  - Config/perfis/bindings ficam em localStorage, com export/import.
  - Os args da web têm allowlist, e há o flag `-webDev` de build rápido.
- **Subprojetos 2–4** foram feitos em paralelo e mergeados em `claude/web-port`:
  - `claude/web-audio-e4c515`: WebAudio via .jslib, microfone → PitchTracker.
  - `claude/web-song-fc09dc`: carregar música do disco (zip/.sng/pasta).
  - `claude/web-mp-f0bc42`: multiplayer por relay WebSocket com senha de lobby, servidor em `Server/YARG.Relay`.
  - Os branches do Core estão no submódulo (local, sem push).
- **Correções da integração** (`16b7c276`):
  - `GameManager`: os resultados aparecem uma vez só. A cena continuava atualizando durante o unload, salvava o replay em dobro e dava `NullReferenceException`. Isso afetava o desktop também.
  - Modo "Jogar de novo" na volta da tela de score.
  - Fallback de cultura no `Genrelizer` (era o `'en-US'` do multiplayer).
  - Fonte padrão de músicas na web.
  - `PlatformTask.Run` no carregamento.
  - Os 31 `.meta` novos.
  - Os `.lib` do Discord ficam fora de todos os players.

## Verificação

- **Testes rápidos**: verdes (Core, Multiplayer, Relay).
- **Suíte de navegador no build `-webDev`: 10 passaram, 1 pulado** (o "web song" do `audio.spec`; o `gameplay.spec` cobre o caso).
  - Testes que passaram:
    - boot;
    - perfis em localStorage;
    - armazenamento corrompido ou com falha;
    - tom pelo mixer C#;
    - microfone falso → PitchTracker;
    - partida multiplayer com senha pelo relay;
    - música até a tela de score, "carregar outra" e músicas inválidas.
  - Gameplay na GPU (RTX 4060 via Edge/ANGLE D3D11): **~158 FPS, p95 de 6,2 ms**, áudio audível sem clipping, tempo da música preso ao relógio de áudio.
- **Build web de release** (`~/yarg-build-web`, 93 MB em Brotli, 31 min de build): os 4 testes de boot passam. O build de release ignora `?web-test`, então os demais testes não se aplicam a ele.
- **Regressão desktop** (build Win64 de `Editor.MultiplayerTestBuild`, instâncias minimizadas e mudas):
  - `-mp-autotest offline 1`: PASS.
  - Host + cliente por TCP: PASS nos dois, com `Band: score=609100 stars=5 players=2` igual nos dois lados.
  - Plugins em `YARG_Data/Plugins` idênticos aos do build anterior ao port (BASS, Discord, SQLite, VLC, libusb, RtMidi, `yarg_audio`).
  - Três `NullReferenceException` só na inicialização ou no encerramento, nenhum deles regressão do port:
    - `FocusHandler.OnApplicationFocus` já aparecia nas execuções antigas;
    - `Dialog.OnDisable` e `ScoreScreenMenu.OnDisable` vêm de código inalterado em relação ao `master`, ao fechar o jogo com a tela de score e o diálogo antipirataria abertos.

## Observações

- O diálogo antipirataria não responde a Enter, porque nenhum botão do diálogo fica selecionado para o teclado. Os testes clicam no botão. É igual no desktop.
- Com um perfil de teclado conectado, o Escape deixa de ser "voltar" no menu (Vermelho é `2`). É comportamento existente do desktop.

## Rodada de 2026-11-01 (`claude/web-lobby`)

- **Áudio**:
  - O `AudioSynchronizer` corrigia o relógio mudando a taxa de reprodução, e na web isso muda o tom (vibrato audível). O `YargWebAudio.jspre` passou a suavizar o relógio e a ancorar o início das fontes.
  - O início e cada retomada da música tocavam a até 1,5× por ~0,3 s (um "chirp"). O seek caía na posição alvo, mas o sincronizador mira a posição menos a latência de saída (~49 ms na web). Agora o `WebStemMixer` já faz o seek descontando a latência.
  - Medido: taxa entre 0,96 e 1,03, fora de 1× em 0–1,2% dos frames. O `songs.spec.mjs` exige menos de 2%.
- **Músicas reais** (`songs.spec.mjs` com as 3 `.sng` de Rush em `YARG_WEB_SONGS`): 158–160 FPS, deriva ≤ 5 ms, carregamento em 0,14–0,27 s. Soliloquy soa baixa (RMS 0,023), mas o trecho é calmo.
- **Lobby online**: a cadeia de diálogos virou a tela `OnlineMenu`, feita em código, sem prefab.
  - Salas: a lista de salas e o formulário de criar ou entrar ficam na mesma tela, com os erros no próprio formulário.
  - Lobby: a música com capa e progresso do download, a banda com a parte e o status de cada um, as vagas abertas e o botão Iniciar.
  - Design: artifact https://claude.ai/artifact/7EH4rsKd8RpUQZ8JkXZ6mj.
  - Protocolo na versão 4 (`LobbyPlayer.HasPart`).
  - O `lobby.spec.mjs` joga tudo pela UI: senha errada, depois a certa; diferença de 2,5–14 ms entre host e convidado; o host continua quando o convidado sai.
- **Deploy** em `deploy/`: Docker Compose com o relay, o Caddy servindo o build local por bind mount e um sidecar Tailscale.
  - Unity dentro do Docker ficou de fora (licença, `Library` de 8 GB, 31 min de build): o build é feito no PC e montado.
  - Não testados contra hosts reais: o primeiro boot do sidecar e o `publish.sh`.
- **HTTPS é necessário.** Microfone (vocais) e AudioWorklet só existem em contexto seguro, e o Unity recusa addressables por `http:` fora de localhost. Use `https://yarg.<tailnet>.ts.net` pelo sidecar (tabela no `deploy/README.md`).
- **Suíte web**: 14 passaram e 1 foi pulado. **Regressão desktop**: offline PASS; host + cliente PASS, com `Band: score=608800 stars=5` nos dois lados.

## Falta

1. Pendências menores registradas em `.superpowers/sdd/2026-10-31-web-port-1-boot/final-review.md` (14 Minors).
2. Com o usuário:
   - deploy do relay na VPS e hospedagem do build web: pronto em `deploy/` (Docker Compose com relay, Caddy e um
     sidecar Tailscale com HTTPS no tailnet; ver `deploy/README.md`); falta rodar na VPS.
   - a UI do lobby no desktop não é exercitada pelo `-mp-autotest` (que pula os menus).

## Como retomar

- Build web rápido: `bash <scratchpad>/webdev.sh <nome-do-log>` → `~/yarg-build-web-dev` (incremental de ~2–3 min; `-webDiagnostics` como argumento extra dá stack traces completos, em ~7 min).
- Suíte: `cd Tests/WebSmoke && YARG_WEB_BUILD="$(cygpath -w ~/yarg-build-web-dev)" npx playwright test`. Leva ~5 min, roda headless e mudo, e põe screenshots, WAVs e JSON de desempenho em `test-results/`.
- Regressão desktop: `<scratchpad>/desktop.sh` (build) e `<scratchpad>/desktop-autotest.ps1` (offline e host + cliente, minimizados e mudos).
