# 7. Vigia de E-mail (triagem em dois modelos)

> Discutido em 01/09/2026. Nada implementado ainda — este arquivo é o rastro da decisão.
> Encosta na ideia [01 (Aspirador Matinal)](01_Aspirador_Matinal_RAG.md), que também fala em
> ler e-mail corporativo. A diferença: lá o e-mail é fonte de conhecimento para RAG; aqui ele
> é fila de trabalho a ser triada.

## O Problema (nas palavras do usuário)

> *"O meu volume de trabalho atual faz com que seja difícil eu achar uma janela para verificar
> os e-mails, e o volume de e-mails faz com que eu perca muito tempo quando faço."*

Não é falta de percepção de urgência. É que **abrir a caixa custa caro** e não há janela para
pagar esse custo. Portanto a entrega **não é um alarme** — é um substituto para abrir a caixa.

O que compra tempo não é a lista do que importa. É o número de descartados ser confiável o
bastante para o usuário **não abrir a caixa**. Todo o resto do desenho existe para sustentar
esse número.

## Cenário

Duas contas, ambas Gmail:

- pessoal
- corporativa (Google Workspace, acessada só pelo navegador — **não há Outlook desktop**, então
  o caminho por COM está descartado)

## Medições da máquina (01/09/2026)

Números reais, não estimativas. São eles que decidem o desenho.

| | |
|---|---|
| RAM total | 15,7 GB |
| RAM livre (com o 9B fixado) | **2,1 GB** |
| `qwen3.5:9b` | 6,6 GB, residente `Forever`, 100% CPU, ctx 16384 |
| `qwen3.5:4b` | 3,4 GB, instalado |
| `qwen3.5:0.8b` | **não instalado** — falta `ollama pull` |
| `OLLAMA_MAX_LOADED_MODELS` | 2 (escopo de usuário) |
| Geração, 9B | ~3,2 tok/s |
| Prefill, 9B | ~34 tok/s |

Consequências diretas:

- **O 0.8b é o único que cabe** ao lado do 9B. O 4b (3,4 GB) não entra em 2,1 GB livres — ele
  despejaria o 9B. Não é preferência, é aritmética.
- Com 1,4 GB de folga depois de carregar o pequeno, **paginação é risco real**. Num setup
  CPU-only, paginar é catastrófico — pior que qualquer custo que se tentava economizar.
- Por isso: **o pequeno NÃO fica fixado.** O 9B fica com `keep_alive=-1` porque carregar 6,6 GB
  do disco é caro; o 0.8b carrega em ~1 s e sai depois (`keep_alive=2m`). Nas 19 h do dia em que
  não há sondagem, a máquina fica como está hoje.
- Confirmar que o serviço do Ollama subiu **depois** de `MAX_LOADED=2` existir. Se for mais
  velho que a variável, ainda está rodando com 1 e o primeiro teste mostra o 9B sendo despejado.

## A restrição que manda em tudo: o cache de prefixo

Uma completação fora de banda **despeja o cache de prefixo da conversa**. O modelo continua
residente, mas o próximo turno do usuário paga prefill frio: ~4.000 tokens a 34 tok/s ≈ **2 min
de espera**, uma vez por acordada.

Logo: **acordar o 9B é caro em tempo do usuário, não em CPU.** Sondar de 20 em 20 minutos com o
9B seria trocar 6 min de CPU por dia por várias esperas de 2 minutos. Péssimo negócio.

Daí a separação em três laços com cadências diferentes.

## Os Três Laços

```
a cada 20 min   código   vigias, rajadas, remetentes prioritários → acorda o 9B na hora
                0.8b     "chegou algo que parece pedir resposta" → linha provisória
a cada 20 min   código   lista aberta: quem já foi respondido? quem envelheceu?
3×/dia          9B       reescreve as linhas, monta o digest, atualiza os vigias
```

Horários do digest: **8h25 / 12h55 / 16h55** (antes das janelas naturais de leitura).

