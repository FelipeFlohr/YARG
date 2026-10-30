# Multiplayer online (banda co-op) — Design

Data: 2026-10-30
Status: aguardando revisão

## 1. Objetivo

Permitir que um pequeno grupo de amigos (2–4 máquinas) toque uma música juntos em
**banda co-op** pela internet, com um jogador hospedando localmente e os outros
conectando pelo **Tailscale** (sem servidor dedicado). Cada jogador vê a própria
pista e as **pistas completas dos outros**. Músicas que um cliente não tem são
**transferidas automaticamente do host**.

O port para navegador é um objetivo posterior; este design apenas garante que ele
seja possível trocando o transporte (ver §4.1).

### Critérios de sucesso

- Host abre uma sessão; clientes entram digitando `IP:porta` (IP Tailscale).
- Host escolhe uma música; clientes sem ela recebem e a música passa a tocar neles.
- Todos começam a música em instantes próximos (erro de poucos ms).
- Cada jogador vê as pistas dos remotos tocando com os acertos/erros reais deles,
  com um atraso fixo de ~200 ms.
- Score de banda, multiplicador de banda e star power em uníssono funcionam.
- Tela de resultados mostra os stats de cada jogador.

### Premissas aceitas

- Alguma dessincronização entre jogadores é aceitável (como no Guitar Hero online).
- Grupo de confiança: sem anti-cheat, sem validação de score alheio.
- O host decide: escolhe música, velocidade e dá o start.

### Fora do escopo (MVP)

- CON / RBCON (packed ou unpacked) no compartilhamento de músicas.
- Mais de um jogador local por máquina conectada (o host também só tem um).
- Reconexão no meio da música.
- Modo versus.
- Port para navegador (somente preparado via abstração de transporte).
- Matchmaking, descoberta automática de sessões, relay.

## 2. Contexto no código existente

| Fato | Onde |
|---|---|
| Toda música tem um hash do arquivo de chart | `SongEntry.Hash` (`YARG.Core/YARG.Core/Song/Entries/SongEntry.cs`), calculado em `SongEntry.IniBase.cs` e `SongEntry.RBCON.cs` |
| `.sng` é um arquivo único por música | `YARG.Core/YARG.Core/IO/SngHandler/` |
| Vários jogadores numa banda local, com score/multiplicador de banda | `GameManager.cs` (`BandScore`, `BandMultiplier`), `EngineManager` |
| Jogador em replay consome uma lista de `GameInput` com timestamp | `BasePlayer.cs` (`_replayInputs`, `UpdateInputs`, `SetReplayTime`) |
| `UpdateInputs` só enfileira inputs com `input.Time <= time` e depois chama `BaseEngine.Update(time)` | `BasePlayer.UpdateInputs` |
| Stats completos por jogador | `BaseStats` e subclasses (`YARG.Core/YARG.Core/Engine/`) |
| Perfil do jogador e flag de replay | `YargPlayer` (`Assets/Script/Player/YargPlayer.cs`) |
| Pastas de música configuradas | `SettingsManager.Settings.SongFolders` |

## 3. Arquitetura

### 3.1 Topologia

Estrela. O host é um jogador comum que também roda um servidor TCP. Cada cliente
mantém uma conexão com o host; o host retransmite mensagens de jogo de um cliente
para os demais. **Cada máquina é autoridade sobre o próprio jogador**: seus inputs
e stats são aceitos como enviados.

Como o host escolhe a música da própria biblioteca, **o host sempre tem a música**;
toda transferência é host → cliente.

### 3.2 Componentes

Todo o código novo fica em `Assets/Script/Multiplayer/`.

| Unidade | Responsabilidade | Depende de |
|---|---|---|
| `Transport/` | `INetTransport` (conectar, aceitar, enviar/receber mensagens inteiras, eventos de conexão/desconexão); `TcpTransport` (servidor + cliente, framing com prefixo de tamanho); `LaggyTransport` (decorador para testes) | somente .NET |
| `Protocol/` | Tipos de mensagem e serialização binária. **Sem dependência de UnityEngine**, para reuso no port web | `YARG.Core` (`GameInput`, `HashWrapper`, stats) |
| `Session/` | `HostSession` / `ClientSession`: lista de jogadores (nome do perfil, instrumento, dificuldade, estado), máquina de estados da sessão, timeouts | Protocol, Transport |
| `ClockSync` | Estimativa do offset de relógio de cada cliente em relação ao host via ping/pong | Session |
| `SongShare/` | Matching por hash, empacotamento, transferência em chunks, extração segura, registro da música no cache | `YARG.Core.Song`, Session |
| `Gameplay/` | Ponte com o `GameManager`: captura e envio de inputs locais; criação e alimentação dos jogadores remotos; checkpoints de stats | `BasePlayer`, `GameManager`, `EngineManager` |
| `Menu/` | Telas "Hospedar / Entrar", lobby (jogadores, instrumentos, progresso de download, start), configurações de multiplayer | `MenuManager`, Settings |

### 3.3 Máquina de estados da sessão

