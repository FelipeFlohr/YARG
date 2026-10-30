# Multiplayer online (banda co-op) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Banda co-op online para 2–4 máquinas: host local acessado via Tailscale, pistas remotas reproduzidas a partir dos inputs, e músicas transferidas do host para quem não as tem.

**Architecture:** Um núcleo C# puro (`Assets/Script/Multiplayer/Core/`, assembly própria sem UnityEngine) contém transporte TCP, protocolo, sincronização de relógio, buffers de input, sessões host/cliente e compartilhamento de músicas; ele é testado fora da Unity com `dotnet test`. Uma camada fina em `Assembly-CSharp` (`Assets/Script/Multiplayer/*.cs` + pequenas alterações em `GameManager`, `BasePlayer`, `YargPlayer`, menus e settings) liga o núcleo ao jogo. Jogadores remotos são `YargPlayer`s em modo "remoto" (parecido com replay), cuja engine consome inputs recebidos pela rede com atraso de ~200 ms.

**Tech Stack:** Unity 6000.3.5f2, C# 9 / .NET Standard 2.1, `YARG.Core`, UniTask, NUnit 4.5.1 (`dotnet test`, net10.0), `System.Net.Sockets`, `System.IO.Compression`.

**Spec:** `docs/superpowers/specs/2026-10-30-online-multiplayer-design.md`

## Global Constraints

- Núcleo (`Assets/Script/Multiplayer/Core/**`): sem `using UnityEngine`; sem `record`/`init` (Unity não tem `IsExternalInit`); C# 9; namespace raiz `YARG.Multiplayer` (sub-namespaces `.Transport`, `.Protocol`, `.Sync`, `.Session`, `.SongShare`).
- Constantes (em `ProtocolInfo`): `Version = 1`, `DefaultPort = 7676`, `MaxMessageSize = 1 MiB`, `MaxChunkSize = 64 KiB`, `MaxSongBytes = 2 GiB`, `MaxMachines = 4`.
- Tempos: lote de inputs a cada 50 ms; `StatsCheckpoint` a cada 1 s; ping a cada 500 ms; offset = média das 5 amostras de menor RTT entre as últimas 20; timeout de par = 5 s; `StartAt` = agora + 3 s; atraso remoto D padrão 200 ms (setting 50–1000 ms); espera de `PlayerSetup` = 15 s; espera de `Finished` no fim = 3 s.
- Extensões permitidas na extração: `.ini .mid .midi .chart .ogg .opus .mp3 .wav .flac .png .jpg .jpeg .webm .mp4 .yarground .txt`.
- Só um jogador local ativo (não `SittingOut`) por máquina em sessão; hospedar/entrar com mais de um mostra erro.
- CON/RBCON (`EntryType.CON`, `EntryType.ExCON`) não podem ser oferecidas; o host recebe um toast de erro.
- Textos visíveis ao jogador via `Localize.Key`/`Localize.KeyFormat` com chaves `Menu.Multiplayer.*`, adicionadas em `Assets/StreamingAssets/lang/en-US.json` e `pt-BR.json`.
- Arquivos `.meta` não são escritos à mão: a Unity os gera ao abrir o projeto; comite-os junto na tarefa seguinte que abrir o editor.
- Não commitar mudanças em `YARG.Core/` (submódulo upstream).

## Review Focus

