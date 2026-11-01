# Port web, subprojeto 3: carregar música — Plano

**Spec:** `docs/superpowers/specs/2026-11-01-web-port-3-song-design.md` (detalha a §5 do design do port web).

**Branches:** YARG `claude/web-song-fc09dc`; Core `claude/web-song`.

Execução nativa, sem subagentes; todo o código primeiro, testes no fim. Sem Unity neste worktree: a integração
(compilar, gerar `.meta`, build WebGL, Playwright) fica com a sessão controladora.

## Tarefas

1. **Core**
   - `Song/SingleSongLoader.cs`: `SingleSongLoader.Load(string path)` → `SingleSongResult { Entry, Error, Reason }`.
   - `IO/Images/YARGImage.cs`: `DecoderOverride` e `FromPixels`.
   - `IO/SngHandler/SngFileStream.cs`: `UseParallelDecrypt`.
   - Testes: `Song/SingleSongLoaderTests.cs`, `IO/SngDecryptTests.cs`, `IO/YARGImageOverrideTests.cs`.
   - Rodar `dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build`; commit no submódulo.
2. **jslib** `Assets/Plugins/WebGL/YargWebSong.jslib`: init, `PickSong(folder)`, `SetDropEnabled`, gravação em
   MEMFS, `window.yargTest`. `node --check` (embrulhado, já que `mergeInto` não é módulo).
3. **C#** `Assets/Script/Song/Web/`: `WebSongBridge`, `WebSongLoader` (+ `WebSongLoadException`),
   `WebImageDecoder`, `WebSongTestHooks`.
4. **Menu** `Assets/Script/Menu/Web/WebSongMenu.cs` montada em código; ganchos em `MainMenu.QuickPlay`,
   `GameModesMenu.Practice` e `MenuManager.Start`. Textos em `lang/en-US.json` e `lang/pt-BR.json` (`WebSong`).
5. **Testes web** `Tests/WebSmoke/fixtures/song/*`, `fixtures/make-song.mjs`, rota `/__fixtures/` no `serve.mjs`,
   `song.spec.mjs`, README.
6. Commit do YARG com o ponteiro do submódulo.

## Não verificado até a build Unity

Compilação do C# Unity, layout e aparência da tela (não verificada visualmente), `SendMessage` do jslib, decodificação
de capa por `Texture2D.LoadImage`, `song.spec.mjs` inteiro.
