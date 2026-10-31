# Multiplayer com a banda inteira — Plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Garantir e provar por testes automáticos que uma partida online tem pontuação final idêntica em todas as
máquinas, mute de stem por erro visto por todos (com a regra "todos do instrumento erraram"), bots entrando
normalmente e todos os instrumentos funcionando, com até 8 máquinas por sala.

**Architecture:** Lógica pura nova em `Assets/Script/Multiplayer/Core` (`StemMuteTracker`, `BandResult`,
`ScriptedFiveFretInputs`), testada por `Tests/YARG.Multiplayer.Tests`; integração pequena no `GameManager`/players;
autoteste estendido; e uma bancada ponta a ponta (build Linux própria + Docker com Xvfb + verificador de logs em
Python) em `Tests/MultiplayerBand/`.

**Tech Stack:** C# 9 (Unity 6000.3.5f2, `netstandard2.1`, NUnit em `net10.0`), BASS, Docker, Xvfb, Python 3.

**Spec:** `docs/superpowers/specs/2026-10-31-mp-banda-completa-design.md` (leia antes de qualquer tarefa).

## Global Constraints

- Worktree: `/home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa`, branch `claude/mp-banda-completa`. Use caminhos absolutos; o cwd do shell é resetado entre chamadas.
- C# 9 em todo lugar (nada de `record struct`, `file`-scoped namespace só onde o arquivo vizinho já usa, nada de C# 10+). Siga o `.editorconfig`.
- Não edite `.unity`, `.prefab`, `.asset`, `.meta` à mão. `.cs` novo em `Assets/` precisa de `.meta` gerado pelo Unity: comite os `.meta` gerados pela compilação em batchmode (inclusive os de pastas novas).
- Toda chamada ao Unity passa por `flock /tmp/yarg-unity.lock`. Ela pode passar de 10 min: rode com `run_in_background` e espere a notificação.
- Nunca escreva em `~/yarg-build` nem em `~/yarg-build-log.txt` (outra sessão usa). Build deste plano: `~/yarg-build-mp-banda`. Execuções ponta a ponta: `~/yarg-mp-banda-runs`.
- Mudança no `YARG.Core` (só se um teste de fidelidade exigir): commit e PR no submódulo (`FelipeFlohr/YARG.Core`, branch `claude/mp-banda-completa`), depois atualizar o ponteiro no YARG.
- Regra de mute online: `AllMustMiss` em stems que não são o background, sempre que `MultiplayerGameplay.IsInMatch`; offline, só com `MuteOnlyWhenAllPlayersMiss`. `MuteOnMiss` continua valendo nos dois.
- Vocal nunca muta. Música sem stems nunca muta com `MuteOnMiss = MultitrackOnly`.
- `ProtocolInfo.MaxMachines = 8`. Testes ponta a ponta: no máximo 4 instâncias.
- Instâncias do jogo: `-screen-fullscreen 0 -screen-width 640 -screen-height 360`, `-persistent-data-path` próprio, `settings.json` com `{ "DisablePerSongBackgrounds": true, "DisableGlobalBackgrounds": true, "DisableDefaultBackground": true }` (+ `SongFolders` no host).
- Músicas: `/home/felipeserver/repos/musicas yarg/` — `Rush - 2112 Pt. 6 - Soliloquy (Neversoft).sng` (stems guitar, bass, drums_1..4, vocals, song), `Rush - Subdivisions (Harmonix).sng` (guitar, rhythm, drums, keys, vocals, song; baixo usa o stem `Rhythm`), `Rush - Making Memories (Emperor).sng` (só `song.opus`).

### Comandos de verificação (referenciados nas tarefas)

- **MP-TEST:** `cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/Tests/YARG.Multiplayer.Tests && dotnet test` → `Passed!`, 0 falhas.
- **MP-TEST-FILTER X:** `cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/Tests/YARG.Multiplayer.Tests && dotnet test --filter "FullyQualifiedName~X"`.
- **CORE-TEST:** `cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build` (só se o Core mudar).
- **UNITY-COMPILE** (em background):

  ```bash
  cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa && \
  flock /tmp/yarg-unity.lock "$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$PWD" \
    -logFile "${TMPDIR:-/tmp}/unity-mpbanda.log"; \
  grep -E 'error CS|Compilation failed|Scripts have compiler errors' "${TMPDIR:-/tmp}/unity-mpbanda.log"
  ```

  Passou = o `grep` não imprime nada. Depois: `git status --short` deve mostrar os `.meta` dos arquivos/pastas novos; comite-os junto. Se `UNITY_EDITOR` não existir, pare e reporte.

## Review Focus