1. **Input remoto chegando com tempo anterior ao alvo já processado** (input de dispositivo com timestamp atrasado, depois de a marca d'água já ter sido enviada). Esperado: ele ainda é entregue à engine, em ordem, e contado em `LateInputs`; nunca é descartado. Teste em Task 5.
2. **Cliente com relógio do sistema muito diferente do host** (fuso, relógio errado em minutos). Esperado: o início ocorre no instante certo porque só `Stopwatch` monotônico e o offset estimado são usados, nunca `DateTime`. Teste em Task 4.
3. **Música com nome contendo caracteres inválidos para caminho** (`AC/DC: Live?`) ou duas músicas com o mesmo nome. Esperado: pasta de destino sanitizada e única por hash. Teste em Task 7.
4. **Cliente desconecta no meio da transferência ou o host oferece outra música antes de terminar.** Esperado: arquivo parcial apagado, nenhuma pasta meio-extraída na biblioteca. Teste em Task 7 e Task 6.
5. **Cliente tenta entrar com a partida em andamento ou com a sessão cheia.** Esperado: `Reject` com motivo claro, sem afetar a partida. Teste em Task 6.

---

### Task 1: Assembly do núcleo, projeto de teste e `MessageFramer`

**Files:**
- Create: `Assets/Script/Multiplayer/Core/YARG.Multiplayer.Core.asmdef`
- Create: `Assets/Script/Multiplayer/Core/ProtocolInfo.cs`
- Create: `Assets/Script/Multiplayer/Core/Transport/MessageFramer.cs`
- Create: `Tests/YARG.Multiplayer.Tests/YARG.Multiplayer.Tests.csproj`
- Test: `Tests/YARG.Multiplayer.Tests/Transport/MessageFramerTests.cs`

**Interfaces:**
- Produces: `static class ProtocolInfo` (constantes das Global Constraints: `Version`, `DefaultPort`, `MaxMessageSize`, `MaxChunkSize`, `MaxSongBytes`, `MaxMachines`).
- Produces: `sealed class MessageFramer` com `static byte[] Frame(byte[] payload)`, `void Append(ReadOnlySpan<byte> data)`, `bool TryRead(out byte[] message)`.

- [ ] **Step 1: Pré-requisito — instalar o .NET SDK 10**

Run: `dotnet --version`
Expected: `10.x`. Se não existir, instalar (Windows: `winget install --id Microsoft.DotNet.SDK.10 --exact`; Linux: `sudo apt install dotnet-sdk-10.0` ou o script `dotnet-install.sh --channel 10.0`). Os comandos do plano rodam no Git Bash do Windows sem alterações.

- [ ] **Step 2: Criar asmdef e csproj**

`YARG.Multiplayer.Core.asmdef`: `"name": "YARG.Multiplayer.Core"`, `"references": ["YARG.Core.Package"]`, `"noEngineReferences": true`, `"autoReferenced": true`.

`YARG.Multiplayer.Tests.csproj`: `TargetFramework net10.0`, `LangVersion 9`, `Nullable enable`, `AllowUnsafeBlocks true`; pacotes iguais a `YARG.Core/YARG.Core.UnitTests/YARG.Core.UnitTests.csproj` (Microsoft.NET.Test.Sdk 18.3.0, NUnit 4.5.1, NUnit3TestAdapter 6.2.0); `<Compile Include="../../Assets/Script/Multiplayer/Core/**/*.cs" />`; `<ProjectReference Include="../../YARG.Core/YARG.Core/YARG.Core.csproj" />`.

- [ ] **Step 3: Escrever os testes que falham**

```csharp
[Test] public void Frame_PrefixesLittleEndianLength()
    => Assert.That(MessageFramer.Frame(new byte[] { 1, 2, 3 }), Is.EqualTo(new byte[] { 3, 0, 0, 0, 1, 2, 3 }));

[Test] public void TryRead_ReassemblesMessageSplitIntoSingleBytes()
{
    var framed = MessageFramer.Frame(new byte[] { 9, 8, 7 });
    var framer = new MessageFramer();
    for (int i = 0; i < framed.Length - 1; i++)
    {
        framer.Append(framed.AsSpan(i, 1));
        Assert.That(framer.TryRead(out _), Is.False);
    }
    framer.Append(framed.AsSpan(framed.Length - 1, 1));
    Assert.That(framer.TryRead(out var msg), Is.True);
    Assert.That(msg, Is.EqualTo(new byte[] { 9, 8, 7 }));
}

[Test] public void TryRead_SplitsTwoConcatenatedMessages() // Append(Frame(a)+Frame(b)) → a, depois b, depois false
[Test] public void TryRead_AllowsEmptyMessage()             // Frame([]) → TryRead true, mensagem vazia
[Test] public void Append_ThrowsWhenDeclaredLengthExceedsMax()
    // header com ProtocolInfo.MaxMessageSize + 1 → Throws<InvalidDataException>
```

- [ ] **Step 4: Rodar e ver falhar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests`
Expected: erro de compilação (`MessageFramer` não existe).

- [ ] **Step 5: Implementar `ProtocolInfo` e `MessageFramer`**

Buffer interno crescente (`List<byte>` ou `byte[]` + offset); o tamanho é validado assim que os 4 bytes do header estão disponíveis.

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests`
Expected: 5 testes PASS.

- [ ] **Step 7: Commit**

```bash
git add Assets/Script/Multiplayer/Core Tests/YARG.Multiplayer.Tests
git commit -m "Add multiplayer core assembly and message framer"
```

---

### Task 2: Protocolo (mensagens e codec)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Protocol/MessageType.cs`
- Create: `Assets/Script/Multiplayer/Core/Protocol/Messages.cs`
- Create: `Assets/Script/Multiplayer/Core/Protocol/LobbyPlayer.cs`
- Create: `Assets/Script/Multiplayer/Core/Protocol/MessageCodec.cs`
- Create: `Assets/Script/Multiplayer/Core/Protocol/ReplayFrameCodec.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Protocol/MessageCodecTests.cs`

**Interfaces:**
- Produces: `enum MessageType : byte` com um valor por mensagem abaixo, na ordem listada, começando em 1.
- Produces: `abstract class NetMessage { public abstract MessageType Type { get; } }` e uma `sealed class` por mensagem, propriedades get-only atribuídas no construtor:
  - `Hello(ushort ProtocolVersion, string GameVersion, string ProfileName)`
  - `Welcome(int PlayerId)`
  - `Reject(RejectCode Code, string HostGameVersion)` — `enum RejectCode : byte { VersionMismatch, SessionFull, SessionInProgress }`
  - `LobbyState(LobbyPlayer[] Players, SongOffer? Offer)`
  - `SetPart(Instrument Instrument, Difficulty Difficulty)`
  - `Ping(double SentAt)`, `Pong(double PingSentAt, double ReplySentAt)`
  - `SongOffer(HashWrapper Hash, string Name, string Artist, SongPackageKind Kind, long TotalBytes, float SongSpeed)` — `enum SongPackageKind : byte { IniFolderZip, Sng }`
  - `HaveSong(HashWrapper Hash)`, `NeedSong(HashWrapper Hash)`
  - `SongChunk(HashWrapper Hash, long Offset, byte[] Data)`
  - `SongReady(HashWrapper Hash)`, `SongFailed(HashWrapper Hash, string Reason)`
  - `BeginLoad()`, `Loaded()`, `StartAt(double HostTime)`, `ReturnToLobby()`
  - `PlayerSetup(int PlayerId, byte[] Frame)`
  - `InputBatch(int PlayerId, double Watermark, GameInput[] Inputs)`
  - `StatsCheckpoint(int PlayerId, byte[] Frame)`, `Finished(int PlayerId, byte[] Frame)`
  - `Leave(string Reason)`
- Produces: `sealed class LobbyPlayer(int PlayerId, string Name, Instrument Instrument, Difficulty Difficulty, LobbyPlayerStatus Status, byte Progress)` — `enum LobbyPlayerStatus : byte { Idle, MissingSong, Downloading, HasSong, SongFailed, Loaded, Playing, Finished, Left }`; `Instrument`/`Difficulty` de `YARG.Core`.
- Produces: `static class MessageCodec { byte[] Encode(NetMessage m); NetMessage Decode(byte[] data); }` — `Decode` lança `InvalidDataException` para tipo desconhecido, dados truncados, bytes sobrando, `SongChunk.Data` > `MaxChunkSize`, `InputBatch.Inputs` > 10 000 itens ou `LobbyState.Players` > 16.
- Produces: `static class ReplayFrameCodec { byte[] Encode(ReplayFrame f); ReplayFrame Decode(byte[] data); }`.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
// Um teste de ida e volta por mensagem; exemplo representativo:
[Test] public void InputBatch_RoundTrips()
{
    var inputs = new[] { new GameInput(1.25, 3, 1), new GameInput(1.5, 4, -7) };
    var decoded = (InputBatch) MessageCodec.Decode(MessageCodec.Encode(new InputBatch(2, 1.6, inputs)));
    Assert.That(decoded.PlayerId, Is.EqualTo(2));
    Assert.That(decoded.Watermark, Is.EqualTo(1.6));
    Assert.That(decoded.Inputs.Select(i => (i.Time, i.Action, i.Integer)),
        Is.EqualTo(inputs.Select(i => (i.Time, i.Action, i.Integer))));
}
[Test] public void SongOffer_RoundTripsHashAndFields()   // HashWrapper.Hash(new byte[]{1,2,3}) igual após decode, TotalBytes = 5_000_000_000
[Test] public void LobbyState_RoundTripsWithAndWithoutOffer()
[Test] public void Decode_ThrowsOnUnknownType()           // new byte[] { 255 }
[Test] public void Decode_ThrowsOnTruncatedData()         // Encode(Hello) sem o último byte
[Test] public void Decode_ThrowsOnTrailingBytes()         // Encode(Loaded) + {0}
[Test] public void Decode_ThrowsOnOversizedChunk()        // SongChunk com MaxChunkSize + 1 bytes
[Test] public void ReplayFrameCodec_RoundTripsProfileAndStats()
    // ReplayFrame(new YargProfile { Name = "Ana", GameMode = GameMode.FiveFretGuitar },
    //   params do preset de guitarra de EnginePreset.Default (Create em EnginePreset.Instruments.cs:144),
    //   new GuitarStats { CommittedScore = 1234 }, Array.Empty<GameInput>())
    // → Profile.Name == "Ana", Stats.CommittedScore == 1234
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests --filter MessageCodecTests`
Expected: erro de compilação.

- [ ] **Step 3: Implementar mensagens e codec**

`BinaryWriter`/`BinaryReader` sobre `MemoryStream`; primeiro byte = `MessageType`. `GameInput` como `double Time, int Action, int Integer` (igual a `ReplayFrame.Serialize`). `HashWrapper` com `hash.Serialize(writer)` / `HashWrapper.Deserialize(reader.BaseStream)`. `EndOfStreamException` é convertida em `InvalidDataException`. `ReplayFrameCodec.Decode`: `FixedArray.Read(new MemoryStream(data), data.Length)` → `ToValueStream()` → `new ReplayFrame(ref stream, ReplayIO.REPLAY_VERSIONS.CURRENT)`, liberando o `FixedArray` no fim.

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests --filter MessageCodecTests`
Expected: todos PASS.

- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/Protocol Tests/YARG.Multiplayer.Tests/Protocol
git commit -m "Add multiplayer protocol messages and codec"
```

---

### Task 3: Transportes (TCP, em memória, com lag)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Transport/INetTransport.cs`
- Create: `Assets/Script/Multiplayer/Core/Transport/TcpTransport.cs`
- Create: `Assets/Script/Multiplayer/Core/Transport/InMemoryNetwork.cs`
- Create: `Assets/Script/Multiplayer/Core/Transport/LaggyTransport.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Transport/TransportTests.cs`

**Interfaces:**
- Consumes: `MessageFramer` (Task 1).
- Produces:

```csharp
public interface INetEventListener
{
    void OnPeerConnected(int peerId);
    void OnPeerDisconnected(int peerId, string reason);
    void OnMessage(int peerId, byte[] message);
}

public interface INetTransport : IDisposable
{
    void StartHost(int port);               // peerIds de clientes começam em 1
    void Connect(string host, int port);    // no cliente, o host é sempre peerId 0
    void Send(int peerId, byte[] message);  // mensagem inteira (sem framing; o transporte faz)
    void Disconnect(int peerId);
    void Poll(INetEventListener listener);  // entrega eventos enfileirados na thread chamadora
}
```

- `TcpTransport : INetTransport` — `TcpListener`/`TcpClient`, `NoDelay = true`, uma thread de leitura por conexão, eventos numa `ConcurrentQueue`; mensagem maior que `MaxMessageSize` derruba a conexão com motivo `"message too large"`.
- `InMemoryNetwork` — `INetTransport CreateHost()`, `INetTransport CreateClient()`; `Connect` em um cliente liga ao host criado na mesma rede (host e porta ignorados). Entrega somente em `Poll`.
- `LaggyTransport(INetTransport inner, Func<double> clock, double latencySeconds, double jitterSeconds, int seed) : INetTransport` — atrasa a entrega de mensagens recebidas; `deliverAt = max(deliverAtAnterior, agora + latência + jitter aleatório)` por par, preservando a ordem (semântica TCP).

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Test] public void Tcp_LoopbackDeliversMessagesInOrder()
    // host StartHost(0 → porta livre; expor int LocalPort), cliente Connect("127.0.0.1", LocalPort),
    // cliente envia 100 mensagens de 1..60 KiB; host Poll até receber todas (timeout 5 s);
    // conteúdo e ordem iguais; OnPeerConnected(1) no host antes da primeira mensagem
