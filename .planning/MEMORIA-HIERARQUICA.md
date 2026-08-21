# Proposta: memória hierárquica por compactação

**Status:** proposta, não implementada.
**Origem:** padrão observado em jogos de RP com IA — a cada N mensagens vira capítulo, a cada N capítulos vira ato.
**Substitui:** o `Trim` atual, que apaga mensagens antigas em vez de destilá-las.

---

## 1. Por que

O `ConversationService.Trim` hoje é descarte puro:

```csharp
int firstRemovable = (_history[0] is SystemChatMessage) ? 1 : 0;
while (acimaDoOrcamento) _history.RemoveAt(firstRemovable);
```

O começo da conversa deixa de existir. Numa cadeia de ferramentas longa, o modelo perde justamente os passos iniciais — o plano que ele mesmo traçou.

Compactação hierárquica preserva a **informação** e descarta os **tokens**.

O ganho estrutural é o caminho de ESCRITA. Sistemas de memória costumam falhar porque
dependem do modelo decidir "isso vale guardar" — e ele nunca decide, ou decide mal. Aqui a
promoção é por **volume**, não por julgamento: nada precisa ter bom senso. O que sobrevive até
virar fato durável é o que apareceu com frequência.

---

## 2. Os três níveis

| nível | conteúdo | quando nasce | teto |
|---|---|---|---|
| **0 — vivo** | mensagens cruas | sempre | resto do orçamento |
| **1 — capítulo** | resumo de um bloco de turnos + artefatos literais | orçamento vivo estoura | proporcional |
| **2 — ato** | resumo de capítulos + fatos promovidos | N capítulos acumulados | proporcional |

**A unidade de compactação é o TURNO COMPLETO**, nunca a mensagem solta: começa numa mensagem
de usuário e termina no texto final do assistente, incluindo todos os pares
`assistant(tool_calls)` + `tool(result)` no meio.

Isto não é detalhe de estilo. O provider rejeita `tool_calls` sem o `tool(result)` de id
correspondente — foi exatamente o bug do `RemoveRange` do warmup que o refactor corrigiu.
Compactar por mensagem individual reintroduz a mesma classe de falha.

---

## 3. O que NUNCA é resumido

Num RP, resumir *"Carlo entrou na taverna"* preserva tudo que importa. Aqui, resumir
`C:\Users\Carlo\CPAPS\AIB\AIBWindows\Services\Ai\OllamaProvider.cs` como *"um arquivo do
provider"* **destrói** a informação: o valor está no literal exato.

Todo capítulo carrega duas partes:

```
RESUMO (gerado pelo LLM, narrativo, perde detalhe por design)
ARTEFATOS (extraídos por código, literais, nunca passam pelo modelo)
```

Artefatos extraídos, por ferramenta:

| ferramenta | extrai |
|---|---|
| `write_file` | caminho absoluto resolvido, tamanho, criado/sobrescrito |
| `read_file` | caminho absoluto resolvido |
| `run_command` | comando exato, e se falhou, a mensagem de erro literal |
| qualquer | recusa do usuário no portão (o que foi negado, e quando) |

Extração é **código puro**, não chamada de modelo. Determinística e testável.

---

## 4. Ordem do prompt: por frequência de mudança

Regra que vale mais que qualquer outra aqui — **o que muda menos vem primeiro**:

```
[SOUL do personagem]        ← muda quando o usuário troca de persona
[SYSTEM_PROMPT base]        ← praticamente imutável
[FATOS duráveis]            ← muda a cada promoção de ato (raro)
[ATOS]                      ← muda a cada N capítulos
[CAPÍTULOS recentes]        ← muda a cada compactação
─────────────────────────────
[mensagens vivas]           ← muda todo turno
```

Motivo: cache de prefixo. Medido nesta máquina com qwen3.5:9b — turno que só ACRESCENTA ao
final reaproveita o prefixo e custa **2.672 ms**; turno que muda o começo custa **50.536 ms**.

Colocar os fatos depois dos capítulos invalidaria o prefixo inteiro toda vez que um capítulo
nascesse. Nesta ordem, só a parte instável é reavaliada.

---

## 5. Orçamento proporcional, não fixo

Teto por nível (`LevelService.GetMaxTokensForLevel`): 8192 no nível 1, 12288 no 9.

Alocação fixa não funciona. Conta real: a alma da Ayano tem ~3.900 tokens; no nível 1 sobram
4.292 para tudo. Reservar 2.400 fixos de memória deixaria 1.892 para a conversa — inutilizável.

Proporcional, com pisos:

```
disponível   = orçamentoDoNível − tokens(SOUL + SYSTEM_PROMPT)
memória      = disponível × 0,25        (piso 0 se disponível < 2000)
  ├─ fatos      = memória × 0,20
  ├─ atos       = memória × 0,30
  └─ capítulos  = memória × 0,50
vivo         = disponível − memória
```

Quando `disponível` é pequeno demais, a memória é **desligada** em vez de espremer a conversa.
Um agente sem memória funciona; um sem espaço para conversar, não.

---

## 6. Gatilho e o custo do cache

Compactar reescreve o começo do prompt e **invalida o prefixo inteiro**. Toda compactação
custa um prefill frio.

Consequências de desenho:

