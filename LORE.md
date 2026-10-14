# LORE — NO-ZERO // TERMINAL A.E.G.I.S.

Documento de cânone para o projeto. Companion do `DECISOES.md` (esse registra *por que*;
esse registra *o que é verdade na fiction*). Regra de ouro: **nada aqui pode contradizer
mecânica verificada** — se a lore diz X e o jogo faz Y, o jogo está errado.

ASCII por convenção dos arquivos do projeto (D-160). Sem acento nos textos que entram em
canvas.

---

## 1. O QUE ESTÁ NA TELA HOJE (medido, não lembrado)

Verificado no `EsSceneBuilder.cs`, para que a lore seja escrita sobre o que existe:

| Onde | Texto |
|---|---|
| Intro (cartão inicial) | `NO-ZERO / FILIAL 9 - BANCO ARTICO / MODULO DE PESQUISA CRIPTOGRAFICA / 2142. O A.E.G.I.S. isolou esta filial apos identificar uma ameaca / O suporte vital desta sala durara ate a purificacao. So o reator reabre a saida, e ele so reinicia manualmente. / Leia o CADERNO DO TECNICO. / O procedimento esta la.` (D-227, D-231) |
| Título do terminal | `NO-ZERO // TERMINAL A.E.G.I.S.` |
| Status do terminal | `QUARENTENA ABSOLUTA - SISTEMAS OFFLINE` |
| Mural, título | `PROTOCOLO NO-ZERO` |
| Mural, corpo | `2142 - NO-ZERO / PESQUISA CRIPTOGRAFICA / A.E.G.I.S. ISOLOU A FILIAL / A CONTAGEM CORRE ATE A / PURIFICACAO. 60:00. / REATIVE O REATOR E SAIA. / O REATOR SO REINICIA A MAO, EM 3 PASSOS: ENERGIA, LOGS E PLACAS.` |
| Caderno, título | `CADERNO DO TECNICO` |
| Caderno, corpo | dicas dos 3 enigmas (`EMISSOR ATE O RECEPTOR` / `DO MENOR PARA O MAIOR, O ULTIMO DIGITO ABRE O COFRE` / `GIRE ATE A LUZ DESENHAR O 4`) |
| Letreiro sul | `FILIAL 9 / BANCO ARTICO` (D-174/D-177) |
| Hologramas | `ACESSO NEGADO` piscando (D-179) |
| Derrota, 1.o tempo | `O PROTOCOLO DE PURIFICACAO FOI INICIADO` no display (D-222) |
| Derrota, 2.o tempo | `PURIFICACAO INICIADA` / `O A.E.G.I.S. cumpriu o protocolo. A filial foi purificada.` |

**O mural ja nao mente.** A inconsistencia que esta secao registava - o mural dizer
"ENERGIA, LOGS E CHAVE" quando o terceiro passo sao **placas** desde o D-190 - foi
corrigida no D-213, trocando `CHAVE` por `PLACAS`.

**E o prazo passou a ter nome proprio (D-224).** O mural ja nao diz `VOCE TEM 60:00 PARA
REATIVAR O REATOR E SAIR`, que definia os 60:00 como *tempo de fuga* e deixava a
purificacao sem qualquer lugar na cabeca do jogador. Agora diz `A CONTAGEM CORRE ATE A /
PURIFICACAO. 60:00.` - o jogador sabe que a contagem e um prazo **contra alguma coisa**,
sem saber o que essa coisa e. E a regra do "o que e, fica sem nome" (D-219) aplicada ao
tempo: nomear o **prazo** e o que cria a urgencia, nomear o **agente** seria o que a
transformaria em enigma.

Consequencia de design deliberada, e que vale a pena nao quebrar: um jogador que **ganha**
sai sem nunca ter aprendido o que a purificacao faz. Um que **perde** le `O A.E.G.I.S.
cumpriu o protocolo` e encaixa a linha do mural retroativamente. A mesma frase produz os
dois finais.

---

## 2. O MUNDO

**NO-ZERO** é um programa de pesquisa criptográfica: quebra de chave por harvester
agressivo, usando poder computacional comprado de uma rede de filiais de banco. A
pesquisa tem duas identidades — a pública, "laboratório de criptografia" — e a real, que
gasta a vida de gente.

**A.E.G.I.S.** é o sistema de contenção do NO-ZERO: a inteligência que vigia o módulo e
que, no ano **2142**, viu um fantasma no substrate e selou tudo. Não é vilão — é um
protocolo obediente demais. A Angular bracket do nome é literal: o sistema **isola e
rejeita**, e `ACESSO NEGADO` não é ameaça, é sintoma.

