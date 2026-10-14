# AGENTS.md — Escape Room (Unity)

## LEIA PRIMEIRO

**`DECISOES.md`** na raiz do projeto. Contém o registro de auditoria com **todas as
decisões tomadas, o motivo de cada uma e a evidência que a motivou** (IDs `D-01`..`D-28`).
Não reverta uma decisão sem antes ler a justificativa lá. Se precisar tomar uma decisão
nova, **adicone-a ao `DECISOES.md`** com o próximo ID livre.

## Contexto rápido

- Unity `6000.6.2f1` · URP 17.6.0 · Cinemachine 6.6.0
- **`activeInputHandler: 1` → só Input System.** Nunca use `Input.GetKey`/`Input.GetAxis`
  legado. Use `UnityEngine.InputSystem` (`Keyboard.current`, `Mouse.current`).
- Cena de trabalho: `Assets/trabalho_marqueto.unity` (110 objetos do usuário — **não
  remover nem reposicionar**; o set novo é adicionado ao redor).
- Scripts do projeto: `Assets/EscapeRoom/Scripts/` · builder de editor:
  `Assets/EscapeRoom/Editor/EsSceneBuilder.cs` (menu **Tools/Escape Room/Build Level**)
- Áudio: `Assets/EscapeRoom/Audio/` (gerado por `generate_audio.py` via `uv run`)

## Regras operacionais ao usar o MCP do Unity

1. **Chamadas de tool do Unity são SEQUENCIAIS.** Nunca `Promise.all` com tools `unity.*` —
   duas em paralelo falham com `No Unity Editor instances found` (corrida na seleção de
   instância do servidor).
2. Após recompilar scripts ou trocar de cena, há **domain reload**: a primeira chamada
   pode falhar com `No Unity Editor instances found`. É transitório — reconecte com
   `set_active_instance` e tente de novo.
3. **Nunca** dispare `File > New Scene` / `close_scene`: um modal trava a main thread do
   Unity e mata a ponte MCP.
4. A sessão MCP precisa estar ativa no Unity (**Window → MCP for Unity → Start Session**).
   `AutoStartOnLoad` é `false` por padrão, então isso é manual a cada abertura.
   **A porta não é fixa** — depois de um crash o Unity pode voltar noutra (o 6401 foi o
   caso real aqui). Confira com `mcpforunity://instances` e use a porta que aparecer,
   não a 6400 por hábito.
5. Confira erros de compilação com `read_console` depois de criar scripts.

## Testes em play mode (armadilha que custou duas sessões)

**O editor Unity não avança frames em play mode sem `Application.runInBackground`.**
Com a janela do Unity sem foco, `isPlaying=True` e `isPaused=False`, mas
`Time.frameCount` fica em 2 e `Time.time` em 0.02. Qualquer teste baseado em tempo ou
corrotina fica **inconclusivo, e dá a ilusão de bug**.

A regra que salva:

- Código **antes** do primeiro `yield` funciona sem frames (corre de forma síncrona) —
  por isso o primeiro tempo de um `ShowEnd`/`SetStatus` "funciona" e faz o resto parecer morto.
- Código **a seguir** a um `yield` precisa de frames para existir.

**Antes de qualquer teste temporal em play:** entrar em play, correr
`Application.runInBackground = true` via `execute_code`, e confirmar que `Time.frameCount`
cresce ao longo de um `Start-Sleep`. Só depois testar. Registado em `DECISOES.md` D-223.

Notas de `execute_code` (CodeDom, C# 6) que custaram tempo:

- Nada de `out var`, lambdas locais, funções locais, `cachedTextInfoForRendering`.
- `System.Component` não existe nessa referência — use `UnityEngine.Component`.
- `try { } catch { }` de uma linha parte o compilador.
- Campos **privados** não aparecem em `GetField` sem
  `BindingFlags.Instance | NonPublic | Public`.
- Prefira localizar o objeto por `mb.GetType().Name == "..."` a varrer assemblies:
  `asm.GetTypes()` pode lançar em assemblies parciais e mata o snippet inteiro.

## Restrições de UI (armadilhas conhecidas)

- **A cena não tem `EventSystem`.** Criar com **`InputSystemUIInputModule`**, nunca
  `StandaloneInputModule` — com Input System ativo o botão morre em silêncio.
- **TextMeshPro não tem os Essential Resources importados** neste projeto. Usar
  `UnityEngine.UI.Text` + `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")`.
- UI usa cursor travado: o clique é resolvido pelo raycast do centro da tela.

## Estado atual

Implementação em andamento. Checklist vivo e status de conformidade por requisito no
`DECISOES.md` (§5 e §6).
