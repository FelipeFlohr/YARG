# Port web — estado em 2026-11-01

Branch `claude/web-port`, com tudo commitado (`16b7c276`, `ddda08cd` e este registro). Spec:
`docs/superpowers/specs/2026-10-31-web-port-design.md`. Nada foi enviado com push, e nenhum PR foi aberto.

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

## Falta

1. Pendências menores registradas em `.superpowers/sdd/2026-10-31-web-port-1-boot/final-review.md` (14 Minors).
2. Com o usuário:
   - push e PRs (primeiro o Core em `FelipeFlohr/YARG.Core`, depois o YARG em `FelipeFlohr/YARG`, contra `master`), marcando as mudanças de UI como "não verificadas visualmente";
   - deploy do relay na VPS e hospedagem do build web: pronto em `deploy/` (Docker Compose com relay, Caddy e um
     sidecar Tailscale com HTTPS no tailnet; ver `deploy/README.md`); falta rodar na VPS.

## Como retomar

- Build web rápido: `bash <scratchpad>/webdev.sh <nome-do-log>` → `~/yarg-build-web-dev` (incremental de ~2–3 min; `-webDiagnostics` como argumento extra dá stack traces completos, em ~7 min).
- Suíte: `cd Tests/WebSmoke && YARG_WEB_BUILD="$(cygpath -w ~/yarg-build-web-dev)" npx playwright test`. Leva ~5 min, roda headless e mudo, e põe screenshots, WAVs e JSON de desempenho em `test-results/`.
- Regressão desktop: `<scratchpad>/desktop.sh` (build) e `<scratchpad>/desktop-autotest.ps1` (offline e host + cliente, minimizados e mudos).