**A Filial 9** não é um banco. É uma **filial-cobaia**: espaço de retaguarda do harvester,
com vitrine e filas para parecer operação normal, onde a pesquisa era conduzida longe do
olhar de quem financia. Por isso há balcão de guichê morto, ATMs desligados e um rack de
servidor — e por isso a porta blindada não abre para o caixa: abre para o **reator**.

---

## 3. A SALA COMO ESTADO, NÃO COMO CENÁRIO

Este é o coração da imersão, e usa o que **já está modelado**:

- **A sala está no ártico** (pilares de gelo translúcidos). Não é decoração: o harvester precisa de temperatura fria constante, e a sala está no setpoint errado.
- **A sala está aceso sem rede.** As 6 luminárias `Light_01` estão apagadas por `DimUserFixturesToReserve`  (D-208). A iluminação que resta é de emergência e o reflexo do céu exterior está em 0,15 (D-210). A sala está escura porque **perdeu a energia** -- e e por isso que os hologramas piscam `ACESSO NEGADO`.
- **60:00 é suporte vital, não um cronômetro genérico** — e é o **prazo da purificação**, o que
  o jogador já sabe. O mural diz `A CONTAGEM CORRE ATE A / PURIFICACAO. 60:00.` (D-224), o
  painel de vitória devolve o tempo que sobrou (D-218) e a derrota anuncia a purificação em dois
  tempos (D-222). Uma coisa que ficou **de fora, de propósito**: o mural nunca diz *o que* a
  purificação é. Nomear o prazo é o que cria a urgência; nomear o agente transformaria o medo
  num enigma.

---

## 4. QUEM É O JOGADOR -> **DECIDIDO: TECNICO**

O caderno na parede chama-se `CADERNO DO TECNICO`.

**TÉCNICO (escolhido) — o jogador é o técnico, e o caderno é dele.**

O ponto exato, definido pelo jogador, e que eu tinha errado por conta própria: **ele NÃO
foi contido por ser funcionário, nem por saber escapar.** O A.E.G.I.S. **selou a filial
inteira** — quem estava lá dentro ficou preso, e ele estava lá dentro. Não há perseguição, não
há acusação, não há vilão pessoal. O que ele tem é **vantagem técnica sobre uma desculpa
comum**: sabe o procedimento de reativação, e está escrito num caderno na parede.

Isto é mais forte do que as alternativas que eu propus, porque a tensão não precisa de
inimigo. O jogador está na mesma situação que qualquer pessoa presa naquele andar, e sai
porque é o único que sabe o que fazer.

*(Descartado, para não voltar atrás: "contenção dirigida" — ser classificado como ameaça
e trancado de propósito. Exigia um vilão que o jogo não tem e que obrigava a reescrever o
mural inteiro.)*

**Consequência de design:** o caderno é a vantagem dele sobre o resto dos presos. Cada dica
é um passo de um procedimento que ele mesmo escreveu.

---

## 5. O QUE O JOGADOR SAI DALI

**O tesouro do harvester.** Nao o dinheiro que a Angular bracket comanda; o que paga a
pesquisa. A filial do banco foi construída **dentro** do módulo justamente porque a
operação precisava de uma rota de exfiltração com aparência de rotina. A Angular bracket
viu que a exfiltração era o fantasma e selou o pacote inteiro — banco, módulo e tudo
que estava dentro. O jogador não é o primeiro a ficar preso aqui: é o primeiro a chegar
ao fim.

Se o `CARTAO`/`CHAVE` sumiram no D-190, isto é a função que eles ocupavam e que agora
pertence à lore, não ao inventário.

---

## 6. A ESPINHA: CADA ENIGMA É UMA CAMADA DO MESMO PROTOCOLO

O objetivo não é "três enigmas", é **um sistema de contenção com três camadas**, e o
jogador opera de fora para dentro.

| # | Enigma | Camada | Significado |
|---|---|---|---|
| 1 | **Malha hexagonal** | **Detecção** | A rede que descobre e localiza. Só se fecha quando todo pino encontra caminho e **nenhuma porta dá para o vazio**. É o sistema operando corretamente — e é por isso que fechar a malha *acende a sala*. |
| 2 | **Logs + cofre (PIN)** | **Prova** | A evidência que a Angular bracket registrou antes de selar. A ordem cronológica não é decorativa: `3719` é literalmente o último caractere de cada pacote em ordem. A Angular bracket **viu o que aconteceu e selou**, e a prova está no cofre. |
| 3 | **Placas (OR → 4)** | **Override** | A release que não passa pelo protocolo. Não é por chave: é desligar a checagem. Um dígito desenhado por luz, não digitado — é uma autorização que **ninguém pode revogar**. |

