# Port web, subprojeto 1: build web que inicializa — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Uma build WebGL do YARG que abre no Chrome/Edge/Firefox, chega ao menu principal, guarda settings e perfis no `localStorage` e exporta/importa esses dados como JSON, sem quebrar a build desktop.

**Architecture:** A web entra por pontos de composição: `PlatformInfo.IsWeb` escolhe as implementações na inicialização (`GlobalVariables`, `PathHelper`, `CommandLineArgs`). O `persistentDataPath` da web aponta para o MEMFS (`/tmp/yarg`), então todo o código de arquivo existente funciona em memória e some ao recarregar; só `settings.json` e `profiles/profiles.json` são espelhados no `localStorage`. StreamingAssets são baixados para o MEMFS (`/streaming`) antes de o C# rodar, por um `.jspre`. O áudio fica mudo (`SilentAudioManager`) até o subprojeto 2.

**Tech Stack:** Unity 6000.3.5f2 (WebGL, IL2CPP), C# 9, `.jslib`/`.jspre` (Emscripten), NUnit (YARG.Core), Node 20 + Playwright (smoke test).

**Spec:** `docs/superpowers/specs/2026-10-31-web-port-design.md` (§3 Arquitetura, §7 Persistência, §8 Erros, §9 Testes; subprojeto 1 da §2).

## Global Constraints

- C# 9 em tudo (Core, Unity, testes); nada de recursos mais novos. Siga o `.editorconfig`.
- YARG.Core sem dependência de Unity; mudanças no Core são aditivas e não mudam o desktop.
- `#if UNITY_WEBGL` só nos pontos de composição; a lógica de jogo não ganha `#if`.
- Na web, nada é gravado em `Application.persistentDataPath` (IndexedDB). Persistência só via `localStorage`.
- Chaves do `localStorage`: `yarg.settings`, `yarg.profiles`, `yarg.schemaVersion` (valor `"1"`). Nada de `PlayerPrefs`.
- Arquivo exportado: `yarg-config.json`, formato `{ "format": "yarg-web-config", "version": 1, "settings": {…}, "profiles": […] }`.
- Projeto WebGL: heap crescente até 2048 MB, sem threads do WebGL, compressão Brotli, template próprio.
- Navegadores-alvo: Chrome, Edge, Firefox de desktop.
- Arquivo `.cs` novo em `Assets/` ou `YARG.Core/YARG.Core` precisa de `.meta` gerado pelo Unity (rode o editor; nunca escreva `.meta` à mão).
- Mudanças no Core: branch e commit no submódulo `YARG.Core` primeiro, depois atualizar o ponteiro no YARG.
- Branch do YARG: `claude/web-port`. Branch do Core: `claude/web-port`.
- Builds e testes Unity exigem `UNITY_EDITOR` definida e o módulo WebGL Build Support; se faltar, pare e avise.
- Mudanças de UI, template e settings: marcar "não verificadas visualmente" no PR.
- Navegadores de teste nunca aparecem nem fazem som: Playwright sempre headless (nunca `headed`/`--headed`), com `--mute-audio` nos `launchOptions.args`. Se algum teste precisar de navegador com janela, ela abre minimizada (`--start-minimized`) e muda.

## Review Focus

1. `localStorage` com JSON corrompido em `yarg.profiles` → o jogo chega ao menu (o tratamento atual de arquivo inválido entra em ação). Teste: `corrupt profiles still boot` (Task 7).
2. `localStorage.setItem` lançando exceção (cota, bloqueio) → o jogo chega ao menu e segue em memória. Teste: `storage failure still boots` (Task 7).
3. Nome de perfil com acentos e emoji passa pelo `.jslib` (UTF-8) e volta igual depois de recarregar. Teste: `profiles round-trip through localStorage` com `"Ação 🎸"` (Task 7).
4. Query string com `+`, `%20`, chave sem valor e chave repetida → args corretos. Testes em `QueryStringArgsTests` (Task 1).
5. Import de arquivo que não é um bundle (outro JSON, versão futura, `settings` não-objeto) → erro, nada muda. Testes em `ConfigBundleTests` (Task 2).