- Jogador remoto que sai **enquanto mudo** não pode deixar o stem mudo nem, saindo audível, segurar o stem audível para sempre → `StemMuteTrackerTests.RemoveParticipant_*` (Tarefa 1) e `DropOut` chamando `RemoveStemParticipant` (Tarefa 6).
- Stem sem participantes (ex.: chamada de mute para um stem que ninguém registrou) dava `NaN` no modo proporcional → `GetAudibleFraction_NoParticipants_IsOne` (Tarefa 1).
- Baixo sem `bass.opus` cai em `Rhythm`: o registro no tracker e o `_stem` do player precisam usar o mesmo stem transformado, senão o mute do baixista nunca bate → passo de conferência na Tarefa 6 e cenário `keys` na Tarefa 13.
- Inputs com roteiro mais antigos que o tempo atual da engine (o autoteste chega atrasado) virariam inputs no passado → `Generate_DropsInputsBeforeNotBefore` (Tarefa 5).
- Eventos do stem `Crowd` (o `CrowdEventHandler` também muta) não podem reprovar o cenário "nenhum stem muda" → `test_crowd_events_are_ignored` (Tarefa 12).

---

### Task 1: `StemMuteTracker` (regra de mute por participante)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Audio/StemMuteTracker.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Audio/StemMuteTrackerTests.cs`

**Interfaces:**
- Produces (namespace `YARG.Multiplayer.Audio`, usa `YARG.Core.Audio.SongStem`):
  - `public enum StemMuteRule { Proportional, AllMustMiss }`
  - `public sealed class StemMuteTracker` com `void AddParticipant(SongStem stem, int participantId, int weight = 1)`, `void RemoveParticipant(int participantId)`, `bool SetMuted(SongStem stem, int participantId, bool muted)` (true se o estado mudou), `bool HasParticipants(SongStem stem)`, `IReadOnlyList<SongStem> StemsOf(int participantId)`, `double GetAudibleFraction(SongStem stem, StemMuteRule rule)`.
  - Participante novo começa audível. `AddParticipant` repetido no mesmo stem/id substitui o peso sem duplicar.

- [ ] **Step 1: Escrever os testes** (NUnit, `[TestFixture] public class StemMuteTrackerTests`):
  - `Proportional_OneOfTwoMuted_IsHalf` → `0.5`.
  - `AllMustMiss_OneOfTwoMuted_IsOne`; `AllMustMiss_BothMuted_IsZero`; `AllMustMiss_ThreeParticipants_OnlyZeroWhenAllMuted`.
  - `MissThenHit_Unmutes`: 1 participante, `SetMuted(true)` → `0`, `SetMuted(false)` → `1`.
  - `SetMuted_SameStateTwice_ReturnsFalseAndKeepsFraction`.
  - `RemoveParticipant_WhileMuted_LeavesOthersInCharge`: A mudo, B audível, remove B → `AllMustMiss` dá `0`; remove A em vez de B → `1`.
  - `RemoveParticipant_RemovesFromEveryStem` (participante em `Drums1` e `Drums2`; `StemsOf` vazio depois).
  - `GetAudibleFraction_NoParticipants_IsOne` (nos dois modos, sem `NaN`).
  - `UnknownParticipant_IsIgnored` (`SetMuted` retorna false, fração inalterada).
  - `Weight_CountsInProportional`: A peso 2 mudo, B peso 1 audível → `1.0/3`.
- [ ] **Step 2:** MP-TEST-FILTER `StemMuteTracker` → falha de compilação (tipo inexistente).
- [ ] **Step 3:** Implementar `StemMuteTracker` (`Dictionary<SongStem, Dictionary<int, (int Weight, bool Muted)>>`).
- [ ] **Step 4:** MP-TEST-FILTER `StemMuteTracker` → todos passam; MP-TEST → tudo verde.
- [ ] **Step 5: Commit** — `git add Assets/Script/Multiplayer/Core/Audio Tests/YARG.Multiplayer.Tests/Audio && git commit -m "Add a per-participant stem mute tracker"` (o `.meta` vem na Tarefa 6).

### Task 2: `BandResult` (pontuação de banda autoritativa)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Gameplay/BandResult.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Gameplay/BandResultTests.cs`

**Interfaces:**
- Produces: `namespace YARG.Multiplayer.Gameplay { public static class BandResult { public static (int Score, int Stars) Compute(IEnumerable<(int TotalScore, int BandBonusScore)> players, int[] starScoreThresholds); } }`. Estrelas = quantidade de limiares com `score > limiar` (mesma regra de `EngineManager.UpdateStars`, sem fração).

- [ ] **Step 1: Testes:** `Compute_SumsScoreAndBonus` (`(100,10),(200,0)` → `310`); `Compute_StarsCountThresholdsStrictlyBelow` (limiares `{100,200,300,400,500,600,700}`, score `300` → `2`, score `301` → `3`); `Compute_Empty_IsZero` (`(0,0)`); `Compute_AboveAllThresholds_IsThresholdCount`.
- [ ] **Step 2:** MP-TEST-FILTER `BandResult` → falha de compilação.
- [ ] **Step 3:** Implementar.
- [ ] **Step 4:** MP-TEST-FILTER `BandResult` → passa.
- [ ] **Step 5: Commit** — `git add Assets/Script/Multiplayer/Core/Gameplay Tests/YARG.Multiplayer.Tests/Gameplay/BandResultTests.cs && git commit -m "Add the authoritative band result calculation"`.

