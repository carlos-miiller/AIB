# 04 — Memória hierárquica

A conversa com o modelo não pode crescer sem limite: a janela é finita e, no Ollama em CPU, cada mil tokens de prompt custam dezenas de segundos de prefill. A AIB resolve isso com uma memória em camadas, montada no fim de cada turno:

```
turnos vivos (crus, no histórico)
   └─► capítulo  (vários turnos → um bloco por seções)
          └─► ato  (vários capítulos → um bloco por fusão)
fatos duráveis (facts.md) — atravessam conversas
```

O que sai do prompt **não sai do disco**: `raw.jsonl` guarda todos os turnos, para sempre.

Arquivos (todos em `AIBWindows/Services/Memory/`, salvo indicação):

| Papel | Arquivo |
|---|---|
| Orquestração: gatilho, seleção, capítulo, ato, poda | `AIBWindows/Services/ConversationService.cs` |
| Cotas | `MemoryBudget.cs` (`MemoryQuota`) |
| Orçamento por nível | `AIBWindows/Services/LevelService.cs` |
| Resumidor | `Compactor.cs` |
| Registros | `Chapter.cs`, `Act.cs`, `Turn.cs`, `TurnRecord.cs` |
| Texto por seções (v2) | `BlocoEstruturado.cs` |
| Estado dos arquivos e comandos | `EstadoDoTrecho.cs`, `ComandoDeShell.cs`, `ComandoQueApaga.cs` |
| Pedidos literais do usuário | `Combinado.cs` (`Combinados`, `FalaCombinada`) |
| Pontas soltas | `Pendencia.cs` (`Pendencias`) |
| Conferência do que o modelo escreveu | `Conferencia.cs` |
| Artefatos literais | `ArtifactExtractor.cs`, `Artifact.cs`, `ArtifactDigest.cs` |
| Fatos duráveis | `FactStore.cs` |
| Montagem do bloco no prompt | `MemoryLayer.cs`, `MemoryRender.cs` |
| Esconder resultados antigos | `ResultadosAntigos.cs` |
| Disco | `SessionMemory.cs`, `TurnoDoRegistro.cs`, `RegistroDaCompactacao.cs` |
| Divisão em turnos | `TurnSplitter.cs`, `ThinkBlockStripper.cs` |

---

## 1. Unidade: o turno

- Um **turno** (`Turn`) é a fala do usuário e tudo o que vem depois até a próxima fala do usuário: chamadas de ferramenta, resultados, falas do assistente. `TurnSplitter.Split` ignora mensagens de sistema e descarta o que vem antes do primeiro `user`.
- Um turno está **fechado** (`TurnSplitter.IsClosed`) quando a última mensagem é assistant sem tool_calls. Só turno fechado vai para o `raw.jsonl` e só turno fechado entra em capítulo — um `tool_calls` sem resultado quebra a requisição seguinte. Por isso todo turno que termina sem resposta recebe a marca `[turno encerrado sem resposta: …]` (ver `02-turno-e-provedores.md`, §2.7).
- `Turn.UserText` é o texto da mensagem que abre; `Turn.AssistantText` é a última fala sem tool_calls, sem blocos `<think>`.

---

## 2. Orçamento e cota

### 2.1 Orçamento do nível (`LevelService.GetMaxTokensForLevel`)

Derivado da janela em vigor (`ChatRequestOptions.JanelaAtual`), nunca digitado: nível 1 recebe 1/4 da janela, nível 9 recebe 3/4, em oito passos iguais. Com a janela padrão de 32.768: nível 1 = 8.192, passo de 2.048, nível 9 = 24.576.

Esse orçamento é **de compactação**, não limite do modelo. A poda de emergência age em outro teto (`TetoDaPoda` = janela − 2.048).

### 2.2 Cota (`MemoryBudget.Compute`)

Função pura. Reparte o que sobra do orçamento **depois do prefixo fixo** (a primeira mensagem de sistema: alma + regras + skills). Alocação fixa não sobrevive: uma alma de ~3.900 tokens no nível 1 deixaria quase nada para conversar.

```
disponível = orçamento do nível − prefixo fixo
se disponível <= 0      → tudo zero (memória desligada, poda cuida)
se disponível < 2.000   → memória desligada; conversa viva = disponível   (MinimumAvailable)
memória   = disponível × MemoryFraction          (padrão 0,25; saneado entre 0,05 e 0,60)
fatos     = memória × 0,20
atos      = memória × 0,30
capítulos = memória − fatos − atos               (~0,50; fica com o resto, nada some no arredondamento)
viva      = disponível − memória
```

Só a **primeira** mensagem de sistema conta como prefixo. A segunda é o próprio bloco de memória; contá-la encolheria a cota a cada capítulo, num aperto que se realimenta (`ConversationService.CurrentQuota`).

Exemplo: nível 1, janela 32.768, prefixo de 3.000 tokens → disponível 5.192; memória 1.298 (fatos 259, atos 389, capítulos 650); conversa viva 3.894.

### 2.3 Gatilho e alvo