---

### Task 1: Core — args por query string e logger sem thread

**Files:**
- Create: `YARG.Core/YARG.Core/Utility/QueryStringArgs.cs`
- Modify: `YARG.Core/YARG.Core/Logging/YargLogger.cs:27-37`
- Test: `YARG.Core/YARG.Core.UnitTests/Utility/QueryStringArgsTests.cs`

**Interfaces:**
- Produces: `public static string[] QueryStringArgs.Parse(string query)` em `YARG.Core.Utility`. Recebe `location.search` com ou sem `?`. Cada par `k=v` vira `"-k"` seguido dos tokens de `v` (decodificado; `+` vira espaço; separado por espaço, tokens vazios descartados). Chave sem valor vira só `"-k"`. Ordem preservada; repetições preservadas. Não inclui o nome do executável.
- Produces: com o símbolo `YARG_NO_THREADS`, `YargLogger` não cria a thread; quem hospeda chama `YargLogger.FlushLogQueue()` (já público). `public static bool YargLogger.HasOutputThread { get; }`.

- [ ] **Step 1: Criar o branch do submódulo**

```bash
cd YARG.Core && git switch -c claude/web-port origin/master
```

- [ ] **Step 2: Escrever os testes que falham**

```csharp
[TestCase("", new string[0])]
[TestCase("?", new string[0])]
[TestCase("?offline", new[] { "-offline" })]
[TestCase("?mp-autotest=offline", new[] { "-mp-autotest", "offline" })]
[TestCase("mp-autotest=join+127.0.0.1&mp-players=2", new[] { "-mp-autotest", "join", "127.0.0.1", "-mp-players", "2" })]
[TestCase("?mp-song=My%20Song", new[] { "-mp-song", "My", "Song" })]
[TestCase("?lang=pt-BR&lang=en-US", new[] { "-lang", "pt-BR", "-lang", "en-US" })]
[TestCase("?a=&b", new[] { "-a", "-b" })]
public void Parse_MapsQueryToArgs(string query, string[] expected)
{
    Assert.That(QueryStringArgs.Parse(query), Is.EqualTo(expected));
}
```

(`My%20Song` vira dois tokens de propósito: os args da linha de comando são separados por espaço, igual a `-mp-song My Song` no desktop sem aspas. Documente isso no XML doc.)

- [ ] **Step 3: Rodar e ver falhar**

Run: `cd YARG.Core && dotnet test YARG.Core.UnitTests --filter "FullyQualifiedName~QueryStringArgsTests"`
Expected: erro de compilação, `QueryStringArgs` não existe.

- [ ] **Step 4: Implementar `QueryStringArgs.Parse`** com `Uri.UnescapeDataString` depois de trocar `+` por espaço.

- [ ] **Step 5: Logger** — envolver a criação da thread no construtor estático com `#if !YARG_NO_THREADS`; `HasOutputThread` retorna `true` só quando a thread foi iniciada.

- [ ] **Step 6: Verificar**

Run: `cd YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build`
Expected: tudo passa.
Run: `cd YARG.Core && dotnet build YARG.Core/YARG.Core.csproj -p:DefineConstants=YARG_NO_THREADS`
Expected: build OK.

- [ ] **Step 7: Commit no submódulo**

```bash
cd YARG.Core && git add -A YARG.Core/Utility/QueryStringArgs.cs YARG.Core/Logging/YargLogger.cs YARG.Core.UnitTests/Utility/QueryStringArgsTests.cs
git commit -m "Map URL query strings to args and allow a logger without a thread"
```

### Task 2: Core — bundle de configuração (exportar/importar)

**Files:**
- Create: `YARG.Core/YARG.Core/IO/ConfigBundle.cs`
- Test: `YARG.Core/YARG.Core.UnitTests/IO/ConfigBundleTests.cs`

