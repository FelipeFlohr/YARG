# Multiplayer online com a banda inteira: pontuação, mute de stems, bots e instrumentos — Design

Data: 2026-10-31
Status: escrito sem revisão do usuário (sessão autônoma); decisões em §8

## 1. Objetivo

Garantir, com testes automatizados, que uma partida online deste fork funciona com a banda inteira:

1. **Pontuação sincronizada:** ao final da música, todas as máquinas mostram a mesma pontuação de cada jogador e a
   mesma pontuação/estrelas de banda.
2. **Mute de stem por erro, visto por todos:** se a música tem stems separados e um jogador erra, o stem do
   instrumento dele fica mudo até o próximo acerto, na máquina dele e nas dos outros (com o atraso de rede já
   aceito). Sem stems separados, nada muta. Com vários jogadores no mesmo instrumento, o stem só muta enquanto
   **todos** eles estiverem no estado "errou e ainda não acertou de novo".
3. **Bots:** um jogador com perfil de bot entra na sala normalmente e toca como bot em todas as máquinas.
4. **Instrumentos:** guitarra, baixo/rhythm, bateria, teclado (5 lanes e pro), vocal funcionam online.
5. **Até 8 máquinas por sala** (o limite atual é 4); os testes usam no máximo 4 instâncias.

O que o usuário disse está nos itens 1–4; o limite de 8 vem da frase "pode ter 8 players no máximo por enquanto".

### Critérios de sucesso

- `dotnet test` (Core e `Tests/YARG.Multiplayer.Tests`) cobre: regra de mute, cálculo do resultado de banda,
  fidelidade da pista remota por instrumento (humano e bot) e o gerador de inputs de teste.
- O `-mp-autotest` estendido roda, sem ninguém no teclado, quatro cenários (§6.3) com 2 a 4 instâncias numa build
  Linux, e um verificador lê os logs de todas as instâncias e só passa se pontuações, estrelas e eventos de mute
  baterem com o esperado.

## 2. O que já existe (lido no código)

| Fato | Onde |
|---|---|
| Jogador remoto é um `BasePlayer` com engine própria alimentada pelos inputs de rede, rodando `D = 200 ms` atrás | `BasePlayer.UpdateRemoteInputs`, `YargPlayer(ReplayFrame, int, RemoteInputBuffer)` |
| `OnNoteMissed`/`OnNoteHit` do `TrackPlayer` chamam `UpdateMuteState` → `SetStemMuteState` também para jogadores remotos (só `IsSeekingReplay` bloqueia). Ou seja, o mute por erro de um remoto **já acontece** nas outras máquinas | `TrackPlayer.cs` (~1260–1350) |
| Contagem de mute por stem: `StemState.Total`/`Audible` (contadores). Regra padrão **proporcional** (`Volume * Audible / Total`); `MuteOnlyWhenAllPlayersMiss` (padrão **desligado**) troca para "só muta quando todos erram" | `GameManager.Audio.cs`, `SettingsManager.Settings.cs` |
| Música sem stems separados: o único canal vira `_backgroundStem`; `MuteOnMiss = MultitrackOnly` (padrão) ignora mute no background, e os players nem acham o próprio stem em `_stemStates` | `GameManager.Audio.cs`, `GameManager.Loading.cs` (~683) |
| Baixo sem `bass.opus` usa o stem `Rhythm` (Subdivisions tem `rhythm.opus`, não `bass.opus`) | `FiveFretGuitarPlayer.Initialize`, `FiveLaneKeysPlayer`, `GameManager.Loading.cs` |
| Bateria muta `Drums1..4` separadamente; vocal **nunca** muta (`VocalsPlayer.SetStemMuteState` vazio) | `DrumsPlayer.cs`, `VocalsPlayer.cs` |
| `TrackPlayer.DropOut` (remoto que saiu) chama `SetStemMuteState(false)`: o jogador que saiu continua contando como "audível" para sempre | `TrackPlayer.cs` (~152) |
| Pontuação individual na tela de resultados: stats do `Finished` que o próprio jogador mandou (autoritativos) | `GameManager.GetResultStats` |
| Pontuação de banda na tela de resultados: `EngineManager.Score` = soma das **simulações locais** (`player.Score + BandBonusScore`). Diverge entre máquinas porque o multiplicador de banda (`_starpowerCount * 2`) depende de quando cada máquina vê o star power dos outros, e as engines remotas estão atrasadas | `GameManager.Update`, `GameManager.ShowResults`, `EngineManager.Band.cs` |
| Bot remoto: `PlayerSetup` leva `IsBot`; a engine remota roda o bot localmente. Nada no lobby bloqueia perfil bot | `MultiplayerGameplay.OnGameMessage`, `MessageCodec` |
| Limite de máquinas: `ProtocolInfo.MaxMachines = 4` | `Multiplayer/Core/ProtocolInfo.cs` |
| Autoteste: sempre transforma o perfil local em bot e escolhe o primeiro instrumento possível; loga `Result:` por jogador | `MultiplayerAutoTest.cs` |
| Testes anteriores: só guitarra, 2 instâncias, Windows | `docs/superpowers/plans/2026-10-30-online-multiplayer-checklist.md` |
| Esta máquina: sem `Xvfb` instalado e sem sudo; Docker disponível (usuário no grupo `docker`); `/dev/dri` presente | verificado no shell |

