# Port web, subprojeto 3: carregar música do disco — Design detalhado

Detalha a §5 de `2026-10-31-web-port-design.md`. Vale tudo de lá (§3 pontos de troca, §8 erros, §9 testes).

## Peças

| Peça | Onde | O que faz |
|---|---|---|
| `SingleSongLoader` | Core, `Song/SingleSongLoader.cs` | Recebe um caminho (`.sng` ou pasta) e devolve um `SongEntry` ou um erro tipado. Mesma regra do `CacheHandler` (`song.ini` obrigatório fora do UltraStar, áudio obrigatório), sem scan nem cache. Desce por pastas com uma única subpasta (zip com pasta raiz). |
| `YARGImage.DecoderOverride` | Core, `IO/Images/YARGImage.cs` | Gancho opcional para decodificar png/jpg sem o STB2CSharp nativo. `YARGImage.FromPixels` cria a imagem a partir de pixels já decodificados. |
| `SngFileStream.UseParallelDecrypt` | Core | Na web a decriptação do `.sng` é sequencial (sem threads). Padrão `true`; o desktop não muda. |
| `YargWebSong.jslib` | `Assets/Plugins/WebGL` | Seletor de arquivo (`.sng,.zip`), seletor de pasta (`webkitdirectory`), arrastar e soltar, gravação no MEMFS em `/tmp/yarg-song/<id>/` e `window.yargTest`. |
| `WebSongBridge` | `Assets/Script/Song/Web` | `DllImport` do jslib (stubs fora da web). |
| `WebSongLoader` | `Assets/Script/Song/Web` | API pública (contrato com o subprojeto 5), objeto `YargWebSong` que recebe os `SendMessage`. |
| `WebImageDecoder` | `Assets/Script/Song/Web` | `Texture2D.LoadImage` → RGBA de cima para baixo → `YARGImage.FromPixels`. Instalado só na web. |
| `WebSongMenu` | `Assets/Script/Menu/Web` | Tela montada em código (sem prefab): capa, nome, artista, instrumentos e dificuldades; botões "Carregar música…", "Carregar pasta…", "Jogar", "Carregar outra", "Voltar". |
| `WebSongTestHooks` | `Assets/Script/Song/Web` | Comandos de teste vindos de `window.yargTest` (jogador bot, iniciar gameplay direto). |

## Fluxo

1. Na web, `MainMenu.QuickPlay` e `GameModesMenu.Practice` abrem a `WebSongMenu` em vez da `MusicLibrary`.
2. "Carregar música…" chama `WebSongLoader.PickAsync` dentro do clique (gesto do usuário). O jslib abre o
   `<input type=file>`, soma os tamanhos, recusa acima de 300 MB **antes** de ler, grava com `FS.writeFile` e chama
   `SendMessage("YargWebSong", "OnPickResult", "ok|<caminho>")`. Cancelamento: `"cancel|"`. Erro: `"error|<código>"`.
3. `LoadFromPath`: `.zip` → `SafeExtractor.Extract` para `<pasta>/song` (limite 300 MB) e apaga o zip; `.sng` e
   pasta → `SingleSongLoader.Load`. Sucesso: a música anterior é apagada do MEMFS (`Clear`) e vira `Current`.
4. Arrastar e soltar: o jslib grava e manda `OnDropResult` (mesmo formato). Só aceito com a tela aberta
   (`SetDropEnabled`).
5. "Jogar": `GlobalVariables.State.CurrentSong = Current`, `ShowSongs = [Current]` e `DifficultySelect`, como a
   biblioteca faz. A tela de resultado não grava nada (`ScoreContainer.IsAvailable` já é falso na web).
6. Ao voltar da tela de resultado para o menu, `MenuManager.Start` reabre a `WebSongMenu` em modo resultado:
   "Jogar de novo" (Verde) e "Carregar outra" (Amarelo).

## Erros

| Situação | Código | Mensagem (`WebSong.Error.*`) |
|---|---|---|
| Extensão desconhecida, CON, zip inválido ou inseguro | `UnsupportedFormat` | "Formato não suportado" |
| Nenhum `notes.*` (+ `song.ini`) | `NoChart` | "Nenhum chart encontrado" |
| Chart não faz parse | `ParseFailed` | "Não foi possível ler o chart: {motivo}" |
| Nenhum áudio | `NoAudio` | "A música não tem áudio" |
| Mais de 300 MB | `TooLarge` | "A música passa de 300 MB" |
| Falha do navegador ao ler | `ReadFailed` | "Não foi possível ler os arquivos" |

Na tela o erro aparece em vermelho e os botões de carregar continuam ativos (volta ao seletor).

## Testes

- Core: `SingleSongLoaderTests` (pasta, pasta aninhada, `.sng` gerado no teste, sem áudio, sem ini, chart quebrado,
  formato desconhecido) e `SngFileStream` paralelo = sequencial. `YARGImage.FromPixels` e o gancho.
- Web (`Tests/WebSmoke/song.spec.mjs`, depois da integração): fixture em `Tests/WebSmoke/fixtures/song/` (chart de
  ~10 s + `calibration_music.ogg` do próprio repositório), `.zip` e `.sng` gerados por `fixtures/make-song.mjs`.
  Carrega via `yargTest.loadSongFromUrl`, tira screenshots da tela da música, do gameplay e da tela de resultado,
  testa "Carregar outra" pelo seletor real (evento `filechooser` do Playwright) e um erro de formato.