**Interfaces:**
- Produces, em `YARG.Core.IO`:
  - `public const string ConfigBundle.FORMAT = "yarg-web-config";`, `public const int ConfigBundle.VERSION = 1;`
  - `public static string ConfigBundle.Create(string? settingsJson, string? profilesJson)` — JSON indentado; campo omitido quando o argumento é `null`. Os textos entram como JSON (`JToken.Parse`), não como string escapada.
  - `public static bool ConfigBundle.TryParse(string bundleJson, out string? settingsJson, out string? profilesJson, out string error)` — devolve os textos re-serializados indentados.

- [ ] **Step 1: Escrever os testes que falham**

```csharp
[Test] public void RoundTrip_KeepsSettingsAndProfiles()
// Create("{\"A\":1}", "[{\"Name\":\"Ação 🎸\"}]") → TryParse == true;
// JToken.DeepEquals(JToken.Parse(settings), JObject {A:1}); profiles[0].Name == "Ação 🎸"; error == null

[Test] public void Create_OmitsNullParts()
// Create(null, "[]") não contém a chave "settings"; TryParse dá settingsJson == null, profilesJson == "[]"

[TestCase("not json")]
[TestCase("[]")]
[TestCase("{\"format\":\"other\",\"version\":1,\"settings\":{}}")]
[TestCase("{\"format\":\"yarg-web-config\",\"version\":2,\"settings\":{}}")]
[TestCase("{\"format\":\"yarg-web-config\",\"version\":1}")]
[TestCase("{\"format\":\"yarg-web-config\",\"version\":1,\"settings\":[]}")]
[TestCase("{\"format\":\"yarg-web-config\",\"version\":1,\"profiles\":{}}")]
public void TryParse_RejectsInvalid(string json)
// TryParse == false; settingsJson == null; profilesJson == null; error não vazio
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd YARG.Core && dotnet test YARG.Core.UnitTests --filter "FullyQualifiedName~ConfigBundleTests"`
Expected: erro de compilação, `ConfigBundle` não existe.

- [ ] **Step 3: Implementar `ConfigBundle`** com Newtonsoft (`JObject`/`JToken`); sem lançar exceção em `TryParse`.

- [ ] **Step 4: Verificar**

Run: `cd YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build`
Expected: tudo passa.

- [ ] **Step 5: Commit no submódulo e no YARG**

```bash
cd YARG.Core && git add YARG.Core/IO/ConfigBundle.cs YARG.Core.UnitTests/IO/ConfigBundleTests.cs && git commit -m "Add the web config bundle format"
git push -u origin claude/web-port
cd .. && git add YARG.Core && git commit -m "Update YARG.Core for the web port"
```

(Os `.meta` dos dois arquivos novos do Core são gerados na Task 3, quando o editor abrir, e commitados no submódulo nela.)

### Task 3: Unity — pontos de troca de plataforma

**Files:**
- Create: `Assets/Script/Helpers/PlatformInfo.cs`, `Assets/Script/Helpers/PlatformTask.cs`, `Assets/Script/Helpers/Web/WebBridge.cs`, `Assets/Plugins/WebGL/YargWeb.jslib`
- Modify: `Assets/Script/Persistent/CommandLineArgs.cs:50`, `Assets/Script/Multiplayer/MultiplayerAutoTest.cs:43`, `Assets/Script/Helpers/PathHelper.cs:92-130`, `Assets/Script/Persistent/GlobalVariables.cs` (`Update`), `Assets/Script/Menu/Credits/CreditsMenu.cs:217`, `Assets/Script/Localization/LocalizationManager.cs:44`, `Assets/Script/Persistent/LoadingScreen.cs:93`, `Assets/Script/Menu/ProfileList/ProfileView.cs:357`, `Assets/Script/Helpers/Extensions/ImageExtensions.cs:128`

