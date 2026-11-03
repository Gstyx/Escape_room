# COMO ESCAPAR — os tres enigmas, como funcionam e o que exigem

> **Atualizacao D-235 (modo feira):** os enigmas foram facilitados. Malha: basta a energia chegar
> ao receptor (sem regra de vazamento). Logs: horarios `07:13 09:27 12:41 16:59` e a tela mostra a
> senha 3719. Placas: cada botao GIRAR fica verde quando a placa esta certa. O texto abaixo descreve
> a versao estrita (`EsHexGrid.easyMode = false`).


Precede `LORE.md` e responde a uma pergunta que a lore sozinha nao responde: **que
pessoa tem que ser o jogador** para estes tres enigmas fazerem sentido. Le a secao 4
depois de ler os tres.

Tudo aqui foi lido do codigo (`EsHexGrid.cs`, `EsLogSortPuzzle.cs`, `EsSafeKeypad.cs`,
`EsPlateReader.cs`, `EsAcrylicPlate.cs`) e das linhas citadas. Nada e suposicao sobre
como o jogo se sente; a parte cognitiva e leitura minha e esta marcada como tal.

---

## 1. O QUE VOCE TEM DE Fazer (a rota)

O mural e o terminal dao a ordem. Os tres station estao em paredes diferentes, e o fluxo
atravessa a sala de proposito (D-189):

```
   hex (norte)  ->  logs + cofre (oeste)  ->  placas + leitor (sudeste)  ->  porta (norte)
```

Isso e proposito de design, nao acaso: o jogador **nao fica num corredor**. Ele sai da
parede norte, anda ate a oeste, atravessa a sala, resolve no canto sudeste, e so entao
volta pra porta norte. A travessia e o que faz a sala ser uma sala em vez de um menu.

---

## 2. ENIGMA 1 — MALHA HEXAGONAL (o reativador)

### A regra, literalmente

Cada hexagono tem **1 ou 2 pinos** (portas) numa borda, e pode girar em 6 posicoes.
Dois hexagonos sao fixos: **emissor** (esquerda) e **receptor** (direita).
Uma **malha** e o conjunto de hexagonos que a energia alcança a partir do emissor,
seguindo porta-encontra-porta.

Vence quando **as duas coisas** valem:

1. o **receptor esta alimentado**, e
2. **nenhuma malha energizada vaza** — nenhuma porta energizada pode apontar para
   o vazio (borda inclusive) ou para um vizinho sem a porta correspondente.

`EsHexGrid.StrictSolved`, linhas 423-442:

```csharp
if (!live.Contains((cols - 1) * rows + r0)) return false;   // receptor escuro
foreach (int key in live)
    for (int d = 0; d < 6; d++)
        if (!Neighbour(c, r, d, ...)) return false;           // vazio = vazamento
        if ((EffMask(nc, nr, ...) & (1 << Opp(d))) == 0) return false;  // sem par = vazamento
```

### Por que a regra 2 existe (e por que ela e justa)

Sem a regra 2, bastava ligar emissor->receptor pelo caminho mais curto e pronto. Com ela,
o jogador tem de **cuidar das pontas**: os dois **terminadores** (caps, D-184) sao
hexagonos de **1 pino** que, se ficarem vazar, reprovam o tabuleiro **mesmo com o
receptor aceso**. O D-180 mediu isso: as portas do caminho nunca se encostam (0/400), entao
o jogo do vazamento mora **inteiro** nesses dois caps.

O jogador que entende isso antes de tentar e um jogador que pensa em **rede**, nao em
caminho. Quem so conecta emissor->receptor trava e sobe a perceber que "chegar" nao e o
mesmo que "estar fechado".

### O que a estetica diz

- hex emissor/receptor: **ambar**, fixo
- hex de 1 pino (cap): **teal** (D-183) — a cor que marca o que precisa ser plugado
- hex energizado: tile ciano, com pulso viajando emissor->receptor (D-181)
- apagado/corrompido: quase preto

### O que o jogador recebe

`SetPowerRestored()` — a luz vermelha de emergencia vira azul, e a sala para de estar em
quarentena visivel. **A recompensa do passo 1 e uma mudanca no estado do mundo**, e e a
primeira prova de que agir funciona.

---

## 3. ENIGMA 2 — LOGS E COFRE (a prova)

### A regra

