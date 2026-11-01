# Port para navegador (WebGL) — Design

Data: 2026-10-31
Status: aguardando revisão

## 1. Objetivo

Rodar o YARG no navegador para um grupo de amigos, com um servidor próprio numa
VPS. O jogador abre a página, carrega uma música do próprio computador e joga
como no desktop. No online, um jogador cria uma sala com senha, os outros entram
e o host escolhe a música fazendo upload dela.

A build desktop continua existindo no mesmo projeto, com o comportamento atual.

### Critérios de sucesso

- A página carrega no Chrome, Edge e Firefox de desktop e chega ao menu depois de
  um clique ("Clique para começar").
- Singleplayer: o jogador carrega um `.sng`, uma pasta de música ou um `.zip` de
  pasta, escolhe instrumento/dificuldade e joga a música até a tela de resultado,
  com áudio sincronizado e mute de stem ao errar.
- Vocal funciona com o microfone do navegador (permissão pedida na hora).
- Settings e perfis (incluindo mapeamento de controles) sobrevivem a recarregar a
  página, e podem ser exportados/importados como um arquivo JSON.
- Online: criar sala com nome e senha, ver a lista de salas, entrar com a senha
  certa (e ser recusado com a errada), o host escolher uma música por upload, os
  clientes recebem a música e todos tocam em banda como no desktop.
- A build desktop, o `-mp-autotest` e os cenários de banda continuam passando.

### Decisões tomadas

| Tema | Decisão |
|---|---|
| Público e hospedagem | Amigos; VPS própria |
| Desktop | Continua no mesmo código; a web entra atrás de abstrações |
| Crossplay | Não. Lobbies com senha só na web; desktop mantém o TCP direto |
| Navegadores | Chrome, Edge e Firefox de desktop. Sem Safari, sem celular |
| Velocidade da música | `playbackRate`: muda a velocidade e o tom juntos |
| Persistência | Só settings e perfis, no `localStorage`; exportar/importar JSON |
| Áudio | Backend WebAudio próprio (`.jslib` + JS), não o `AudioSource` do Unity |
| Servidor | Relay WebSocket que só repassa frames; não conhece o protocolo do jogo |
| Senha | Obrigatória em toda sala |
| Background | Venue 3D e vídeo desligados na web; imagem ou cor apenas |

### Fora do escopo

- Safari, celular, tablet, toque.
- Crossplay desktop ↔ web; TCP direto na web.
- CON / RBCON / PKG.
- Scores, replays, histórico, career e biblioteca de músicas na web.
- Time-stretch (velocidade sem mudar o tom), whammy com pitch bend e demais
  efeitos do BASS FX na web.
- Mais de um microfone por navegador (harmonias locais).
- Instrumentos HID/USB (LibUSB), Discord, saída ASIO/WASAPI na web.
- Vídeo de fundo (VLC) e venue 3D (AssetBundle `Yarground`) na web.
- Persistência de qualquer coisa além de settings e perfis.

## 2. Divisão em subprojetos

O port não cabe num plano só. Cada subprojeto tem spec de detalhe própria (quando
precisar), plano e PR. Esta spec é a visão geral e já serve de spec para o
subprojeto 1.

| # | Subprojeto | Entrega | Depende de |
|---|---|---|---|
| 1 | Build web que inicializa | Build WebGL abre, chega ao menu, settings/perfis no `localStorage`, exportar/importar, sem threads, sem SQLite, sem venue/vídeo | — |
| 2 | Backend WebAudio | Stems, SFX, sincronia e velocidade; gameplay singleplayer completo com uma música fixa de teste | 1 |
| 3 | Carregar música | Botão "Carregar música…", arrastar e soltar, `.sng`/pasta/`.zip` no MEMFS | 1, 2 |
| 4 | Microfone | `WebMicDevice` via `getUserMedia` + AudioWorklet alimentando o `PitchTracker` | 2 |
| 5 | Multiplayer web | Relay na VPS, `WebSocketTransport`, menu de salas com senha, upload da música pelo host | 1, 3 |