**Interfaces:**
- Consumes: `QueryStringArgs.Parse` (Task 1), `YargLogger.HasOutputThread`/`FlushLogQueue` (Task 1).
- Produces:
  - `public static class PlatformInfo { public static bool IsWeb { get; } }` (`YARG.Helpers`). `true` em `UNITY_WEBGL && !UNITY_EDITOR`. É o único lugar que testa esse símbolo para lógica.
  - `public static string[] PlatformInfo.GetArgs()`: desktop `Environment.GetCommandLineArgs()`; web `["YARG", ..QueryStringArgs.Parse(WebBridge.GetQueryString())]`.
  - `public static class PlatformTask` (`YARG.Helpers`): `UniTask Run(Action action)` e `UniTask<T> Run<T>(Func<T> func)`. Desktop: `UniTask.RunOnThreadPool`. Web: `await UniTask.Yield();` e executa no main thread.
  - `internal static class WebBridge` (`YARG.Helpers.Web`), externs `[DllImport("__Internal")]`, só compilados com `UNITY_WEBGL && !UNITY_EDITOR` (no editor e no desktop, stubs que retornam `null`/`false`):
    - `string GetQueryString()`
    - `string StorageGet(string key)` (null se ausente ou indisponível)
    - `bool StorageSet(string key, string value)` (false em exceção)
    - `void DownloadText(string fileName, string text)`
    - `void PickTextFile(string accept, string gameObjectName, string callbackMethod)` — chama `SendMessage(gameObjectName, callbackMethod, conteúdo)`; conteúdo vazio se o usuário cancelar.
    - `void Reload()`
- `YargWeb.jslib`: implementa as funções acima com `UTF8ToString`/`lengthBytesUTF8`/`_malloc`/`stringToUTF8` para strings retornadas; `try/catch` em todo acesso a `localStorage`; download via `Blob` + `<a download>`; seletor via `<input type="file">` criado na hora.

- [ ] **Step 1: Criar `PlatformInfo`, `PlatformTask`, `WebBridge` e `YargWeb.jslib`** com as assinaturas acima.

- [ ] **Step 2: Trocar `Environment.GetCommandLineArgs()` por `PlatformInfo.GetArgs()`** em `CommandLineArgs.cs:50` e `MultiplayerAutoTest.cs:43`.

- [ ] **Step 3: `PathHelper.Init` na web**: `PersistentDataPath = RealPersistentDataPath = "/tmp/yarg"` (criar o diretório) e `StreamingAssetsPath = "/streaming"`; ignora o override por CLI. Desktop sem mudança.

- [ ] **Step 4: Trocar `UniTask.RunOnThreadPool` por `PlatformTask.Run`** nas quatro linhas listadas (Localization, LoadingScreen, ProfileView, ImageExtensions). `CreditsMenu.cs:217` passa a usar `PathHelper.StreamingAssetsPath`.

- [ ] **Step 5: Logger na web**: em `GlobalVariables.Update`, `if (!YargLogger.HasOutputThread) YargLogger.FlushLogQueue();`.

- [ ] **Step 6: Compilar o desktop e gerar os `.meta`**

Run: o comando de compilação Unity em batchmode do `CLAUDE.md`.
Expected: nenhum `error CS`; `.meta` criados para os arquivos novos (Core e Assets).

- [ ] **Step 7: Commit** (os `.meta` do Core no submódulo primeiro, depois o ponteiro e os arquivos do YARG)

```bash
git add Assets/Script/Helpers Assets/Plugins/WebGL Assets/Script/Persistent Assets/Script/Multiplayer/MultiplayerAutoTest.cs Assets/Script/Menu Assets/Script/Localization YARG.Core
git commit -m "Add platform seams for the web build"
```

### Task 4: Persistência web e exportar/importar

**Files:**
- Create: `Assets/Script/Settings/Web/WebConfigStore.cs`, `Assets/Script/Settings/Web/WebConfigTransfer.cs`
- Modify: `Assets/Script/Settings/SettingsManager.cs` (`SaveSettings`, ~linha 404; tabs), `Assets/Script/Settings/SettingsManager.Settings.cs` (botões), `Assets/Script/Player/PlayerContainer.cs:760` (`SaveProfiles`), `Assets/Script/Persistent/GlobalVariables.cs` (`SingletonAwake`), `Assets/StreamingAssets/lang/en-US.json`