### Task 3: Sala com até 8 máquinas

**Files:**
- Modify: `Assets/Script/Multiplayer/Core/ProtocolInfo.cs` (`MaxMachines`)
- Modify: `Tests/YARG.Multiplayer.Tests/Session/SessionTests.cs` (`Join_RejectsFifthMachine`)

- [ ] **Step 1:** Renomear o teste para `Join_RejectsMachineBeyondTheLimit` e acrescentar `Assert.That(ProtocolInfo.MaxMachines, Is.EqualTo(8));` no início.
- [ ] **Step 2:** MP-TEST-FILTER `Join_RejectsMachineBeyondTheLimit` → falha (`4` ≠ `8`).
- [ ] **Step 3:** `public const int MaxMachines = 8;`. Procure (`grep -rn "MaxMachines\|4 players\|four" Assets/Script/Multiplayer`) textos ou arrays dimensionados para 4 e ajuste.
- [ ] **Step 4:** MP-TEST → tudo verde.
- [ ] **Step 5: Commit** — `git commit -am "Allow up to 8 machines in an online session"`.

### Task 4: Testes de fidelidade da pista remota por instrumento

Prova (itens 3 e 4 da spec) que uma engine alimentada pelo caminho de rede chega aos mesmos stats que a original,
para humano e bot, em cada instrumento.

**Files:**
- Modify: `Tests/YARG.Multiplayer.Tests/YARG.Multiplayer.Tests.csproj` (copiar o chart de teste para a saída)
- Create: `Tests/YARG.Multiplayer.Tests/Gameplay/TestCharts.cs`
- Create: `Tests/YARG.Multiplayer.Tests/Gameplay/RemoteFidelityTests.cs`

**Interfaces:**
- Consumes: `InputBatcher(int playerId)`, `.Add(GameInput)`, `.SetWatermark(double)`, `.TryFlush(double now) → InputBatch?`; `MessageCodec.Encode/Decode`; `RemoteInputBuffer(double delaySeconds)`, `.AddBatch`, `.GetTargetTime(double localTime)`, `.TryDequeue(double upTo, out GameInput)`.
- Produces: `static class TestCharts { public static SongChart Load(); }` (lê `drawntotheflame.mid` da pasta de saída com `MidiFile.Read` + `SongChart.FromMidi(in ParseSettings.Default_Midi, midi)`, como `YARG.Core.UnitTests/Engine/EngineTester.cs`) — usado pela Tarefa 5.

- [ ] **Step 1:** No csproj: `<None Include="../../YARG.Core/YARG.Core.UnitTests/Engine/Test Charts/drawntotheflame.mid" Link="TestCharts/drawntotheflame.mid" CopyToOutputDirectory="PreserveNewest" />`. Se `Melanchall.DryWetMidi` não estiver acessível pelo `ProjectReference`, acrescente o mesmo `PackageReference` que `YARG.Core.csproj` usa.
- [ ] **Step 2:** Escrever `RemoteFidelityTests` com dois laços de simulação (helpers privados):
  - *Original:* `t` de `0` até `fim + 1` em passos de `1/60`; antes de `Update(t)`, `QueueInput` de todo input com `Time <= t`.
  - *Remota:* cada input passa por `InputBatcher` (watermark = `t` do laço original, `TryFlush(t)`), o lote vai por `MessageCodec.Encode` → `Decode` → `RemoteInputBuffer(0.2).AddBatch`; a engine remota anda em passos irregulares determinísticos (ciclo `7, 23, 41, 16` ms) com `localTime`, dequeuing até `GetTargetTime(localTime)` e chamando `Update(target)` quando `target` avança — o mesmo que `BasePlayer.UpdateRemoteInputs`. No fim, `Update(fim + 1)`.
  - Comparar `TotalScore`, `NotesHit`, `NotesMissed`, `MaxCombo`, `StarPowerPhrasesHit`, `BandBonusScore` (`Assert.Multiple`).
  - Casos (`[TestCaseSource]` por instrumento; `Assert.Ignore` se o chart não tiver notas Expert, mas `FiveFretGuitar`, `FiveFretBass`, `ProDrums`, `Keys` e `Vocals` precisam existir — falhe se faltar algum): `Bot_RemoteMatchesOriginal(instrument)` (engine com `isBot: true`, sem inputs) e `Human_RemoteMatchesOriginal(instrument)` com inputs determinísticos (`new Random(1234)`): guitarra/baixo usam `ScriptedFiveFretInputs` se a Tarefa 5 já existir, senão inputs aleatórios de `GuitarAction.Fret1..Fret5`/`StrumDown`; bateria `GameInput.Create(t, DrumsAction.*, 1f)` aleatórios; teclado `KeysAction` press/release `bool`; vocal `VocalsAction.Pitch` com `float` entre 40 e 80 a cada 1/60 s. Crie engines e parâmetros copiando a construção de `YARG.Core.UnitTests/Engine/{EngineTester,DrumEngineTester,KeysEngineTester,VocalsEngineTests}.cs` (e `ProKeysUtilitiesTests` para pro keys, se houver chart).