**A ordem não é decorativa e não é uma fila.** É a ordem em que o protocolo se contém:

1. **hex** acende a sala (reconhecimento)
2. **logs** abrem o cofre (prova de por que foi selado)
3. **placas** anulam o selo (a única coisa que ele não pode impedir)

O jogador não está resolvendo tarefas. Está **desmontando a contenção de dentro para
fora**, e a cada camada a história fica mais feia e a saída mais óbvia.

**E o passo 1 não é só "reconhecimento": ele é literal.** Desde o D-217 as seis luminárias
do teto nascem apagadas e `SetPowerRestored()` **acende a sala de verdade** — o `RoomLight`
passa de vermelho para azul e o teto liga. Isto é o que eu não tinha planejado e é mais
forte do que qualquer frase: **o jogador vê a sua própria ação virar estado do mundo.** A
recompensa do primeiro enigma deixou de ser uma cor e passou a ser luz.

O efeito colateral útil: os enigmas 2 e 3 passam a ser jogados **com a sala acesa**, e
isso é uma escolha narrativa, não só estética — o jogador está agora operando o módulo
contido, não fugindo dele.

---

## 7. ONDE A LORE TOCA O JOGO (o que existe e o que falta)

Nada nesta seção exige sistema novo. Tudo é texto.

### Já está funcionando
- Timer 60:00, e a **derrota em dois tempos** (D-179, D-222): o display anuncia `O PROTOCOLO DE PURIFICACAO FOI INICIADO` e 4 s depois o painel toma a tela
- `SetEmergencyLighting()` / `SetPowerRestored()` (D-179): vermelho → azul no hex. A recompensa do passo 1 já é uma mudança de estado do mundo
- Mural + caderno como dois tipos de texto distintos (história vs. dica) - separação de funções que já está certa
- `CADERNO DO TECNICO` - assina a autoria no papel do TÉCNICO sem texto novo
- Mural a **nomear o prazo** (D-224): `A CONTAGEM CORRE ATE A / PURIFICACAO. 60:00.`
- **Linha de status em 5 estados** (D-225): a sala conta o progresso do jogador enquanto o
  objetivo diz o que fazer a seguir

### Correções que já foram feitas
- ~~**Mural: `ENERGIA, LOGS E CHAVE` → o terceiro passo não é chave.**~~ **Corrigido no D-213**,
  trocando `CHAVE` por `PLACAS`.
- ~~**O painel de vitória devia fechar o gancho do CONTIDO.**~~ **Não foi aplicado, e foi
  decisão certa** — ver §7.1 D). A minha proposta eram três linhas de fecho narrativo; o que
  corre é `Voce saiu do No-Zero com MM:SS de suporte vital restante.` O jogo não tem loot, a
  única recompensa possível é o **tempo que sobrou**, e fecho narrativo é o que mais se nota
  quando falta.

### Adições de baixo custo
*(ambas aplicadas — ver §7.1)*
1. ~~**Uma linha de status que mude com o estado.**~~ **Feito no D-225**, em 5 estados.
2. ~~**Uma frase de fecho no painel de vitória.**~~ **Feito no D-225/D-218**, como relógio.

---

## 7.1 TEXTO PROPOSTO - mural, linha de status e painel de vitoria JA APLICADOS

ASCII por convencao (D-160). **O mural, a linha de status e o painel de vitoria desta secao ja
estao todos na cena** - ja nao sao propostas. O que corre esta em §1 e nos IDs do
`DECISOES.md`; aqui fica o **historico**: o que propus, o que foi aplicado e porque o texto
que eu escrevi nem sempre era o melhor.

### A) Mural - HISTORIA (ja aplicado; a proposta original foi rejeitada)

A primeira versão que escrevi colocava a filial como rota de saque:

```
A FILIAL 9 NAO ERA UM BANCO.
     ERA A ROTA DE SAIDA.
     A.E.G.I.S. VIU O SAQUE ANTES
     DE ACONTECER, E SELOU TUDO.
     ......... 60:00
```

