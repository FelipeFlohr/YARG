# Port web — estado em 2026-11-01 (pausado)

Branch `claude/web-port` (HEAD `c9a3283c`, mais as mudanças **não commitadas** listadas abaixo). Spec:
`docs/superpowers/specs/2026-10-31-web-port-design.md`. Nada foi enviado com push, e nenhum PR foi aberto.

## Feito

- **Subprojeto 1 (build web até o menu)**: commitado, com as correções da revisão final (`0c1a3e19`).
  - `SyncAddressables` faz o preload na web.
  - Config/perfis/bindings ficam em localStorage, com export/import.
  - Os args da web têm allowlist, e há o flag `-webDev` de build rápido.
- **Subprojetos 2–4** foram feitos em paralelo e mergeados em `claude/web-port`:
  - `claude/web-audio-e4c515`: WebAudio via .jslib, microfone → PitchTracker.
  - `claude/web-song-fc09dc`: carregar música do disco (zip/.sng/pasta).
  - `claude/web-mp-f0bc42`: multiplayer por relay WebSocket com senha de lobby, servidor em `Server/YARG.Relay`.
  - Os branches do Core estão no submódulo (local, sem push).
- **Testes rápidos**: verdes (Core, Multiplayer, Relay).
- **Suíte de navegador (`Tests/WebSmoke`) no build `-webDev` integrado: 10 passaram, 1 pulado.**
  - Passaram:
    - boot;
    - round-trip de perfis em localStorage;
    - armazenamento corrompido ou com falha;
    - tom pelo mixer C# (nível, pitch e tempo);
    - microfone falso → PitchTracker;
    - partida multiplayer com senha pelo relay;
    - música carregada até a tela de score, "carregar outra" e músicas inválidas;
    - gameplay na GPU (RTX 4060 via Edge/ANGLE D3D11): **~158 FPS, p95 de 6,2 ms**, áudio audível sem clipping, tempo da música preso ao relógio de áudio.
  - Pulado: "web song" do `audio.spec`. Ele pede `YARG_WEB_SONG_QUERY`, e o `gameplay.spec` já cobre o caso.

## Correções desta rodada (não commitadas)

- `GameManager.cs`: flag `_resultsShown`. Antes, a cena de gameplay continuava atualizando durante o unload assíncrono e chamava `ShowResults` duas vezes. Isso salvava o replay em dobro e dava `NullReferenceException` em `GlobalVariables.LoadScene`. Afeta o desktop também.
- `WebSongMenu.cs`: o modo "Result" (jogar de novo) agora vem de `MusicLibraryMenu.CurrentlyPlaying`. O `ScoreScreenMenu.OnDisable` reseta `GlobalVariables.State` antes de o menu abrir.
- `Genrelizer.cs`: `TextInfo` cai para `InvariantCulture` quando a cultura não existe na web. Era o `KeyNotFoundException 'en-US'` do multiplayer.
- `SongSources.cs`: há fonte padrão de backup quando nenhuma lista de fontes foi carregada (web). O ícone carrega via `PlatformTask.Run`.
- `GameManager.Loading.cs`, `Sidebar.cs`: `PlatformTask.Run` no lugar de `Task.Run` (sem threads na web), mais o `using YARG.Helpers`.
- `WebSongLoader.cs`, `CareerMenu.cs`: ajustes de compilação (overloads ambíguos de `LogFormat*`).
- Metas do Discord `.lib` (`Assets/Plugins/DiscordGameSDK/...lib.meta`): reimportados pelo Unity 6. O `WebBuild` os exclui da web.
- Testes:
  - `playwright.config.mjs`: o projeto `gpu` usa o Edge (o chromium do Playwright está bloqueado nesta máquina), e o microfone falso fica na config.
  - `audio.spec.mjs`: limites de software (FPS > 5, p95 < 1 s).
  - `song.spec.mjs`: volta com `2` (Vermelho), porque com perfil de teclado o Escape não é "voltar".
  - `gameplay.spec.mjs` (novo).
  - `fixtures/dialogs.mjs` (novo): clica em "I understand" no diálogo antipirataria.
- **31 `.meta` novos** gerados pelo Unity para os arquivos novos (Audio/Web, Song/Web, Menu/Web, Multiplayer, Plugins/WebGL). Precisam entrar no commit.

## O que estava em andamento

O build desktop de regressão (`Editor.MultiplayerTestBuild` → `~/yarg-build-desktop`) foi **interrompido no meio** a pedido. O próximo build do Unity pode demorar mais, porque troca o target de volta.

## Falta

1. Commitar as correções acima, mais os metas e os testes. Mensagens com `Co-Authored-By`.
2. Regressão desktop:
   - build Win64 (`desktop.sh` no scratchpad, ou o comando de `MultiplayerTestBuild`);
   - `-mp-autotest` host/join e offline, com instâncias minimizadas, `FullscreenMode 3` e `-persistent-data-path` distintos;
   - conferir os plugins em `YARG_Data/Plugins` contra o `master` (Discord, BASS etc. presentes).
3. Build web de release (sem `-webDev`; ~25 min) mais um smoke rápido. O build de release ignora `?web-test`, então só o `boot.spec` se aplica.
4. Pendências menores registradas em `.superpowers/sdd/2026-10-31-web-port-1-boot/final-review.md` (14 Minors).
5. Com o usuário:
   - push e PRs (primeiro Core em `FelipeFlohr/YARG.Core`, depois YARG em `FelipeFlohr/YARG`, contra `master`), marcando as mudanças de UI como "não verificadas visualmente";
   - deploy do relay na VPS.

## Como retomar

- Build web rápido: `bash <scratchpad>/webdev.sh <nome-do-log>` → `~/yarg-build-web-dev` (incremental de ~2–3 min; `-webDiagnostics` como argumento extra dá stack traces completos, em ~7 min).
- Suíte: `cd Tests/WebSmoke && YARG_WEB_BUILD="$(cygpath -w ~/yarg-build-web-dev)" npx playwright test`. Leva ~5 min, roda headless e mudo, e põe screenshots, WAVs e JSON de desempenho em `test-results/`.