[Test] public void Tcp_ClientDisconnectRaisesPeerDisconnected()
[Test] public void Tcp_OversizedMessageDropsConnection()
    // escrever no socket cru um header de MaxMessageSize + 1 → host recebe OnPeerDisconnected(1, "message too large")
[Test] public void InMemory_RoutesBetweenHostAndTwoClients()
[Test] public void Laggy_DelaysDeliveryAndPreservesOrder()
    // clock manual; latency 0.2, jitter 0.1; 50 mensagens; nada entregue antes de t = 0.2;
    // todas entregues até t = 0.3 + 50 * epsilon; ordem preservada
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests --filter TransportTests`
Expected: erro de compilação.

- [ ] **Step 3: Implementar os três transportes**

`TcpTransport` expõe também `int LocalPort { get; }` (porta efetiva após `StartHost(0)`).

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test Tests/YARG.Multiplayer.Tests --filter TransportTests`
Expected: todos PASS.

- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/Transport Tests/YARG.Multiplayer.Tests/Transport
git commit -m "Add TCP, in-memory and laggy multiplayer transports"
```

---

### Task 4: `ClockSync`

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Sync/ClockSync.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Sync/ClockSyncTests.cs`

**Interfaces:**
- Produces: `sealed class ClockSync { const int WindowSize = 20; const int BestSamples = 5; void AddSample(double pingSentLocal, double hostReplyTime, double pongReceivedLocal); bool HasEstimate { get; } double OffsetSeconds { get; } double HostToLocal(double hostTime); }`
  - amostra: `rtt = recebido − enviado`, `offset = hostReplyTime − (enviado + recebido) / 2`; `OffsetSeconds` = média dos `BestSamples` offsets de menor RTT entre as últimas `WindowSize`; `HasEstimate` verdadeiro a partir da primeira amostra; `HostToLocal(t) = t − OffsetSeconds`.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Test] public void SymmetricLatency_RecoversExactOffset()
    // relógio do host = local + 1000.0 (Review Focus 2); RTT 0.04 simétrico → OffsetSeconds ≈ 1000.0 (±1e-9)
[Test] public void HighRttOutliers_AreIgnored()
    // 15 amostras RTT 0.02 com offset 5.0; 5 amostras RTT 0.5 assimétricas com offset 5.2 → OffsetSeconds ≈ 5.0 (±0.001)