- [ ] **Step 3:** MP-TEST-FILTER `RemoteFidelity` → esperado PASS (o código de produção já existe). Se algum caso falhar, é bug real: use superpowers:systematic-debugging; se a causa for não-determinismo de engine no `YARG.Core`, corrija lá com teste em `YARG.Core.UnitTests`, rode CORE-TEST, faça commit/PR no submódulo e atualize o ponteiro (Global Constraints).
- [ ] **Step 4:** MP-TEST → tudo verde.
- [ ] **Step 5: Commit** — `git add Tests/YARG.Multiplayer.Tests && git commit -m "Test remote track fidelity for every instrument, human and bot"`.

### Task 5: `ScriptedFiveFretInputs` (jogador humano que erra de propósito)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/AutoTest/ScriptedFiveFretInputs.cs`
- Test: `Tests/YARG.Multiplayer.Tests/AutoTest/ScriptedFiveFretInputsTests.cs`

**Interfaces:**
- Consumes: `TestCharts.Load()` (Tarefa 4).
- Produces: `namespace YARG.Multiplayer.AutoTest { public static class ScriptedFiveFretInputs { public static List<GameInput> Generate(IReadOnlyList<GuitarNote> notes, double missFrom, double missTo, double notBefore = double.NegativeInfinity); } }`. `notes` são as notas-pai (como em `TrackPlayer.Notes`). Para nota fora de `[missFrom, missTo]` e com `Time - 0.005 >= notBefore`: trastes ajustados para a máscara do acorde em `Time - 0.005` (`GameInput.Create(t, GuitarAction.FretN, bool)`, só as mudanças), `StrumDown` `true` em `Time` e `false` em `Time + 0.02`; nota aberta solta tudo. Na entrada da janela, solta os trastes pressionados. Saída ordenada por tempo (estável).

- [ ] **Step 1: Testes** (engine `YargFiveFretGuitarEngine` Expert de `TestCharts.Load()`, parâmetros como `EngineTester`, laço de 1/60 s da Tarefa 4):
  - `Generate_HitsAlmostEverythingOutsideTheWindow`: janela `30–40` s → `hits fora / notas fora >= 0.99`.
  - `Generate_HitsNothingInsideTheWindow`: 0 acertos com `30 <= Time <= 40` (use `OnNoteHit` da engine para registrar os tempos).
  - `Generate_FirstNoteAfterTheWindowIsHit`.
  - `Generate_DropsInputsBeforeNotBefore`: `notBefore = 20` → nenhum input com `Time < 20`.
  - `Generate_IsSortedByTime`.
  - Confira no Core como `NoteMask` mapeia trastes (`FiveFretGuitarFret.Open = 0`, `Green = 1`...) antes de escrever o mapeamento.
- [ ] **Step 2:** MP-TEST-FILTER `ScriptedFiveFretInputs` → falha de compilação.
- [ ] **Step 3:** Implementar. Se a Tarefa 4 usou inputs aleatórios para guitarra/baixo, troque pelo gerador.
- [ ] **Step 4:** MP-TEST → tudo verde.
- [ ] **Step 5: Commit** — `git add Assets/Script/Multiplayer/Core/AutoTest Tests/YARG.Multiplayer.Tests && git commit -m "Add scripted five-fret inputs for automated miss tests"`.

### Task 6: Integrar o `StemMuteTracker` no jogo (regra online, drop-out, evento)

**Files:**
- Modify: `Assets/Script/Gameplay/GameManager.Audio.cs` (remover `Total`/`Audible`/`SetMute` do `StemState`; tracker; regra; evento; `RemoveStemParticipant`)
- Modify: `Assets/Script/Gameplay/GameManager.Loading.cs` (~683–700: registro; ~285: crowd)
- Modify: `Assets/Script/Gameplay/Player/BasePlayer.cs` (`StemParticipantId`)
- Modify: `Assets/Script/Gameplay/Player/TrackPlayer.cs` (`DropOut`)
- Modify: `FiveFretGuitarPlayer.cs`, `FiveLaneKeysPlayer.cs`, `DrumsPlayer.cs`, `ProKeysPlayer.cs`, `Assets/Script/Playback/CrowdEventHandler.cs` (+ qualquer outro de `grep -rn ChangeStemMuteState Assets/Script`)

**Interfaces:**
- Consumes: `StemMuteTracker`, `StemMuteRule` (Tarefa 1).
- Produces (usados pela Tarefa 9):
  - `BasePlayer`: `public int StemParticipantId { get; set; }`
  - `GameManager`: `public const int CROWD_PARTICIPANT_ID = -1;`, `public void ChangeStemMuteState(int participantId, SongStem stem, bool muted, float duration = 0.0f)`, `public void RemoveStemParticipant(int participantId)`, `public event Action<SongStem, double> StemAudibleFractionChanged;`

