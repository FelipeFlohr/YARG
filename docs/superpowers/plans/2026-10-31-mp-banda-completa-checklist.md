# Multiplayer com a banda inteira — verificação ponta a ponta

Tudo abaixo foi rodado em 2026-10-31 com a build real (Linux, `~/yarg-build-mp-banda`, refeita a cada mudança em
`Assets/`), em Docker (`yarg-mp-banda`) com Xvfb, `--network none` e todas as instâncias no mesmo contêiner, uma
janela de 640x360 por instância, OpenGL por software (llvmpipe) em 4 núcleos, sem placa de som (PCM nulo).
Comando de cada cenário (a partir de `Tests/MultiplayerBand`): `python3 run_scenario.py <cenário>`; ele roda o
contêiner, copia os logs e roda `check_band_logs.py`. Saída `0` e `OK: <cenário>` em todos.

**Nada foi verificado visualmente nem auditivamente**: não houve tela, nem som, nem humano. "Mute" significa o evento
`[AutoTest] Stem ... fraction=` que o jogo loga quando o volume do stem muda, não áudio ouvido.

Lembrete para ler os tempos: `song=` é o tempo local de música no quadro em que o evento foi aplicado. Com 3 ou 4
instâncias em 4 núcleos o jogo roda com quadros de 1 s ou mais, então todos os tempos têm essa granularidade e os
eventos se agrupam no mesmo `song=`.

## Resultado

| Cenário | Música | Resultado | Duração | Comando |
|---|---|---|---|---|
| `same-instrument` | Soliloquy | PASS | 4 min | `python3 run_scenario.py same-instrument` |
| `no-stems` | Making Memories | PASS | 4,5 min | `python3 run_scenario.py no-stems` |
| `band4` | Soliloquy | PASS na build c1108023; **FAIL** nas 3 execuções da build da rodada 2, só pela folga de desmute/mute remoto (ver abaixo) | 5,7 min | `python3 run_scenario.py band4` |
| `keys` | Subdivisions | PASS (build da rodada 2, 1ª tentativa) | 8,3 min | `python3 run_scenario.py keys` |

**Rodada de correção 2 (build da rodada 2, `YARG.Core` com o conserto do bot de vocal).** `keys`: OK na 1ª tentativa. `band4`: 3
tentativas, todas reprovadas só por tempo (as pontuações e o `Band:` ficaram iguais nas 4 máquinas, o vocal fez 14/14):
- tentativa 1: desmute remoto em 47,12 a 49,39 (limite 45,0); tentativa 2: só o vocals, 48,51; tentativa 3: drums 45,42 e o
  vocals só mutou em 42,87 (limite de mute 42,0).
- load average de 1 min logo antes de cada execução: band4 2,77 / 4,42 / 4,28 e keys 4,62 (a máquina estava livre). Logo depois:
  29,39 / 26,67 / 28,88 e 22,70. **O load de 23 a 29 vem da própria execução** (4 instâncias com render por software em 4
  núcleos); corrijo a explicação anterior de que eram outras sessões, que não se sustenta com esses números.
- Conclusão: a defasagem do relógio de música entre as instâncias (vários segundos) é própria deste harness e a folga de 5 s/2 s do
  verificador fica no limite com 4 instâncias; com 2 ou 3 passa. Não afrouxei a regra. `band4` continua FAIL aqui.
- Tempos (mute/desmute) por tentativa. 1: host 34.48/42.23, bass 40.44/47.74, drums 41.42/49.39, vocals 39.58/47.12. 2: host
  31.74/41.61, bass 34.66/44.15, drums 35.93/43.38, vocals 34.93/48.51. 3: host 34.36/41.90, bass 39.70/44.46, drums 40.90/45.42,
  vocals 42.87/45.57. `keys`: guitar 61.62/70.08, host 64.27/72.19, prokeys 64.71/72.84.

`keys` falhou na 1ª execução por 0,15 s: o desmute chegou na máquina `prokeys` em `song=75.15` e o verificador exige
até 75.0 (janela 60-70 + 5 s de folga). O verificador não foi mexido; repeti o cenário e passou (desmute em 74.61). A
folga de 5 s está perto do limite nesta máquina (ver "Preocupações").

**Rodada de correção 3 (revisão final).** Build refeita depois de: piso de 50% do stem de background no `StemMuteTracker`,
regra de dificuldade jogável compartilhada (`PartDifficulty`) no seletor online e no autoteste, e estatísticas do jogador local
iguais às enviadas em `Finished`. Só `same-instrument` e `no-stems` foram refeitos: ambos `OK` (build `~/yarg-build-mp-banda`).
`band4` e `keys` não foram refeitos nesta rodada.