- **Gatilho** (`MemoryBudget.CompactionThreshold`): `viva × CompactionTrigger`, padrão 0,85 (saneado entre 0,50 e 0,99). Não é 1,0 de propósito: tem de disparar antes de a poda de emergência entrar e comer as mensagens que o capítulo iria resumir. No exemplo acima: 3.309.
- **Teto de tokens soltos** (`TokensSoltos`, padrão 100.000, saneado 8.000–1.000.000): o gatilho de tokens é o MENOR entre a fração acima e este teto (`CompactionThreshold(quota, fração, tokensSoltos)`). A fração protege a janela pequena; o teto, o custo em janela grande — num modelo de 1 M de janela os 85% nunca chegavam, e uma sessão de navegador reenviou 165 mil tokens a cada requisição.
- **Turnos soltos** (`TurnosSoltos`, padrão 20, saneado 4–200): com este tanto de turnos fechados fora de capítulo (`TurnosSoltosAgora`), fecha UM capítulo, mesmo com a conversa leve.
- **Alvo** (`LimitesDoProvedor.AlvoDepoisDeCompactar`): depois de compactar por tokens, a conversa viva cai para 0,5 (Ollama) ou 0,3 (OpenRouter) do menor entre a cota viva e `TokensSoltos`. Compactar só até encostar no gatilho faria a próxima compactação disparar quase junto — e cada compactação reescreve o começo do prompt (prefill frio no Ollama, cache perdido e pago no OpenRouter).

---

## 3. Quando a compactação roda

### 3.1 Automática (`CompactIfNeededAsync`)

- No `finally` de `StreamResponseAsync`, ainda com o portão do turno, **só se o turno não foi cancelado**. Nunca no meio de uma cadeia de ferramentas.
- Em laço: enquanto `LiveTokens()` (tudo menos as mensagens de sistema do começo) passar do gatilho, seleciona turnos e fecha um capítulo. Até `MaxCapitulosPorPassada` = 10 por passada (cada capítulo é uma chamada ao modelo); o que sobra fica para o fim do turno seguinte, e o diário registra.
- Sem turno elegível: registra `PULOU` com a causa ("os recentes curtos ficam fora…").
- **Falha ou cancelamento** põe a compactação em descanso por `CompactionCooldownTurns` = 3 turnos. A compactação segura o portão, então uma compactação lenta que falha a cada turno transformaria toda mensagem numa espera de minutos. Enquanto isso, a poda de emergência cuida do contexto.
- A chamada de resumo **não tem prazo**. Já teve (quatro minutos) e cortava trabalho válido. Quem desiste é o usuário: `InterromperCompactacao` cancela `_desistencia`, e os eventos `CompactacaoAndou`/`CompactacaoAcabou` alimentam a faixa de sistema. Interromper não perde nada: os turnos só saem do contexto **depois** de o capítulo existir.

### 3.2 A pedido: `/compact` (`ForcarCompactacaoAsync`)

- Fecha **todos** os capítulos que couberem (até 10), cada um via `ForcarCapituloAsync` (que pega o portão e usa `SelectTurnsToCompact(…, forcado: true)`, ignorando o alvo de tokens).
- Depois promove atos pela **mesma regra de cota** da passada automática (`CapitulosParaAto`). Já foi "de dois em dois", e numa sessão real quatro capítulos viraram dois atos que economizaram 119 e 570 tokens com 21 mil tokens de cota sobrando — dois resumos de resumos pagos por nada.
- Devolve a frase que a interface mostra, inclusive a de recusa.
- `/memoria` mostra a conta (`MemoriaEmTexto`): capítulo por capítulo, atos, Pendente, cru resumido, memória no prompt, economia, descartado ao reabrir, custo e linhas do cache.

### 3.3 Ato forçado (`ForcarAtoAsync`)

Pedido explícito, sem regra de cota. Mínimo `max(2, minimoParaOAto ?? 2)`: ato sobre um capítulo só seria resumo de resumo sem ganho — e esconderia o original, que deixa de ser renderizado. Não há comando na interface; quem chama sem mínimo são os testes e a avaliação.

---

## 4. Seleção dos turnos (`SelectTurnsToCompact`)

1. Divide os turnos vivos.
2. **Recentes que ficam** (`RecentesQueFicam`): até `KeepRecentTurns` = 2 turnos do fim, e só enquanto somados cabem em `FracaoDosRecentes` = 15% da cota viva. Era "os dois últimos, sempre": um turno de 18 ferramentas com 19 mil tokens não saía do contexto e o `/compact` respondia "nada a compactar". Turno grande recente vira capítulo; o que ele deixou por fazer segue na seção Pendente.
3. Percorre do mais antigo, parando quando:
   - atingiu o alvo de tokens (a não ser que `forcado`);
   - chegou em `TurnosPorCapitulo` (padrão 15, era 8; saneado 2–20);
   - encontrou um turno **não fechado** (para; não pula);
   - somar o próximo turno passaria do teto de tokens do capítulo.
4. **Teto de tokens** (`TetoDeTokensDoCapitulo`): `TokensPorCapitulo` (padrão 20.000, saneado 4.000–60.000). No **Ollama** é limitado a `FracaoDaJanelaPorCapitulo` = 60% da janela, porque um prompt maior que o `num_ctx` é **truncado em silêncio pelo começo** — o resumidor perderia as instruções. Com a janela padrão de 32.768 isso dá 19.660, abaixo dos 20.000. No OpenRouter vale o número da tela (estourar dá erro, não corte calado).
5. **O primeiro turno sempre entra**, mesmo sozinho maior que o teto: cortar dentro de um turno quebraria o par tool_call/resultado. Motivo do teto de tokens: oito turnos com saídas de `docker exec` somaram 95.164 tokens e viraram um capítulo de 171 tokens.
6. Cada turno escolhido recebe o **índice do `raw.jsonl`** (`_indiceNoRegistro`), não a posição no vivo — o vivo recomeça do zero a cada compactação. Turno nunca gravado (contexto recuperado de outro chat) fica com o índice seguinte ao anterior.