- [ ] **Step 1:** `GameManager`: campo `private readonly StemMuteTracker _stemMutes = new();` (recriado em `LoadAudio`). `ChangeStemMuteState` mantém as saídas antecipadas atuais; regra = `AllMustMiss` se `stem != _backgroundStem && (MuteOnlyWhenAllPlayersMiss.Value || MultiplayerGameplay.IsInMatch)`, senão `Proportional`; volume = `state.Volume * fração`; tween igual ao de hoje; dispara `StemAudibleFractionChanged(stem, fração)` só quando a fração muda. `RemoveStemParticipant` guarda `StemsOf(id)`, remove, reaplica volume (sem tween) e dispara o evento para os stems cuja fração mudou.
- [ ] **Step 2:** `CreatePlayer`: depois de criar o player, `_players[_players.Count - 1].StemParticipantId = _playerIndex;` e trocar `++state.Total; ++state.Audible;` por `_stemMutes.AddParticipant(transformedStem, _playerIndex)`; background com `weight: 2`. Crowd: `AddParticipant(SongStem.Crowd, CROWD_PARTICIPANT_ID)` no lugar de `Total = 1; Audible = 1`. Confira que a transformação `Bass → Rhythm` do registro é a mesma do `_stem` em `FiveFretGuitarPlayer`/`FiveLaneKeysPlayer` (Review Focus).
- [ ] **Step 3:** Chamadores passam `StemParticipantId` (crowd passa `GameManager.CROWD_PARTICIPANT_ID`). `TrackPlayer.DropOut`: trocar `SetStemMuteState(muted: false)` por `GameManager.RemoveStemParticipant(StemParticipantId)`.
- [ ] **Step 4:** UNITY-COMPILE → sem erros; `.meta` de `Audio/`, `Gameplay/`, `AutoTest/` (pastas e `.cs` das Tarefas 1, 2 e 5) aparecem no `git status`.
- [ ] **Step 5:** MP-TEST → verde (garante que nada do Core puro quebrou).
- [ ] **Step 6: Commit** — `git add -A Assets/Script && git commit -m "Track stem mutes per player and mute online only when every player of the part missed"`.

Comportamento de áudio: não verificável visualmente/auditivamente aqui; coberto pelos eventos no autoteste (Tarefa 13).

### Task 7: Resultado de banda autoritativo na tela de resultados

**Files:**
- Modify: `Assets/Script/Gameplay/GameManager.cs` (`ShowResults`, ~776–815)

**Interfaces:**
- Consumes: `BandResult.Compute` (Tarefa 2), `GetResultStats(BasePlayer)`, `EngineManager.StarScoreThresholds`.

- [ ] **Step 1:** Em `ShowResults`, se `MultiplayerGameplay.IsInMatch`: `var (score, stars) = BandResult.Compute(_players.Select(p => { var s = GetResultStats(p); return (s.TotalScore, s.BandBonusScore); }), EngineManager.StarScoreThresholds);` e usar no `ScoreScreenStats` (`BandScore = score`, `BandStars = stars`); `YargLogger.LogFormatInfo("Band result: simulated {0}, authoritative {1}", BandScore, score)`. Offline mantém `BandScore`/`(int) BandStars`. `RecordScores` não muda.
- [ ] **Step 2:** UNITY-COMPILE → sem erros.
- [ ] **Step 3: Commit** — `git commit -am "Show the band score from every player's own results in online matches"`.

### Task 8: Inputs com roteiro no `BasePlayer`

**Files:**
- Modify: `Assets/Script/Gameplay/Player/BasePlayer.cs` (`UpdateInputs`, ~294–336)
- Modify: `Assets/Script/Gameplay/Player/TrackPlayer.cs` (expor notas)

**Interfaces:**
- Produces (usados pela Tarefa 9): `BasePlayer.SetScriptedInputs(List<GameInput> inputs)`; `TrackPlayer<TEngine, TNote>.ChartNotes` → `public IReadOnlyList<TNote> ChartNotes => Notes;`.

- [ ] **Step 1:** `SetScriptedInputs` guarda a lista e um índice. Em `UpdateInputs`, no caminho local (depois de `time += InputCalibration`, antes de `BaseEngine.Update(time)`, só se `!Player.IsReplay && !Player.IsRemote`), para cada input com `Time <= time`: `BaseEngine.QueueInput(ref input); OnInputQueued(input); _replayInputs.Add(input); if (MultiplayerGameplay.IsInMatch) MultiplayerGameplay.OnLocalInput(this, input);` — o mesmo fim de `OnGameInput`, sem reconverter tempo nem calibração.
- [ ] **Step 2:** UNITY-COMPILE → sem erros.
- [ ] **Step 3: Commit** — `git commit -am "Let a local player be driven by scripted inputs"`.

### Task 9: Autoteste estendido (perfil, instrumento, bot, roteiro, logs)