**Interfaces:**
- Consumes: `PlatformInfo.IsWeb`, `WebBridge.*` (Task 3), `ConfigBundle` (Task 2).
- Produces:
  - `public enum WebConfigFile { Settings, Profiles }`
  - `public static class WebConfigStore` (`YARG.Settings.Web`):
    - `static void Restore()` — na web, para cada `WebConfigFile` com valor no `localStorage` (e `yarg.schemaVersion == "1"`), grava o texto em `PersistentDataPath/settings.json` ou `PersistentDataPath/profiles/profiles.json`. No desktop, nada.
    - `static void Persist(WebConfigFile file)` — na web, lê o arquivo e grava na chave; também grava `yarg.schemaVersion`. Se `StorageSet` falhar, mostra uma única vez `ToastManager.ToastWarning(Localize.Key("Web.ConfigNotSaved"))` e segue. No desktop, nada.
    - `static string ReadFile(WebConfigFile file)` e `static void WriteFile(WebConfigFile file, string text)` — usados pelo transfer.
  - `public sealed class WebConfigTransfer : MonoBehaviour` — criado sob demanda (`new GameObject("WebConfigTransfer")`, `DontDestroyOnLoad`):
    - `static void Export()` → `WebBridge.DownloadText("yarg-config.json", ConfigBundle.Create(ReadFile(Settings), ReadFile(Profiles)))`.
    - `static void Import()` → `WebBridge.PickTextFile(".json", "WebConfigTransfer", nameof(OnImportFile))`.
    - `void OnImportFile(string text)` — vazio: nada. `TryParse` falhou: `ToastError(Localize.Key("Web.ImportInvalid"))`. Sucesso: `WriteFile` + `Persist` em cada parte presente e `WebBridge.Reload()`.
- Hooks: `SettingsManager.SaveSettings` chama `WebConfigStore.Persist(WebConfigFile.Settings)` depois do `File.WriteAllText`; `PlayerContainer.SaveProfiles` chama `Persist(WebConfigFile.Profiles)` depois do `File.Move`. `GlobalVariables.SingletonAwake` chama `WebConfigStore.Restore()` antes de `ReplayContainer.Init()`.
- Settings: novos métodos `ExportConfig()`/`ImportConfig()` em `SettingContainer`; no tab "General", `new HeaderMetadata("WebConfig")` + `new ButtonRowMetadata(nameof(Settings.ExportConfig), nameof(Settings.ImportConfig))`, ambos com `visibleWhen: () => PlatformInfo.IsWeb` (adicione o overload de `ButtonRowMetadata` com vários botões e `visibleWhen`, e o de `HeaderMetadata` se faltar).
- Localização (`en-US.json`): cabeçalho `"WebConfig": "Settings Backup"`; botões `"ExportConfig": "Export Settings and Profiles"`, `"ImportConfig": "Import Settings and Profiles"`; novo objeto de topo `"Web"` com `"ConfigNotSaved": "Your browser is blocking storage. Settings will not be saved."` e `"ImportInvalid": "That file is not a YARG settings backup."`.

- [ ] **Step 1: Implementar `WebConfigStore` e `WebConfigTransfer`** com as interfaces acima.
- [ ] **Step 2: Ligar os hooks** em `SaveSettings`, `SaveProfiles` e `SingletonAwake`.
- [ ] **Step 3: Botões, metadata e strings** de localização.
- [ ] **Step 4: Compilar o desktop** (comando do `CLAUDE.md`). Expected: nenhum `error CS`.
- [ ] **Step 5: Commit**

```bash
git add Assets/Script/Settings Assets/Script/Player/PlayerContainer.cs Assets/Script/Persistent/GlobalVariables.cs Assets/StreamingAssets/lang/en-US.json
git commit -m "Mirror settings and profiles to localStorage and add export/import"
```