[Test] public void OnlyLast20SamplesCount()
    // 20 amostras offset 1.0, depois 20 amostras offset 2.0 → ≈ 2.0
[Test] public void HostToLocal_SubtractsOffset() // offset 3.0 → HostToLocal(10.0) == 7.0
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test Tests/YARG.Multiplayer.Tests --filter ClockSyncTests` → erro de compilação.
- [ ] **Step 3: Implementar `ClockSync`.**
- [ ] **Step 4: Rodar e ver passar** — mesmo comando → 4 PASS.
- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/Sync/ClockSync.cs Tests/YARG.Multiplayer.Tests/Sync/ClockSyncTests.cs
git commit -m "Add multiplayer clock sync"
```

---

### Task 5: `InputBatcher`, `RemoteInputBuffer` e teste de fidelidade

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Sync/InputBatcher.cs`
- Create: `Assets/Script/Multiplayer/Core/Sync/RemoteInputBuffer.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Sync/RemoteInputTests.cs`

**Interfaces:**
- Consumes: `InputBatch` (Task 2), `MessageCodec` (Task 2), `InMemoryNetwork`/`LaggyTransport` (Task 3).
- Produces: `sealed class InputBatcher { const double IntervalSeconds = 0.05; InputBatcher(int playerId); void Add(GameInput input); void SetWatermark(double engineTime); InputBatch? TryFlush(double now); }` — `TryFlush` retorna um lote (mesmo vazio) quando `now − últimoFlush ≥ IntervalSeconds`, senão `null`; o primeiro `TryFlush` sempre retorna lote.
- Produces: `sealed class RemoteInputBuffer { RemoteInputBuffer(double delaySeconds); double DelaySeconds { get; } double Watermark { get; } int LateInputs { get; } void AddBatch(InputBatch batch); double GetTargetTime(double localTime); bool TryDequeue(double upToTime, out GameInput input); }`
  - `Watermark` começa em `double.NegativeInfinity` e só aumenta.
  - `AddBatch` insere mantendo ordem por `Time` (estável: empate mantém ordem de chegada). Input com `Time` menor que o último `upToTime` já usado em `TryDequeue` incrementa `LateInputs` e vai para a frente da fila (Review Focus 1).
  - `GetTargetTime(t) = min(t − DelaySeconds, Watermark)`, e nunca menor que o alvo retornado anteriormente.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Test] public void Batcher_FlushesEvery50msEvenWhenEmpty()
    // TryFlush(0) → lote vazio; TryFlush(0.03) → null; TryFlush(0.05) → lote vazio com Watermark atual
[Test] public void Buffer_TargetNeverPassesWatermark()
    // AddBatch(watermark 1.0); GetTargetTime(5.0) == 1.0; GetTargetTime(1.1) == 0.9 → retorna 1.0 (monotônico)
[Test] public void Buffer_DequeuesOnlyInputsUpToTarget()
[Test] public void Buffer_LateInputIsStillDeliveredAndCounted()
    // TryDequeue(2.0) esvazia; AddBatch com input Time 1.9 → LateInputs == 1; TryDequeue(2.0) devolve esse input
[Test] public void Fidelity_InputsSurviveJitteryNetworkUnchanged()
    // 2000 inputs sintéticos (Random seed 42, tempos crescentes em 0..60 s, actions 0..10);
    // emissor: a cada passo de 1/60 s adiciona os inputs com Time ≤ t, SetWatermark(t), TryFlush(t) → Encode → Send
    // via LaggyTransport(latency 0.08, jitter 0.12) sobre InMemoryNetwork;
    // receptor: Poll → Decode → AddBatch; target = GetTargetTime(t); drena TryDequeue(target)
    // Asserções: sequência recebida == sequência enviada (Time, Action, Integer);
    // todo input saiu com input.Time ≤ target daquele passo; LateInputs == 0
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test Tests/YARG.Multiplayer.Tests --filter RemoteInputTests` → erro de compilação.
- [ ] **Step 3: Implementar `InputBatcher` e `RemoteInputBuffer`.**
- [ ] **Step 4: Rodar e ver passar** — mesmo comando → 5 PASS.
- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/Sync Tests/YARG.Multiplayer.Tests/Sync/RemoteInputTests.cs
git commit -m "Add input batching and remote input buffer"
```

---

### Task 6: Sessões host e cliente

**Files:**
- Create: `Assets/Script/Multiplayer/Core/Session/SessionState.cs`
- Create: `Assets/Script/Multiplayer/Core/Session/SessionConfig.cs`
- Create: `Assets/Script/Multiplayer/Core/Session/IMultiplayerSession.cs`
- Create: `Assets/Script/Multiplayer/Core/Session/HostSession.cs`
- Create: `Assets/Script/Multiplayer/Core/Session/ClientSession.cs`
- Test: `Tests/YARG.Multiplayer.Tests/Session/SessionTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4.
- Produces:

```csharp
public enum SessionState { Lobby, Transferring, Loading, Playing, Results, Ended }

public sealed class SessionConfig
{
    public SessionConfig(string gameVersion, string localProfileName,
        double timeoutSeconds = 5, double pingIntervalSeconds = 0.5, int chunksPerTick = 16,
        Action<string>? log = null);
    // propriedades get-only correspondentes
}

public interface IMultiplayerSession : IDisposable
{
    bool IsHost { get; }
    int LocalPlayerId { get; }                 // host = 0
    SessionState State { get; }
    IReadOnlyList<LobbyPlayer> Players { get; }
    SongOffer? CurrentOffer { get; }
    double? StartTimeLocal { get; }            // relógio local (segundos) em que a música começa
    event Action LobbyChanged;
    event Action<NetMessage> GameMessageReceived;  // PlayerSetup / InputBatch / StatsCheckpoint / Finished de outros
    event Action<string> Ended;                    // motivo
    event Action BeginLoadRequested;
    event Action ReturnToLobbyRequested;
    void Tick();                               // Poll do transporte, pings, timeouts, envio de chunks
    void SetLocalPart(Instrument instrument, Difficulty difficulty);
    void SendGameMessage(NetMessage message);  // só os 4 tipos de jogo; host faz broadcast, cliente envia ao host
    void ReportLoaded();
    void Leave();
}

public sealed class HostSession : IMultiplayerSession
{
    public HostSession(INetTransport transport, SessionConfig config, Func<double> clock);
    public void Start(int port);
    public void OfferSong(SongOffer offer, Func<Stream> openPackage); // Lobby/Results → Transferring
    public bool CanBeginLoad { get; }  // todos os não-Left estão HasSong e têm parte definida
    public void BeginLoad();           // InvalidOperationException se !CanBeginLoad; → Loading
    public void ReturnToLobby();       // Results → Lobby, status de todos → Idle
}

public sealed class ClientSession : IMultiplayerSession
{
    public ClientSession(INetTransport transport, SessionConfig config, Func<double> clock);
    public void Connect(string host, int port);
    public event Action<SongOffer> SongOffered;
    public event Action<SongChunk> SongChunkReceived;
    public void RespondToOffer(bool haveSong);
    public void ReportSongReady();
    public void ReportSongFailed(string reason);
}
```

