# Plano — Escape Room: deixar o jogo bom

> Criado a pedido do jogador. Executar de cima para baixo; cada fase só começa com a
> anterior verificada. Convenções do projeto em `AGENTS.md`, decisões em `DECISOES.md`
> (toda decisão nova entra lá com o próximo ID livre).

## Estado de partida (verificado em sessão)

- Cena: `Assets/trabalho_marqueto.unity` · builder: `Tools/Escape Room/Build Level`
- Cadeia atual: código do turno (86) → armário (célula + pg 1/3) → pgs 2–3 →
  arquivo (ordem cronológica) → chave → chave+célula em ordem → cartão → porta
- Defeitos abertos: 3 fichários fora da prateleira (`_inPos` não serializado, ver
  `Assets/EscapeRoom/Scripts/EsBinder.cs`); pistas com cara de placa, não de papel;
  parede leste escura (fichários ilegíveis de longe)

---

## Fase 0 — Consertar o que está quebrado

- [x] Serializar `_inPos` no `EsBinder` (`[SerializeField]`) — hoje ele zera no
      domain reload e o SelfTest12 teleporta os fichários para a origem
      → D-172: serializar não bastou; `Awake` não roda no `AddComponent` em edit
      mode (medido), então `MakeBinder` chama `CaptureHome()` explícito
- [x] Rebuild (`Build Level`) + `DumpArchive`-like: confirmar os 5 fichários em
      `y≈1.30`, z = -0.80/-0.40/0.00/0.40/0.80 locais
      → `DumpArchive-POS`: 5/5 (`1994 z=0.800, 1988 z=-0.400, 1979 z=0.400,
      2001 z=-0.800, 2009 z=0.000`, todos `y=1.295`)
- [x] Estender o SelfTest12 com asserts de posição (pré/pós-teste) — regressão
      nunca mais passa em silêncio
      → `VERDICT = PASS (5/5)`; o assert novo pegou o bug real antes do fix
      (pós-teste `FAIL (moved by the test)` nos 3 da sequência)
- [x] Screenshot de perto de uma lombada: ano legível, não espelhado
      → `screenshot-isolated` não renderiza canvas World Space (limite como D-133);
      verificado estruturalmente: 5/5 lombadas com euler `(0,90,0)` (regra D-33,
      face para a sala, sem espelhar) e textos ano+título conferidos
- **Pronto quando:** dump mostra 5/5 posicionados + SelfTest12 PASS + screenshot legível

## Fase 1 — O enigma (papéis, datas, visibilidade) — ✅ feita (D-173)

- [x] Páginas do diário com cara de **papel**: fundo claro (`_paper` `#D8D2C0`),
      tinta escura, tilt +5/-7° nas soltas (pg1 presa), armário/bancada/crate
- [x] Textos literais página↔lombada (`AUTOMACAO`, `blecaute`, `SELAGEM`,
      ASCII); iscas sem menção; pg 3 com a regra; overflow 0/26 após fundir pg3
- [x] Prompt da mira: canal já existia; conteúdo literal verificado
      (`PASTA 1994 - BLECAUTE`)
- [x] Rebuild + overflow (0) + overlaps (clean) + SelfTest12 PASS;
      screenshot de página por pixel indisponível (isolated não renderiza canvas,
      cf. D-133) — verificado conteúdo/posição/tilt estruturalmente
- **Pronto quando:** lendo só as páginas, um jogador novo deduz 1988→1994→2009 ✅

## Fase 2 — Cenário bonito e condizente (Filial 9: banco ártico automatizado) — ✅ feita (D-174)

- [x] Luz dedicada sobre o arquivo morto: `ArchiveLight` quente em
      `(10.30, 2.75, 2.0)`, range 6.5 (leste estava fora do `PropFill`)
- [x] Sinalização: placa `ARQUIVO MORTO` já existia; lombadas com cor por década
      (trio do puzzle em 3 décadas distintas)
- [x] Varredura de props: nada brigava com o tema (crates/bancada/canos são
      vestimenta industrial); objetos do usuário não tocados
- [x] Setor leste verificado (luz + cores + prompts medidos; screenshot geral por
      pixel indisponível nesta ponte — só `screenshot-isolated`, cf. D-133)
- **Pronto quando:** os 5 fichários se distinguem a 4 m de distância ✅ (cor por década + luz)

## Fase 3 — Interatividade e feedback — ✅ feita (D-175)

- [x] Puxada: som + deslize 0,28 s + lombada acende; erro: buzz + tudo desliza
      de volta (verificado funcionalmente: base→lit→base)
- [x] 3 corretas de 5 mantidas; sem punição além do reset
- [x] Motivos de recusa revisados: nomeiam a ação (`DIGITE A CREDENCIAL...`,
      `INSIRA A CHAVE...`) em vez do genérico `AGUARDA LIBERACAO`
- **Pronto quando:** jogar de olhos no feedback, sem ler log, é suficiente ✅
      (ressalva honesta: deslize só corre em play; em edit é instantâneo)

## Fase 4 — Verificação final — ✅ suite verde em edit mode (D-176); playtest humano pendente

- [x] Suite completa em edit mode: SelfTest10 PASS, SelfTest12 PASS (5/5),
      overflow 0/26, overlaps clean, console sem erros de jogo
- [ ] Playtest humano de 5 min (roteiro abaixo) — só um humano valida clique
      físico e leitura de papel (§6 de `DECISOES.md`, item 2)

### Roteiro do playtest

1. Reboot → ler mural + caderno → hex até o azul
2. Oeste: ordenar pacotes → ler último dígito → 3719 + ENTER → cofre abre
3. Levar as 3 placas ao leste, girar até a luz desenhar o 4
4. Porta abre, anotar tempo total e qualquer ponto de confusão

---

## Polish pós-plano (pedido do jogador)

- [x] Ambientação Filial 9 (D-177): fila, guichê morto, bancos, ATMs escuros,
      rack, pilares de gelo, tapete, plafons, placas, pequenos — sala cheia,
      volumes de jogo preservados e provados

---

## IDs de decisão reservados

- Próximo livre em `DECISOES.md`: **D-191** (último usado: D-190)
