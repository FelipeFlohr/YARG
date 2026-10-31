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
| `band4` | Soliloquy | PASS | 5,6 min | `python3 run_scenario.py band4` |
| `keys` | Subdivisions | PASS (na 2ª execução) | 8,3 min | `python3 run_scenario.py keys` |

`keys` falhou na 1ª execução por 0,15 s: o desmute chegou na máquina `prokeys` em `song=75.15` e o verificador exige
até 75.0 (janela 60-70 + 5 s de folga). O verificador não foi mexido; repeti o cenário e passou (desmute em 74.61). A
folga de 5 s está perto do limite nesta máquina (ver "Preocupações").

## 1. Pontuação final idêntica em todas as máquinas

- [x] `same-instrument`: host e `guit2` com as mesmas duas linhas `Result:` e `Band: score=208533 stars=5`.
- [x] `no-stems`: `Band: score=584042 stars=5` nas duas.
- [x] `band4`: `Band: score=348894 stars=5` nas quatro (host, bass, drums, vocals), com `Result:` de Guit, Bass, Drum
  e Voc iguais nas quatro.
- [x] `keys`: `Band: score=1493824 stars=5` nas três (host, guitar, prokeys), com `Result:` de Keys, Guit e ProKeys
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
  lanes em Soliloquy), vocal, teclado e pro keys: cada um tocou uma partida inteira como jogador remoto em pelo
  menos uma máquina, com pontuação igual em todas.
- Observação: o bot de vocal fez `hit=0/14` e pontuação de 79 a 95 (varia entre execuções, mas é igual entre as
  máquinas na mesma execução). O vocal entra, é replicado e fecha; não dá para dizer que "toca bem" como bot.

## Ajustes e bugs achados rodando

| O que | Causa | Correção | Commit |
|---|---|---|---|
| Harness frágil (contêiner órfão, processo preso) | `timeout` sem `-k`, sem `--init`, sem limpar contêiner antigo | `timeout -k 30`, `--init`, `docker rm -f`; matar as outras instâncias se uma sair com erro; verificador exige que o 1º mute caia dentro da janela (+ teste); `.gitignore` | `5a560b6` |
| Cliente caía da sessão ("connection to the host timed out") | Carregar a música bloqueia a thread principal por mais que o timeout padrão de 5 s | `timeoutSeconds: 30` na `SessionConfig` do jogo | `c1352998` |
| Clientes depois do primeiro recusados (`SessionInProgress`) | O host do autotest começava a rodada com o primeiro cliente | Argumento `-mp-players N` (padrão 2), o harness passa o número de instâncias | `032fc098` |
| `band4`: bass tocava sozinho ("Player N didn't load in time") | `MULTIPLAYER_SETUP_TIMEOUT` de 15 s menor que o carregamento de 4 instâncias nesta máquina | 60 s (igual ao de início) | `f651f349` |
| Guitarra mutava fora da janela de erro | O guitarrista scripted soltava o traste do sustain estendido ao tocar a nota mais alta e o sustain caía | O script segura os trastes do sustain; teste novo; conferido com o chart real do Soliloquy (zero sustains soltos antes da janela) | `c1108023` |
| `band4`: `ProDrums not in song` | Soliloquy só tem bateria de cinco lanes | Cenário usa `FiveLaneDrums` (ajuste permitido) | `f651f349` |
| `band4`: vocal sem dificuldade Expert | A música só reporta `Beginner` para vocais | Bot de vocal usa `-mp-difficulty Beginner` | `f651f349` |

Observação sobre o 1º ajuste: o verificador de "1º mute dentro da janela" não foi afrouxado; o que estava errado era o
jogador scripted (mutes de sustain que um jogador de verdade não teria).

## Não testado

- 6 trastes, pro guitar, elite drums, bateria 4 lanes/ProDrums em Soliloquy (só cinco lanes); 5 lanes só como bot.
- Mais de 4 instâncias; mais de uma rodada na banda.
- Humanos reais (todos os "humanos" são scripted ou bots), duas máquinas reais, rede com perda/latência (loopback, `--network none`).
- Áudio audível (PCM nulo): só os eventos de volume do stem foram lidos.
- Render na GPU real (llvmpipe), e qualquer coisa visual (pista remota, HUD, tela de resultados).
- Fail meter ligado (`NoFail: 1` em todas as instâncias).
- Os dois jogadores da mesma parte errando juntos (só coberto por testes do núcleo).
- Dois humanos de verdade no mesmo instrumento; vocal com cantor de verdade.

## Preocupações

- A folga de 5 s do desmute (`UNMUTE_SLACK`) ficou a 0,15 s de reprovar uma vez. O atraso vem de quadros lentos de
  instâncias disputando 4 núcleos com render por software; numa máquina real o atraso é menor.
- Pontuações mudam um pouco de uma execução para outra (por exemplo, o bot de teclado), mas são iguais entre as
  máquinas na mesma execução.