```
Lobby ──(host escolhe música)──▶ Transferindo ──(todos têm a música)──▶ Carregando
  ▲                                                                       │
  │                                                          (todos Loaded; host envia StartAt)
  │                                                                       ▼
Resultados ◀──────────────(todos Finished ou timeout)────────────────── Jogando
```

Em `Resultados`, o host volta todos para `Lobby` ao escolher a próxima música.

## 4. Detalhes

### 4.1 Transporte

- `INetTransport` expõe: `StartHost(port)`, `Connect(address, port)`,
  `Send(peerId, ReadOnlySpan<byte>)`, `Broadcast(...)`, e eventos
  `PeerConnected`, `PeerDisconnected`, `MessageReceived(peerId, bytes)`.
- `TcpTransport`: cada mensagem = `uint32` (tamanho, little-endian) + payload.
  Tamanho máximo de mensagem: 1 MiB; acima disso a conexão é derrubada.
- I/O de rede em thread própria; mensagens recebidas vão para uma fila
  consumida no main thread da Unity (`Update`).
- Porta padrão configurável (sugestão: 7676).
- Port web futuro: implementar `WebSocketTransport` com o mesmo contrato; o
  protocolo não muda.

### 4.2 Protocolo

Cada mensagem começa com um `byte` de tipo. Todas as mensagens de jogo carregam
o `playerId` atribuído pelo host.

| Mensagem | Direção | Conteúdo |
|---|---|---|
| `Hello` | C→H | versão do protocolo, versão do jogo, nome do perfil |
| `Welcome` / `Reject` | H→C | `playerId` atribuído / motivo da recusa |
| `LobbyState` | H→C | jogadores (id, nome, instrumento, dificuldade, estado), música escolhida |
| `SetPart` | C→H | instrumento e dificuldade escolhidos |
| `Ping` / `Pong` | ambos | timestamps locais (para `ClockSync` e keep-alive) |
| `SongOffer` | H→C | hash do chart, nome, artista, tipo (`IniFolder` / `Sng`), tamanho total, velocidade da música |
| `HaveSong` / `NeedSong` | C→H | hash |
| `SongChunk` | H→C | hash, offset, bytes (≤ 64 KiB) |
| `SongReady` / `SongFailed` | C→H | hash / motivo |
| `BeginLoad` | H→C | todos têm a música e a parte escolhida; carregar a cena de gameplay |
| `Loaded` | C→H | — |
| `StartAt` | H→C | instante de início no relógio do host |
| `PlayerSetup` | ambos (host retransmite) | `playerId`, `ReplayFrame` serializado sem inputs (perfil + parâmetros de engine), enviado ao carregar; necessário para a engine remota reproduzir exatamente a do jogador |
| `ReturnToLobby` | H→C | voltar ao lobby após os resultados |
| `InputBatch` | ambos (host retransmite) | `playerId`, marca d'água (tempo de música), lista de `GameInput` |
| `StatsCheckpoint` | ambos (host retransmite) | `playerId`, stats serializados do jogador |
| `Finished` | ambos (host retransmite) | `playerId`, stats finais |
| `Leave` | ambos | motivo |

### 4.3 Compartilhamento de músicas

**Matching.** No `SongOffer` o cliente procura o hash no próprio cache de músicas
e responde `HaveSong` ou `NeedSong`.

**Formatos suportados.**
- `.sng`: enviado como está.
- Pasta ini (unpacked): o host compacta a pasta da música em zip (em memória ou em
  arquivo temporário) e envia.
- CON/RBCON: o host não pode escolher essas músicas em sessão multiplayer (a lista
  de músicas filtra ou avisa).

**Destino no cliente.** Nova configuração de multiplayer:
- `SaveMultiplayerSongs` (bool, padrão: ligado).
- `MultiplayerSongsFolder` (caminho; padrão: `<persistentDataPath>/Multiplayer Songs`).

Se ligado, a música é extraída em `MultiplayerSongsFolder` e essa pasta é
incluída automaticamente na varredura de músicas. Se desligado, a música vai para
uma pasta temporária, apagada ao sair da sessão (e na inicialização do jogo, para
limpar restos de crash).

**Registro.** Após extrair, a música é adicionada ao cache sem varredura completa
(escanear apenas a pasta nova) e o hash do chart é conferido contra o do
`SongOffer`.

**Progresso.** O lobby mostra porcentagem por jogador.

### 4.4 Início sincronizado

- `ClockSync` envia `Ping` a cada 500 ms durante toda a sessão. O offset é a média
  das N amostras com menor RTT (N = 5 de uma janela de 20).
- Quando todos enviaram `Loaded`, o host envia `StartAt(T_host + 3 s)`.
- O cliente converte para o próprio relógio e inicia o `SongRunner` naquele
  instante (usando o atraso inicial já existente, `SONG_START_DELAY`).

### 4.5 Inputs, ticks e pista remota

**Envio.** A ponte de gameplay intercepta os `GameInput` que o jogador local já
gera (os mesmos gravados em `_replayInputs`). A cada 50 ms envia um `InputBatch`
com os inputs novos e a **marca d'água** = `InputTime` atual do jogador local
(garantia: nenhum input futuro terá tempo menor que isso). Lotes vazios também
são enviados.

