# Port web, subprojeto 5: multiplayer — Plano

Spec: `docs/superpowers/specs/2026-11-01-web-port-5-multiplayer-design.md`. Execução nativa, sem subagentes por
tarefa: todo o código primeiro, testes rápidos no fim. Unity não é rodado neste worktree (o controlador compila,
gera os `.meta` e roda a build web depois de juntar os subprojetos).

1. **Relay** — `Server/YARG.Relay`: `YARG.Relay.csproj`, `Program.cs`, `RelayApp` (montagem e opções),
   `RelayOptions`, `RoomRegistry` (salas, peers, limites), `RelayConnection` (fila de saída e laço de escrita),
   `RelayHandler` (laço de leitura e despacho), `PasswordHasher`, `WrongPasswordLimiter`, `IdleRoomSweeper`.
   `Dockerfile`, `docker-compose.yml`, `Caddyfile`, `README.md` (deploy na VPS).
2. **Testes do relay** — `Tests/YARG.Relay.Tests`: `RelayFixture` (Kestrel na porta 0), `RelayClient` (socket
   cru), testes de controle, roteamento, limites e fechamento.
3. **Transporte** — `Core/Transport/IWebSocket.cs`, `WebSocketTransport.cs`, `RelayRoom.cs`,
   `RelayException.cs` (JSON com `Newtonsoft.Json.Linq`, que o YARG.Core já usa e o Unity tem pelo pacote
   `com.unity.nuget.newtonsoft-json`).
4. **Testes do transporte e das sessões** — `ClientWebSocketAdapter` e `WebSocketTransportTests`/`WebSessionTests`
   em `Tests/YARG.Multiplayer.Tests`, que passa a referenciar o relay.
5. **Navegador** — `Plugins/WebGL/YargWebSocket.jslib`, `Multiplayer/BrowserWebSocket.cs`.
6. **Jogo** — `MultiplayerManager` (`HostWeb`/`JoinWeb`, `PlatformTask`, estado para o relay),
   `MultiplayerSongLibrary` (MEMFS, `WebSongLoader`), `MultiplayerMenuFlow` (desvio na web, "Escolher música…"),
   `MultiplayerWebMenu` (salas), `RenameDialog.SetPasswordMode`, textos em `en-US.json`/`pt-BR.json`.
7. **Autotest** — papéis `webhost`/`webjoin` em `MultiplayerAutoTest`.
8. **Smoke** — `Tests/WebSmoke/multiplayer.spec.mjs` + README.
9. **Verificação** — `dotnet test` nos dois projetos; commit.