Sem os tetos, uma compactação que falha volta na seguinte com mais turnos (5, 6, 7… 11 turnos contra o Ollama real), cada tentativa mais cara.

---

## 5. Fechar um capítulo (`FecharCapituloAsync`)

1. `Compactor.SummarizeAsync(índice, turnos)` monta o capítulo.
2. Se a conversa foi trocada durante o resumo (minutos), aborta: remover "as N mais antigas" apagaria o começo da conversa nova.
3. Mede o custo dos turnos **antes** de removê-los e só então os remove (`RemoveOldestMessages`). Remover antes e falhar o resumo perderia os turnos das duas pontas.
4. Se sobrou turno vivo depois do capítulo, tira as pendências `interrompido` (a interrupção já teve resposta).
5. Adiciona à `MemoryLayer`, grava em `chapters.jsonl`, soma o custo.
6. **Promove atos em laço** (`PromoverAsync` com `CapitulosParaAto`) **antes** de reescrever o bloco: o prefixo é invalidado uma vez só.
7. Retitula a conversa pelo primeiro capítulo (uma vez; o prefixo já foi invalidado).
8. `RefreshMemoryMessage` reescreve a segunda mensagem de sistema.

---

## 6. O Compactor

### 6.1 Regras gerais

- Lista de ferramentas **vazia**: resumir não é agir. Um resumidor com ferramentas acaba "verificando" o que resume e executando comandos.
- Opções `ChatRequestOptions.DeServico(...)`: temperatura 0, **`Think: false`**, janela do Ollama em vigor. O raciocínio desligado vem de medição: cinco turnos no qwen3.5:4b levavam 286,6 s, dos quais 226,7 s eram 1.738 tokens de raciocínio para um resumo de 117 — que o `ThinkBlockStripper` jogava fora. Sem raciocínio: 14,7 s.
- **`NumPredict`** é a rede contra modelo que ignora o formato: `MaxSummaryTokens` = 400 no capítulo; `LimitesDoProvedor.TetoDoResumoDoAto` no ato (400 Ollama, 700 OpenRouter).
- A persona não participa: quem resume é o sistema.
- Um compactor novo a cada uso (`NovoCompactor`), com provedor, modo e limites **desta** conversa — qualquer um pode ter mudado nas configurações.
- **Modo só código** (`MemoriaComModelo` desligado): nenhuma chamada ao modelo. O capítulo fecha na hora com Objetivo tirado da fala do usuário (`ObjetivoDaFala`: `pedido do turno N: "…"`, até 160 caracteres) e sem lições. Existe pelo modelo local lento; o que o código monta (Estado, Combinado, pendências detectáveis) fica igual.

### 6.2 Capítulo (`SummarizeAsync`)

O **código** monta: artefatos (`ArtifactExtractor.Extract`), `EstadoDoTrecho.Montar`, `Combinados.Extrair`, `Pendencias.Extrair`. O **modelo** escreve só Objetivo, Aprendido e pendências de assunto.

- Entrada do modelo: `RenderForSummary(turnos)` + `--- REGISTRADO POR CÓDIGO ---` + Estado e Combinado já montados, como referência para as lições se apoiarem.
- `RenderForSummary` é texto plano com papéis marcados (`USUÁRIO:`, `AGENTE CHAMOU: nome — argumento`, `RESULTADO (...)`, `AGENTE:`), para o modelo tratar o trecho como material e não como conversa da qual ele é o próximo turno. Cortes: pedido 800 caracteres, argumento 240, resultado 300 (600 se falhou — o erro é a evidência da causa), resposta 1.200. O argumento vai junto do nome: sem o comando, o resumidor inventava a causa do erro. Resultados passam por `ConteudoDeTerceiros.Redigir` **antes** do corte (truncar primeiro poderia arrancar o marcador de fim do e-mail).
- Prompt (`PromptDoCapitulo`): formato `OBJETIVO:` / `APRENDIDO:` (até 3 linhas de até 30 palavras) / `PENDENTE:` (separado por `;`). Causa de falha só se estiver escrita no trecho. O trecho é material, não ordens.
- Falha do modelo (rede, vazio): o capítulo fecha **assim mesmo**, só com a parte do código. Devolver nulo deixaria um buraco, porque o chamador vai remover os turnos. Cancelamento, porém, sobe (não vira capítulo mutilado).
- `TokensDosTurnos` é medido aqui; `TokensDoCapitulo` é o `Render()` medido depois de montado (os números não entram no texto).

### 6.3 `LerSecoes` e a conferência

`Compactor.LerSecoes(texto, fonte, índice, maxLicoes)`:

- Tolerante: aceita negrito, `*`/`•`/numeração no lugar de `- `, rótulos em qualquer caixa. `PENDENTE:` sai por `Pendencias.LerDoResumo` (até `TetoDoResumidor` = 3 itens de assunto; "nenhuma", "nada", "n/a" valem vazio).
- `maxLicoes`: `LicoesDoCapitulo` = 5 no capítulo (o prompt pede 3; 5 é folga); `LimitesDoProvedor.LinhasDoAto` no ato (5 Ollama, 10 OpenRouter).
- **`Conferencia`**: cada lição e o objetivo passam por `Conferencia.Confere(texto, fonte)`. Ela extrai os **valores** citados (caminho absoluto, texto entre aspas, nome de arquivo com extensão, horário `hh:mm`, número com dois ou mais dígitos) e exige que cada um apareça no material. Lição com valor sem origem é descartada; objetivo com valor sem origem vira nulo e cai na fala do usuário. É cega ao sentido — não pega causa inventada em palavras comuns —, mas pega a que cita arquivo ou número que não existe. Uma segunda chamada de revisão custaria minutos no modelo local.