### Task 5: Composição da inicialização web

**Files:**
- Create: `Assets/Script/Audio/Web/SilentAudioManager.cs`
- Modify: `Assets/Script/Persistent/GlobalVariables.cs`, `Assets/Script/Scores/ScoreContainer.cs` (`Init`, `Destroy`, `RecordScore`), `Assets/Script/Integration/DiscordController.cs` (início do método que cria o cliente, ~linha 72), `Assets/Script/Persistent/LoadingScreen.cs` (sources/genres e `RunRefresh`), `Assets/Script/Menu/Persistent/MusicPlayer.cs`, `Assets/Script/Settings/SettingsManager.cs` (`LoadSettings`, tabs), `Assets/Script/Menu/Main/MainMenu.cs` (`Start`)

**Interfaces:**
- Consumes: `PlatformInfo.IsWeb` (Task 3).
- Produces:
  - `public sealed class SilentAudioManager : AudioManager` (`YARG.Audio.Web`): sem dispositivos, sem mixer (`CreateMixer` retorna `null`), volume no-op, `SupportedFormats` vazio, `CreatePushStream` lança `NotSupportedException`. Mesmo estilo de override do `BassAudioManager`. Substituído no subprojeto 2.
  - `GlobalVariables`: `GlobalAudioHandler.Initialize<SilentAudioManager>()` na web, `BassAudioManager` no desktop (único `#if` aqui, por causa do `using YARG.Audio.BASS`).
  - `ScoreContainer.IsAvailable` (`public static bool`): `false` na web; `Init` não abre o SQLite, `Destroy` usa `_db?.Dispose()`, `RecordScore` retorna cedo.
  - Discord: não cria o cliente na web. `LoadingScreen`: na web pula sources, genres e `SongContainer.RunRefresh`. `MusicPlayer`: não toca na web.
  - `SettingsManager.LoadSettings`: na web força `DisableDefaultBackground`, `DisableGlobalBackgrounds` e `DisablePerSongBackgrounds` = `true` depois de carregar.
  - Filtro de settings na web, aplicado no construtor estático de `SettingsManager`: remove as tabs `SongManager`, `FileManagement` e `LightingPeripherals`; remove os campos/botões `OpenVenueFolder`, `DisableDefaultBackground`, `DisableGlobalBackgrounds`, `DisablePerSongBackgrounds`, `WaitForSongVideo`, `AllowRemoteContent`, `DiscordRichPresence`, `SaveMultiplayerSongs`, `ChooseMultiplayerSongsFolder`, `OutputMode`, `OutputDevice`, `AsioBufferSize`, `OpenAsioControlPanel` e `OutputChannel*`; remove os cabeçalhos que ficarem sem itens.
  - `MainMenu.Start` loga `YargLogger.LogInfo("Main menu ready")` (em todas as plataformas). É o sinal do smoke test.
  - Fora desta task, de propósito: tirar do menu principal as entradas sem função na web (spec §7). Isso fica para os subprojetos 3 e 5, que já reescrevem o singleplayer e o online; até lá essas telas abrem vazias.

- [ ] **Step 1: `SilentAudioManager`** e a escolha em `GlobalVariables`.
- [ ] **Step 2: Guardas** de `ScoreContainer`, Discord, `LoadingScreen` e `MusicPlayer`.
- [ ] **Step 3: Backgrounds forçados, filtro de settings e o log `Main menu ready`.**
- [ ] **Step 4: Compilar o desktop e rodar a regressão de multiplayer**

Run: compilação Unity do `CLAUDE.md`; depois `cd Tests/YARG.Multiplayer.Tests && dotnet test`.
Expected: nenhum `error CS`; testes passam.

- [ ] **Step 5: Commit**

```bash
git add Assets/Script
git commit -m "Compose the web startup: silent audio, no SQLite, no library scan"
```

### Task 6: Template, StreamingAssets e build WebGL