Regras que os testes fixam:
- Host aceita `Hello` só em `Lobby`/`Results`; senão `Reject(SessionInProgress)`. Mais de `MaxMachines` máquinas → `Reject(SessionFull)`. `ProtocolVersion` ou `GameVersion` diferentes → `Reject(VersionMismatch, hostVersion)`.
- Host transmite `LobbyState` a cada mudança. Progresso de download = bytes enviados / `TotalBytes`, calculado no host.
- `OfferSong` durante uma transferência cancela os envios anteriores (Review Focus 4).
- `NeedSong` de um par que já recebeu (ou está recebendo) a música reinicia a transferência para ele a partir do offset 0 — é assim que o cliente pede a segunda tentativa.
- Quando todos os não-Left mandam `Loaded`, o host envia `StartAt(clock() + 3)` e passa a `Playing`; o cliente define `StartTimeLocal = clockSync.HostToLocal(hostTime)`; no host, `StartTimeLocal = hostTime`.
- Mensagens de jogo de um cliente são retransmitidas a todos os outros (nunca ecoadas ao remetente) e disparam `GameMessageReceived` no host.
- Par sem mensagens por `timeoutSeconds` → desconectado; seu `LobbyPlayer.Status = Left`. Queda do host → `Ended` no cliente.
- Quando todos os não-Left mandaram `Finished`, o host vai para `Results`.

- [ ] **Step 1: Escrever os testes que falham** (todos sobre `InMemoryNetwork` com relógio manual)

```csharp
[Test] public void Join_AssignsIdsAndAllSeeThreePlayers()          // host + 2 clientes → ids {0,1,2} em todas as Players
[Test] public void Join_RejectsVersionMismatch()                   // cliente "v0.14" vs host "v0.15" → Ended no cliente; Reject.HostGameVersion == "v0.15"
[Test] public void Join_RejectsFifthMachine()                      // SessionFull (Review Focus 5)
[Test] public void Join_RejectsWhilePlaying()                      // SessionInProgress (Review Focus 5)
[Test] public void SilentClient_IsMarkedLeftAfterTimeout()         // avança 5.1 s sem Tick do cliente → Status Left
[Test] public void HostDisconnect_EndsClientSession()
[Test] public void Offer_ClientWithSong_GoesStraightToHasSong()
[Test] public void Offer_ClientWithoutSong_ReceivesAllChunksAndProgress()
    // pacote de 1 MiB + 1 byte; chunks ≤ MaxChunkSize, offsets contíguos, soma == TotalBytes;
    // Progress chega a 100; após ReportSongReady → HasSong
[Test] public void Offer_NewOfferCancelsPreviousTransfer()         // nenhum chunk do hash antigo após o novo OfferSong
[Test] public void BeginLoad_RequiresAllPlayersReady()             // CanBeginLoad false até SetPart + HasSong de todos
[Test] public void AllLoaded_SchedulesStartThreeSecondsAhead()
    // host clock = 100, cliente clock = host − 40 → StartTimeLocal do cliente ≈ 63.0 (após pings)
[Test] public void GameMessages_RelayToOthersButNotSender()
[Test] public void AllFinished_MovesHostToResults_ThenReturnToLobbyResetsStatuses()
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test Tests/YARG.Multiplayer.Tests --filter SessionTests` → erro de compilação.
- [ ] **Step 3: Implementar `HostSession` e `ClientSession`** — cada uma implementa `INetEventListener` internamente e chama `transport.Poll(this)` em `Tick()`; `MessageCodec.Decode` com `InvalidDataException` → `Disconnect` do par e motivo enviado a `SessionConfig.Log`.
- [ ] **Step 4: Rodar e ver passar** — mesmo comando → todos PASS.
- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/Session Tests/YARG.Multiplayer.Tests/Session
git commit -m "Add multiplayer host and client sessions"
```

---

### Task 7: Compartilhamento de músicas (empacotar, receber, extrair com segurança)

**Files:**
- Create: `Assets/Script/Multiplayer/Core/SongShare/SongPackage.cs`
- Create: `Assets/Script/Multiplayer/Core/SongShare/SongPackager.cs`
- Create: `Assets/Script/Multiplayer/Core/SongShare/SongReceiver.cs`
- Create: `Assets/Script/Multiplayer/Core/SongShare/SafeExtractor.cs`
- Create: `Assets/Script/Multiplayer/Core/SongShare/SongShareNaming.cs`
- Test: `Tests/YARG.Multiplayer.Tests/SongShare/SongShareTests.cs`

**Interfaces:**
- Consumes: `SongOffer`, `SongChunk`, `SongPackageKind` (Task 2).
- Produces:
  - `sealed class SongPackage : IDisposable { SongPackageKind Kind; long Length; Stream Open(); }` — para zip temporário, `Dispose` apaga o arquivo.
  - `static class SongPackager { SongPackage FromIniFolder(string folder, string tempDir); SongPackage FromSng(string sngPath); }` — zip com `CompressionLevel.Fastest`, entradas com caminho relativo à pasta da música e `/` como separador.
  - `sealed class SongReceiver : IDisposable { SongReceiver(SongOffer offer, string stagingDir); long BytesWritten; bool IsComplete; void Write(SongChunk chunk); string Install(string destinationRoot); void Abort(); }` — `Write` exige `chunk.Offset == BytesWritten` (senão `InvalidDataException`) e ignora chunks de outro hash; `Install` devolve o caminho final; `Abort`/`Dispose` sem `Install` apagam o parcial.
  - `static class SafeExtractor { void Extract(Stream zip, string destinationDir, long maxTotalBytes); }` — valida **todas** as entradas antes de escrever qualquer arquivo; lança `UnsafeArchiveException` (nova, no mesmo arquivo) para `..`, caminho absoluto/rooted, extensão fora da lista das Global Constraints ou soma de `Length` > `maxTotalBytes`; extrai para `destinationDir + ".partial"` e renomeia ao final.
  - `static class SongShareNaming { string EntryName(string artist, string name, HashWrapper hash); }` — `"{artist} - {name} [{8 primeiros hex do hash}]"`, caracteres de `Path.GetInvalidFileNameChars()` mais `/\:*?"<>|` trocados por `_`, máximo 120 caracteres antes do sufixo. `.sng` instala como `<EntryName>.sng`; pasta ini instala como `<EntryName>/`.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Test] public void IniFolder_PackThenReceiveThenInstall_ReproducesFiles()
    // pasta temp com song.ini, notes.mid, song.ogg (bytes aleatórios) → SongPackager → chunks de 64 KiB → SongReceiver → Install
    // → mesma árvore e mesmos bytes em <root>/<EntryName>/