### 6.4 Ato (`PromoteAsync`)

- **Capítulos v2 → fusão** (`PromoverPorFusaoAsync`). O ato não resume resumos:
  - Estado: `EstadoDoTrecho.Fundir` (código).
  - Combinado: `Combinados.Juntar` (código; até `TetoNoAto` = 16, os mais recentes).
  - Artefatos: `ArtifactDigest.Condense` (um por chave, até 12, falhas primeiro).
  - Pendências: `Pendencias.Resolver` sobre os capítulos; as de assunto são trocadas pelas que o modelo apontar, se ele escrever a linha `PENDENTE:`.
  - Objetivo e Aprendido: o modelo recebe `RenderChaptersForSummary` (objetivo, lições e pendências de cada trecho) e o `PromptDoAto(linhas)` pede a versão atualizada — juntar lições repetidas, manter a mais recente, tirar pendência resolvida. Sem modelo (ou em falha): objetivo do capítulo mais recente e as últimas `LinhasDoAto` lições distintas.
  - Motivo: foi no parágrafo sobre parágrafos que horários pedidos pelo usuário sumiram e a causa inventada de um capítulo virou fato.
- **Algum capítulo v1 → caminho antigo**: `ActPrompt` pede um parágrafo de até 150 palavras + `PENDENTE:`; em falha, o resumo vira `[resumo indisponível: …]`.
- `TokensDosTurnos` do ato é o cru herdado dos capítulos (economia total); `TokensDosCapitulos` separa a economia da promoção (`EconomiaDaPromocao`).

---

## 7. Formato por seções (versão 2)

`Chapter` e `Act` têm `Versao`. `BlocoEstruturado.Versao` = 2. Registro sem o campo é v1.

### 7.1 Ordem das seções (`BlocoEstruturado.Render`)

Ordenadas pela **confiança**, não pela narrativa:

1. Título: `### Capítulo N (turnos a–b)` ou `### Ato N (capítulos a–b, turnos x–y)`. Numeração a partir de 1, pela numeração do registro.
2. `Objetivo:` — curto, para situar.
3. `Combinado com o usuário (palavras dele):` — literal, código.
4. `Estado ao fim do capítulo N (pasta …):` + `Consultados:` — literal, código.
5. `Aprendido:` — o que o modelo concluiu; a parte que pode estar errada vai por último.

A seção **Pendente não mora no bloco**: é uma só, no fim da memória, já resolvida entre os trechos.

### 7.2 Encolher para caber (`BlocoEstruturado.Fit`)

Quando o bloco não cabe na cota, cede do menos valioso para o mais, tentando em sequência: sem a linha Consultados → Estado com 10 itens → 5 itens → sem Aprendido → só os 4 pedidos mais recentes do Combinado → só 2 → Estado com 3 itens e sem Combinado → só título e objetivo → e por fim o Objetivo aparado (`MemoryRender.Fit`). A primeira tentativa usa o mesmo teto de itens do `Render` inteiro, para o bloco que cabe não sair menor do que o que foi medido. **Um bloco nunca pode sumir inteiro**: já aconteceu de o Combinado de um ato passar da cota sozinho e a memória toda sair do prompt.

### 7.3 Exemplo (inventado, realista) de capítulo renderizado

```
### Capítulo 2 (turnos 4–7)
Objetivo: Ajustar os horários de atendimento no JSON da agenda e regenerar a página.
Combinado com o usuário (palavras dele):
- (turno 4) "Sábados: 12:00 ~ 14:00"
- (turno 6) "gera de novo o index.html na pasta saida"
Estado ao fim do capítulo 2 (pasta C:\Users\Fulano\Projetos\agenda):
- horarios.json: editado pelo agente (+2 linhas, −2 linhas) (turno 4)
- gerar.ps1: criado pelo agente (1840 caracteres) (turno 5)
- saida: APAGADO pelo agente — já feito, não repetir sem o usuário pedir (turno 6)
- saida\index.html: apagado junto com a pasta (turno 6)
- comando `powershell -ExecutionPolicy Bypass -File C:\Users\Fulano\Projetos\agenda\gerar.ps1` (2×): última: ok — efeitos em disco não rastreados (turno 7)
Consultados: horarios.json, README.md (5 consultas)
Aprendido:
- gerar.ps1 falhou porque a pasta saida não existia depois de apagada; o script passou a criá-la antes de gravar.
```

---

## 8. Estado do trecho (`EstadoDoTrecho`)

Substitui, no prompt, a lista cronológica de artefatos. A lista dizia "gravou X" para um arquivo que três turnos depois foi apagado com a pasta, e misturava onze leituras com dez ações. Rastro de arquivos é onde resumidores por LLM mais erram, por isso sai do modelo e fica com o código.

### 8.1 `Montar(turnos)`

Pareia chamada e resultado por id e dobra cada ação no seu alvo (`ItemDeEstado`: alvo, tipo arquivo/pasta/comando, situação, detalhe, turno, vezes):