Quatro **pacotes de log** com timestamp, fora de ordem, num console. O jogador clica um
pacote e clica um slot: o pacote vai para o slot (click-click, **nao** arrastar — com o
cursor travado o clique resolve no centro da tela, D-65/D-71/D-99, entao um drag real nao
tem como ser mirado).

Ordena **do menor para o maior** (o proprio texto do puzzle diz). A solucao existe: o
`SelfTest14` confirma `solved=True pin=3719` e que a ordem embaralhada e **recusada**
(scrambled accepted = False).

O **PIN do cofre e 3719**: `EsSafeKeypad` valida `correctPin` contra o array ordenado,
e o PIN vem do **ultimo caractere de cada pacote na ordem** (D-185). Entao o PIN **nao e
arbitrario**: ele e a resposta da etapa anterior. Digitar 3719 no teclado abre o cofre.

Dentro do cofre: **3 placas acrilicas** (D-186), inalcancaveis ate a porta do cofre
swingar — a mesma garantia de geometria do armario antigo (D-32).

### Onde isso fica na parede

O `SafePad` foi **subido** nesta sessao (D-198): antes, y 0,88..1,56 ficava 37% atras da
porta do cofre. Agora e 1,16..1,84, centro na altura do olho, 100% visivel (medido por
raycast: 0 de 11 amostras bloqueadas).

### O que o jogador recebe

Um **objeto fisico**: as placas. A etapa 2 nao e so "achar um numero", e "abrir e pegar
coisa". E a unica vez no jogo em que o jogador sai de uma estacao com algo na mao.

---

## 4. ENIGMA 3 — PLACAS (o override)

### A regra

Cada placa e uma grade **4x4 de pixels**. Um pixel aceso **bloqueia** a luz (o codigo
chama isso: `0 = light passes`). Aplicando as 3 placas em conjunto com operador **OR**,
o leitor revela **um digito**. O alvo e o digito **4** (D-186; antes eram 2 digitos e nao
cabia em 16 px).

O gabarito (verificado por execucao contra o template, `match=True`):

```
0110      placa 1 = rotacao   0 graus
0110      placa 2 = rotacao 270 graus
0000      placa 3 = rotacao 180 graus
1110
```

Por comutatividade do OR, **a ordem das placas nao importa**. O jogador encaixa as 3,
depois usa os **botoes GIRAR** do leitor (nunca clicando na placa — clicar nela seria
pick-up, porque a placa assentada continua `GrabbableItem`; dai o `pickupLocked`).

Aprovado -> o leitor dispensa o `Cartao de Saida` no tabuleiro -> o jogador leva-o ao
soquete da porta (`CARTAO DE SAIDA`, no sítio da `SealPlate`) -> a folha gira, o `Bulkhead`
recolhe -> o jogador atravessa a porta e o volume de saída dispara `Win()` (D-230). O painel
de vitória aparece **depois da porta**, nunca em cima do leitor.

### O que o jogador precisa perceber

- **16 pixels so mostram 1 digito** — o jogador precisa ler uma imagem, nao um numero
- **acender = bloquear**, o oposto do instinto
- **OR comutativa** remove a ansiedade de ordem
- e, critico: **a solucao esta escrita na placa**. Ele nao precisa deduzir nada; precisa
  **reconhecer** o 4 na luz.

### O que o jogador recebe

A porta. O jogo acaba.

---

## 5. O QUE CADA ENIGMA PEDE DA CABECA DO JOGADOR

Aqui e leitura minha (marcado como tal), e e a resposta a tua pergunta. Os tres nao pedem
a mesma coisa:

| Enigma | Operacao mental | Equivalente real |
|---|---|---|
| hex | **prevenir vazamento** — ver o sistema inteiro, nao o caminho | engenharia de rede: fechar um circuito sem deixar boleado |
| logs | **ordenar por dado** e **extrair um valor** de algo lerdo | working com log/timestamp: o detalhe e o objetivo |
| placas | **reconhecer um padrao** e **aplicar uma regra** | ler um indicador: o 4 ja esta la, e o trabalho e limpar o ruido |

Ou seja: **diagnostico -> dado -> leitura**. Nenhum dos tres e "achar a senha escondido".
Os tres pedem que o jogador **olhe para o sistema**, nao para o objeto.

E um detalhe que so aparece quando os tres estao juntos: **as tres premiacoes sao de
tipos diferentes**. hex muda o mundo (luz), logs da um objeto (a placa), placas acaba
o jogo. A progressao e de "entender" -> "ter" -> "sair".