Conclusão: o mute remoto e os bots remotos já funcionam em princípio; faltam (a) a regra "todos erraram" online,
(b) o jogador que sai não pode segurar o stem audível, (c) a banda autoritativa no resultado, (d) o limite de 8 e
(e) testes que provem tudo isso para todos os instrumentos.

## 3. Arquitetura da mudança

### 3.1 `StemMuteTracker` (novo, sem Unity)

Arquivo `Assets/Script/Multiplayer/Core/Audio/StemMuteTracker.cs`, namespace `YARG.Multiplayer.Audio`. Substitui
os contadores `Total`/`Audible` do `GameManager.StemState` (que continua existindo para reverb e whammy).

```csharp
public enum StemMuteRule { Proportional, AllMustMiss }

public sealed class StemMuteTracker
{
    public void AddParticipant(SongStem stem, int participantId, int weight = 1);
    public void RemoveParticipant(int participantId);              // em todos os stems
    public bool SetMuted(SongStem stem, int participantId, bool muted); // true se mudou
    public bool HasParticipants(SongStem stem);
    public IEnumerable<SongStem> StemsOf(int participantId);
    public double GetAudibleFraction(SongStem stem, StemMuteRule rule);
}
```

- Estado por stem: `participantId → (weight, muted)`. Mute é por participante (não contador), então chamadas
  repetidas e saídas não corrompem a conta.
- `Proportional`: soma dos pesos não mudos / soma dos pesos. `AllMustMiss`: `1` se algum participante está
  audível, senão `0`. Stem sem participantes: `1` (o código antigo dividia por zero).
- `SetMuted`/`RemoveParticipant` de participante desconhecido não fazem nada.

### 3.2 Integração no `GameManager`

- Cada `BasePlayer` ganha `StemParticipantId` (int único na cena; o `GameManager` atribui em `CreatePlayer`, ex.
  `_playerIndex`). O crowd usa a constante `CROWD_PARTICIPANT_ID = -1`.
- O bloco "Add (or increase total of) the stem state" de `CreatePlayer` passa a chamar
  `AddParticipant(stem, id)` (peso 2 no background, como hoje).
- `ChangeStemMuteState(SongStem stem, bool muted, float duration = 0)` vira
  `ChangeStemMuteState(int participantId, SongStem stem, bool muted, float duration = 0)`; os chamadores
  (`FiveFretGuitarPlayer`, `DrumsPlayer`, `FiveLaneKeysPlayer`, `ProKeysPlayer`, `CrowdEventHandler` e qualquer
  outro achado por `grep ChangeStemMuteState`) passam o próprio id.
