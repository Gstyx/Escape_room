# COMO JOGAR EM VR — guia para a feira

Decisao tecnica completa em `DECISOES.md` **D-234**. Este arquivo e o passo a passo.

## Hardware suportado

**PC VR via OpenXR**, com o jogo rodando no computador:

| Headset | Como conectar |
|---|---|
| Meta Quest 2 / 3 / 3S / Pro | **Meta Quest Link** (cabo USB-C) ou **Air Link** (Wi-Fi 5 GHz) |
| Valve Index, HTC Vive, Pico (via Streaming) | **SteamVR** |
| Windows Mixed Reality | SteamVR ou o runtime WMR |

> Quest **standalone** (APK, sem PC) exige o modulo **Android Build Support** no Unity Hub,
> que nao esta instalado. Depois de instalar, rode o menu de configuracao de novo
> (ele detecta o modulo) e troque a plataforma para Android.

## Configuracao (uma vez)

1. Abra o projeto no Unity **6000.6.2f1**.
2. Menu **Tools > Escape Room > VR > Configure VR (OpenXR)**. Ja foi rodado; pode rodar
   de novo sem medo (e idempotente — e tambem o botao "conserta o VR").
3. Defina o **runtime OpenXR ativo** do PC:
   - Quest: app **Meta Quest Link** > Configuracoes > Geral > *Runtime OpenXR* > **Definir Meta Quest Link como ativo**.
   - SteamVR: SteamVR > Configuracoes > OpenXR > **Definir SteamVR como runtime OpenXR**.

## Testar no editor

1. Conecte o headset (Link ativo / SteamVR aberto).
2. Abra `Assets/trabalho_marqueto.unity` e de **Play**. O console mostra
   `[VR] Rig VR ativo: ...`. Sem headset aparece `Nenhum headset ativo - jogo segue no
   modo desktop` e o jogo funciona com mouse e teclado como antes.

## Gerar o executavel para a feira

**File > Build Profiles > Windows > Build.** A cena do jogo ja e a cena 0. Um mesmo `.exe`
serve para VR e desktop: com headset conectado ele abre em VR.

## Controles

| Acao | Controle |
|---|---|
| Interagir (pegar, largar, inserir, apertar botao) | **Gatilho** ou **grip**, em qualquer mao |
| Andar | **Analogico esquerdo** (na direcao que voce olha) |
| Girar 30° | **Analogico direito** para os lados |
| Andar de verdade | permitido, a sala acompanha (*room-scale*) |
| Ver tempo restante | olhe para o **relogio no pulso esquerdo** |

A mao que aperta o gatilho vira o ponteiro. O laser fica **verde** quando o que esta na mira
reage ao clique; um rotulo junto ao ponto do laser diz o que vai acontecer.

## Operador da feira (entre um visitante e outro)

| Acao | Como |
|---|---|
| **Reiniciar a partida** | **F5** no teclado do PC, ou visitante segura o **botao menu esquerdo por 2 s** |
| **Recentralizar** (visitante virado para o lado errado) | **F6**, ou clique no **analogico direito** |

Dicas:
- Marque no chao o ponto em que o visitante deve ficar e aperte **F6** com ele olhando para frente.
- O monitor do PC espelha a visao do headset — bom para o publico acompanhar.
- O suporte vital e de **60:00**. Para filas longas, reduza `reserveSeconds` no
  `EscapeGameManager` (o relogio comeca no INICIAR).

## Limitacoes conhecidas

- **Nao foi testado com headset real** nesta sessao (nenhum disponivel). Faca um playtest
  completo antes da feira: intro > malha hex > logs > cofre (3719) > placas > cartao > porta.
- Itens sao pegos pelo laser (alcance de 3,4 m), nao encostando a mao no objeto.
- Locomocao suave pode enjoar quem nunca usou VR: prefira que o visitante ande de verdade
  e use o analogico so para distancias maiores.