**Rodada 4 (review do PR).** Build `~/yarg-build-mp-banda` refeita no commit 3d58cdfc depois de: seletor de parte só com partes
que a música tem (`PartRules.HasPlayablePart`, regra movida para `Multiplayer/Core` e testada), timeout de conexão de 10 s
separado do de 30 s da sessão, e `players=N` nos logs `Band result:` e `Band:` (o verificador compara o número entre as
máquinas). Só `same-instrument` e `no-stems` foram refeitos: ambos `OK` (`Band: score=208097 stars=5 players=2` nas duas
máquinas de `same-instrument`; `score=583762 stars=5 players=2` em `no-stems`). `band4` e `keys` continuam valendo para a
build da rodada 2.

## 1. Pontuação final idêntica em todas as máquinas

- [x] `same-instrument`: host e `guit2` com as mesmas duas linhas `Result:` e `Band: score=208533 stars=5`.
- [x] `no-stems`: `Band: score=584042 stars=5` nas duas.
- [x] `band4`: `Band: score=505724 stars=5` nas quatro (host, bass, drums, vocals), com `Result:` de Guit, Bass, Drum
  e Voc iguais nas quatro (última execução, build da rodada 2).
- [x] `keys`: `Band: score=1487405 stars=5` nas três (build da rodada 2) (host, guitar, prokeys), com `Result:` de Keys, Guit e ProKeys
  iguais nas três.

O verificador compara todas as linhas `Result:` (pontuação, acertos/total) e `Band:` entre as máquinas.

## 2. Stems separados: o erro do guitarrista muta a guitarra nas outras máquinas e desmuta no próximo acerto

- [x] `band4` (Soliloquy, host erra de 30 a 40 s). **Vale para a build antiga (c1108023)**; na build da rodada 2 o `band4` reprovou só pela folga de desmute do verificador, não por falha de mute. Primeiro `Stem Guitar fraction=0.00` e primeiro `1.00` depois:

  | Máquina | Papel | mute (0.00) | desmute (1.00) |
  |---|---|---|---|
  | host | guitarrista (local) | 34.51 | 41.23 |
  | bass | remota | 36.49 | 44.55 |
  | drums | remota | 34.86 | 42.41 |
  | vocals | remota | 41.63 | 43.92 |

  A guitarra foi mutada nas 4 máquinas dentro da janela (30 a 42) e voltou a 1.00 até 45. Os remotos atrasam de
  0,4 s a 7 s sobre o host nesta execução; o atraso inclui o buffer de entrada dos remotos e o quadro lento. Depois
  da janela há mutes e desmutes de sustain (fim de sustain solto) que o verificador aceita.
- [x] `keys` (Subdivisions, guitarrista erra de 60 a 70 s). Tempos da tabela: build c1108023. Na build da rodada 2 `keys` passou, com desmute em 70.08 (guitar), 72.19 (host) e 72.84 (prokeys):

  | Máquina | Papel | mute (0.00) | desmute (1.00) |
  |---|---|---|---|
  | guitar | guitarrista (local) | 60.22 | 71.14 |
  | host | Keys (bot), remota | 64.00 | 74.34 |
  | prokeys | ProKeys (bot), remota | 64.25 | 74.61 |

  Na 1ª execução: 61.52/71.72 (guitar), 64.44/73.56 (host), 64.64/75.15 (prokeys).

## 3. Dois jogadores no mesmo instrumento: só muta se os dois errarem

- [x] `same-instrument`: host (humano scripted, erra 30 a 40 s, 340/364 acertos) e `guit2` (bot, 364/364) na guitarra.
  Nenhum evento `Stem` em nenhuma máquina (o verificador exige zero). Um erra sozinho, não muta.
- [ ] Os dois errando ao mesmo tempo não foi jogado ponta a ponta (a regra `AllMustMiss` é coberta pelos testes do
  núcleo, `StemMuteTracker`).

## 4. Música sem stems nunca muta

- [x] `no-stems` (Making Memories, guitarrista erra de 30 a 40 s, 857/913 acertos, bateria bot): nenhum evento
  `Stem` nas duas máquinas.

## 5. Cliente com perfil de bot entra e toca como bot