- Regra: `AllMustMiss` se `stem != _backgroundStem` e (`MuteOnlyWhenAllPlayersMiss` ligado **ou**
  `MultiplayerGameplay.IsInMatch`); senão `Proportional`. As saídas antecipadas atuais (`MuteOnMiss == Off`,
  stem inexistente, `MultitrackOnly` no background) continuam iguais.
- Volume aplicado = `StemState.Volume * GetAudibleFraction(...)`, com o mesmo tween de hoje quando `duration > 0`.
- Novo evento `public event Action<SongStem, double> StemAudibleFractionChanged` disparado quando a fração de um
  stem muda. Só o autoteste escuta.
- `TrackPlayer.DropOut`: em vez de `SetStemMuteState(false)`, chama `GameManager.RemoveStemParticipant(id)`, que
  remove o jogador do tracker e reaplica o volume dos stems dele. Vale offline também (drop-out local).

### 3.3 Resultado de banda autoritativo

Arquivo `Assets/Script/Multiplayer/Core/Gameplay/BandResult.cs`, namespace `YARG.Multiplayer.Gameplay`:

```csharp
public static class BandResult
{
    // Soma TotalScore + BandBonusScore de cada jogador; estrelas = quantos limiares a pontuação passa (score > limiar),
    // a mesma regra de EngineManager.UpdateStars sem a parte fracionária.
    public static (int Score, int Stars) Compute(IEnumerable<(int TotalScore, int BandBonusScore)> players,
        int[] starScoreThresholds);
}
```

Em `GameManager.ShowResults`, quando `MultiplayerGameplay.IsInMatch`: `BandScore`/`BandStars` do
`ScoreScreenStats` vêm de `BandResult.Compute` sobre `GetResultStats(player)` de todos os `_players` e de
`EngineManager.StarScoreThresholds` (iguais em todas as máquinas, pois dependem só de chart/partes). Loga
`"Band result: simulated {0}, authoritative {1}"`. Offline nada muda.

Durante a música o placar de banda continua sendo a soma das simulações locais: diverge um pouco entre máquinas
(atraso `D` e multiplicador de banda) e converge para o mesmo valor na tela de resultados.

### 3.4 Limite de 8 máquinas

`ProtocolInfo.MaxMachines = 8`; o teste `SessionTests` que enche a sala segue o valor da constante. Nenhum teste
ponta a ponta passa de 4 instâncias.

### 3.5 Jogador "com roteiro" para errar de propósito

Para provar mute → desmute nas outras máquinas é preciso um jogador **humano** (não bot) que acerte, erre num
trecho e volte a acertar, com inputs que trafegam pela rede como os de uma pessoa.

- `Assets/Script/Multiplayer/Core/AutoTest/ScriptedFiveFretInputs.cs` (namespace `YARG.Multiplayer.AutoTest`):
  `static List<GameInput> Generate(IReadOnlyList<GuitarNote> notes, double missFrom, double missTo)`. Para cada
  nota-pai fora de `[missFrom, missTo]`: ajusta os botões de traste para a máscara do acorde 5 ms antes
  (`GuitarAction.Fret1..Fret5`, `bool`), e `StrumDown` pressionado na hora da nota e solto 20 ms depois. Notas
  abertas soltam todos os trastes. Dentro da janela não gera nada e solta os trastes no início dela.
  Ordenado por tempo. Só cinco trastes (guitarra/baixo/rhythm/coop).
- `BasePlayer.SetScriptedInputs(List<GameInput>)`: no caminho local (não remoto, não replay) de `UpdateInputs`,
  antes de `BaseEngine.Update(time)`, cada input com `Time <= time` segue exatamente o caminho de um input ao vivo
  já convertido (`QueueInput`, `OnInputQueued`, `_replayInputs.Add`, `MultiplayerGameplay.OnLocalInput`). Os tempos
  já estão em tempo de engine, sem calibração.

### 3.6 Autoteste estendido (`MultiplayerAutoTest.cs`)

Argumentos novos (todos opcionais; sem eles o comportamento atual se mantém, inclusive "vira bot"):