Ordem: 1 → 2 → 3 → 4 → 5. O 4 e o 5 são independentes entre si.

## 3. Arquitetura

### 3.1 Pontos de troca por plataforma

O projeto é um só; a build WebGL é um alvo novo. Cada dependência de plataforma
fica atrás de uma abstração, e a implementação é escolhida na inicialização
(`GlobalVariables`, `GlobalAudioHandler.Initialize<T>()`).

| Ponto de troca | Desktop (como hoje) | Web (novo) |
|---|---|---|
| Áudio e microfone | `BassAudioManager` | `WebAudioManager` + `YargWebAudio.jslib` |
| Rede | `TcpTransport` (IP:porta) | `WebSocketTransport` → relay |
| Persistência | arquivos em `PathHelper.PersistentDataPath` | `IKeyValueStore` → `localStorage` |
| Scores e replays | SQLite (`ScoreDatabase`, career) | desligados (`NullScoreContainer`) |
| Origem das músicas | scan de pastas + cache | seletor de arquivos → um `SongEntry` |
| Trabalho em background | thread pool / `Parallel` | `PlatformTask`: main thread, em pedaços |
| Background | venue, vídeo, imagem | imagem ou cor |
| Integrações | Discord, HID/USB | excluídas da build |

Regras:

- `#if UNITY_WEBGL` só aparece nos pontos de composição: quem escolhe a
  implementação, os asmdefs e a configuração de importação dos plugins nativos.
  A lógica de jogo não ganha `#if`.
- YARG.Core continua sem dependência de Unity. As mudanças nele são aditivas e não
  mudam o comportamento no desktop.
- Plugins nativos (BASS, `yarg_audio`, VLC, Discord, sqlite3, LibUSB) ficam
  desmarcados para WebGL nas configurações de importação; o código que os usa fica
  em asmdefs ou arquivos excluídos da build web.
- STB2CSharp (decodificação de imagem) é C# puro e continua na web; se algum
  trecho depender de nativo, a capa usa `Texture2D.LoadImage`.

### 3.2 Threads

O C# no WebGL não tem threads. Todo `Task.Run`, `Parallel.*`,
`UniTask.RunOnThreadPool` e `new Thread` usado em caminhos que a web executa passa
por um helper `PlatformTask`:

- Desktop: delega ao thread pool, como hoje.
- Web: roda no main thread. Trabalhos longos fazem `await UniTask.Yield()` entre
  pedaços para a página não travar.

Pontos conhecidos: `GameManager.Loading.cs` (carregar chart e áudio),
`PreviewContext`, `MusicPlayer`, `LoadingScreen`, `LocalizationManager`,
`ProfileView`, `Sidebar`, `MultiplayerManager` e `MultiplayerSongLibrary`
(empacotar/instalar música), `SngFileStream` (`Parallel.For`) e o logger
(`YargLogger`, que na web escreve direto no `console`). O scan de pastas
(`CacheHandler`, `SongSorting`, `SongSearching`, `SongSources`, `SongContainer`)
não roda na web.

No Core, os `Parallel.*` dos caminhos usados pela web ganham uma variante
sequencial escolhida por parâmetro. O desktop continua paralelo.

### 3.3 Configuração do projeto WebGL

- Memória: heap crescente até 2 GB (hoje `webGLMemorySize: 32`, fixo).
- Sem threads do WebGL (`webGLThreadsSupport: 0`), o que evita exigir
  COOP/COEP.
- Compressão Brotli, servida com `Content-Encoding` correto pelo Caddy.
- Template HTML próprio: tela "Clique para começar" (destrava o `AudioContext`),
  barra de carregamento e tela de erro com botão de recarregar.
- Parâmetros da URL viram `CommandLineArgs` (`?mp-autotest=offline` equivale a
  `-mp-autotest offline`), para reaproveitar o autotest.