A entrada converge para quase nada — no estado estável chegam 1 ou 2 mensagens por sondagem, e
quase sempre o 0.8b descarta as duas sem acordar ninguém. **O trabalho do estado estável é a
lista aberta**, e ela não usa modelo nenhum.

## O Funil

| Degrau | Quem | Custo | O que faz |
|---|---|---|---|
| 0 | **Gmail** | zero | `category:primary`, `\Important` — o Google já classificou |
| 1 | **Regras** | zero | To vs Cc, `noreply@`, lista de remetentes |
| 2 | **0.8b** | ~1 s/msg | descarta só o obviamente automático |
| 3 | **9B** | 1 lote | *o que estão me pedindo, e até quando* |

O degrau 0 é o achado que mais economiza trabalho: as categorias do Gmail (Promoções, Social,
Atualizações, Fóruns) e o marcador `\Important` já são calculados no servidor, de graça. Não há
por que reimplementar heurística de newsletter.

### O degrau 2 é invertido de propósito

**O modelo pequeno só tem permissão para dizer "ignorar com certeza". Qualquer outra coisa sobe.**

Um 0.8b não sabe quando está errado — modelos pequenos são confiantes até quando erram. Se ele
decidir o que sobe, os falsos negativos dele nunca chegam ao 9B, nunca chegam ao usuário e não
aparecem em lugar nenhum. E o produto inteiro se apoia no número de descartados ser confiável.

Confirmando descarte óbvio, o erro dele fica barato: um e-mail chato subindo, em vez de um
e-mail importante sumindo.

A escalação **não pergunta ao pequeno se ele está seguro**. Sai de regras sobre a saída dele:

- disse "ignorar" mas o remetente já escreveu antes → **sobe**
- disse "ignorar" mas há resposta sua naquela thread → **sobe**
- disse "ignorar" mas o texto tem data, prazo ou "?" → **sobe**
- remetente aparecendo pela primeira vez → **sobe**
- saída não fez parse → **sobe**

Nicho onde ele ganha o pão: **mensagem que parece humana mas é automática** — notificação de
sistema escrita em prosa, resposta de férias, e-mail de ferramenta imitando gente. Regra não
alcança, e não vale acordar o 9B.

## Como os dois modelos "conversam"

Ideia do usuário: o 9B avisa o pequeno quando algo merece acordá-lo de imediato.

**Correção necessária:** o canal é **estado estruturado lido por código**, nunca prosa
interpretada pelo 0.8b. Instrução sutil em linguagem natural seria aplicada de um jeito hoje e
de outro amanhã — um vigia que às vezes vigia.

```jsonc
// ~/.AIB/email/vigias.json    escrito pelo 9B, lido por CÓDIGO
[
  { "tipo":"thread", "thrid":"17ab…", "porque":"você respondeu; aguarda retorno do Cliente X",
    "ate":"2026-09-08", "acorda9b":true },
  { "tipo":"remetente", "email":"diretoria@empresa.com.br", "acorda9b":true, "toast":true },
  { "tipo":"rajada", "email":"firewall@empresa.com.br", "n":3, "janela":"30m" }
]
```

É a mesma divisão que a memória da AIB já usa — **literal vem do código, prosa vem do modelo**.
O 9B decide *o que merece vigia e por quê*; a comparação é `thrid == thrid`: exata, instantânea,
grátis.

O ciclo fecha sozinho: o 9B escreve os vigias, o código os aplica, o resultado volta ao 9B no
digest seguinte, que ajusta ou expira cada um. Auditável — dá para abrir o arquivo e ver por que
houve interrupção.

**A decisão de interromper o usuário fica inteira na parte que não alucina.**

### "Aguardo resposta" sai de graça

Detectar que o usuário respondeu é IMAP puro: mensagem em `[Gmail]/Sent` com a mesma
`X-GM-THRID`. Sem modelo. Permite a regra:

> **Toda thread onde o usuário enviou algo nos últimos N dias vira thread vigiada.**