| Ferramenta | Efeito |
|---|---|
| `read` ok | vai para **Consultados**; se o arquivo não era conhecido: "já existia" (ou "existia (ou foi gerado por comando)" se antes rodou comando opaco); se estava apagado: "reapareceu (gerado por comando)" |
| `read` falhou | "não encontrado ao ler" (se não era conhecido) |
| `write` | "criado pelo agente" / "recriado pelo agente" (se estava apagado) / "reescrito pelo agente" |
| `edit` | "editado pelo agente"; editar o que o agente criou continua "criado pelo agente" (a origem é o que importa) |
| `write`/`edit` falhou ou recusado | mantém a situação anterior e anota no detalhe |
| `shell` que apaga (`ComandoQueApaga.Eh`) | ver §8.3 |
| `shell` só leitura (`ComandoDeShell.SoLeitura`) | conta como consulta; só entra no Estado se **falhou** ("consulta falhou") |
| outro `shell`, `skill` | comando **opaco**: "ok — efeitos em disco não rastreados" ou "FALHOU", com contagem de vezes |
| `glob`, `grep`, `mail`, `mail_read` | só contam consultas |

O que o Estado **não afirma** é tão importante quanto o que afirma: comando arbitrário não diz o que mudou no disco; apagar só conta com alvo literal e absoluto. Tudo vale "no fim do capítulo": o disco pode ter mudado depois, e conferir na hora de montar mudaria o texto a cada turno e quebraria o cache.

### 8.2 Tetos e texto (`Render`)

- `TetoDeItens` = 20 no capítulo; `TetoDeItensDoAto` = 28 no ato (ele funde muitos capítulos; o `Fit` encolhe se não couber). Passou do teto: ficam os mais recentes e entra "(N item(ns) mais antigo(s) omitido(s))" ou "e mais N item(ns)".
- `TetoDeConsultados` = 12 nomes curtos na linha Consultados, com o total de consultas.
- Arquivos agrupados pela pasta comum (caminho completo uma vez no título, nome curto nas linhas); comandos depois, cortados em 160 caracteres.

### 8.3 Comandos que apagam nunca saem prontos para repetir

Incidente repetido: um capítulo guardou `executou Remove-Item "…\emails fisio" -Recurse -Force`, o bloco dizia que os artefatos "podem ser usados como estão", e num turno seguinte o modelo copiou o comando e apagou de novo a pasta com o template, o CSV e o script que estava corrigindo.

- `ComandoQueApaga.Eh` reconhece `remove-item`, `rm`, `rmdir`, `rd`, `del`, `erase`, `ri`, `clear-content`, `clear-recyclebin`, `format-volume`, `clear-disk`. Erra para o lado de reconhecer demais: custa uma linha menos literal; o contrário custa uma pasta.
- No Estado: com alvo literal absoluto e sem curinga (`*`, `?`, `$`), o item é o **alvo**, com "APAGADO pelo agente — já feito, não repetir sem o usuário pedir", e tudo o que se sabia estar dentro vira "apagado junto com a pasta". Sem alvo identificável: "(comando que apaga arquivos)" / "rodou — alvo não identificado; não repetir sem o usuário pedir". A linha de comando **não** aparece.
- Em `Artifact.Render` (formato v1): `ComandoQueApaga.Descrever` ("apagou X (já feito — não repetir sem o usuário pedir)").
- Na pendência de falha: "um comando para apagar X falhou — não tente de novo sem o usuário pedir".
- O literal continua no registro (`Artifact.Value`, `raw.jsonl`); só a forma no prompt muda.
- Isto **não** é o portão de segurança (esse é o cartão de confirmação e o `CommandFloorList`). Aqui só se decide o que a memória mostra.

### 8.4 `Fundir(estados)`

Junta o estado de vários capítulos como se fossem um: o mais recente de cada alvo vale; uma pasta apagada num trecho posterior leva junto o que um trecho anterior deixou dentro dela; comandos somam as vezes.

### 8.5 `ComandoDeShell`

Duas perguntas no mesmo lugar, nascidas de uma sessão real:

- **`Nucleo(comando)`**: tira cano (`|` e o que vem depois), redirecionamentos (`2>&1 > saida.txt`), embrulhos empilhados (`cd X;`, `sudo`, `&`, `call`, `docker exec … sh -c "`, `pwsh -c`) e, numa corrente `A; B; C` (ou `&&`), fica com o primeiro passo que não é preparo (`cd`, `Start-Sleep`, `cls`, `echo`, `set`, `chcp`…). Só corta fora de aspas: `sh -c "a; b"` é um comando só.
- **`SoLeitura(comando)`**: o núcleo começa com verbo de leitura (`dir`, `ls`, `Get-Content`, `cat`, `Select-String`, `Test-Path`, `grep`, `sed`, `head`, `tail`, `findstr`…) ou com um par programa+subcomando que só lê (`git status|log|diff|show…`, `docker ps|logs|inspect…`, `kubectl get|describe|logs`, `npm ls|view`, `dotnet --info`…), ou com uma consulta de banco que só lê (`mariadb`/`mysql`/`psql` cujo `-e`/`-c` começa com `SELECT`, `SHOW`, `DESCRIBE` ou `EXPLAIN` — o cliente grava tanto quanto lê, então quem decide é a consulta). Motivo: `docker exec glpi sed -n '400,435p' Plugin.php` tomava o Estado de um capítulo como se mudasse o disco.
- **`Assinatura(comando)`**: o núcleo em minúsculas (ou o comando inteiro, se o núcleo for vazio — chave larga demais apagaria pendência alheia). É a chave de "mesmo trabalho" no Estado e nas Pendências: `x.php | Select-Object -Last 20` que falhou e `x.php` que deu certo são o mesmo item.

---

## 9. Pendências (`Pendencias`)

Pontas soltas que um "continue" precisa. Existem para a compactação poder levar também turnos recentes grandes sem perder o fio.

Tipos (`Pendencia.Tipo`):