## 4. Áudio (subprojeto 2)

### 4.1 Estrutura

C#: `WebAudioManager`, `WebStemMixer`, `WebStemChannel`, `WebSampleChannel`,
`WebMicDevice`, implementando as classes abstratas de `YARG.Core/Audio`
(`AudioManager`, `StemMixer`, `StemChannel`, `SampleChannel`, `MicDevice`, …).
Elas guardam handles inteiros e chamam funções `extern` de `YargWebAudio.jslib`.
A lógica de áudio fica num módulo JS com um único
`AudioContext({ latencyHint: "interactive" })`.

### 4.2 Stems

- Os bytes de cada stem (ogg/opus/mp3) são passados ao JS e decodificados com
  `decodeAudioData`.
- Cada stem: `AudioBufferSourceNode` → `GainNode` → master. Todos começam com
  `start(t0, offset)` no mesmo instante do contexto.
- Tempo da música: `(ctx.currentTime − t0) × rate + offset`, lido pelo C# a cada
  frame. O `SongRunner` mantém o papel atual (comparar com o InputTime e
  corrigir).
- Seek e pausa: para e recria as sources.
- Velocidade: `playbackRate` em todas as sources (muda o tom).
- Mute de stem ao errar: rampa no `GainNode`, mesma semântica de `fraction`.
- Whammy com pitch bend e efeitos do BASS FX: no-op na web.

Memória: PCM decodificado em float32 é grande (uma música de 4 min com 6 stems
estéreo passa de 500 MB). Mitigação: os stems que nenhum jogador usa são somados
num único stem de fundo, um de cada vez, antes de decodificar o próximo. Se a
música ainda não couber, o jogo mostra um erro amigável. A alternativa, se isso
não bastar, é decodificar em streaming com WebCodecs + AudioWorklet atrás da mesma
interface `StemMixer`.

### 4.3 Efeitos sonoros

Samples de bateria, star power, metrônomo e sons de menu são `AudioBuffer`s
pré-carregados, tocados como one-shots.

### 4.4 Latência e autoplay

- O `AudioContext` só é criado/retomado depois do clique em "Clique para começar".
- O offset padrão de calibração considera `outputLatency`. A calibração atual
  continua valendo e fica nas settings.

### 4.5 Microfone (subprojeto 4)

- `getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false,
  autoGainControl: false } })`.
- Um AudioWorklet manda blocos de amostras por `port.postMessage` para um ring
  buffer em JS; o C# esvazia o buffer a cada frame e alimenta o `PitchTracker`
  (`Assets/Script/Audio/PitchDetection`), sem mudanças nele.
- Dispositivos via `enumerateDevices`. Um microfone por navegador. Retorno do
  microfone desligado por padrão.
- Permissão negada: aviso, e o jogador pode trocar de instrumento.

## 5. Carregar música (subprojeto 3)

O WebGL do Unity roda sobre o sistema de arquivos em memória do Emscripten
(MEMFS), onde `System.IO` funciona. Os arquivos escolhidos vão para
`/tmp/yarg-song/<id>/` e o Core usa os pontos de entrada por caminho que já
existem. Nada é gravado em `persistentDataPath` na web (lá ele é IndexedDB, ou
seja, persistente).

Entradas aceitas:

- arquivo `.sng`;
- pasta com `song.ini`, `notes.mid`/`notes.chart` e os áudios, via
  `<input webkitdirectory>`;
- `.zip` de uma pasta (mesmo formato do `SongPackager`).

Fluxo:

1. O menu singleplayer mostra "Carregar música…" no lugar da lista. O botão abre o
   seletor (via `.jslib`); arrastar e soltar na página também funciona.
2. O JS grava os arquivos em `/tmp/yarg-song/<id>/` com `FS.writeFile` e avisa o
   C# com o caminho. Um `.zip` é extraído no C# pelo `SafeExtractor`.
