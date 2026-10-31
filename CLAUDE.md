# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# YARG (fork de FelipeFlohr)

Fork do YARG (Unity 6000.3.5f2, URP). O backend fica no submódulo `YARG.Core` (fork `FelipeFlohr/YARG.Core`), que o Unity carrega como pacote local (`Packages/manifest.json` → `file:../YARG.Core/YARG.Core`).

## Ambiente desta máquina

Ubuntu Server 24.04, sem sudo, GPU integrada Intel HD 630, sem display. Todas as ferramentas são locais do usuário:

- `.NET SDK 10` + runtime 8 em `~/.dotnet`
- `git-lfs` e `gh` em `~/.local/bin`
- Unity Editor em `~/Unity/Hub/Editor/6000.3.5f2/Editor/Unity`

Se `dotnet`/`gh` não estiverem no PATH do shell:

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
```

## Verificação

YARG.Core (rápido, sempre rode depois de mexer no Core):

```bash
cd YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build
```

Um teste ou classe só (NUnit):

```bash
cd YARG.Core && dotnet test YARG.Core.UnitTests --filter "FullyQualifiedName~GuitarEngineTester"
```

Multiplayer (rápido, sem Unity): `Tests/YARG.Multiplayer.Tests` compila os fontes de `Assets/Script/Multiplayer/Core` direto contra o `YARG.Core.csproj`.

```bash
cd Tests/YARG.Multiplayer.Tests && dotnet test
```

Projeto Unity (lento; o primeiro import leva muito tempo; exige licença ativada). Compila os scripts em batchmode sem GPU:

```bash
~/Unity/Hub/Editor/6000.3.5f2/Editor/Unity -batchmode -nographics -quit -projectPath "$PWD" -logFile - 2>&1 | tee /tmp/unity.log
grep -E 'error CS|Compilation failed|Scripts have compiler errors' /tmp/unity.log
```

Pacotes NuGet (`Assets/Packages`, fora do git) são restaurados pelo NuGetForUnity ao abrir o editor. Em batchmode o Unity aborta antes disso se o projeto não compila; se `Assets/Packages` estiver faltando, rode uma vez com `-ignorecompilererrors` para restaurar e depois repita sem a flag.

Não dá para jogar nem inspecionar visualmente aqui. Mudanças visuais (cenas, prefabs, UI, shaders) precisam ser marcadas como "não verificadas visualmente" no PR.

Partidas online podem ser testadas sem ninguém no teclado com `-mp-autotest` (ver `Assets/Script/Multiplayer/MultiplayerAutoTest.cs`; o resultado sai no log como `[AutoTest] PASS`/`FAIL` e no exit code). Use `-persistent-data-path` distinto por instância:

```text
YARG -persistent-data-path <host>    -mp-autotest host [-mp-song nome] [-mp-rounds n] [-mp-leave-after s]
YARG -persistent-data-path <cliente> -mp-autotest join 127.0.0.1 [-mp-leave-after s | -mp-expect-leave]
YARG -persistent-data-path <dados>   -mp-autotest offline <jogadores>
```

## Arquitetura

**YARG.Core** (`netstandard2.1`, C# 9, sem dependência de Unity) contém a lógica testável: parsing de charts (`.mid` via DryWetMidi, `.chart` via Moonscraper em `MoonscraperChartParser`), scan/cache de músicas (`Song`), engines de gameplay (`Engine/{Guitar,Drums,Keys,Vocals}`, base em `BaseEngine`, coordenação de banda/combo/fail meter em `EngineManager.*`), replays e IO. O Unity compila esse mesmo diretório como pacote (`YARG.Core.asmdef`, assembly `YARG.Core.Package`), por isso os `.meta` existem também no Core. Builds `dotnet` vão para `.artifacts` (via `Directory.Build.props`) para o Unity não importá-los.

**Jogo Unity** (`Assets/Script`, namespace `YARG.*`):

- Cenas por índice (`SceneIndex` em `Persistent/GlobalVariables.cs`): `PersistentScene` fica sempre carregada e hospeda os singletons (`GlobalVariables`, que lê `CommandLineArgs` e carrega as demais cenas); `MenuScene`, `Gameplay`, `CalibrationScene`, `ScoreScene` são trocadas por cima.
- Gameplay: `Gameplay/GameManager*.cs` (partial) carrega a música e instancia um `BasePlayer` por jogador (`Gameplay/Player/*Player.cs`, genéricos em `TrackPlayer<TEngine, TNote>`). Cada player é uma camada visual/de input em volta de uma engine do Core: o input vai para `BaseEngine.QueueInput` e `BaseEngine.Update(time)`.
- Tempo: `Playback/SongRunner.cs` mantém InputTime/SongTime/VisualTime a partir de `InputState.currentTime` (ver `docs/song_sync.md`). Áudio é BASS (`Audio/Bass`, ver `docs/audio_pipeline.md` e `docs/time-stretch.md`), com código nativo em `Native/YargAudio`.
- Multiplayer online (específico deste fork; design em `docs/superpowers/specs/`): `Multiplayer/Core` é um asmdef sem referências a Unity (`noEngineReferences`) com protocolo, transporte TCP/em memória, sessões host/cliente, sincronização de relógio/input e envio de músicas. A integração com o jogo está em `Multiplayer/*.cs` (`MultiplayerManager`, `MultiplayerGameplay`, menus); jogadores remotos são `BasePlayer`s alimentados por input de rede.

## Regras de trabalho

- Assets `*.png/*.jpg/*.exr/*.fbx/*.ttf` estão no Git LFS.
- Code style: siga o `.editorconfig`. C# 9 em todo lugar (Core, Unity e testes); nada de recursos de C# mais novos.
- Arquivos Unity (`.unity`, `.prefab`, `.asset`, `.meta`) são YAML gerado pelo editor. Evite editar à mão; nunca apague nem regenere `.meta` (os GUIDs quebram referências). Arquivo `.cs` novo dentro de `Assets/` ou `YARG.Core/YARG.Core` precisa de `.meta` (o Unity gera ao importar).
- Trabalhe em branches (`claude/<assunto>`), nunca direto em `master`. Abra PRs contra `master` deste fork (`FelipeFlohr/YARG`), não contra o upstream `YARC-Official`.
- Mudanças no Core: commit/PR no repositório do submódulo primeiro, depois atualize o ponteiro do submódulo no YARG.
- `CONTRIBUTING.md` descreve as regras do upstream; este fork tem direção própria (por exemplo, lift notes no 5LK).