- **`falha`** (código): `write`/`edit` ou comando que falhou e **não** teve sucesso depois no mesmo alvo, dentro do trecho. Leitura que falhou não entra (procurar e não achar é rotina); comando só-leitura também não; recusa do usuário também não (é decisão, e listá-la convidaria a tentar de novo). Cada chave entra uma vez na ordem, mesmo que falhe, dê certo e falhe de novo.
- **`interrompido`** (código): só o **último** turno do trecho, quando termina com a marca de turno morto (`MotivoDaInterrupcao`). Leva o pedido (até 120 caracteres) e a última ação.
- **`assunto`** (modelo): o que o resumidor apontou na linha `PENDENTE:` como pedido e não feito.

`Extrair(turnos)` produz as de código. `Resolver(trechos)` decide as que valem sobre a sequência de atos e capítulos soltos:

- Falha some quando um trecho **posterior** tem sucesso no mesmo alvo. O casamento é pela chave: `arquivo|caminho` ou `comando|Assinatura` — **pela assinatura, não pelo texto**. Pelo texto exato, uma sessão fechou com oito pendências já resolvidas, porque a segunda tentativa tinha um `| Select-Object -Last 10` a mais.
- Interrupção e assunto só valem do trecho mais recente.
- Pendência de assunto que repete uma falha de comando (o resumidor do ato lê o Pendente dos capítulos e às vezes devolve as mesmas falhas) é descartada quando o texto contém a assinatura (de 12 caracteres ou mais).
- Sem repetição entre trechos.

`Render`: seção `### Pendente`, interrupções primeiro, depois falhas, depois assunto; até `TetoNoPrompt` = 8 linhas de até 240 caracteres.

---

## 10. Combinados (`Combinados`)

Os pedidos do usuário que carregam **valor**, copiados por código. Motivo: o usuário pediu "Sábados: 12:00 ~ 14:00" e o ato virou "modificações nos horários".

- `TemValor`: dígito, texto entre aspas, caminho `X:\`, UNC, URL, e-mail, extensão de arquivo.
- Fala com valor de até `FalaCurta` = 280 caracteres entra inteira (quebras de linha viram ` / `). Fala longa contribui com até `LinhasPorFala` = 3 linhas com valor, cada uma até `TetoDaLinha` = 240.
- `TetoNoCapitulo` = 8 (os mais recentes); `TetoNoAto` = 16 via `Juntar` (sem repetir, em ordem de turno).
- Erra para o lado de copiar. "leia os arquivos" e "continue" não entram.

---

## 11. Artefatos (`ArtifactExtractor`, `Artifact`, `ArtifactDigest`)

- **Código puro**, nunca chama modelo. É a metade do capítulo que nunca passa por resumidor: resumir um caminho absoluto como "um arquivo do provider" destrói justamente o que vale.
- `Extract(turno)` pareia `assistant(tool_calls)` com `tool` por id; chamada sem resultado é ignorada (registrar intenção como fato é pior que nada). A ordem é a dos resultados.
- `Construir` é o mesmo caminho exposto para a tela ao vivo — um extrator só, para bolha e capítulo nunca discordarem.
- Tipos: `FileWritten` (`write`, `edit` — detalhe "N caracteres" ou "+a linhas, −b linhas"), `FileRead`, `CommandRun` (`shell`, `skill` com nome e argumentos), `Denied` (recusa do usuário, qualquer ferramenta).
- **`Falhou(resultado)`**: começa com `ERRO`, **ou começa com `ACESSO NEGADO`** (toda negação do portão: nível insuficiente, floor list, contexto que não se descreve, sem interface), **ou contém a recusa do usuário** (`ToolRegistry.RecusaDoUsuario` = "Ação Rejeitada pelo Usuário."). Antes só "ERRO" e a recusa contavam, e uma gravação barrada pelo nível aparecia com chip verde, entrava na memória como feita e limpava o bloqueio de repetição. `Recusado` é só a recusa; `NegadoPeloPortao` é só o prefixo.
- No formato v2 os artefatos continuam gravados no capítulo — alimentam `Pendencias.Resolver`, o `ArtifactDigest` e os fatos —, mas o prompt mostra o Estado no lugar deles.
- `ArtifactDigest.Condense`: um artefato por chave (`arquivo|`, `comando|`, `negado|` + valor), até `MaxArtifactsPerAct` = 12, com o que falhou na frente (comando que já quebrou, se esquecido, é repetido).

---

## 12. Fatos duráveis (`FactStore`)

- `~/.AIB/memory/facts.md` (do **usuário**: ele edita, reordena, apaga; a AIB só acrescenta no fim) e `facts.index.jsonl` (da **máquina**: chaves de tudo que já foi promovido, append-only). Sem o índice, um fato apagado pelo usuário voltaria na próxima promoção.
- Ficam na raiz da memória, fora das sessões: é a única faixa que atravessa conversas. `MemoryLayer.Clear` não os apaga; o `FactStore` não é trocado no `ResetHistory`.
- Promoção (`PromoverAsync`, depois de cada ato): `ArtifactDigest.Distill` sobre **todos** os capítulos da sessão. Um artefato vira fato quando atravessa `FactThreshold` = 3 capítulos distintos (contagem por capítulo, não por ocorrência); **recusa promove na primeira** (decisão não precisa se repetir). Linhas: "o usuário NEGOU esta ação: …", "comando que já falhou aqui: …", "comando usado neste ambiente: …", "arquivo relevante deste trabalho: …". Comando que **apaga**
  nunca entra literal: vira a descrição do `ComandoQueApaga` ("apagou X — já feito, não repetir"). Fatos
  gravados antes dessa regra passam por `ArtifactDigest.ParaOPrompt` na hora de ir ao prompt, que
  só reescreve as linhas com os prefixos gerados pela AIB — o que o usuário escreveu fica intacto.
- **Fatos sobre o usuário** (`remember`, `LembrarTool`): a persona grava na hora, pelo mesmo
  `Promote`, o que o usuário contou sobre si — `- sobre o usuário: …`, chave `usuario|<frase em
  minúsculas>`. Passam pelo mesmo índice (apagado não volta), têm teto de 60 linhas para não
  empurrar os fatos de trabalho para fora da cota, e com texto de terceiros no
  contexto só entram pelo cartão de confirmação, que mostra o fato inteiro. Entram no prompt na próxima montagem da memória (capítulo ou ato novo, ou conversa
  nova); na conversa em que foram ditos, o modelo já os tem no histórico.
- No prompt (`FactStore.Render`): só linhas que começam com `- `, relidas do disco a cada montagem; corte **do fim para o começo** (a ordem é a do usuário, o topo é o que ele quer garantir), dentro de `quota.Facts`.

---

## 13. O bloco de memória no prompt

### 13.1 Ordem

```
[system 0]  alma + regras + contexto local + vigia + skills     (prefixo fixo)
[system 1]  ## Fatos duráveis
            ## Memória da conversa
              (aviso: registro do que JÁ aconteceu; conferir o disco; não repetir comando)
              ### Ato …           (atos, do mais antigo ao mais recente)
              ### Capítulo …      (só capítulos não cobertos por ato)
              ### Pendente        (uma seção só, no fim)
            Arquivos que o usuário anexou…                     (ContextService)