3. O C# cria um único `SongEntry` (`SngEntry` ou `UnpackedIniEntry`
   `.ProcessNewEntry`), sem scan e sem cache.
4. Tela da música: capa, nome, artista, instrumentos e dificuldades. Depois segue o
   fluxo normal até o gameplay e a tela de resultado, que não grava nada.
5. Ao terminar: "Jogar de novo" ou "Carregar outra". Carregar outra apaga a
   anterior do MEMFS.

Erros:

| Situação | Comportamento |
|---|---|
| Formato não suportado, CON | "Formato não suportado" |
| Chart não faz parse | Mensagem com o motivo; volta ao seletor |
| Nenhum áudio | Recusada |
| Mais de 300 MB | Aviso antes de copiar; recusada |

## 6. Multiplayer web (subprojeto 5)

### 6.1 Infra na VPS

- Caddy na frente: HTTPS automático, arquivos estáticos da build WebGL e proxy de
  `/ws` para o relay. Mesma origem: sem CORS, e `wss://` funciona.
- Relay em `Server/YARG.Relay`: ASP.NET Core minimal, C# 9 (`LangVersion` 9).
  Distribuído com `Dockerfile` e `docker-compose.yml` (Caddy + relay).
- Nada é gravado em disco; salas, senhas e contadores só em memória.

### 6.2 Protocolo do relay

Frames de texto (JSON), controle:

| Mensagem | Efeito |
|---|---|
| `list` | Salas abertas: nome, jogadores/máximo, se está em partida |
| `create {name, password, max}` | Cria a sala; quem cria é o host |
| `join {room, password}` | Entra na sala; host recebe "peer conectado" |
| erros | `wrong_password`, `full`, `not_found`, `rate_limited`, `too_large` |

Frames binários, dados: `[peerId u16][payload]`.

- Cliente → relay: o payload vai sempre ao host (o `peerId` enviado é ignorado).
- Host → relay: o `peerId` indica o destino.
- O relay entrega ao host o `peerId` de origem; entrada e saída de clientes viram
  `OnPeerConnected` / `OnPeerDisconnected` no host.
- O host sai: a sala fecha e todos os clientes são desconectados (mesmo
  comportamento de hoje quando o host cai).

### 6.3 Senhas e abuso

- Senha obrigatória. Guardada como hash em memória; comparação em tempo constante.
- 5 tentativas erradas por IP por minuto; depois, `rate_limited`.
- No máximo 3 salas por IP.
- Frame de no máximo 1 MB (acima do maior `SongChunk`).
- Sala ociosa fecha em 30 min. O relay faz ping de WebSocket a cada 10 s.

### 6.4 Lado do jogo

- `WebSocketTransport : INetTransport` em `Multiplayer/Core` (sem Unity), sobre
  uma interface pequena `IWebSocket` (abrir, enviar, receber, `bufferedAmount`,
  fechar). Implementações: `.jslib` na web, `ClientWebSocket` nos testes.
- `PendingSendBytes` → `bufferedAmount`. Keepalive: o do protocolo atual mais o
  ping do relay.
- `INetTransport` não muda. Listar, criar e entrar em sala são métodos próprios do
  `WebSocketTransport` (`ListRoomsAsync`, `CreateRoomAsync`, `JoinRoomAsync`),
  chamados pelo menu antes de a sessão começar. Depois disso a sessão chama
  `StartHost`/`Connect` como hoje; no `WebSocketTransport` eles exigem uma sala já
  criada/aceita e ignoram os argumentos de porta e host.
- Host, sessões, sincronização de relógio e de input e `SongOffer`/`SongChunk` não
  mudam. A latência passa pela VPS (ida e volta); a sincronização de relógio já
  mede o RTT.

### 6.5 Menu online (web)

- Lista de salas com cadeado e botão "Entrar", que pede a senha.
- "Criar sala": nome, senha, máximo de jogadores.
- Na sala, o host vê "Escolher música…" (o seletor da §5); a música é empacotada
  do MEMFS e enviada aos clientes pelo fluxo atual, e cada cliente instala no MEMFS
  dele.
