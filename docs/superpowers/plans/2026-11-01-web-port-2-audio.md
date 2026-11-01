# Port web 2+4 (áudio e microfone) — plano

Design: `docs/superpowers/specs/2026-11-01-web-port-2-audio-design.md`. Escrever todo o código primeiro e testar no
fim; Unity não roda nesta sessão (o controlador compila e gera os `.meta` na integração).

1. **Core** (`YARG.Core`, branch `claude/web-audio`): `OneShotChannel.UpdateSchedule` abstrato; stub do teste
   `PreviewContextTests` atualizado. `dotnet build` + `dotnet test`.
2. **JS** `Assets/Plugins/WebGL/YargWebAudio.jspre`: módulo `YargAudio` (contexto, desbloqueio por gesto, clock,
   mixers e stems com fila de decodificação e soma no fundo, samples, one-shots, microfone com worklet e ring
   buffer, gravação WAV, debug, `yargPerf`). Exporta a lógica pura para `node`.
3. **JS** `Assets/Plugins/WebGL/YargWebAudio.jslib`: funções `WebAudio_*` finas.
4. **C#** `Assets/Script/Audio/Web/`: `WebAudioNative` e as classes `Web*`; `DeferredAudio.cs` em `Audio/`.
5. **Integração**: `GlobalVariables` usa `WebAudioManager` no mesmo `#if`; `GameManager.Audio` grava
   `StemUsageHint` e espera `IDeferredStemMixer`; `MetronomeScheduler`/`CrowdClapScheduler` sem cast para BASS;
   `BassOneShotChannel.UpdateSchedule` com `override`. Apagar `SilentAudioManager.cs` não (tem `.meta`); fica sem uso.
6. **Testes**: `Tests/WebAudio` (`node --test`: clock, ring buffer, WAV, matriz, janela de one-shots, tom de teste);
   `node --check` nos corpos JS; `Tests/WebSmoke/audio.spec.mjs` (tom de teste via hook: RMS, tempo ±50 ms, WAV de
   5 s em `test-results/`, screenshots, FPS; mic falso com pitch ~440 Hz; música real por `?mp-autotest=offline`
   quando `YARG_WEB_SONG_QUERY` estiver definido).
7. **Verificação**: testes do Core e de multiplayer, `node --test`, commit no branch e nota de entrega.
