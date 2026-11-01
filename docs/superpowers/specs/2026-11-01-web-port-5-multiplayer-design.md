# Port web, subprojeto 5: multiplayer pelo relay — Design de detalhe

Data: 2026-11-01
Status: em implementação (sem revisão; o usuário autorizou seguir)
Base: `2026-10-31-web-port-design.md`, §6 (este documento só fixa o que lá ficou em aberto).

## 1. Relay (`Server/YARG.Relay`)

ASP.NET Core minimal, `net10.0`, `LangVersion` 9, sem `ImplicitUsings`. Um único endpoint WebSocket em `/ws` e
`/health` (texto `ok`). Tudo em memória; nada em disco.

### 1.1 Mensagens de controle (frames de texto, JSON)

Cliente → relay:

| Mensagem | Quem | Resposta |
|---|---|---|
| `{"type":"list"}` | qualquer | `{"type":"rooms","rooms":[{"id","name","players","max","playing","locked":true}]}` |
| `{"type":"create","name","password","max"}` | fora de sala | `{"type":"created","room":id}` |
| `{"type":"join","room","password"}` | fora de sala | `{"type":"joined","room":id,"peer":n}` |
| `{"type":"kick","peer":n}` | host | (o host recebe `peer_left`) |
| `{"type":"state","playing":bool}` | host | nenhuma; muda o `playing` da lista |

Relay → cliente, sem pedido:

| Mensagem | Para | Quando |
|---|---|---|
| `{"type":"peer_joined","peer":n}` | host | cliente entrou |
| `{"type":"peer_left","peer":n}` | host | cliente saiu, caiu ou foi expulso |
| `{"type":"closed","reason":r}` | clientes | `host_left`, `idle` ou `kicked`; o relay fecha o socket em seguida |
| `{"type":"error","code":c}` | quem pediu | ver tabela abaixo |

Erros: `wrong_password`, `full`, `not_found`, `rate_limited`, `too_large` (os da spec geral) e mais
`too_many_rooms` (limite de salas por IP), `already_in_room`, `not_host` e `bad_request` (JSON inválido, campo
faltando, nome/senha vazios ou longos, `max` fora de 2..8). `too_large` fecha o socket (código 1009).

Os pedidos de um socket são respondidos na ordem em que chegam, então o cliente casa resposta e pedido por fila.

### 1.2 Dados (frames binários)

`[peerId u16 little-endian][payload]`. Cliente → relay: o `peerId` é ignorado e o payload vai ao host. Host →
relay: o `peerId` é o destino (desconhecido: descartado). Relay → host: o `peerId` é a origem. Relay → cliente:
`peerId` 0.

### 1.3 Limites

| Limite | Valor (opção em `Relay:*`) |
|---|---|
| Senha | PBKDF2-SHA256, sal aleatório de 16 bytes, 10 000 iterações; `FixedTimeEquals` |
| Senhas erradas | 5 por IP numa janela deslizante de 1 min; a 6.ª tentativa (certa ou não) recebe `rate_limited` |
| Salas por IP | 3 abertas ao mesmo tempo |
| Frame | 1 MiB + 2 bytes (binário); 16 KiB (texto) |
| Sala ociosa | 30 min sem frame binário nem controle dos membros; verificação a cada 30 s |
| Ping | `KeepAliveInterval` 10 s, `KeepAliveTimeout` 30 s |
| Nome / senha | 1..32 / 1..64 caracteres |
| Fila de saída | 64 frames por conexão; quem escreve espera até 15 s, depois o destino lento é derrubado |

IP: o do socket; com `Relay:TrustForwardedFor=true` (compose com Caddy na frente), o primeiro valor de
`X-Forwarded-For`. O Caddy substitui esse cabeçalho quando o cliente não é um proxy confiável.

O host sair (ou cair) fecha a sala: cada cliente recebe `closed host_left` e é desconectado.

## 2. Jogo

### 2.1 `IWebSocket` e `WebSocketTransport` (`Multiplayer/Core/Transport`)

```csharp
public interface IWebSocket : IDisposable
{
    WebSocketStatus Status { get; }      // Connecting, Open, Closed
    string? CloseReason { get; }
    long BufferedAmount { get; }
    void Open(string url);
    void SendText(string text);
    void SendBinary(byte[] data);
    bool TryReceive(out WebSocketFrame frame);
    void Close();
}
```

