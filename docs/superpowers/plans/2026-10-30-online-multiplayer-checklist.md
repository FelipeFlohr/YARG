# Multiplayer online — verificação ponta a ponta

## Como foi testado

O fork compila e gera build desde que a branch `fix/sync-upstream-dev` trouxe o `upstream/dev` (o mesmo commit de
`YARG.Core` que o fork aponta). A entrada "Online Multiplayer" está no menu principal.

As partidas foram jogadas com o modo `-mp-autotest` (`Assets/Script/Multiplayer/MultiplayerAutoTest.cs`): duas
instâncias do build no mesmo PC, cada uma com sua pasta de dados (`-persistent-data-path`), perfis bot, volumes
zerados e janelas minimizadas. A música foi "Subdivisions" (`.sng`, guitarra Expert, 1039 notas).

```
YARG.exe -persistent-data-path <host> -mp-autotest host -mp-rounds 2 [-mp-leave-after 20]
YARG.exe -persistent-data-path <cliente> -mp-autotest join 127.0.0.1 [-mp-leave-after 20 | -mp-expect-leave]
YARG.exe -persistent-data-path <dados> -mp-autotest offline <jogadores>
```

Cada instância loga as etapas com o prefixo `[AutoTest]` e fecha com código 0 (PASS) ou 1 (FAIL). Toda espera tem
timeout (2 min por etapa, 10 min pela música).

## Step 1 — suíte do núcleo

- [x] 2026-10-31 — `dotnet test Tests/YARG.Multiplayer.Tests` → 111/111 aprovados.

## Step 2 — duas instâncias em localhost

1. [x] 2026-10-31 — Cliente entra no lobby, inclusive depois de o host já ter oferecido a música.
   Versão diferente → recusada pelo protocolo (`VersionMismatch`, coberto pelos testes; não jogado).
2. [x] 2026-10-31 — Cliente que já tem a música → vai direto para "tem a música" (2ª rodada).
3. [ ] Pasta ini que o cliente não tem — não jogado (o download de ini é coberto pelos testes do núcleo).
4. [x] 2026-10-31 — `.sng` que o cliente não tem → baixa, instala e aparece na biblioteca.
5. [ ] Música CON → toast de erro no host — não jogado.
6. [x] 2026-10-31 — Começar → as duas instâncias iniciam juntas.
7. [x] 2026-10-31 — A pista remota reproduz a partida: a mesma pontuação nos dois lados.
8. [x] 2026-10-31 — Pontuação e star power iguais aos do offline (443.085 online × 442.285 offline solo e dupla
   local; 10 ativações de star power nos dois).
9. [x] 2026-10-31 — Tela de resultados com os stats de cada um, uma vez por rodada.
10. [x] 2026-10-31 — Pause duplo no cliente → ele volta ao menu sem sessão; o host termina a música e a pista do
    cliente é baixada (drop-out).
11. [x] 2026-10-31 — Host sai no meio da música ou fecha a sala → o cliente recebe "the host left" e volta ao menu.
12. [ ] "Salvar músicas" desligado → a música some ao sair — não jogado.

Regressão offline: [x] 2026-10-31 — Quick Play com 1 e 2 bots locais (`-mp-autotest offline`). Replay e "tocar com
replay" não foram jogados.

## Bugs achados jogando, todos corrigidos

| Bug | Commit |
|---|---|
| Conectar a `127.0.0.1` ou a um IP do Tailscale falhava no Mono (socket IPv6 dual-mode) | `ddec9941` |
| Cliente travava em "Downloading 100%" (scan sem tela de loading dava exceção) | `73d51651` |
| Entrar na sala com música já oferecida era recusado como "sessão em andamento" | `4f9d87b1` |
| Música baixada e apagada continuava no cache → o carregamento falhava no meio da partida | `f586da52` |
| Recorde do jogador remoto buscado no banco local → exceção e falso "novo recorde" | `f586da52` |
| Resultados mostrados duas vezes → replay salvo e pontuação gravada em dobro | `f586da52` |
| Bot remoto errava todas as notas na outra máquina e queimava o star power do jogador local | `3c7dadcc` |
| Pista de quem saía da partida ficava congelada na tela | `3c7dadcc` |
| Host começava a 2ª música enquanto o cliente ainda carregava o menu → exceção e a partida não começava | `5aaa19bb` |

## Step 3 — duas máquinas via Tailscale

Pendente. Repetir os itens 1, 4, 6, 7 e 11 usando o IP `100.x.y.z` do host, com jogadores humanos.