- O menu de IP e porta do desktop não aparece na web.
- URL do relay: mesma origem por padrão; `?relay=wss://…` para desenvolvimento.

## 7. Persistência (subprojeto 1)

- `IKeyValueStore`: arquivos no desktop (comportamento atual), `localStorage` via
  `.jslib` na web. `PlayerPrefs` não é usado (no WebGL ele grava em IndexedDB).
- Chaves `yarg.settings` e `yarg.profiles`, com o mesmo JSON de `settings.json` e
  `profiles/profiles.json`, mais `schemaVersion`.
- Exportar: baixa `yarg-config.json` com `{ version, settings, profiles }`.
- Importar: seletor de arquivo; valida versão e formato; substitui e recarrega o
  menu. Arquivo inválido: erro, nada muda.
- `localStorage` indisponível ou cota estourada: o jogo segue com os dados em
  memória e avisa "as configurações não serão salvas".
- Settings que não se aplicam à web (biblioteca de músicas, venue, vídeo, Discord,
  saída de áudio, ASIO/WASAPI) ganham uma marcação de plataforma e somem do menu.
  As três flags `Disable*Backgrounds` ficam ligadas por padrão na web.
- Menus sem função na web somem: scores, histórico, career, replays.

## 8. Erros gerais

| Situação | Comportamento |
|---|---|
| Falta de memória / crash do WebGL | Template mostra a mensagem e "Recarregar" |
| `AudioContext` não inicia | Tela de erro |
| WebSocket cai | Toast de desconexão atual |
| Microfone negado | Aviso; troca de instrumento |
| `localStorage` indisponível | Segue em memória com aviso |

## 9. Testes

1. **Core:** `dotnet test` como sempre, mais testes que comparam as variantes
   sequenciais com as paralelas.
2. **Relay:** novo `Tests/YARG.Relay.Tests`: senha certa e errada, rate limit,
   sala cheia, limite de salas por IP, roteamento por `peerId`, saída do host,
   frame grande demais.
3. **Transporte:** em `Tests/YARG.Multiplayer.Tests`, os testes de sessão rodam
   também sobre `WebSocketTransport` com o relay em processo (Kestrel em porta
   aleatória, `ClientWebSocket`).
4. **Regressão desktop:** compilação Unity em batchmode, `-mp-autotest` e cenários
   de banda.
5. **Build web:** `Editor/WebBuild.cs` gera a build WebGL em batchmode. Um teste
   Playwright com Chromium headless serve a build localmente e:
   - roda `?mp-autotest=offline` e espera `[AutoTest] PASS` no console;
   - abre duas abas (`host` e `join`) contra um relay local, para o online;
   - usa `--autoplay-policy=no-user-gesture-required` e microfone falso
     (`--use-fake-device-for-media-stream --use-file-for-fake-audio-capture`) com
     um `.wav` para o vocal.
6. Mudanças de UI, cenas, prefabs e template marcadas como "não verificadas
   visualmente" nos PRs.

Pré-requisitos do ambiente: `UNITY_EDITOR` definida (hoje não está) e o módulo
WebGL Build Support instalado no Unity 6000.3.5f2. Playwright com Chromium para o
item 5.

## 10. Riscos

| Risco | Mitigação |
|---|---|
| Memória do PCM decodificado | Soma dos stems não usados; erro amigável; WebCodecs como plano B |
| Trabalho pesado no main thread trava a página | `PlatformTask` em pedaços com `Yield`; tela de carregamento |
| Código desktop vazando para a build web (plugins, `System.Threading`) | Build WebGL no teste do item 5; plugins desmarcados para WebGL |
| Deriva entre `AudioContext` e InputTime | `SongRunner` já corrige deriva; tempo lido do contexto a cada frame |
| Tamanho do download da build | Brotli; venue/vídeo/plugins fora da build |