**Files:**
- Modify: `Assets/Script/Multiplayer/MultiplayerAutoTest.cs`

**Interfaces:**
- Consumes: Tarefas 5, 6, 8 (`ScriptedFiveFretInputs.Generate`, `GameManager.StemAudibleFractionChanged`, `SetScriptedInputs`, `ChartNotes`).
- Produces (formato exato lido pela Tarefa 12, todos com o prefixo `[AutoTest] ` do `Log`):
  - `Playing as {nome} bot={0|1} instrument={Instrument} difficulty={Difficulty}`
  - `Stem {SongStem} fraction={0.00} song={0.00}` (`InvariantCulture`, `SongTime` do `GameManager`)
  - `Result: {nome} remote={True|False} score={n} hit={h}/{t} ...` (já existe; manter o começo idêntico)
  - `Band: score={n} stars={n}`
  - `FAIL: {Instrument} not in song; available: {a, b, ...}`

- [ ] **Step 1:** Ler argumentos `-mp-profile`, `-mp-instrument` (`Enum.TryParse<Instrument>`), `-mp-difficulty`, `-mp-bot 0|1`, `-mp-miss <de>-<até>` (`InvariantCulture`). `PrepareLocalPlayer`: com `-mp-profile`, achar perfil pelo nome ou criar (`new YargProfile { Name = ..., GameMode = instrument.ToNativeGameMode(), CurrentInstrument = instrument }`, `PlayerContainer.AddProfile`, `PlayerContainer.SaveProfiles()`), deixar só ele ativo e `CreatePlayerFromProfile(profile, true)` se necessário. `-mp-bot` grava `profile.IsBot` e salva; `-mp-miss` força `IsBot = false` e exige `GameMode.FiveFretGuitar` (senão `FAIL`). Sem nenhum desses argumentos: comportamento atual (vira bot). Logar a linha `Playing as ...`.
- [ ] **Step 2:** `PickPart`: com `-mp-instrument`, usar só ele (dificuldade pedida ou a maior disponível); se ausente na música, logar `FAIL: ... available: ...` (instrumentos com `song.HasInstrument`) e fazer a rodada falhar.
- [ ] **Step 3:** Em `PlayThroughMatch`, ao chegar em gameplay: assinar `StemAudibleFractionChanged` do `GameManager` (desassinar ao sair) e logar `Stem ...`; com `-mp-miss`, esperar o `FiveFretGuitarPlayer` local (`!Player.IsRemote`) com `ChartNotes != null` e chamar `SetScriptedInputs(ScriptedFiveFretInputs.Generate(notes, de, até, notBefore: engine.CurrentTime + 0.5))`. Na tela de resultados, logar `Band: score={stats.BandScore} stars={stats.BandStars}` depois das linhas `Result:`.
- [ ] **Step 4:** Atualizar o `<summary>` da classe e a seção de autoteste do `CLAUDE.md` do repositório com os argumentos novos.
- [ ] **Step 5:** UNITY-COMPILE → sem erros.
- [ ] **Step 6: Commit** — `git commit -am "Extend the multiplayer autotest with profiles, parts, scripted misses and stem logs"`.

### Task 10: Script de build e build Linux própria

**Files:**
- Create: `Assets/Editor/MultiplayerTestBuild.cs` (+ `.meta` gerado)

**Interfaces:**
- Produces: `namespace Editor { public static class MultiplayerTestBuild { public static void Build(); } }`. Lê `-mpBuildOutput <dir>` de `Environment.GetCommandLineArgs()` (padrão `$HOME/yarg-build-mp-banda`); recusa (exit 1) se o caminho resolvido for `~/yarg-build`. `BuildPlayerOptions` com as cenas habilitadas de `EditorBuildSettings.scenes`, `target = BuildTarget.StandaloneLinux64`, `locationPathName = <dir>/YARG.x86_64`. Loga `[MultiplayerTestBuild] result: {summary.result}` e chama `EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1)`.

- [ ] **Step 1:** Escrever o script (siga o padrão de `Assets/Editor/MakeTestBuild.cs`).
- [ ] **Step 2:** Build (background; demora):

  ```bash
  cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa && \
  flock /tmp/yarg-unity.lock "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PWD" -buildTarget Linux64 \
    -executeMethod Editor.MultiplayerTestBuild.Build -mpBuildOutput "$HOME/yarg-build-mp-banda" \
    -logFile "${TMPDIR:-/tmp}/unity-mpbanda-build.log"; echo "exit=$?"
  ```

  Esperado: `exit=0`, `grep "\[MultiplayerTestBuild\] result: Succeeded" "${TMPDIR:-/tmp}/unity-mpbanda-build.log"` acha a linha e `test -x ~/yarg-build-mp-banda/YARG.x86_64`.
- [ ] **Step 3: Commit** — `git add Assets/Editor/MultiplayerTestBuild.cs Assets/Editor/MultiplayerTestBuild.cs.meta && git commit -m "Add an editor script for the multiplayer test build"`.