[Test] public void Sng_InstallsAsSingleFile()
[Test] public void Receiver_RejectsOutOfOrderChunk()
[Test] public void Receiver_AbortDeletesPartialFile()                       // Review Focus 4
[Test] public void Extractor_RejectsParentTraversal()                       // entrada "../evil.ini" → UnsafeArchiveException, nada criado
[Test] public void Extractor_RejectsAbsolutePath()                          // "/tmp/x.ini" e "C:/x.ini"
[Test] public void Extractor_RejectsDisallowedExtension()                   // "run.exe"
[Test] public void Extractor_RejectsOversizedArchive()                      // maxTotalBytes 10, entrada de 11 bytes
[Test] public void Naming_SanitizesAndDisambiguatesByHash()                 // Review Focus 3
    // EntryName("AC/DC", "Live?", h1) não contém '/' nem '?'; mesmo nome com h2 ≠ resultado com h1
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test Tests/YARG.Multiplayer.Tests --filter SongShareTests` → erro de compilação.
- [ ] **Step 3: Implementar as cinco classes.**
- [ ] **Step 4: Rodar e ver passar** — mesmo comando → 9 PASS; depois `dotnet test Tests/YARG.Multiplayer.Tests` → suíte inteira PASS.
- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Multiplayer/Core/SongShare Tests/YARG.Multiplayer.Tests/SongShare
git commit -m "Add song packaging, transfer and safe extraction"
```

---

### Task 8: Integração Unity — settings, `MultiplayerManager` e biblioteca de músicas

A partir daqui o código é verificado no editor da Unity (não há Test Runner instalado). Abrir o projeto e confirmar que compila sem erros no Console é a verificação mínima de cada tarefa. Editor necessário: Unity 6000.3.5f2 (`ProjectSettings/ProjectVersion.txt`), instalado pelo Unity Hub.

**Files:**
- Modify: `Assets/Script/Settings/SettingsManager.Settings.cs` (junto a `SongFolders` e `OpenVenueFolder`)
- Modify: `Assets/Script/Settings/SettingsManager.cs` (aba geral, depois do bloco `HeaderMetadata("Other")`)
- Modify: `Assets/Script/Song/SongContainer.cs:144-149` (lista `directories` de `RunRefresh`)
- Create: `Assets/Script/Multiplayer/MultiplayerManager.cs`
- Create: `Assets/Script/Multiplayer/MultiplayerSongLibrary.cs`
- Modify: `Assets/StreamingAssets/lang/en-US.json`, `Assets/StreamingAssets/lang/pt-BR.json`

**Interfaces:**
- Consumes: `HostSession`, `ClientSession`, `TcpTransport`, `SongPackager`, `SongReceiver`, `ProtocolInfo` (Tasks 1–7).
- Produces (settings): `ToggleSetting SaveMultiplayerSongs = new(true)`; `string MultiplayerSongsFolder = string.Empty` (campo oculto, como `SongFolders`); `IntSetting MultiplayerRemoteDelayMs = new(200, 50, 1000)`; `void ChooseMultiplayerSongsFolder()` (usa `FileExplorerHelper.OpenChooseFolder`). Na aba: `new HeaderMetadata("Multiplayer")`, `nameof(SaveMultiplayerSongs)`, `new ButtonRowMetadata(nameof(ChooseMultiplayerSongsFolder))`, `new FieldMetadata(nameof(MultiplayerRemoteDelayMs), isAdvanced: true)`.
- Produces:

```csharp
public static class MultiplayerSongLibrary
{
    public static string DownloadRoot { get; }       // Save ligado: MultiplayerSongsFolder ou <PersistentDataPath>/Multiplayer Songs; desligado: TempRoot
    public static string TempRoot { get; }           // <PersistentDataPath>/Multiplayer Temp
    public static IEnumerable<string> ExtraScanDirectories { get; } // DownloadRoot (se existir) + TempRoot (se existir)
    public static void CleanupTemp();                // apaga TempRoot
    public static SongEntry? FindByHash(HashWrapper hash);          // SongContainer.SongsByHash
    public static SongPackage? CreatePackage(SongEntry song);       // Ini → FromIniFolder(ActualLocation), Sng → FromSng; CON/ExCON → null
    public static UniTask<SongEntry?> InstallAndRegister(SongReceiver receiver, SongOffer offer);
        // Install(DownloadRoot) → await SongContainer.RunRefresh(quick: false) → FindByHash(offer.Hash)
}

public sealed class MultiplayerManager : MonoBehaviour   // DontDestroyOnLoad, criado sob demanda
{
    public static MultiplayerManager Instance { get; }
    public static bool IsActive { get; }      // Session != null
    public static bool IsHosting { get; }
    public IMultiplayerSession? Session { get; }
    public double Now { get; }                // Stopwatch.Elapsed.TotalSeconds
    public YargPlayer? LocalPlayer { get; }   // único jogador ativo de PlayerContainer.Players
    public void Host(int port);               // ProtocolInfo.DefaultPort por padrão
    public void Join(string address);         // "ip" ou "ip:porta"
    public void Leave();
    public void OfferSong(SongEntry song, float songSpeed);   // chamado pelo host após o DifficultySelect
}
```