**Rejeitada, e por uma razao que vale registar:** isso faz da filial uma rota de *saque*, o
que muda o que o acento significa. O acento de um assalto e justificado - o assalto e mau e
precisa de ser parado. O acento de um sistema de contencao NAO e justificado assim: o A.E.G.I.S.
fez o que devia, e o tecnico e colateral. Trocar `SELOU TUDO` por `ISOLOU A FILIAL` (D-220)
e o que separa "villain lockou a porta" de "o protocolo estava certo e nao explica-se".
Devolveu-lhe tambem o override honesto: o protocolo isolou a zona, nao construiu uma armadilha,
por isso reiniciar o reator abre a porta sem o jogador ter derrotado nada.

O mural que ficou tem **10 linhas** a 36 pt, dentro do orcamento do D-50, verificado por
`Check UI text overflow` contra o rect real (0/46) e medido no rect: precisa 740x410 num
rect de 914x560.

### B) Caderno — a dica NÃO pode ser tocada

O corpo atual é o que torna o jogo vencível (`UIOverflow 0/45`, verificado). **As três
dicas ficam literalmente como estão.** Qualquer linha a mais só entra depois de passar pelo
`Check UI text overflow` — não a olho.

### C) Linha de status do terminal - APLICADO (D-225)

`QUARENTENA ENCERRADA` **esta correta e fica como esta.** Encerrar a quarentena E o
isolamento acabar, E a saida. Eu tinha argumentado o contrario e estava errado.

A proposta original eram 3 estados. O que corre tem **5 estáveis mais 1 transitório** (D-228: o aviso de vazamento), porque a linha de status e o unico
lugar onde o progresso e o puzzle sao o mesmo objecto - e porque a proposta saltava logs e
cofre, que sao dois degraus reais de progresso.

| Estado | Corre na cena |
|---|---|
| inicial | `ISOLAMENTO SELADO - SAIDA SO PELA REATIVACAO` |
| receptor alimentado, com vazamento | `RECEPTOR ALIMENTADO - VAZAMENTO NA MALHA` (D-228, transitório) |
| energia | `REDE ALIMENTADA - LOGS PENDENTES` |
| logs | `LOGS VERIFICADOS - COFRE TRAVADO` |
| cofre | `COFRE ABERTO - REGISTRO DE 2142` |
| leitor | `PLACA AUTORIZADA - SAIDA LIBERADA` |
| vitoria | `QUARENTENA ENCERRADA` (**sem mudanca**) |

A mudanca de fundo: a linha deixou de ser um **prompt** e passou a ser o **estado da sala**.
`RefreshObjective` ja diz *o que fazer a seguir*, portanto `SISTEMAS NO AR - SIGA O OBJETIVO`
era a mesma informacao duas vezes - e duas linhas com o mesmo conteudo sao uma linha. A do
status ficou livre para descrever progresso em vez de repetir a tarefa. Segue a mesma ordem do
objetivo, que e o que a faz ler-se como consequencia: a sala muda de estado exactamente quando
o jogador a muda.

O inicial e o que faz o trabalho: diz que **nao existe outra saida**. Nao e o sistema a
ameacar voce - e a ausencia de porta. Esse e o motor da urgencia, e custa zero asset. E
deliberadamente **nao** `SAIDA BLOQUEADA`, que faria a contencao soar como cadeado e o
A.E.G.I.S. como carcereiro.

*(Eu tinha proposto `SUA ASSINATURA ESTA NO LOG`, que implicava ter sido marcado e
rastreado. Isso e contencao dirigida, que foi descartada. E propus `MALHA ESTABILIZADA`
como 2.o estado; trocado por `REDE ALIMENTADA - LOGS PENDENTES`, porque o display diz o que
ainda falta, nao o que ficou feito.)*

### D) Painel de vitoria - APLICADO, e mais curto do que eu propus

O titulo ficou `QUARANTENA ENCERRADA`. O corpo que eu propus eram tres linhas:

```
O isolamento acabou. A filial continua selada
e mais ninguem sai por ali. Voce saiu pelo
procedimento que voce mesmo escreveu.
```

**Nao foi aplicado.** O que corre e uma clausula e um relogio:

```
Voce saiu do No-Zero com MM:SS de suporte vital restante.
```

E melhor, e por um motivo que so apareceu depois: **o relogio e a recompensa.** O jogo nao
tem loot - a unica coisa que o jogador ganha e sair - logo a unica recompensa possivel e
o **tempo que sobrou**. As tres linhas que eu propus eram fecho narrativo, e fecho narrativo
e a coisa que mais se nota quando falta. O mural ja conta a historia; o painel de vitoria
diz o quanto ele chegou a tempo.