| Argumento | Efeito |
|---|---|
| `-mp-profile <nome>` | Usa (ou cria e salva) um perfil com esse nome |
| `-mp-instrument <Instrument>` | Nome do enum `Instrument` (`FiveFretGuitar`, `FiveFretBass`, `FiveFretRhythm`, `ProDrums`, `FourLaneDrums`, `Keys`, `ProKeys`, `Vocals`, `Harmony`...). O `GameMode` do perfil vira `instrument.ToNativeGameMode()` |
| `-mp-difficulty <Difficulty>` | Padrão: a maior disponível |
| `-mp-bot 0\|1` | Grava `IsBot` **no perfil** (não força depois). Com `-mp-profile` e sem `-mp-bot`, o perfil fica como está salvo |
| `-mp-miss <de>-<até>` | Jogador local humano com roteiro (§3.5) que erra no intervalo (segundos de música). Exige instrumento de cinco trastes e implica `-mp-bot 0` |

Logs novos (prefixo `[AutoTest]`, formato fixo para o verificador):

- `Playing as <nome> bot=<0|1> instrument=<Instrument> difficulty=<Difficulty>`
- `Stem <SongStem> fraction=<0.00> song=<s.ss>` a cada `StemAudibleFractionChanged`
- `Result: <nome> remote=<bool> score=<n> hit=<h>/<t> ...` (já existe)
- `Band: score=<n> stars=<n>` lido do `ScoreScreenStats`
- Se o instrumento pedido não existir na música: `FAIL: <Instrument> not in song; available: <lista>`

### 3.7 Infra de teste ponta a ponta (`Tests/MultiplayerBand/`)

- `Assets/Editor/MultiplayerTestBuild.cs`: `Editor.MultiplayerTestBuild.Build()` para `-executeMethod`; Linux64,
  cenas do `EditorBuildSettings`, saída em `-mpBuildOutput <dir>` (padrão `~/yarg-build-mp-banda`). Nunca escreve em
  `~/yarg-build` (usado por outra sessão).
- `Tests/MultiplayerBand/Dockerfile`: Ubuntu 24.04 + Xvfb + Mesa + bibliotecas do player Unity + ALSA com
  dispositivo nulo. Sem display nem áudio no host, cada cenário roda num contêiner com um Xvfb próprio
  (`1280x720x24`), `--network none` (as instâncias falam por `127.0.0.1` dentro do contêiner), `/dev/dri`
  repassado para usar a iGPU, usuário do host (`--user`) para os arquivos saírem com o dono certo.
- `Tests/MultiplayerBand/run-scenario.sh <cenário>`: cria as pastas de dados (`settings.json` com venue desligado e
  `SongFolders` só no host), sobe o contêiner, lança as instâncias com `-screen-fullscreen 0 -screen-width 640
  -screen-height 360 -logFile`, espera todas saírem e chama o verificador.
- `Tests/MultiplayerBand/check_band_logs.py` (+ `test_check_band_logs.py`, `unittest` com logs de exemplo):
  valida os logs conforme a expectativa do cenário.

## 4. Fluxo de dados do mute (exemplo: guitarra com roteiro no host, guitarra bot no cliente)

```
host: engine local erra nota ─▶ FiveFretGuitarPlayer.SetStemMuteState(true)
      ─▶ GameManager.ChangeStemMuteState(idHost, Guitar, true) ─▶ tracker: host mudo, bot audível
      ─▶ AllMustMiss (online) ⇒ fração 1 ⇒ nada muda
cliente: inputs do host chegam ~D depois ─▶ engine remota do host erra a mesma nota ─▶ mesmo caminho ─▶ fração 1
```

Sem o bot, a fração vai a 0 nas duas máquinas (no cliente, `D` depois) e volta a 1 no primeiro acerto após a
janela.

## 5. Erros e casos de borda