---

## 6. QUEM TEM QUE SER O JOGADOR (a resposta direta)

O caderno na parede chama-se `CADERNO DO TECNICO` (D-190). O que os enigmas exigem e o
que decide isso:

**O jogador tem que ser alguem que ja mexeu com isso.** As tres tarefas sao de
*operacao*, nao de misterio. Ninguem que nunca viu um log resolve a etapa 2 por deducao —
eles **sabem** que timestamp ordena. Ninguem que nunca viu circuito resolve a etapa 1
por tentativa, eles pensam em porta-sem-par. E a etapa 3 assume que alguem **sabe ler
indicador**, nao que alguem adivinhe.

Duas leituras, como no `LORE.md`:

**A — o jogador e o tecnico.** O caderno e dele. Ele ja sabe tudo isso. O impacto e
"fui trancado aqui dentro e preciso sair". O caderno **explica os tres passos** porque
ele mesmo escreveu as instrucoes. **Custo zero de texto** — o caderno ja esta na parede,
o titulo ja assina a autoria, e o corpo dele ja diz "EMISSOR ATE O RECEPTOR / DO MENOR
PARA O MAIOR / GIRE ATE A LUZ DESENHAR O 4", que sao literalmente as instrucoes dos tres.

**B — o jogador foi contido.** O A.E.G.I.S. trancou ele e ele **nao** escreveu o
caderno. Isso muda o peso: fechar a malha deixa de ser "achar o caminho" e vira
**concordar com o protocolo**, e a unica saida e a release que ele deveria estar
impedindo. Custa reescrever `A.E.G.I.S. VIU UM FANTASMA` para "selou **voce**", e
transforma o caderno de "instrucoes" em "prova deixada por alguem".

**Recomendacao: A como base, com um gancho de B na vitoria.** O jogador comeca neutro e
descobre no fim que foi contencao dirigida — o golpe cai quando a porta abre, e custa
**uma frase** no painel de vitoria. Uma frase e a unica coisa que compra o momento mais
importante do jogo.

---

## 7. O QUE A LORE NAO PODE QUEBRAR

Regras que valem pra qualquer texto que a gente escrever:

1. **O caderno tem que continuar sendo a dica que faz o jogo ser vencivel.** Os tres
   enigmas sao resolviveis porque ele diz o operador de cada um. Se a lore "enfeitar" o
   caderno e esconder a dica, o jogo fica impossivel. (`UIOverflow 0/64` segue verde.)
2. **O mural promete "ENERGIA, LOGS E PLACAS"** - e o terceiro passo E placas. Esta regra
   nasceu errada: o mural dizia `CHAVE`, heranca da cadeia `credencial > armario > chave >
   cartao` que o D-190 apagou, e foi corrigido no D-213. Fica aqui para lembrar que o mural
   tem de descrever o jogo que existe, nao o que existiu.
3. **A vitoria diz `QUARENTENA ENCERRADA`** - e esta correta. Encerrar a quarentena E o
   isolamento acabar, E a saida. Tive argumentado o contrario duas vezes e estava errado nas
   duas: argumentar por semantica propria sobre texto de ficcao nao tem autoridade.
4. **O mural nomeia o PRAZO, nunca o AGENTE** (D-224). Diz `A CONTAGEM CORRE ATE A
   PURIFICACAO. 60:00.`, o que da urgencia sem dizer o que a purificacao e. Nomear o agente
   transformaria o medo num enigma - o jogador ia caçar algo em vez de sentir um sistema.
5. **Nenhum texto pode sugerir que existe um 4o enigma.** O mural diz "3 passos"; se a
   lore falar de "camadas", tem que ser exatamente 3. O `Cartao de Saida` (D-230) nao e um
   4o passo: e transporte, o premio fisico do 3o. Nao tem enigma, tem soquete.

---

## 8. O QUE ESTE DOCUMENTO NAO AFIRMA

- Nao afirma nada que eu nao tenha lido no codigo: as regras vem com arquivo e linha.
- A secao 5 e 6 (o que cada enigma exige da cabeça) e **leitura/interpretação minha**, nao
  medicao. O codigo me diz o que o puzzle *e*, nao o que o jogador *sente*.
- Nao-touch nos self tests, nos puzzles, ou no `DECISOES.md`.