**Jogador remoto.** Para cada jogador remoto é criado um `YargPlayer` em modo
"remoto" (análogo ao modo replay) cujo `BasePlayer` consome uma lista de inputs
que cresce conforme os lotes chegam.

- A engine remota avança até `t_remoto = min(InputTime − D, marcaDágua)`, com
  **D = 200 ms** (configurável).
- A pista remota é desenhada em `VisualTime − D`, para que notas e acertos
  fiquem alinhados na própria pista.
- Se a marca d'água parar (lag), a pista remota congela e depois avança até
  alcançar. A pista e o áudio locais nunca esperam.
- Erros do remoto silenciam o stem do instrumento dele, como no multiplayer local.

O motivo da espera por marca d'água: `BasePlayer.UpdateInputs` só aplica inputs
com `input.Time <= time` antes de `BaseEngine.Update(time)`; um input que chegasse
depois de a engine passar daquele tempo seria julgado incorretamente.

**Checkpoints.** A cada 1 s cada máquina envia `StatsCheckpoint` do próprio
jogador; ao terminar, `Finished` com os stats finais.

### 4.6 Banda e resultados

- O `EngineManager` de cada cliente calcula score de banda, multiplicador e
  uníssono de star power com as engines locais e remotas.
- Pequenas divergências entre clientes são aceitas.
- A tela de resultados usa, para cada jogador, os stats do `Finished` que ele
  mesmo enviou (ou o último `StatsCheckpoint`, se ele saiu). Divergências entre a
  simulação local e os stats recebidos são apenas registradas em log.

### 4.7 Pausa e saídas

- Sem pausa real online: a tecla de pause abre um menu com "Sair da partida"; a
  música continua.
- Cliente sai/cai: seu jogador passa a `SittingOut` para os demais; a partida
  continua.
- Host sai/cai: a sessão termina para todos, que voltam ao menu com um aviso.
- Falha por falta de público fica desativada em partidas online (equivale a No
  Fail), pois cada cliente encerraria a música sozinho.
- Jogadores remotos não são gravados no banco de scores local.

## 5. Tratamento de erros

| Situação | Comportamento |
|---|---|
| Versão de protocolo/jogo diferente | `Reject` com "versão incompatível: host X, você Y" |
| Sem mensagens de um par por 5 s | Par considerado desconectado (regras de §4.7) |
| Conexão cai durante a transferência | Arquivo parcial apagado |
| Hash do chart não confere após extrair | Uma nova tentativa; se falhar, `SongFailed` e o jogador assiste à partida |
| Zip com `..`, caminho absoluto ou extensão fora da lista permitida | Extração abortada, `SongFailed` |
| Tamanho total da música acima do limite (padrão 2 GiB) | `SongFailed` antes de baixar |
| Mensagem malformada ou maior que 1 MiB | Conexão com aquele par derrubada, motivo no log |
| Instrumento/dificuldade inexistente no chart | Lobby bloqueia o start e pede para o jogador trocar |

Extensões permitidas na extração: `.ini`, `.mid`, `.midi`, `.chart`, `.ogg`,
`.opus`, `.mp3`, `.wav`, `.flac`, `.png`, `.jpg`, `.jpeg`, `.webm`, `.mp4`,
`.yarground`, `.txt`.

## 6. Testes

1. **Unitários** (NUnit via `dotnet test`, num projeto de teste que compila o
   núcleo puro `Assets/Script/Multiplayer/Core/`, no mesmo padrão de
   `YARG.Core.UnitTests`; o projeto não tem Unity Test Framework instalado):
   - ida e volta da serialização de cada mensagem;
   - framing TCP com mensagens partidas e concatenadas;
   - empacotar/extrair pasta ini; rejeição de zips maliciosos;
   - `ClockSync` com amostras sintéticas;
   - buffer de inputs remotos com marca d'água.
2. **Fidelidade:** alimentar uma sequência de inputs pelo caminho de jogador
   remoto (`InputBatcher` → codec → `LaggyTransport` com atraso, jitter e lotes
   irregulares → `RemoteInputBuffer`) e verificar que a engine remota recebe
   exatamente os mesmos inputs, na mesma ordem, e nenhum depois do tempo dele.
   Com isso, o determinismo da engine (já exercitado pelo `ReplayAnalyzer`)
   garante stats idênticos.
3. **Manual:** duas instâncias em `localhost`; depois duas máquinas via
   Tailscale. Checklist: entrar no lobby, transferir pasta ini e `.sng`, start
   sincronizado, pistas remotas, saída de cliente, saída do host, configuração de
   salvar/temporário.

## 7. Ordem de implementação sugerida

1. Transport + Protocol + testes unitários.
2. Session + ClockSync + menu Hospedar/Entrar + lobby básico.
3. Jogador remoto com inputs (sem transferência: ambos já têm a música) + teste de
   determinismo.
4. Banda, checkpoints, resultados, pausa/saídas.
5. SongShare + configurações de download.