Comportamento do `MultiplayerManager` (fluxo do cliente, todo aqui): `SongOffered` → se `FindByHash` → `RespondToOffer(true)`; senão `TotalBytes > MaxSongBytes` → `ReportSongFailed`; senão `RespondToOffer(false)` e cria `SongReceiver(offer, TempRoot)`, alimentado por `SongChunkReceived`; ao completar → `InstallAndRegister`; se o resultado for `null` (hash não confere), tenta mais uma vez pedindo a música de novo; na segunda falha, `ReportSongFailed`. `BeginLoadRequested` → `GlobalVariables.State.CurrentSong = entry`, `SongSpeed = offer.SongSpeed`, `PlayingWithReplay = false`, `GlobalVariables.Instance.LoadScene(SceneIndex.Gameplay)`. `Ended` → toast de erro com o motivo, `CleanupTemp()` se Save desligado, `LoadScene(SceneIndex.Menu)` se estiver no gameplay. `Update()` chama `Session.Tick()`. `CleanupTemp()` também roda na primeira criação do manager.

- [ ] **Step 1: Adicionar settings, aba e strings de localização** (`Name`/`Description` seguindo o formato de `NoFail`; texto do botão seguindo `OpenVenueFolder`).
- [ ] **Step 2: Adicionar `MultiplayerSongLibrary.ExtraScanDirectories` às `directories` de `SongContainer.RunRefresh`**, sem duplicar entradas.
- [ ] **Step 3: Implementar `MultiplayerSongLibrary` e `MultiplayerManager`.**
- [ ] **Step 4: Verificar no editor**

Abrir o projeto na Unity. Expected: Console sem erros de compilação; em Settings → General aparece o cabeçalho "Multiplayer" com o toggle e o botão de pasta; o botão abre o seletor de pasta e o valor persiste após reiniciar.

- [ ] **Step 5: Commit** (inclui os `.meta` gerados nesta e nas tarefas anteriores)

```bash
git add Assets/Script/Multiplayer Assets/Script/Settings Assets/Script/Song/SongContainer.cs Assets/StreamingAssets/lang/en-US.json Assets/StreamingAssets/lang/pt-BR.json
git commit -m "Add multiplayer manager, settings and song library glue"
```

---

### Task 9: Integração Unity — jogadores remotos no gameplay

**Files:**
- Modify: `Assets/Script/Player/YargPlayer.cs` (novo construtor ao lado do construtor de replay, linhas 60–80)
- Modify: `Assets/Script/Gameplay/Player/BasePlayer.cs` (`:136`, `:193-196`, `UpdateInputs` em `:249`, gravação de input em `:366`)
- Modify: `Assets/Script/Gameplay/GameManager.Loading.cs` (`Start()` em `:106`; `CreatePlayers()` em `:402`; inscrição de `OnSongFailed` em `:253`; `enabled = true` em `:270`)
- Modify: `Assets/Script/Gameplay/GameManager.cs` (`Pause` em `:371`; gravação de score em `:735`; `ScoreScreenStats` em `:617`; `OnSongFailed`)
- Create: `Assets/Script/Multiplayer/MultiplayerGameplay.cs`

**Interfaces:**
- Consumes: `MultiplayerManager` (Task 8), `InputBatcher`, `RemoteInputBuffer` (Task 5), `ReplayFrameCodec`, mensagens de jogo (Task 2).
- Produces em `YargPlayer`: `bool IsRemote { get; }`, `int RemotePlayerId { get; }`, `RemoteInputBuffer? RemoteInputs { get; }`, construtor `YargPlayer(ReplayFrame setup, int remotePlayerId, RemoteInputBuffer buffer)` — mesma resolução de presets do construtor de replay, sem `ReplayData` (cai em `CustomContentManager` e depois `Default`); `IsReplay = true`, `IsRemote = true`, `EngineParameterOverride = setup.EngineParameters`.
- Produces:

```csharp
public static class MultiplayerGameplay
{
    public static void OnLocalInput(BasePlayer player, GameInput input);      // → InputBatcher
    public static void OnLocalEngineTime(BasePlayer player, double time);     // → SetWatermark + TryFlush + checkpoint a cada 1 s
    public static void SendSetup(BasePlayer localPlayer);                     // PlayerSetup com ConstructReplayData().Frame
    public static UniTask<List<YargPlayer>> AwaitRemotePlayers(double timeoutSeconds); // 15 s; quem não chegou fica Left
    public static UniTask WaitForStart();                                     // ReportLoaded + espera Now ≥ StartTimeLocal
    public static void SendFinished(BasePlayer localPlayer);
    public static UniTask<IReadOnlyDictionary<int, ReplayFrame>> AwaitFinishedFrames(double timeoutSeconds); // 3 s; cai no último checkpoint
}
```

Mudanças, em ordem:
1. **`BasePlayer`**:
   - depois de `_replayInputs.Add(input)` (`:366`): `if (MultiplayerManager.IsActive && !Player.IsRemote) MultiplayerGameplay.OnLocalInput(this, input);`
   - `UpdateInputs`: se `Player.IsRemote`, **sem** somar `InputCalibration` (os tempos já incluem a calibração do remetente): `target = Player.RemoteInputs.GetTargetTime(time)`; drenar `TryDequeue(target)` com `QueueInput` + `OnInputQueued` + `_replayInputs.Add`; `BaseEngine.Update(target)`; guardar `RemoteLagSeconds = time − target`; `return`. No caminho local, depois de `BaseEngine.Update(time)`, chamar `MultiplayerGameplay.OnLocalEngineTime(this, time)` quando ativo.
   - chamadas de `UpdateVisuals(GameManager.VisualTime)` (`:136`, `:196`): para remotos, usar `GameManager.VisualTime − RemoteLagSeconds`. Vocais remotos ficam no tempo local, porque a `VocalTrack` é compartilhada (limitação aceita no MVP).
2. **`GameManager.Loading`**:
   - extrair o corpo do `foreach` de `CreatePlayers()` para `CreatePlayer(YargPlayer player)`, com `index`, `highwayIndex`, `vocalIndex` e `vocalTrackInitialized` virando campos, para permitir uma segunda chamada;
   - em `Start()`, com multiplayer ativo: `YargPlayers` = só o jogador local; `CreatePlayers()`; `SendSetup`; `await AwaitRemotePlayers(15)`; `CreatePlayer` para cada remoto; `YargPlayers` = local + remotos; só então calcular `StarScoreThresholds` (`:204`);
   - não inscrever `OnSongFailed` com multiplayer ativo;
   - antes de `enabled = true` (`:270`): `await MultiplayerGameplay.WaitForStart()`.