| Caso | Comportamento |
|---|---|
| Jogador remoto sai mudo | `DropOut` remove o participante; o stem volta a depender só de quem ficou |
| Jogador remoto sai audível | Idem; se os que ficaram estão mudos, o stem muta |
| Vários jogadores em `Rhythm` (baixo sem stem + rhythm) | Mesmo stem, mesma regra "todos erraram" |
| Vocal/harmonia | Nunca muta (como no singleplayer) |
| Música sem stems (`song.opus` só) | Nenhum evento de fração em nenhuma máquina |
| `MuteOnMiss = Off` | Nada muta, online ou offline |
| Jogador sem `Finished` (saiu) | Usa o último `StatsCheckpoint` (já é assim); a banda soma o mesmo valor em todas as máquinas que receberam o mesmo checkpoint |
| Instrumento pedido ausente no autoteste | `FAIL` com a lista de partes disponíveis |

## 6. Testes

### 6.1 Unitários (`Tests/YARG.Multiplayer.Tests`, `dotnet test`)

- `Audio/StemMuteTrackerTests`: proporcional; todos-erraram com 1, 2 e 3 participantes; erro → acerto desmuta;
  remover participante mudo/audível; stem sem participantes; ids desconhecidos; peso 2.
- `Gameplay/BandResultTests`: soma com bônus, limiares (igual ao limiar não conta), lista vazia.
- `Gameplay/RemoteFidelityTests`: com `drawntotheflame.mid` (copiado do `YARG.Core.UnitTests` para a saída do
  teste), para cada instrumento com notas no chart entre `FiveFretGuitar`, `FiveFretBass`, `ProDrums`, `Keys`,
  `ProKeys`, `Vocals`:
  - **bot:** engine bot atualizada a 60 Hz × engine bot atualizada em passos irregulares atrasados (como a remota)
    → stats iguais (`TotalScore`, `NotesHit`, `MaxCombo`, `StarPowerPhrasesHit`, `BandBonusScore`);
  - **humano:** inputs (gerador de §3.5 para guitarra/baixo; gerador determinístico simples para os demais)
    passando por `InputBatcher` → `MessageCodec` → `RemoteInputBuffer` → engine replicada × engine original → stats
    iguais.
- `AutoTest/ScriptedFiveFretInputsTests`: com a engine real de guitarra Expert, ≥ 99% de acerto fora da janela,
  0 acertos dentro, e a primeira nota depois da janela é acerto.
- `Session/SessionTests`: sala enche em 8.

Se algum teste de fidelidade mostrar não-determinismo de engine, a correção vai no `YARG.Core` em PR próprio no
submódulo (`FelipeFlohr/YARG.Core`, branch `claude/mp-banda-completa`), depois o ponteiro no YARG.

### 6.2 Compilação Unity

Batchmode com `flock /tmp/yarg-unity.lock` depois de criar `.cs` em `Assets/`, para gerar os `.meta` e pegar
`error CS`.

### 6.3 Ponta a ponta (build Linux + Docker + Xvfb)

| Cenário | Música | Instâncias | Esperado |
|---|---|---|---|
| `band4` | 2112 Pt. 6 | host guitarra com roteiro (erra 30–40 s); clientes baixo, bateria, vocal com perfil bot | Todos PASS; em **todas** as 4 máquinas `Stem Guitar` vai a 0 entre 30 s e 42 s e volta a 1 depois de 40 s; nenhum outro stem muda; `Result:` e `Band:` idênticos nas 4 |
| `same-instrument` | 2112 Pt. 6 | host guitarra com roteiro (erra 30–40 s); cliente guitarra bot | Nenhum evento `Stem Guitar` nas 2 máquinas; resultados idênticos |
| `keys` | Subdivisions | host teclado bot (`Keys`); cliente guitarra com roteiro (erra 60–70 s); 2º cliente `ProKeys` bot se o chart tiver, senão `FiveFretBass` bot | Guitarra muta/desmuta nas 3; `Keys` nunca muta (os dois tecladistas são bots ou só um existe); resultados idênticos |
| `no-stems` | Making Memories | host guitarra com roteiro (erra 30–40 s); cliente com perfil bot no primeiro instrumento disponível | Nenhum evento `Stem` em nenhuma máquina; resultados idênticos |

Em todos: o cliente bot loga `bot=1` vindo do perfil (sem forçar), e o jogador com roteiro tem `hit < total`.