### Task 11: Imagem Docker com Xvfb e teste de fumaça

**Files:**
- Create: `Tests/MultiplayerBand/Dockerfile`
- Create: `Tests/MultiplayerBand/README.md` (como rodar; curto)

- [ ] **Step 1:** `Dockerfile`: `FROM ubuntu:24.04`; `apt-get install -y --no-install-recommends xvfb xauth python3 libgl1 libglx-mesa0 libgl1-mesa-dri libegl1 libvulkan1 mesa-vulkan-drivers libasound2t64 libpulse0 libxcursor1 libxrandr2 libxi6 libxinerama1 libxss1 libgtk-3-0t64 libudev1 ca-certificates`; `/etc/asound.conf` com `pcm.!default { type null }`.
- [ ] **Step 2:** `docker build -t yarg-mp-banda /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/Tests/MultiplayerBand` → sucesso.
- [ ] **Step 3:** Dependências: `docker run --rm -v "$HOME/yarg-build-mp-banda:/game:ro" yarg-mp-banda bash -c 'ldd /game/UnityPlayer.so /game/YARG_Data/Plugins/*.so 2>/dev/null | grep "not found" | sort -u'` → vazio (se não, acrescente o pacote e repita o Step 2).
- [ ] **Step 4: Fumaça** (1 instância offline, 2112, bot):

  ```bash
  RUN=$HOME/yarg-mp-banda-runs/smoke && rm -rf "$RUN" && mkdir -p "$RUN/data" && \
  echo '{ "DisablePerSongBackgrounds": true, "DisableGlobalBackgrounds": true, "DisableDefaultBackground": true, "SongFolders": ["/songs"] }' > "$RUN/data/settings.json" && \
  timeout 900 docker run --rm --network none --device /dev/dri --group-add "$(stat -c %g /dev/dri/renderD128)" \
    --user "$(id -u):$(id -g)" -e HOME=/work -v "$RUN:/work" -v "$HOME/yarg-build-mp-banda:/game:ro" \
    -v "/home/felipeserver/repos/musicas yarg:/songs:ro" yarg-mp-banda \
    bash -c 'Xvfb :99 -screen 0 1280x720x24 & export DISPLAY=:99; sleep 2; /game/YARG.x86_64 -screen-fullscreen 0 -screen-width 640 -screen-height 360 -persistent-data-path /work/data -logFile /work/player.log -mp-autotest offline 1 -mp-song Soliloquy'; echo "exit=$?"
  ```

  Esperado: `exit=0` e `grep "\[AutoTest\] PASS" "$RUN/player.log"`. Se falhar sem perfil, sem áudio (`Failed to load audio`) ou sem GPU, ajuste (ex.: `LIBGL_ALWAYS_SOFTWARE=1`, `--device /dev/snd`, criar perfil) e registre no README o que foi preciso. Se o `offline` não criar perfil numa pasta nova, use `-mp-profile Smoke -mp-instrument FiveFretGuitar -mp-bot 1` (Tarefa 9) e, se o modo `offline` não aceitar esses argumentos, estenda-o na mesma linha da Tarefa 9 e recompile/rebuild.
- [ ] **Step 5: Commit** — `git add Tests/MultiplayerBand && git commit -m "Add a Docker image with Xvfb for headless multiplayer runs"`.

### Task 12: Orquestrador de cenários e verificador de logs

**Files:**
- Create: `Tests/MultiplayerBand/scenarios.py`, `Tests/MultiplayerBand/run_scenario.py`, `Tests/MultiplayerBand/launch_in_container.py`, `Tests/MultiplayerBand/check_band_logs.py`, `Tests/MultiplayerBand/test_check_band_logs.py`

**Interfaces:**
- Consumes: formato de log da Tarefa 9.
- Produces:
  - `scenarios.py`: `SCENARIOS: dict[str, dict]` com `song` (substring para `-mp-song`), `instances` (lista de `{"name", "role": "host"|"join", "args": [...]}`) e `expect` = `{"muted": {"Guitar": {"from": 30, "to": 40}}, "scripted": "nome"}`; stems fora de `muted` não podem ter eventos (exceto `Crowd`). Os quatro cenários da spec §6.3: `band4`, `same-instrument`, `keys`, `no-stems`.
  - `check_band_logs.py`: `parse_log(text) -> dict` (passed, playing, stems: lista `(stem, fraction, song)`, results: `{nome: (score, hit, total)}`, band: `(score, stars)`); `check(scenario: dict, logs: dict[str, str]) -> list[str]` (lista de erros; vazia = passou); `main` que lê `<pasta>/<instância>.log` e sai com 1 se houver erro.
  - Regras de `check`: todo log tem `PASS`; `results` e `band` idênticos em todos; o jogador `scripted` tem `hit < total`; instâncias com `-mp-bot 1` logam `bot=1`; para cada stem em `muted`, em **cada** log há evento `fraction=0.00` com `from <= song <= to + 2`, um evento `fraction=1.00` depois dele com `song <= to + 5`, e o último evento do stem é `1.00`; nenhum outro stem (fora `Crowd`) tem eventos.
  - `run_scenario.py <nome>`: cria `~/yarg-mp-banda-runs/<nome>/<instância>/settings.json` (host com `"SongFolders": ["/songs"]`, clientes sem), roda o contêiner da Tarefa 11 (mesmos `docker run` flags, `-v Tests/MultiplayerBand:/tests:ro`, `timeout` de 1500 s) com `python3 /tests/launch_in_container.py <nome>`, copia os logs para `<instância>.log` e chama `check`.
  - `launch_in_container.py`: sobe `Xvfb :99 -screen 0 1280x720x24`, lança o host, espera 20 s, lança os clientes (`-mp-autotest join 127.0.0.1`), cada um com `-persistent-data-path /work/<instância> -logFile /work/<instância>/player.log` e os flags de janela; espera todos (mata o resto se um passar de 1400 s) e sai com 0 só se todos saíram com 0.