**Files:**
- Create: `Assets/WebGLTemplates/YARG/index.html`, `Assets/WebGLTemplates/YARG/TemplateData/style.css`, `Assets/Plugins/WebGL/YargStreamingAssets.jspre`, `Assets/Editor/Build/WebBuild.cs`
- Modify: `Assets/Editor/MultiplayerTestBuild.cs` (`ReimportShaderGraphs` vira `internal static`), `ProjectSettings/ProjectSettings.asset` (gerado pelo editor ao rodar o `WebBuild`)

**Interfaces:**
- Produces:
  - `Editor.WebBuild.Build()` — uso: `-buildTarget WebGL -executeMethod Editor.WebBuild.Build [-webBuildOutput dir]`, padrão `~/yarg-build-web`. Passos: `MultiplayerTestBuild.ReimportShaderGraphs()`, conteúdo Addressables, `PlayerSettings.WebGL` (`memoryGrowthMode = Geometric`, `maximumMemorySize = 2048`, `threadsSupport = false`, `compressionFormat = Brotli`, `template = "PROJECT:YARG"`, `decompressionFallback = false`), `BuildPlayerOptions.extraScriptingDefines = { "YARG_NO_THREADS" }`; depois do build, gravar `StreamingAssets/web-manifest.json` (array com os caminhos relativos de todos os arquivos de `StreamingAssets`, exceto `venue/`). Saída `EditorApplication.Exit(0)` em sucesso, `1` em falha de build, `2` em falha do Addressables.
  - `YargStreamingAssets.jspre`: `Module.preRun.push` com `addRunDependency("yarg-streaming")`; busca `(Module.streamingAssetsUrl || "StreamingAssets") + "/web-manifest.json"`, baixa cada arquivo e grava com `FS.mkdirTree` + `FS.writeFile` em `/streaming/<caminho>`; `removeRunDependency` no fim. Falha de rede: `console.error("[YargWeb] StreamingAssets preload failed: …")` e mostra a tela de erro.
  - `index.html`: canvas em tela cheia; barra de carregamento; overlay `#yarg-start` com "Clique para começar" que some no clique e define `window.yargUserGesture = true`; tela de erro `#yarg-error` com a mensagem e um botão "Recarregar" (`location.reload()`), usada por `createUnityInstance(...).catch` e por `window.onerror` quando a mensagem contém `"memory"`.

- [ ] **Step 1: Template, `.jspre` e `WebBuild.cs`.**
- [ ] **Step 2: Rodar o build WebGL**

Run:
```bash
"$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$PWD" -buildTarget WebGL -executeMethod Editor.WebBuild.Build -logFile - 2>&1 | tee "${TMPDIR:-/tmp}/unity-web.log"
grep -E 'error CS|Build failed|Exception' "${TMPDIR:-/tmp}/unity-web.log"
```
Expected: exit 0; `~/yarg-build-web/index.html` e `~/yarg-build-web/StreamingAssets/web-manifest.json` existem.

- [ ] **Step 3: Corrigir o que falhar só na build WebGL.** Erros esperados: tipos dos plugins nativos (BASS, VLC, Discord, sqlite, LibUSB) e `System.Threading` em código que a web compila. Corrija nos pontos de composição: excluir o arquivo da WebGL com `#if !UNITY_WEBGL` no arquivo inteiro (como os arquivos BASS já fazem com `UNITY_STANDALONE`), ou desmarcar a WebGL na importação do plugin pelo Inspector do editor (nunca à mão no `.meta`). Repita o Step 2 até passar.
- [ ] **Step 4: Recompilar o desktop** (comando do `CLAUDE.md`). Expected: nenhum `error CS`.
- [ ] **Step 5: Commit**

```bash
git add Assets/WebGLTemplates Assets/Plugins/WebGL Assets/Editor ProjectSettings/ProjectSettings.asset Assets/Script
git commit -m "Add the WebGL template, StreamingAssets preload and web build script"
```

### Task 7: Smoke test no navegador