[2…]        turnos vivos
```

A ordem segue a **frequência de mudança**, porque o cache de prefixo só reaproveita o começo. Medido: turno que só acrescenta ao fim, 2.672 ms; turno que mexe no começo, 50.536 ms. Fato muda raramente, ato a cada vários capítulos, capítulo a cada compactação, Pendente fica colado às mensagens vivas (é o que um "continue" retoma).

O aviso no topo da faixa narrativa substituiu "podem ser usados como estão" — o convite que fez o modelo copiar um `Remove-Item -Recurse`.

### 13.2 `MemoryLayer.Render` e `RenderNarrative`

- `Render` = fatos + narrativa. `RenderNarrative` = só atos, capítulos e Pendente, com o cabeçalho. É a narrativa, e só ela, que o contador compara com os turnos engolidos (fatos e anexos não substituíram conversa).
- **Cotas separadas**: sobra de ato não vira espaço de capítulo, senão o tamanho do bloco de capítulos mudaria a cada ato novo.
- Capítulo coberto por ato (`Index <= LastCoveredChapter`) não é renderizado — contaria a mesma coisa duas vezes.
- **`Fit`** escolhe do mais recente para o mais antigo até a cota acabar e devolve em ordem cronológica. Item que sozinho não cabe é pulado, sem bloquear os anteriores. Se **nada** coube, o mais recente entra aparado (`Render(maxTokens, counter)`) em vez de a faixa sair vazia — um ato de 330 tokens contra uma cota de 321 sumia, e os capítulos que ele cobria já não apareciam.
- v1 apara com `MemoryRender.Fit`: só o resumo encolhe (com ` …[truncado]`), cabeçalho e artefatos ficam, porque o literal é o que não pode ser perdido.
- `PendenciasVivas` = `Pendencias.Resolver` sobre atos e capítulos soltos, em ordem.

### 13.3 Contas da memória

- `TokensCrus`: soma dos `TokensDosTurnos` dos **capítulos** (cada turno passou por exatamente um capítulo; somar atos contaria duas vezes).
- `TokensDaMemoria`: atos + capítulos não cobertos.
- `MedidaCompleta`: falso se algum capítulo/ato é anterior à medição (`TemMedida` = `TokensDosTurnos > 0`). A tela diz "medida desconhecida", nunca zero.
- `LastCoveredTurn`: último turno do `raw.jsonl` coberto por capítulo. Lida com capítulos antigos que gravavam a posição **relativa** no vivo: se um capítulo começa num índice que não passa do já coberto, ele é relativo e soma a quantidade de turnos; senão é absoluto e vale o `LastTurn`.

---

## 14. Esconder resultados antigos (`ResultadosAntigos`)

- Configuração `EsconderResultadosDepoisDe` (0 = desligado, padrão; saneado 2–50). Aplicada em `LojaDoTurno.Snapshot`, ou seja, **só na cópia que vai ao modelo** — histórico, `raw.jsonl` e tela ficam intactos.
- Resultados de ferramenta de turnos mais antigos que o limite, com 300 caracteres ou mais (`Minimo`), viram `[resultado antigo de X omitido para poupar contexto: N caracteres(, era uma FALHA: …). Repita a chamada se precisar do conteúdo.]`.
- A fronteira anda em degraus de `Passo` = 4 turnos: esconder muda o meio do prompt, e andando de um em um o cache se perderia todo turno.
- Nasce desligado porque, no OpenRouter, cada degrau é cache perdido. Base: estudo da JetBrains ("The Complexity Trap", 2025) — trocar observações antigas por marcador empatou com resumo por LLM.

---

## 15. Disco

### 15.1 Pasta da sessão: `~/.AIB/memory/sessions/{id}/`

`SessionMemory`. O id é `yyyyMMdd-HHmmss-fff` (milissegundos para duas sessões no mesmo segundo não se misturarem) e passa por `Path.GetFileName`. Tudo em UTF-8 **sem BOM**, JSON sem escapar acentos. Nenhuma escrita lança: falha de disco vira log e `false`.

| Arquivo | Conteúdo | Regras |
|---|---|---|
| `raw.jsonl` | Um `TurnRecord` por linha: índice, hora, mensagens (`MessageRecord` com papel, texto, tool_calls, id da chamada e a conta: hora, modelo, tokens de entrada/saída/cache, duração, espera humana, decisão, falhou, custo, provedor) e os artefatos extraídos na época | **Nunca apagado.** Append-only. Corpo de e-mail passa por `ConteudoDeTerceiros.Redigir` antes de gravar. Linha corrompida é pulada na leitura |
| `turno-aberto.json` | O turno em curso, regravado a cada passo (temporário + troca atômica) | Apagado quando o turno é registrado; na reabertura, `RecuperarTurnoAberto` fecha com a marca e grava no `raw.jsonl` se o id ainda não estiver lá |
| `chapters.jsonl` | Um `Chapter` por linha | Append-only. Capítulos cobertos por ato **continuam aqui**: o ato substitui no prompt, não no disco |
| `acts.jsonl` | Um `Act` por linha | Append-only |
| `compactacao.log` | Diário da compactação (`RegistroDaCompactacao`) | Só com `CompactionLogging` ligado (padrão desligado) |

Na raiz `~/.AIB/memory/`: `facts.md` e `facts.index.jsonl` (§12).

### 15.2 `RegistroDaCompactacao`

Registra o **custo** e as **falhas**, que antes morriam no console: `GATILHO` (vivo, limite, espaço da conversa, turnos escolhidos), `A PEDIDO`, `CAPÍTULO n`/`ATO n`, `· resumo` (duração, prefill e saída do provedor, palavras, custo), `· fechado` (tokens removidos → custo do capítulo, artefatos, vivo agora; no ato, fatos novos), `FALHOU`, `PULOU`, `PODA` (a poda de emergência mora no mesmo diário porque acontece no lugar da compactação). A pasta e a chave são resolvidas **a cada escrita** (a sessão troca). Nunca lança.

### 15.3 Reabrir uma conversa (`LoadConversation` → `RestaurarMemoria`)

1. Recupera o turno interrompido, se houver.
2. Lê turnos, capítulos e atos; reconstrói a `MemoryLayer`.
3. Turnos cobertos por capítulo **não** voltam ao contexto. Os demais voltam **inteiros** via `TurnoDoRegistro.Remontar`, com chamadas e resultados — só as falas faziam a compactação seguinte fechar capítulos sem artefato. O que não fecha par é descartado e contado em `_descartadoAoReabrir` (aparece no `/memoria` como "Descartado ao reabrir"; entra no custo cru, não na economia).
4. Custo e cache somados de `raw.jsonl`, `chapters.jsonl` e `acts.jsonl`. Capítulos sem medida têm o cru recontado do `raw.jsonl` (`_crusSemMedida`).
5. `_indiceNoRegistro` recebe os índices, para esses turnos não serem gravados de novo.

### 15.4 Compatibilidade com registros v1

- `Versao` ausente no JSON = 1. `Chapter.Render`/`Act.Render` desenham v1 no formato antigo: título, parágrafo (`Summary`) e lista `Artefatos:` (`Artifact.Render`, já com a proteção de comando que apaga).
- Ato sobre capítulos mistos (algum v1) usa o caminho antigo (`ActPrompt`, parágrafo), porque v1 não tem Estado para fundir.
- Em v2, `Summary` recebe o Objetivo, para quem ainda lê o campo antigo.
- Capítulos antigos com índice de turno relativo são tratados por `LastCoveredTurn` (§13.3); capítulos sem medida, por `TemMedida` e `_crusSemMedida`.
- `RenderChaptersForSummary` lê as duas versões (objetivo e lições no v2, parágrafo no v1).

---

## 16. Regras para não quebrar

- `raw.jsonl` nunca é apagado nem reescrito. Nenhum caminho novo remove arquivos da pasta da sessão. A única exceção é o reset de fábrica, que apaga `~/.AIB` inteira (ver [07](07-configuracoes-e-dados.md)).
- Turnos só saem do contexto **depois** de o capítulo existir. Nunca remova antes do resumo.
- Nunca compacte turno aberto nem corte dentro de um turno. O primeiro turno escolhido entra mesmo grande.
- Só a primeira mensagem de sistema é prefixo fixo. Não conte o bloco de memória no prefixo.
- Nada literal passa pelo modelo: Estado, Combinado, artefatos e pendências de código são montados por código. O modelo só escreve Objetivo, Aprendido e pendências de assunto, e o que ele escreve passa pela `Conferencia`.
- Comando que apaga nunca aparece como linha pronta para repetir no prompt (Estado, artefato, pendência).
- Falha de comando casa com sucesso pela `ComandoDeShell.Assinatura`, não pelo texto.
- `ArtifactExtractor.Falhou` é a definição única de falha (ERRO, ACESSO NEGADO, recusa). Não crie outra.
- Mudar a ordem das faixas do bloco (fatos → atos → capítulos → Pendente → anexos) quebra o cache a cada capítulo.
- Chamadas de resumo usam `ChatRequestOptions.DeServico` (Think desligado, `NumPredict`, janela do Ollama em vigor).
- Tudo que o resumidor lê de resultado de ferramenta passa por `ConteudoDeTerceiros.Redigir` antes de qualquer corte.
- `facts.md` é do usuário: a AIB só acrescenta, nunca reescreve nem remove.