Quem responde geralmente espera retorno. O 9B só acrescenta o *porquê* legível, o prazo, e se
aquela vigia acorda na hora ou espera o digest.

## Rajada: o caso do firewall

Cenário real do dia 01/09: o firewall manda e-mail a cada degradação de link. Chegaram vários de
uma vez — e era a coisa mais urgente do dia.

**O desenho original teria jogado todos fora.** Remetente `noreply`, automático, repetitivo:
todos os filtros baratos matam exatamente isso. A premissa errada era tratar "automático" como
sinônimo de "ruído". A regra certa:

> **Mensagem automática sozinha é ruído. Doze em quarenta minutos é um incidente.**

O sinal não está em nenhuma mensagem — está no **volume e no ritmo**. Um funil que tria mensagem
a mensagem é estruturalmente cego para isso, por mais esperto que seja o modelo.

**Divisão de trabalho:**

- **Código** conta, agrupa por remetente/janela, extrai por regex nomes de interface, horários e
  status. Os números são exatos porque não passaram pelo modelo.
- **9B** lê os fatos já extraídos e escreve o parágrafo legível.

Se o modelo alucinar, alucina na prosa; os fatos acima continuam certos. Um 9B contando 12
mensagens sem trocar `WAN1` por `WAN2` é aposta perdida.

Saída pretendida:

> **⚠ ACONTECENDO AGORA · firewall · desde 09:14**
> 12 alertas em 47 min, ainda abertos.
> `WAN2 (Vivo)` 8 alertas · perda 40% · sem recuperação
> `WAN1 (Claro)` 4 alertas · recuperou 09:31
>
> O link da Vivo está oscilando desde as 9h14 e não voltou. O da Claro caiu duas vezes e
> normalizou.

**Limite honesto:** a LLM diz *o que os e-mails dizem*, não o que está acontecendo com a rede.
Ela não diagnostica. Mas "a Vivo está oscilando há 47 minutos e não voltou" é o que se precisava
saber às 9h20.

**Rajada interrompe na hora** — é o único caso em que o toast em tempo real está certo. Um
incidente às 9h14 descoberto no digest das 12h55 não vale nada, e aqui acordar o 9B se paga.

**Fecha sozinho:** chegou "link restored", sai da tela. Parou de chegar alerta por 30 min,
envelhece para "provavelmente resolvido".

## Arquivos

```
~/.AIB/email/
  regras.md      do usuário, editável, no espírito do facts.md
  vigias.json    escrito pelo 9B, lido por código
  estado.json    por conta: uidValidity, lastUid, lista de abertos
```

```jsonc
// estado.json
{
  "conta": "corporativo",
  "uidValidity": 8271,     // mudou? o lastUid virou lixo: resemeia por data
  "lastUid": 91043,
  "abertos": [ { "thrid":"…", "de":"…", "pedido":"…", "desde":"…" } ]
}
```

`regras.md` mínimo — lista de fontes de alerta, com limiares que só o usuário sabe:

```
firewall@empresa.com.br   → rajada a partir de 3 em 30 min
nobreak@empresa.com.br    → rajada a partir de 2 em 15 min
```

**Nenhum corpo de mensagem em disco.**

## Regras Invioláveis

1. **`BODY.PEEK[]`, sempre.** `FETCH BODY[]` marca a mensagem como lida. Isso não seria um bug —
   seria a AIB destruindo o estado da caixa do usuário, que é o sinal que ele usa para se achar.
2. **V1 é estritamente somente leitura.** Não envia, não arquiva, não marca, não apaga.
3. **Conteúdo de e-mail nunca entra no `raw.jsonl` nem vira capítulo.** Só o veredito. Sem isso,
   o resumidor lê e-mail corporativo e ele reaparece num prompt semanas depois.
4. **Se um dia houver resposta automática, ela nasce como rascunho.** Salva, nunca enviada — o
   portão de confirmação aplicado ao que sai da caixa com o nome do usuário.