3. **`GameManager`**:
   - `Pause()` com multiplayer ativo: não pausa. O primeiro aperto mostra o toast `Menu.Multiplayer.PressAgainToLeave`; um segundo aperto em até 1 s chama `MultiplayerManager.Instance.Leave()` e volta ao menu;
   - fim da música: `SendFinished` para o jogador local e `await AwaitFinishedFrames(3)`. Ao montar `ScoreScreenStats`, os remotos usam `frame.Stats` recebido;
   - `ScoreContainer.RecordScore`: pular jogadores com `Player.IsRemote`.
4. **Remoto que sai no meio da música** (`Status = Left` em `LobbyChanged`): a marca d'água dele para, então a pista congela sem gerar erros (não derruba a banda); nos resultados entram os stats do último `StatsCheckpoint`.

- [ ] **Step 1: `YargPlayer` remoto e ganchos do `BasePlayer`.**
- [ ] **Step 2: Carregamento em duas fases e espera do start no `GameManager.Loading`.**
- [ ] **Step 3: Pausa, fail, resultados e scores no `GameManager`.**
- [ ] **Step 4: Implementar `MultiplayerGameplay`.**
- [ ] **Step 5: Verificar regressão offline no editor**

Expected: Quick Play solo, multiplayer local com 2 perfis, replay e "tocar com replay" funcionam como antes. Nenhum caminho novo roda sem `MultiplayerManager.IsActive`.

- [ ] **Step 6: Commit**

```bash
git add Assets/Script/Player/YargPlayer.cs Assets/Script/Gameplay Assets/Script/Multiplayer/MultiplayerGameplay.cs
git commit -m "Add remote players to gameplay"
```

---

### Task 10: Menus — hospedar, entrar, lobby e escolha de parte

**Files:**
- Create: `Assets/Script/Multiplayer/MultiplayerMenuFlow.cs`
- Modify: `Assets/Script/Menu/Main/MainMenu.cs` (novo `public void Multiplayer()` ao lado de `Replays()`)
- Modify: `Assets/Script/Menu/DifficultySelect/DifficultySelectMenu.cs:557`
- Modify: `Assets/StreamingAssets/lang/en-US.json`, `pt-BR.json`
- Manual (editor Unity): botão novo no prefab/cena do menu principal ligado a `MainMenu.Multiplayer`

**Interfaces:**
- Consumes: `MultiplayerManager` (Task 8), `DialogManager.Instance.ShowList`/`ShowRenameDialog`/`ShowMessage`/`ClearDialog`, `ListDialog.AddListButton`, `ToastManager`.
- Produces: `static class MultiplayerMenuFlow { void OpenEntry(); void OpenLobby(); void OpenPartPicker(SongEntry song); }`
  - `OpenEntry`: lista com "Hospedar" e "Entrar". "Entrar" abre `ShowRenameDialog` pedindo `IP:porta` e chama `Join`. Com mais de um jogador local ativo, mostra `ShowMessage` de erro e não continua.
  - `OpenLobby`: `ShowList` com uma linha por `LobbyPlayer` (`nome — instrumento dificuldade — status/progresso%`). Para o host, também os botões "Escolher música" (abre a biblioteca de músicas normal) e "Começar" (só faz algo com `CanBeginLoad`; senão mostra toast). Mais "Sair" para todos. É reconstruída em `LobbyChanged` no máximo 4×/s (`ClearDialog` + reabrir).
  - `OpenPartPicker`: lista os pares (instrumento, dificuldade) em que `song[instrument][difficulty]` é verdadeiro. A escolha grava `LocalPlayer.Profile.CurrentInstrument`/`CurrentDifficulty` e chama `SetLocalPart`.
- `DifficultySelectMenu:557`: `if (MultiplayerManager.IsHosting) { MultiplayerManager.Instance.OfferSong(GlobalVariables.State.CurrentSong, speed); MultiplayerMenuFlow.OpenLobby(); return; }` no lugar do `LoadScene`. A parte do host vem da própria tela de dificuldade e é enviada com `SetLocalPart`.

- [ ] **Step 1: Implementar `MultiplayerMenuFlow`, `MainMenu.Multiplayer()` e o desvio no `DifficultySelectMenu`.**
- [ ] **Step 2: Adicionar o botão no menu principal pelo editor da Unity** e ligá-lo a `MainMenu.Multiplayer`, com rótulo localizado `Menu.Multiplayer.Title`.
- [ ] **Step 3: Verificar no editor**

Expected: o botão abre Hospedar/Entrar. Hospedar mostra o lobby com o host. Entrar com `127.0.0.1:7676` numa segunda instância (build) mostra os dois jogadores nas duas telas.

- [ ] **Step 4: Commit**

```bash
git add Assets/Script/Multiplayer/MultiplayerMenuFlow.cs Assets/Script/Menu Assets/StreamingAssets/lang Assets/Scenes Assets/Prefabs
git commit -m "Add multiplayer menus and lobby"
```

---

### Task 11: Verificação ponta a ponta

**Files:**
- Create: `docs/superpowers/plans/2026-10-30-online-multiplayer-checklist.md` (resultado de cada item, com data)

- [ ] **Step 1: Suíte do núcleo** — `dotnet test Tests/YARG.Multiplayer.Tests` → todos PASS.
- [ ] **Step 2: Duas instâncias em `localhost`** (editor + build, ou dois builds). Checklist:
  1. Entrar no lobby; versão diferente é recusada com mensagem.
  2. Host escolhe uma música que o cliente já tem → vai direto para "tem a música".
  3. Host escolhe uma pasta ini que o cliente não tem → progresso até 100% e a música aparece na biblioteca do cliente.
  4. Mesmo teste com um `.sng`.
  5. Música CON → toast de erro no host.
  6. Começar → as duas músicas iniciam juntas (diferença perceptível < 50 ms).
  7. A pista remota mostra os acertos e erros reais, ~200 ms atrasada.
  8. Score e multiplicador de banda e uníssono de star power funcionam.
  9. Tela de resultados com os stats de cada um.
  10. Pause duplo no cliente sai da partida e o host continua com o cliente como Left.
  11. Fechar o host → o cliente volta ao menu com aviso.
  12. Com "Salvar músicas" desligado, a música baixada some ao sair da sessão.
- [ ] **Step 3: Duas máquinas via Tailscale** — repetir os itens 1, 3, 6, 7 e 11 usando o IP `100.x.y.z` do host.
- [ ] **Step 4: Commit do checklist**

```bash
git add docs/superpowers/plans/2026-10-30-online-multiplayer-checklist.md
git commit -m "Record multiplayer end-to-end verification"
```