Modelo por polling: sem threads, sem timers, porque na web tudo roda no main thread. `WebSocketTransport.Pump()`
lê os frames recebidos, resolve os pedidos pendentes e enfileira os `NetEvent`; `Poll` chama `Pump` antes de
despachar. Os pedidos (`ListRoomsAsync`, `CreateRoomAsync`, `JoinRoomAsync`) devolvem `Task` completada dentro do
`Pump`; quem espera precisa bombear (o menu faz `Pump` + `UniTask.Yield` com prazo). Erro do relay vira
`RelayException` com `Code`.

- `StartHost`/`Connect` exigem sala criada/aceita (senão `InvalidOperationException`) e ignoram porta e host.
  `Connect` enfileira `Connected(0)`; no host, `peer_joined`/`peer_left` viram `Connected`/`Disconnected`.
- `PendingSendBytes` → `BufferedAmount` do socket (um só socket para todos os peers).
- `StartKeepAlive` não faz nada: o relay pinga e o navegador responde fora do main thread.
- `Disconnect(peer)`: host manda `kick`; cliente fecha o socket. Os dois enfileiram o `Disconnected` local.
- Socket fechou: cliente → `Disconnected(0, motivo)`; host → `Disconnected` de cada peer e `IsClosed`.
- `SetRoomPlaying(bool)` manda `state` (host).

Implementações: `BrowserWebSocket` (fora do Core, `Multiplayer/BrowserWebSocket.cs`, sobre
`Plugins/WebGL/YargWebSocket.jslib`, que guarda os frames recebidos numa fila JS) e `ClientWebSocketAdapter`
(testes, `System.Net.WebSockets.ClientWebSocket` com laços de envio e recebimento em background).

### 2.2 Integração

- `MultiplayerManager.HostWeb(transport)` / `JoinWeb(transport)` criam as sessões sobre um transporte já na sala.
  `Update` repassa o estado ao relay (`SetRoomPlaying`) e encerra a sessão do host se o socket cair.
- `OfferSong` e a instalação usam `PlatformTask.Run` no lugar de `UniTask.RunOnThreadPool`.
- Na web, `MultiplayerSongLibrary` usa `/tmp/yarg-mp` como pasta temporária (MEMFS), `FindByHash` olha
  `WebSongLoader.Current`, e a instalação vai para `/tmp/yarg-song/mp-<hash>/` seguida de
  `WebSongLoader.Clear()` antes e `WebSongLoader.LoadFromPath` depois, sem `SongContainer.RunRefresh`.
- Menu (`MultiplayerWebMenu`): lista de salas (`🔒 nome  n/máx`, "em partida"), "Entrar" pede a senha (campo
  mascarado, `RenameDialog.SetPasswordMode`), "Criar sala" pede nome, senha e máximo (2..8), "Atualizar" e
  "Voltar". Na sala, o host vê "Escolher música…" → `WebSongLoader.PickAsync` → `OfferSong` → escolha de parte.
  O menu de IP/porta não aparece na web (`OpenEntry` desvia).
- URL do relay: `-relay` (`?relay=wss://…`) ou a mesma origem (`wss://<host>/ws`, `ws://` em http).

### 2.3 Autotest web

`-mp-autotest webhost` e `-mp-autotest webjoin` (`?mp-autotest=webhost&…`):

- `webhost`: cria a sala `-mp-room` com `-mp-password` e `-mp-players` (máximo = esse número), espera
  `WebSongLoader.Current` (o teste chama `window.yargTest.loadSongFromUrl`) e segue como `host`.
- `webjoin`: lista as salas até achar `-mp-room`; se `-mp-wrong-password` vier, tenta essa antes e exige
  `wrong_password` (loga `Wrong password refused`); entra com `-mp-password` e segue como `join`.

## 3. Testes

- `Tests/YARG.Relay.Tests`: relay em Kestrel na porta 0 e `ClientWebSocket` crus — roteamento, cada erro, rate
  limit (e a janela), limite de salas, sala cheia, saída do host e do cliente, `kick`, ociosidade, frame grande.
- `Tests/YARG.Multiplayer.Tests`: referencia o relay e roda `HostSession` + 2 `ClientSession` sobre
  `WebSocketTransport` + `ClientWebSocketAdapter`, com oferta de música e transferência dos chunks.
- `Tests/WebSmoke/multiplayer.spec.mjs` (rodado pelo controlador depois da integração): sobe o relay, duas
  páginas headless, `webhost`/`webjoin`, senha errada recusada, música carregada pelo hook, cliente recebe,
  screenshots no gameplay e no resultado.

## 4. Não verificado aqui

Compilação Unity, jslib no navegador, menu e glifo do cadeado (TMP), autotest web e o smoke test: dependem da
integração com os subprojetos 1–3 e da build WebGL.