5. **Enviesado para recall.** Na dúvida, sinaliza. Um alerta a mais custa 3 segundos; um a menos
   custa o que já se perde hoje.
6. **A lista de descartados é auditável.** Sem poder conferir o que a triagem jogou fora, ela
   vira confiança falsa — e confiança falsa num ponto fraco reconhecido é pior que nada.

## Decisões Técnicas Tomadas

| Decisão | Por quê |
|---|---|
| Serviço nativo em C#, **não skill** | tem agendador, aba própria e dependência de biblioteca; skill é para extensão do usuário |
| **MailKit** (NuGet, MIT) | .NET não traz cliente IMAP, e o PowerShell também não |
| Senha de app no `SettingsService` | ele **já** cifra com DPAPI `CurrentUser` — nenhuma criptografia nova a escrever |
| Uma implementação, duas contas | as duas caixas são Gmail: mesmo código, mesma autenticação |
| Dedup por `Message-ID` | encaminhamento entre as contas, ou cópia nas duas, duplicaria a mesma mensagem |
| Arranque com `newer_than:3d` | triar backlog é trabalho jogado fora; ninguém lê 300 pendências de três meses. Backlog vira comando manual explícito |

## Estágios

| | Entrega | Vale sozinho? |
|---|---|---|
| **V1** | 2 contas, degraus 0/1/3, digest 3×/dia, rajada, aba "Atenção". **Sem 0.8b.** | Sim |
| **V2** | degrau 2 (0.8b) + sondagem de 20 min + lista aberta que envelhece | Sim |
| **V3** | `regras.md` completo + correção com um clique ("isso importava") | Sim |
| **V4** | rascunho de resposta salvo no Gmail | Sim |

A rajada entra **na V1** apesar de parecer avançada: ela muda a cadência do laço, e um laço só
de 3×/dia não vira laço de 20 min sem reescrita. É o tipo de coisa que não se enxerta depois.

O 0.8b fica para a V2 de propósito — é otimização de um custo **ainda não medido**. Se o lote de
3×/dia custar 1 minuto, não há o que otimizar.

## Como Validar o 0.8b (V2)

Não dá para decidir no papel. Uma semana com o degrau 2 **registrando o que descartaria sem
descartar de verdade** — tudo sobe para o 9B do mesmo jeito. No fim, comparar as colunas:

- concordância > 95% nos descartes → liga o corte
- abaixo disso → joga fora uma semana de log, em vez de e-mails

Mesmo padrão da medição de raciocínio no histórico, em que a hipótese estava errada e o número
disse. 0.8b em português, com e-mail corporativo, é território que ninguém mediu.

## Bloqueios em Aberto

1. **Pré-voo das senhas de app** (só o usuário pode fazer) — em cada conta:
   `myaccount.google.com` → Segurança → Senhas de app; e Gmail → Configurações → POP/IMAP →
   IMAP ativado. O admin do Workspace pode bloquear a conta corporativa.
   - Funcionou nas duas → V1 com as duas caixas
   - Só a pessoal → V1 com uma; a segunda entra depois atrás da mesma interface
   - Nenhuma → plano B: OAuth com a Gmail API, projeto no Google Cloud, refresh token no mesmo
     DPAPI. Mais trabalho, e o admin também pode barrar.
2. **`ollama pull qwen3.5:0.8b`** (~600 MB) — só para a V2.
3. **Confirmar `MAX_LOADED=2` no processo do Ollama**, não só na variável de usuário.
4. **Interface** — a discutir: onde o digest aparece, como é a aba "Atenção", como a rajada
   interrompe sem virar a interrupção que o sistema existe para evitar.

## Consideração Não-Técnica

Guardar credencial da caixa corporativa num projeto pessoal é decisão de política, não de
engenharia. Vale estar deliberado sobre isso.

A favor: **nada sai da máquina.** Ollama é localhost, a triagem roda local, nenhum conteúdo vai
para nuvem. É bem diferente de plugar a caixa corporativa num SaaS de produtividade.
