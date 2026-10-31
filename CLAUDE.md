# YARG (fork de FelipeFlohr)

Fork do YARG (Unity 6000.3.5f2, URP). O backend fica no submódulo `YARG.Core` (fork `FelipeFlohr/YARG.Core`), que o Unity carrega como pacote local (`Packages/manifest.json` → `file:../YARG.Core/YARG.Core`).

## Ambiente desta máquina

Ubuntu Server 24.04, sem sudo, GPU integrada Intel HD 630, sem display. Todas as ferramentas são locais do usuário:

- `.NET SDK 10` + runtime 8 em `~/.dotnet`
- `git-lfs` e `gh` em `~/.local/bin`
- Unity Editor em `~/Unity/Hub/Editor/6000.3.5f2/Editor/Unity`

Se `dotnet`/`gh` não estiverem no PATH do shell:

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
```

## Verificação

YARG.Core (rápido, sempre rode depois de mexer no Core):

```bash
cd YARG.Core && dotnet build YARG.Core.sln && dotnet test YARG.Core.UnitTests --no-build
```

Multiplayer (compila `Assets/Script/Multiplayer/Core` fora do Unity):

```bash
dotnet test Tests/YARG.Multiplayer.Tests
```

Projeto Unity (lento; o primeiro import leva muito tempo; exige licença ativada). Compila os scripts em batchmode sem GPU:

```bash
~/Unity/Hub/Editor/6000.3.5f2/Editor/Unity -batchmode -nographics -quit -projectPath "$PWD" -logFile - 2>&1 | tee /tmp/unity.log
grep -E 'error CS|Compilation failed|Scripts have compiler errors' /tmp/unity.log
```

Pacotes NuGet (`Assets/Packages`, fora do git) são restaurados pelo NuGetForUnity ao abrir o editor. Em batchmode o Unity aborta antes disso se o projeto não compila; se `Assets/Packages` estiver faltando, rode uma vez com `-ignorecompilererrors` para restaurar e depois repita sem a flag.

Não dá para jogar nem inspecionar visualmente aqui. Mudanças visuais (cenas, prefabs, UI, shaders) precisam ser marcadas como "não verificadas visualmente" no PR.

## Regras de trabalho

- Assets `*.png/*.jpg/*.exr/*.fbx/*.ttf` estão no Git LFS.
- Code style: siga o `.editorconfig`.
- Arquivos Unity (`.unity`, `.prefab`, `.asset`, `.meta`) são YAML gerado pelo editor. Evite editar à mão; nunca apague nem regenere `.meta` (os GUIDs quebram referências).
- Trabalhe em branches (`claude/<assunto>`), nunca direto em `master`. Abra PRs contra `master` deste fork (`FelipeFlohr/YARG`), não contra o upstream `YARC-Official`.
- Mudanças no Core: commit/PR no repositório do submódulo primeiro, depois atualize o ponteiro do submódulo no YARG.
- `CONTRIBUTING.md` descreve as regras do upstream; este fork tem direção própria (por exemplo, lift notes no 5LK).