## 7. Fora do escopo

- Placar de banda idêntico **durante** a música (só no resultado).
- Teste com mais de 4 instâncias, e com guitarra de 6 trastes, pro guitar, bateria de 5 lanes ou elite drums de
  ponta a ponta (as músicas de teste não têm esses charts; os unitários cobrem o que o chart de teste tiver).
- Mais de um jogador local por máquina online.
- Teste entre máquinas reais (Tailscale) e com humanos.

## 8. Decisões e premissas

Tomadas sem o usuário (sessão autônoma), com o motivo:

1. **Online sempre usa "só muta quando todos do instrumento erraram"**, mesmo com `MuteOnlyWhenAllPlayersMiss`
   desligado (padrão). Motivo: é a regra pedida explicitamente para multiplayer; offline continua respeitando a
   configuração, e `MuteOnMiss` (Off/MultitrackOnly/On) continua valendo online. Não criei configuração nova (YAGNI).
2. **"Pontuação sincronizada" = resultado final idêntico em todas as máquinas** (jogadores e banda). Durante a
   música o placar de banda pode divergir um pouco e converge no fim. Motivo: as engines remotas rodam `D` atrás e o
   multiplicador de banda depende do momento em que cada máquina vê o star power alheio; igualar ao vivo exigiria
   atrasar o jogador local. A fonte da verdade de cada jogador é a própria máquina dele (já era o design).
3. **Estado de mute por participante** (`StemMuteTracker`) em vez de contadores. Motivo: o jogador que sai
   precisa sair da conta; contadores não sabem quem saiu.
4. **Código novo sem Unity fica em `Assets/Script/Multiplayer/Core`**, não no `YARG.Core`. Motivo: é testável por
   `dotnet test` já existente e evita PR no submódulo; o `StemMuteTracker` é usado também offline, mas mora ali por
   ser o único lugar sem Unity do projeto do jogo. Mudança no `YARG.Core` só se um teste de fidelidade exigir.
5. **Vocal não muta** online nem offline. Motivo: comportamento atual do singleplayer (`VocalsPlayer`), e o pedido
   é "como já acontece em singleplayer".
6. **Música sem stems não muta** com a configuração padrão (`MultitrackOnly`); nada a mudar, só testar.
7. **Limite sobe para 8 máquinas** (`MaxMachines`), um jogador por máquina como antes. Testes ponta a ponta com no
   máximo 4 instâncias.
8. **Erro de propósito por inputs com roteiro**, só para cinco trastes. Motivo: bot não erra e jogador sem input
   nunca volta a acertar; o roteiro exercita o caminho real de um humano (inputs pela rede). Os demais
   instrumentos entram como bots nos testes ponta a ponta e como humano só nos unitários de fidelidade.
9. **Bot "conecta normalmente"** = perfil salvo como bot, sem o autoteste forçar; nenhuma mudança de lobby, pois o
   código já repassa `IsBot` e não bloqueia bots.
10. **Docker + Xvfb dentro do contêiner**, porque o host não tem Xvfb e o Claude não tem sudo. As instâncias de um
    cenário compartilham um contêiner e um Xvfb; `/dev/dri` repassado; áudio cai no dispositivo nulo do ALSA (ou no
    "no sound" do BASS), o que não afeta a lógica de mute (o evento é disparado antes do BASS).
11. **Build própria em `~/yarg-build-mp-banda`** por um script de editor versionado (`Assets/Editor/MultiplayerTestBuild.cs`);
    o exemplo `TmpHeadlessBuild.cs` citado não existe mais no disco.
12. **Janelas de erro em 30–40 s** (2112/Making Memories) e 60–70 s (Subdivisions), com tolerância de 2 s para o
    atraso remoto. Premissa: há notas de guitarra nesses trechos; o plano manda conferir e ajustar se não houver.
13. **Instrumentos das músicas** (premissa a confirmar na execução): 2112 tem guitarra, baixo, bateria e vocal;
    Subdivisions tem também teclado (talvez pro keys); Making Memories tem pelo menos guitarra.