- [ ] **Step 1:** `test_check_band_logs.py` (`unittest`, logs de exemplo inline): `test_passes_when_everything_matches`, `test_fails_on_score_mismatch`, `test_fails_on_band_mismatch`, `test_fails_when_a_machine_never_muted`, `test_fails_when_stem_stays_muted`, `test_fails_on_unexpected_stem_event`, `test_crowd_events_are_ignored`, `test_fails_without_pass`.
- [ ] **Step 2:** `cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/Tests/MultiplayerBand && python3 -I -m unittest test_check_band_logs -v` → falha (módulo inexistente).
- [ ] **Step 3:** Implementar os quatro arquivos. Argumentos dos cenários (todos com `-mp-difficulty Expert` se existir; `-mp-rounds 1`):
  - `band4` (2112, `Soliloquy`): host `-mp-profile Guit -mp-instrument FiveFretGuitar -mp-miss 30-40`; clientes `Bass`/`FiveFretBass`, `Drum`/`ProDrums`, `Voc`/`Vocals`, todos `-mp-bot 1`. `muted: Guitar 30–40`.
  - `same-instrument` (2112): host guitarra `-mp-miss 30-40`; cliente `Guit2` `FiveFretGuitar -mp-bot 1`. `muted: {}`.
  - `keys` (`Subdivisions`): host `Keys` `-mp-bot 1`; cliente guitarra `-mp-miss 60-70`; cliente `ProKeys -mp-bot 1`. `muted: Guitar 60–70`.
  - `no-stems` (`Making Memories`): host guitarra `-mp-miss 30-40`; cliente `ProDrums -mp-bot 1`. `muted: {}`.
- [ ] **Step 4:** Rodar o Step 2 de novo → todos passam.
- [ ] **Step 5: Commit** — `git add Tests/MultiplayerBand && git commit -m "Add the band scenario runner and log checker"`.

### Task 13: Teste ponta a ponta com a build

**Files:**
- Create: `docs/superpowers/plans/2026-10-31-mp-banda-completa-checklist.md`
- Modify (se preciso): `Tests/MultiplayerBand/scenarios.py`

- [ ] **Step 1:** Garantir build atual (refaça a Tarefa 10 Step 2 se houve commit em `Assets/` depois dela) e MP-TEST verde.
- [ ] **Step 2:** Rodar, um por vez (nunca dois cenários ao mesmo tempo), em background:
  `cd /home/felipeserver/repos/YARG/.claude/worktrees/mp-banda-completa/Tests/MultiplayerBand && python3 -I run_scenario.py same-instrument` → saída `0` e nenhuma linha de erro. Depois `no-stems`, `band4`, `keys`.
- [ ] **Step 3:** Ajustes permitidos sem mudar a regra: se um cenário falhar com `FAIL: X not in song; available: ...`, troque o instrumento pelo disponível mais próximo (`ProKeys` → `FiveFretBass`; `ProDrums` → `FourLaneDrums`) e anote; se não houver notas de guitarra na janela de erro, mova a janela para um trecho com notas (confira pelas linhas `Result:`/eventos) e anote. Qualquer outra falha é bug: superpowers:systematic-debugging, corrija no código com teste quando possível, rebuild e rode de novo.
- [ ] **Step 4:** Escrever o checklist no formato de `2026-10-30-online-multiplayer-checklist.md`: comando de cada cenário, data, PASS/FAIL, pontuações por jogador e de banda (iguais em todas as máquinas), tempos dos eventos `Stem Guitar` em cada máquina (mostrando o atraso dos remotos), ajustes feitos e o que não foi testado (6 trastes, pro guitar, 5 lanes, elite, > 4 instâncias, humanos reais). Marcar como "não verificado visualmente/auditivamente".
- [ ] **Step 5: Commit** — `git add docs/superpowers/plans/2026-10-31-mp-banda-completa-checklist.md Tests/MultiplayerBand && git commit -m "Record the full band multiplayer test results"`.