- [x] `Playing as ... bot=1` nos clientes bot (o verificador exige `bot=1`):
  - `same-instrument` `guit2`: `Playing as Guit2 bot=1 instrument=FiveFretGuitar difficulty=Expert`
  - `no-stems` `drums`: `Playing as Drum bot=1 instrument=ProDrums difficulty=Expert`
  - `band4`: `Bass bot=1 FiveFretBass`, `Drum bot=1 FiveLaneDrums`, `Voc bot=1 Vocals difficulty=Expert`
  - `keys`: `Keys bot=1 Keys` (host), `ProKeys bot=1 ProKeys`
  - O guitarrista scripted aparece como `bot=0` em todos os cenários.

## 6. Instrumentos

- [x] Guitarra 5 trastes (humano scripted e bot), baixo 5 trastes, bateria (ProDrums em Making Memories, cinco
  lanes em Soliloquy), teclado e pro keys: cada um tocou uma partida inteira como jogador remoto em pelo
  menos uma máquina, com pontuação igual em todas.
- [x] Vocal: o bot toca. Com o conserto do Core (abaixo) o `band4` mostra `Result: Voc ... score=124000 hit=14/14` nas 4 máquinas
  (3 execuções), contra `hit=0/14` antes. Causa do 0/14 anterior: `VocalsEngine.GenerateQueuedUpdates`
  (`YARG.Core/YARG.Core/Engine/Vocals/VocalsEngine.cs:121`) calculava `nextUpdateIndex * ApproximateVocalFps` em vez de
  `* timeForFrame`, então o laço que simula as entradas do bot nunca rodava e ele só cantava nos `Update` do jogo (a menos de 1
  quadro/s no harness, 0 acertos). Conserto no submódulo, branch `claude/mp-banda-completa`, commit `0774fba`, com teste
  `Bot_HitsEveryPhrase_RegardlessOfTheUpdateRate` (60, 10 e 1 atualização/s; reprovava a 10 e 1 antes).

## Ajustes e bugs achados rodando

| O que | Causa | Correção | Commit |
|---|---|---|---|
| Harness frágil (contêiner órfão, processo preso) | `timeout` sem `-k`, sem `--init`, sem limpar contêiner antigo | `timeout -k 30`, `--init`, `docker rm -f`; matar as outras instâncias se uma sair com erro; verificador exige que o 1º mute caia dentro da janela (+ teste); `.gitignore` | `b5a560b6` |
| Cliente caía da sessão ("connection to the host timed out") | Carregar a música bloqueia a thread principal por mais que o timeout padrão de 5 s | `timeoutSeconds: 30` na `SessionConfig` do jogo | `c1352998` |
| Clientes depois do primeiro recusados (`SessionInProgress`) | O host do autotest começava a rodada com o primeiro cliente | Argumento `-mp-players N` (padrão 2), o harness passa o número de instâncias | `032fc098` |
| `band4`: bass tocava sozinho ("Player N didn't load in time") | `MULTIPLAYER_SETUP_TIMEOUT` de 15 s menor que o carregamento de 4 instâncias nesta máquina | 60 s (igual ao de início) | `f651f349` |
| Guitarra mutava fora da janela de erro | O guitarrista scripted soltava o traste do sustain estendido ao tocar a nota mais alta e o sustain caía | O script segura os trastes do sustain; teste novo; conferido com o chart real do Soliloquy (zero sustains soltos antes da janela) | `c1108023` |
| `band4`: `ProDrums not in song` | Soliloquy só tem bateria de cinco lanes | Cenário usa `FiveLaneDrums` (ajuste permitido) | `f651f349` |
| `band4`: `Vocals has no requested difficulty chart` com Expert | `PartValues` usa o mesmo byte para sub-tracks e para a máscara de dificuldade (`YARG.Core/.../PartValues.cs:17,31,43`; o scan grava `LeadVocals.SubTracks = 1`, `SongEntry.IniBase.cs:600`), então `song[Vocals][Beginner]` só quer dizer "tem vocal principal"; o diagnóstico anterior ("a música só reporta Beginner") estava errado. O bug era do autotest, que checava a máscara também para vocal | `MultiplayerAutoTest.HasDifficulty`: para Vocals/Harmony/PartyVocals checa a existência da parte; `band4` volta a usar vocal Expert | revisão 1 |
| `band4`: Bass com pontuação diferente entre máquinas (64391 contra 64163) | `MULTIPLAYER_FINISH_TIMEOUT` de 3 s: a máquina que termina antes que o Bass (relógios de música defasados em vários segundos sob carga) usa o último checkpoint dele em vez do quadro final | 15 s | revisão 1 |
| Script do guitarrista segurava como "sustain" o traste de notas sem sustain | `sustainMask = mask` na nota sobreposta | Fim de sustain por traste (`sustainEnd[5]`); teste `Generate_OnlyKeepsFretsOfNotesThatAreStillSustaining` | revisão 1 |