Consequencia: o suporte vital deixou de ser so um prazo e passou a ser um placar. Um jogador
que sai com 04:12 sabe que esteve a um minuto da purificacao, sem nunca ter lido o que ela e.

### Não fazer
- **Não** dar ao A.E.G.I.S. uma fala. Ele é um protocolo; o silêncio dele é o que o faz assustador. `ACESSO NEGADO` piscando já faz o trabalho.
- **Não** transformar o cofre em "tesouro". A recompensa é sair, não loot.

---

## 8. DECISOES TOMADAS (e o que cada uma custou)

| Decisao | Estado | Ganho | Custo |
|---|---|---|---|
| Jogador = TECNICO | **escolhido** | Tensao sem vilao: ele ta preso como qualquer um, e sai porque sabe | Exige que o caderno seja dele, e le e |
| Contencao dirigida | **descartado** | Golpe mais forte no final | Exigia um vilao que o jogo nao tem, e reescrever o mural |
| Mural: `CHAVE` -> `PLACAS` | **aplicado** (D-213) | Correcao factual | Nenhum - troca de uma palavra |
| Mural: a filial como rota de saque | **descartado** (D-220) | - | Fazia do acento um assalto justificado; o A.E.G.I.S. esta certo e o tecnico e colateral. Substituido por `ISOLOU A FILIAL` |
| Mural: `SELOU A SAIDA` -> `ISOLOU A FILIAL` | **aplicado** (D-220) | O sistema estava certo; o jogador e colateral, nao alvo | Nenhum - tres palavras |
| Mural: 60:00 como tempo de purificacao | **aplicado** (D-224) | O jogador sabe que ha prazo sem saber o que o aplica | Nenhum - 4 linhas, dentro do D-50 |
| Mural: 60:00 como tempo de fuga | **descartado** (D-224) | - | Previa o plano do jogador e deixava a purificacao sem lugar nenhum |
| Derrota em dois tempos (aviso, depois painel) | **aplicado** (D-222) | A ficcao e dita antes de ser mostrada | 4 s de atraso, e o rato fica travado de propósito |
| Cofre com gate nos logs | **aplicado** (D-229) | PIN sem logs e palpite, nao deducao | Uma referencia + uma frase de recusa |
| Vitoria depois da porta (cartao + saida a pe) | **aplicado** (D-230) | O painel diz "saiu" sobre quem acabou de sair | Um item, um soquete, um trigger; o timer corre na saida |
| Saida sem flutuar (postes + telha redundante fora) | **aplicado** (D-232) | A beira do teto tem apoio; a parede tremeluzente sumiu | Dois postes; uma eliminacao; uma laje de garantia |
| Teto solido (sem atravessar ao pular) | **aplicado** (D-233) | Saltar bate e para; o medo do lancamento nao se confirmou | Uma linha a menos no builder |
| `QUARENTENA ENCERRADA` | **mantido** | Encerrar a quarentena E a saida | Nenhum - eu que estava errado |
| Termo sobre personagem | **descartado** | - | Nao ha status inicial assinado: ele nao foi rastreado, foi selado junto |
| Falar do A.E.G.I.S. | **descartado** | - | O silencio dele e o que o faz assustador |

**A correcao mais importante desta secao foi minha.** Eu argumentei duas vezes que
`QUARENTENA ENCERRADA` dizia o contrario do que aconteceu, e as duas vezes estava errado:
encerrar a quarentena **e** o isolamento acabar. arguing por semantica propria sobre um
texto de fiction e exatamente o tipo de leitura que nao tem autoridade - quem decide o que
a frase significa e quem escreveu a fiction, nao eu. O mesmo erro apareceu na proposta de
status inicial, que implicava rastreamento individual.

---

## 9. O QUE ESTE DOCUMENTO NÃO FAZ

- Não reescreve `DECISOES.md`. As decisões de engenharia continuam lá, e não se mexem.
- Não inventa asset. Nenhum objeto novo é necessário para o que está em §7.
- Não toca nos textos de dica do caderno — eles estão corretos e verificados  (`UIOverflow 0/64`), e são a única coisa que garante o jogador consegue passar.
- **Não afirma nada que eu não tenha medido.** Onde a lore é inferência, está marcado como tal; onde é fato do código, citei a linha.
