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
| `band4` | Soliloquy | PASS na build c1108023; **FAIL** na build final, só por `UNMUTE_SLACK` (ver abaixo) | 5,6 min | `python3 run_scenario.py band4` |
| `keys` | Subdivisions | PASS (2ª execução, build c1108023); **FAIL** na build final, só por `UNMUTE_SLACK` | 8,3 min | `python3 run_scenario.py keys` |

**Rodada de correção (revisão 1).** Depois dos commits da revisão (vocal em Expert, fim de sustain por traste,
`MULTIPLAYER_FINISH_TIMEOUT` de 15 s) `same-instrument` e `no-stems` passaram de novo. `band4` falhou 4 vezes seguidas e
`keys` 1 vez com a mesma e única reprovação: `stem Guitar did not come back to 1.00 by 45.0` (band4) e `by 75.0` (keys).
Pontuações e `Band:` ficaram iguais em todas as máquinas, o mute aconteceu nas máquinas remotas dentro da janela, e o
desmute veio, mas com 0,3 s a 4 s além do limite (janela + 5 s). Durante essas execuções o load average da máquina estava em
20 a 25 por causa de outras sessões do Claude (`uptime`), contra um load baixo na execução que passou. Não afrouxei a
tolerância; repetir quando a máquina estiver livre deve passar (como passou antes), mas isto **não foi demonstrado na
build final**. Tempos (mute/desmute) da última execução: band4 host 33.81/42.76, bass 39.57/47.64, drums 39.69/44.18,
vocals 40.96/49.22; keys guitar 60.29/71.82, host 61.02/75.27, prokeys 62.57/76.49.

`keys` falhou na 1ª execução por 0,15 s: o desmute chegou na máquina `prokeys` em `song=75.15` e o verificador exige
até 75.0 (janela 60-70 + 5 s de folga). O verificador não foi mexido; repeti o cenário e passou (desmute em 74.61). A
folga de 5 s está perto do limite nesta máquina (ver "Preocupações").

## 1. Pontuação final idêntica em todas as máquinas

- [x] `same-instrument`: host e `guit2` com as mesmas duas linhas `Result:` e `Band: score=208533 stars=5`.
- [x] `no-stems`: `Band: score=584042 stars=5` nas duas.
- [x] `band4`: `Band: score=354202 stars=4` nas quatro (host, bass, drums, vocals), com `Result:` de Guit, Bass, Drum
  e Voc iguais nas quatro (última execução, build final).
- [x] `keys`: `Band: score=1493557 stars=5` nas três (última execução) (host, guitar, prokeys), com `Result:` de Keys, Guit e ProKeys
  iguais nas três.

O verificador compara todas as linhas `Result:` (pontuação, acertos/total) e `Band:` entre as máquinas.

## 2. Stems separados: o erro do guitarrista muta a guitarra nas outras máquinas e desmuta no próximo acerto

- [x] `band4` (Soliloquy, host erra de 30 a 40 s). Primeiro `Stem Guitar fraction=0.00` e primeiro `1.00` depois:

  | Máquina | Papel | mute (0.00) | desmute (1.00) |
  |---|---|---|---|
  | host | guitarrista (local) | 34.51 | 41.23 |
  | bass | remota | 36.49 | 44.55 |
  | drums | remota | 34.86 | 42.41 |
  | vocals | remota | 41.63 | 43.92 |

  A guitarra foi mutada nas 4 máquinas dentro da janela (30 a 42) e voltou a 1.00 até 45. Os remotos atrasam de
  0,4 s a 7 s sobre o host nesta execução; o atraso inclui o buffer de entrada dos remotos e o quadro lento. Depois
  da janela há mutes e desmutes de sustain (fim de sustain solto) que o verificador aceita.
- [x] `keys` (Subdivisions, guitarrista erra de 60 a 70 s):

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
  - `band4`: `Bass bot=1 FiveFretBass`, `Drum bot=1 FiveLaneDrums`, `Voc bot=1 Vocals difficulty=Beginner`
  - `keys`: `Keys bot=1 Keys` (host), `ProKeys bot=1 ProKeys`
  - O guitarrista scripted aparece como `bot=0` em todos os cenários.

## 6. Instrumentos

- [x] Guitarra 5 trastes (humano scripted e bot), baixo 5 trastes, bateria (ProDrums em Making Memories, cinco
  lanes em Soliloquy), teclado e pro keys: cada um tocou uma partida inteira como jogador remoto em pelo
  menos uma máquina, com pontuação igual em todas.
- [ ] **Vocal: NÃO verificado como "toca".** O bot de vocal (agora em Expert) entra, é replicado e fecha com pontuação
  igual nas máquinas, mas faz `hit=0/14` (pontuação 79 a 911, varia). Causa (investigada com o engine isolado, teste
  `VocalBotTests`): o bot de vocal só canta nos `Update` que o jogo dá a ele. O Core deveria enfileirar atualizações
  extras para o bot, mas `VocalsEngine.GenerateQueuedUpdates` (`YARG.Core/YARG.Core/Engine/Vocals/VocalsEngine.cs:121`) calcula
  `nextUpdateIndex * ApproximateVocalFps` em vez de `nextUpdateIndex * timeForFrame` (linha 119), então o laço da linha 123
  nunca roda. A 60 atualizações por segundo o bot acerta 14/14 frases no Soliloquy (30/30 no chart de teste); a 10 por
  segundo, 0/14 (2/30); a 1 por segundo, 0/N. No harness o jogo roda a menos de 1 quadro por segundo (3 a 4 instâncias,
  GL por software), por isso 0/14. É comportamento do Core (não alterado), não da sincronização do fork.

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
- Vocal tocando de verdade (o bot faz 0/14 frases no harness, ver acima).
- `band4` e `keys` passando na build final (reprovados só pela folga de desmute com a máquina em load 20 a 25).
- Os dois jogadores da mesma parte errando juntos (só coberto por testes do núcleo).
- Dois humanos de verdade no mesmo instrumento; vocal com cantor de verdade.

## Preocupações

- A folga de 5 s do desmute (`UNMUTE_SLACK`) reprovou `keys` uma vez (por 0,15 s) e, na build final com a máquina ocupada, `band4` e `keys`. O atraso vem de quadros lentos de
  instâncias disputando 4 núcleos com render por software; numa máquina real o atraso é menor.
- Pontuações mudam um pouco de uma execução para outra (por exemplo, o bot de teclado), mas são iguais entre as
  máquinas na mesma execução.
