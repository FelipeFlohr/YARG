# Port web — subprojetos 2 (áudio WebAudio) e 4 (microfone) — detalhe

Complementa as seções 4.1–4.5 de `2026-10-31-web-port-design.md`. Só o que aquele documento não decide.

## Peças

| Arquivo | Papel |
|---|---|
| `Assets/Plugins/WebGL/YargWebAudio.jspre` | Módulo JS `Module.YargAudio`: contexto, mixers, stems, samples, one-shots, microfone, debug. Fica no `.jspre` (código comum, testável com `node`), exposto também em `window.YargAudio`. |
| `Assets/Plugins/WebGL/YargWebAudio.jslib` | Só as funções `extern` (`WebAudio_*`) que copiam argumentos do heap e chamam `Module.YargAudio`. |
| `Assets/Script/Audio/Web/WebAudioNative.cs` | `DllImport`s; fora da build web são stubs que falham (retornam 0). |
| `WebAudioManager`, `WebStemMixer`, `WebStemChannel`, `WebSampleChannel`, `WebDrumSampleChannel`, `WebVoxSampleChannel`, `WebMetronomeSampleChannel`, `WebVenueSampleChannel`, `WebOneShotChannel`, `WebMicDevice`, `WebOutputChannel` | Implementações das classes de `YARG.Core/Audio`. Guardam handles inteiros. |
| `Assets/Script/Audio/DeferredAudio.cs` | `IDeferredStemMixer` (decodificação assíncrona) e `StemUsageHint` (stems tocados pelos jogadores). Neutros de plataforma. |
| `Tests/WebAudio/*.test.mjs` | `node --test` da lógica pura do `.jspre`. |
| `Tests/WebSmoke/audio.spec.mjs` | Playwright para depois da integração. |

## Decisões

1. **Decodificação assíncrona.** `decodeAudioData` não tem versão síncrona e `AddChannel` precisa retornar na hora.
   `AddChannels_Internal` copia os bytes para o JS, cria o `WebStemChannel` e retorna `true`; o JS decodifica numa
   fila, um stem por vez. O mixer implementa `IDeferredStemMixer` (`IsLoading`, `LoadError`). `GameManager` espera
   `IsLoading == false` depois da fila de carregamento e, se houver `LoadError`, segue o caminho de erro de
   carregamento que já existe (toast + volta ao menu). `Length` só fica certo depois disso. `Play()` antes do fim da
   decodificação fica pendente e começa quando tudo estiver pronto (preview do menu).
2. **Memória.** `GameManager.LoadAudio` grava em `StemUsageHint.PlayedStems` os stems dos jogadores (em partida
   online, todos os stems, porque os instrumentos remotos só chegam depois) e volta a null logo depois. Na web, um
   stem fora desse conjunto (e que não é `Crowd`) não ganha canal próprio: seus bytes vão para o stem de fundo
   (`Song`), somados no buffer dele logo depois de decodificados e descartados antes do próximo. Fora do gameplay
   (null: preview e música do menu) ninguém muta nada, então tudo menos `Crowd` vira um stem só. O BASS ignora a dica.
   `RangeError`/falha de alocação na decodificação vira `LoadError` amigável ("A música é grande demais para o
   navegador…").
3. **Tempo.** `pos(t) = (t − t0) × rate + offset`, com `t = ctx.currentTime`. Mudança de `rate` re-ancora
   (`offset = pos(agora)`, `t0 = agora`) e aplica `playbackRate` em todas as sources. `GetPosition` (heard) é `pos`;
   `Control` = `pos − calibração × songSpeed`, como no fallback do BASS. `PlaybackLatency` do manager =
   `outputLatency + baseLatency` (ms), atualizado por frame, e entra na calibração quando
   `AccountForHardwareLatency` está ligado — é assim que "o padrão considera `outputLatency`".
   Para não começar no meio de um quantum, o start é agendado em `ctx.currentTime + 0.05`.
4. **Volume.** Stem: ganho linear com `setTargetAtTime(v, now, 0.015)` (rampa curta, sem clique). Mixer: curva
   exponencial do BASS (`(2^v − 1)`), fades com `linearRampToValueAtTime`. Normalização, reverb, whammy e
   compressor: no-op.
5. **Mogg / StemInfo com índices.** O JS decodifica uma vez e monta cada stem estéreo pela matriz de panning
   (`pan[2i]` esquerda, `pan[2i+1]` direita), igual a `BassHelpers.BuildVolumeMatrix`.
6. **One-shots (metrônomo, palmas).** `OneShotChannel.UpdateSchedule` vira abstrato no Core (branch
   `claude/web-audio`), e os schedulers deixam de fazer cast para `BassOneShotChannel`. Na web, o JS agenda as
   batidas que caem nos próximos 0,5 s a cada `tick` e cancela as agendadas em seek, pausa ou mudança de rate.
7. **Tone channel** (pitch guia de vocal) e **push stream** (áudio de vídeo): não existem na web (null /
   `NotSupportedException`, que os chamadores já tratam).
8. **Autoplay.** O contexto é criado no `Initialize` (fica `suspended`) e `resume()` roda em qualquer
   `pointerdown`/`keydown` da página, o que inclui o clique em `#yarg-start`. Não mexe no `index.html`.
9. **Microfone.** `getUserMedia` com os três processamentos desligados, `deviceId` exato quando o nome casa com
   `enumerateDevices`. Um `AudioWorklet` (código num Blob URL) manda blocos de 128 amostras por `postMessage`; o
   JS guarda num ring buffer mono de 2 s. `WebAudioManager.Update` esvazia o buffer de cada `WebMicDevice` em
   frames de 40 ms e roda a mesma análise do `BassMicAnalyzer` (amplitude em dB, hit, `PitchTracker`). Retorno
   (monitoring) é um `GainNode` em 0 até `SetMonitoringLevel`. Permissão negada: toast de aviso, o device fica
   inválido (sem frames) e o jogador pode trocar de instrumento. A lista de devices vem de `enumerateDevices`; sem
   permissão os nomes vêm vazios e a lista mostra "Microfone padrão".
10. **Debug** (`Module.YargAudio.debug`, também `window.yargAudioDebug`; `window.yargPerf`):
    `state()`, `stems()`, `master()`, `songTime()`, `startRecording()`/`stopRecording()` (WAV PCM 16 bits do
    master por um tap `AudioWorklet`), `mic()` (RMS de entrada e último pitch que o C# detectou), e
    `requestToneTest(seconds)` / `requestMicTest()` que o C# atende no próximo frame criando um mixer de verdade
    (bytes WAV gerados no JS, passados pelo mesmo `AddChannel`) ou um `WebMicDevice` de verdade.
    Esses dois mudam estado, então o C# só os atende em builds de teste (`YARG_WEB_TEST_HOOKS`, definido por
    `-webDev`/`-webTestHooks`); no release o pedido é descartado com um aviso no log. As sondas só de leitura
    (e a gravação do master) ficam no release.
    `yargPerf`: média/p95 dos deltas de `requestAnimationFrame` numa janela de 120 frames.

## Fora daqui

WebCodecs/streaming (plano B do documento principal), efeitos, saída multicanal, seleção de dispositivo de saída.