- **Compactar raramente, em blocos grandes.** Compactar a cada 5 mensagens seria pior que o
  `Trim` atual.
- **Gatilho por token, não por contagem de mensagens.** Turnos variam demais: um `run_command`
  despeja 8.000 caracteres, um "ok" custa 3 tokens. Dispara quando o nível vivo passa de 85%
  da sua cota.
- **Nunca no meio de um turno.** A compactação espera o `_turnGate`.

**Esconder o prefill frio:** após compactar, disparar imediatamente um aquecimento com o novo
prefixo, em background, enquanto o usuário lê a última resposta. Quando ele digitar de novo, o
prefixo já está quente.

Isso reaproveita a infraestrutura de warmup que já existe — e corrige, de passagem, o problema
já diagnosticado de o aquecimento inicial aquecer o prefixo errado
(`system + heartbeat` não é prefixo de `system + pergunta`, o que custa ~50s na primeira
mensagem de toda sessão).

---

## 7. Armazenamento

Em `~/.AIB/memory/`, seguindo a política de que `.AIB` é a fonte de verdade:

```
memory/
  facts.md                       nível 2 destilado, editável à mão pelo usuário
  sessions/
    <sessionId>/
      raw.jsonl                  turnos crus, NUNCA apagados
      chapters.jsonl             resumo + artefatos por capítulo
      acts.jsonl                 resumo de capítulos
```

**`raw.jsonl` nunca é apagado.** Resumo é perda irreversível, e resumo ruim aqui não gera
inconsistência de enredo — gera agente agindo sobre informação errada, com `run_command` na
mão. O cru fora do contexto é a rede de segurança: recuperável quando o resumo se mostrar ruim.

`facts.md` é texto, não banco. O usuário abre, lê e corrige.

---

## 8. Peças novas

```
Services/Memory/
  ArtifactExtractor.cs     puro. turnos → lista de artefatos literais
  MemoryBudget.cs          puro. orçamento + tamanho do SOUL → cotas por nível
  SessionMemory.cs         IO: raw.jsonl, chapters.jsonl, acts.jsonl, facts.md
  Compactor.cs             orquestra: seleciona turnos, resume, promove
  MemoryLayer.cs           monta o bloco de memória para o BuildSystemPrompt
```

Assinaturas centrais:

```csharp
/// Extração literal. Nunca passa pelo modelo — determinística e testável.
public static IReadOnlyList<Artifact> Extract(IReadOnlyList<ChatMessage> turno);

/// Cotas em tokens. Devolve Zero quando não há espaço para memória.
public static MemoryQuota Compute(int budgetDoNivel, int tokensDoPrefixoFixo);

/// Um capítulo a partir de turnos completos. Usa CompleteAsync (não-streaming),
/// SEM ferramentas: resumir não é agir.
Task<Chapter> SummarizeAsync(IReadOnlyList<Turn> turnos, CancellationToken ct);

/// Bloco pronto para o prompt, já dentro da cota.
string Render(MemoryQuota cota);
```

O `Compactor` recebe `IChatProvider` — mesmo contrato dos providers, então é dublável em teste
sem rede, como já fazemos no `AgentLoopTests`.

---

## 9. Modos de falha e o que fazer

| falha | resposta |
|---|---|
| resumo falha (rede, modelo fora) | mantém o cru, tenta no próximo turno. Nunca perde dado |
| resumo vem longo demais | trunca na cota; o cru continua em disco |
| compactação durante turno ativo | espera o `_turnGate` |
| par tool_call/result partido | proibido por construção: a unidade é o turno completo |
| `facts.md` editado à mão com erro | é texto; carrega o que der e loga o resto |

---

## 10. Ordem de implementação

Três fases, cada uma entregando valor sozinha:

**Fase 1 — substrato (risco zero).**
`ArtifactExtractor` + `SessionMemory` gravando `raw.jsonl`. Nenhuma mudança no prompt, nenhum
resumo. Só passa a existir registro. Testável inteiro, sem LLM.

**Fase 2 — capítulos.**
`MemoryBudget` + `Compactor` + `MemoryLayer`. Substitui o `Trim` por compactação. Aqui aparece
o ganho real: cadeia longa deixa de perder o começo.

**Fase 3 — atos e fatos.**
Promoção de capítulos para atos, e destilação para `facts.md`. É a evolução de longo prazo.

Fazer a fase 1 primeiro também valida encanamento (caminhos, serialização, orçamento) antes de
investir na fase 2, que é a maior.

---

## 11. O que isto NÃO resolve

- **Busca semântica episódica.** Recuperar "aquela vez que consertamos o cache" continua fora
  de alcance. Precisaria de embeddings sobre `chapters.jsonl` — os pacotes já estão no
  `.csproj` (`SmartComponents.LocalEmbeddings`, LiteDB) mas não há implementação.
- **Skills.** Memória de FATO é diferente de memória de CAPACIDADE. Cristalizar uma cadeia
  bem-sucedida numa skill reutilizável é outro projeto, e provavelmente o de maior retorno
  depois deste.
- **O portão perguntando a cada comando.** Cadeia de 5 comandos distintos = 5 modais.
  `AlwaysAllowSession` casa byte a byte. Independente de memória.