**Files:**
- Create: `Tests/WebSmoke/package.json`, `Tests/WebSmoke/serve.mjs`, `Tests/WebSmoke/boot.spec.mjs`, `Tests/WebSmoke/playwright.config.mjs`, `Tests/WebSmoke/README.md`

**Interfaces:**
- Consumes: build de `~/yarg-build-web` (Task 6), log `Main menu ready` (Task 5), chaves `yarg.*` (Task 4), overlay `#yarg-start` (Task 6).
- Produces:
  - `serve.mjs`: servidor `http` do Node na porta de `YARG_WEB_PORT` (padrão `8089`) servindo `YARG_WEB_BUILD` (padrão `~/yarg-build-web`). Arquivos `*.br` com `Content-Encoding: br` e o `Content-Type` do arquivo sem o `.br` (`.js` → `application/javascript`, `.wasm` → `application/wasm`, `.data` → `application/octet-stream`).
  - `playwright.config.mjs`: só Chromium, `headless: true`, `launchOptions.args` com `--mute-audio`, `webServer` rodando `serve.mjs`, timeout de 180 s por teste.
  - helper `bootToMenu(page)`: navega, clica em `#yarg-start`, espera `Main menu ready` no console e falha se aparecer um `console.error` com `Exception`.

- [ ] **Step 1: Escrever os testes**

```js
test('boots to the main menu', async ({ page }) => { await bootToMenu(page); });

test('profiles round-trip through localStorage', async ({ page }) => {
  await bootToMenu(page);
  let profiles = JSON.parse(await page.evaluate(() => localStorage.getItem('yarg.profiles')));
  expect(await page.evaluate(() => localStorage.getItem('yarg.settings'))).not.toBeNull();
  expect(await page.evaluate(() => localStorage.getItem('yarg.schemaVersion'))).toBe('1');
  // Se não houver perfil, injeta um copiado de Tests/WebSmoke/fixtures/profile.json
  profiles[0].Name = 'Ação 🎸';
  await page.evaluate(p => localStorage.setItem('yarg.profiles', p), JSON.stringify(profiles));
  await bootToMenu(page); // recarrega
  profiles = JSON.parse(await page.evaluate(() => localStorage.getItem('yarg.profiles')));
  expect(profiles[0].Name).toBe('Ação 🎸');
});

test('corrupt profiles still boot', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('yarg.profiles', '{not json'));
  await bootToMenu(page);
});

test('storage failure still boots', async ({ page }) => {
  await page.addInitScript(() => { Storage.prototype.setItem = () => { throw new Error('QuotaExceededError'); }; });
  await bootToMenu(page);
});
```

Para garantir que exista um perfil, gere `Tests/WebSmoke/fixtures/profile.json` copiando um item de um `profiles.json` gerado pelo desktop.

- [ ] **Step 2: Rodar**

Run: `cd Tests/WebSmoke && npm ci && npx playwright install chromium && npx playwright test`
Expected: 4 passed.

- [ ] **Step 3: Corrigir as falhas na tarefa que é dona do código** (persistência → Task 4, boot → Task 5, preload/template → Task 6) e repetir o Step 2.

- [ ] **Step 4: Regressão desktop completa**

Run: compilação Unity do `CLAUDE.md`; `cd YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build`; `cd Tests/YARG.Multiplayer.Tests && dotnet test`; um cenário de banda nativo (`Tests/MultiplayerBand`, `run_scenario.py <cenário> --native`, ver o README dela).
Expected: tudo passa; o cenário imprime `[AutoTest] PASS`.

- [ ] **Step 5: Commit e PR**

```bash
git add Tests/WebSmoke
git commit -m "Add the browser smoke test for the web build"
```

Abra o PR do submódulo (`FelipeFlohr/YARG.Core`, base `master`) e depois o do YARG (`FelipeFlohr/YARG`, base `master`). No corpo do PR do YARG, inclua:
- template, filtro de settings e botões de exportar/importar "não verificados visualmente";
- o fluxo de importar testado só pelos testes do `ConfigBundle`, sem teste no navegador.