Observação sobre o 1º ajuste: o verificador de "1º mute dentro da janela" não foi afrouxado; o que estava errado era o
jogador scripted (mutes de sustain que um jogador de verdade não teria).

## Não testado

- 6 trastes, pro guitar, elite drums, bateria 4 lanes/ProDrums em Soliloquy (só cinco lanes); 5 lanes só como bot.
- Mais de 4 instâncias; mais de uma rodada na banda.
- Humanos reais (todos os "humanos" são scripted ou bots), duas máquinas reais, rede com perda/latência (loopback, `--network none`).
- Áudio audível (PCM nulo): só os eventos de volume do stem foram lidos.
- Render na GPU real (llvmpipe), e qualquer coisa visual (pista remota, HUD, tela de resultados).
- Fail meter ligado (`NoFail: 1` em todas as instâncias).
- `band4` passando na build da rodada 2 (reprovado só pelos limites de tempo do verificador; ver acima).
- Os dois jogadores da mesma parte errando juntos (só coberto por testes do núcleo).
- Dois humanos de verdade no mesmo instrumento; vocal com cantor de verdade (só o bot, que faz 14/14).

## Preocupações

- Com 4 instâncias neste PC os limites de tempo do verificador (mute até janela+2 s, desmute até janela+5 s) são violados por até 4 s
  no `band4`; `keys` (3 instâncias) passou. Não afrouxei os limites. O atraso vem de quadros lentos de
  instâncias disputando 4 núcleos com render por software; numa máquina real o atraso é menor.
- Pontuações mudam um pouco de uma execução para outra (por exemplo, o bot de teclado), mas são iguais entre as
  máquinas na mesma execução.

## Rodada 5: Windows nativo (pendências, 2026-10-31)

Build de Windows (`MultiplayerTestBuild` agora gera o player da plataforma do editor) rodando como processos comuns
neste PC (12 threads, GPU de verdade), com `run_scenario.py --native`: janelas 640x360 minimizadas, som desligado e
`BackgroundFpsCap` padrão (10 quadros/s nas janelas sem foco). Inclui o keepalive do transporte (protocolo 3).

| Cenário | Resultado | Mute do stem nas máquinas (s) | Banda igual em todas |
|---|---|---|---|
| `band4` | OK | host 30,31–40,29; remotas 30,52–30,58 até 40,51–40,57 | sim |
| `same-instrument` | OK | nenhum (só um guitarrista erra) | sim |
| `keys` | OK | Guitar muta e volta na janela 60–70 | sim |
| `no-stems` | OK | nenhum | sim |
| `same-instrument-both-miss` (novo) | OK | só no trecho comum: 35,11–35,30 até 40,30–40,51 | sim (146957, 4 estrelas) |
| `four-lane` (novo) | OK | 60,21–60,42 até 70,07–70,34 | sim |
| `fail-meter` (novo, `NoFail` 0) | OK | 30,32–30,54 até 40,32–40,54; a banda não falhou | sim |
| `band8` (novo, 8 máquinas) | OK | 60,21–60,50 até 70,06–70,34 | sim (8 jogadores) |

**Item 1 (atraso do mute remoto):** com hardware de verdade o mute e o desmute chegam às máquinas remotas
0,2–0,3 s depois do host, o buffer fixo de 200 ms do `RemoteInputBuffer`, mesmo com 7 das 8 janelas a 10 quadros/s.
Os 2–7 s da rodada 4 eram do PC do servidor (render por software, menos de 1 quadro/s). Folgas do verificador mantidas.

**Pro guitar:** a cena `Gameplay` não tem pista de pro guitar (`_proGuitarPrefab: {fileID: 0}`) e o seletor de modo
do perfil não oferece esse modo, então nenhum jogador chega nele. O autoteste forçava o instrumento e o jogador saía da
partida ("No local player to play online with"); agora `-mp-instrument ProGuitar_*/ProBass_*` falha logo ao ler os
argumentos. O cenário planejado com pro guitar virou `four-lane`.

Continuam sem teste ponta a ponta: 6 trastes e elite drums (nenhuma música da biblioteca de teste tem esses charts),
humanos de verdade, duas máquinas em rede real e o áudio ouvido.
