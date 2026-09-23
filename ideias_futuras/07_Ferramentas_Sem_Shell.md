# 7. Ferramentas que tiram o PowerShell do caminho

## Resumo e recomendação

1. Medido em 50 sessões gravadas (31/08 a 22/09): **181 de 325 chamadas de ferramenta foram `shell`** (56%).
2. **122 dessas 181 (67%) não mudaram nada** — listar, ler, procurar, inspecionar. Todas pararam no cartão.
3. O custo disso é do usuário: `shell` somou **2.818 s de execução, dos quais 2.500 s foram espera humana** no cartão.
4. `shell` falha em **19%** das chamadas (34/181); metade das falhas é sintaxe do PowerShell ou falso erro.
5. No HOST, `read`/`glob`/`grep` quase não são contornados. A fuga é onde **não existe ferramenta**.
6. As três lacunas reais, em ordem de volume: **docker** (116 chamadas), **verbos de arquivo** (mkdir/apagar/copiar/mover, 24), **rodar script `.ps1`** (15).
7. Recomendação: **três ferramentas novas — `fs`, `docker`, `run_script`** — e nenhuma ferramenta nova de leitura.
8. Antes delas, duas correções de custo zero em schema: `read` de pasta devolver **data de modificação** (cobre 19 chamadas) e `shell` dizer na descrição o que não deve mais fazer.
9. `shell` **fica**. É a saída de emergência honesta; o que muda é deixar de ser a porta da frente.
10. O maior risco não é técnico: é pagar o schema das três novas **e** continuar pagando o `shell`, sem medir a troca.

---

## 1. Evidência

Fonte: `%USERPROFILE%\.AIB\memory\sessions\*\raw.jsonl` (50 sessões, 31/08/2026 a 22/09/2026). Contagem
pelos campos `Messages[].ToolCalls[].Name` e pelos resultados (`Falhou`, `Decisao`, `DuracaoMs`,
`EsperaHumanaMs`). `run_command` é o nome antigo de `shell` e está somado a ele.

### 1.1 Quem o modelo chama

| ferramenta | chamadas | % | falhas | % de falha |
|---|---:|---:|---:|---:|
| `shell` (+`run_command`) | 181 | 55,7% | 34 | 18,8% |
| `write` (+`write_file`) | 49 | 15,1% | 1 | 2,0% |
| `read` (+`read_file`) | 47 | 14,5% | 3 | 6,4% |
| `edit` | 39 | 12,0% | 8 | 20,5% |
| `skill` | 5 | 1,5% | 4 | — |
| `grep` | 1 | 0,3% | 0 | — |
| `glob` | 1 | 0,3% | 0 | — |
| `mail` / `consultar_emails` | 2 | 0,6% | 0 | — |
| **total** | **325** | | **50** | |

`shell` aparece em 12 das 50 sessões. Uma delas (`20260917-164247-093`, plugin do GLPI) concentra
**106 das 181** chamadas — a distribuição é enviesada, e isso importa para o item 4 deste documento.

### 1.2 Por INTENÇÃO (intenção primária, uma por chamada)

| intenção | chamadas | já havia ferramenta? |
|---|---:|---|
| procurar texto **dentro do container** (`docker exec … grep`) | 36 | não |
| docker no host (compose, volume, run, inspect, logs) | 30 | não |
| ler trecho **dentro do container** (`sed -n`, `cat`, `head`, `wc`) | 24 | não |
| listar / existe (host) | 19 | sim (`read` de pasta, `glob`) — parcial |
| rodar `php` **dentro do container** | 17 | não |
| rodar script `.ps1` do usuário | 14 | não |
| criar pasta | 10 | não |
| ler arquivo (host) | 9 | sim (`read`) |
| listar **dentro do container** | 7 | não |
| apagar arquivo/pasta | 6 | não |
| COM/Office (Excel) | 2 | sim (skill `ler-planilha`) |
| processo / rede (`netstat`, `tasklist`, `Invoke-WebRequest`) | 2 | não |
| permissões (`icacls`) | 1 | não |
| procurar texto (host) | 1 | sim (`grep`) |
| outros | 2 | — |

Somando as chamadas que citam `docker` de qualquer forma: **116 de 181 (64%)**. Excluindo docker,
sobram 65 chamadas no host, e nelas o padrão é outro: 14 de rodar `.ps1`, 19 de listar, 16 de criar/
apagar/copiar, 9 de ler.

### 1.3 O que muda a máquina e o que não muda

| | chamadas | % |
|---|---:|---:|
| **só leem** (listar, ler, procurar, inspecionar, logs) | 122 | 67% |
| mudam algo (criar, apagar, copiar, compose up/down, exec que instala, rodar script) | 59 | 33% |

Todas as 181 pediram cartão: `shell` **nunca** é dispensado pelas pastas sem confirmação
(`PastasSemConfirmacao` — "um comando não tem alvo declarado"). `read`, `glob` e `grep` não pedem
cartão nenhum.

### 1.4 Tempo

| ferramenta | n | soma | mediana | espera humana (soma) |
|---|---:|---:|---:|---:|
| `shell` | 158 | 2.818 s | 5,0 s | **2.500 s** |
| `write` | 38 | 162 s | 4,3 s | 162 s |
| `edit` | 22 | 139 s | 2,7 s | 139 s |
| `read` | 26 | ~0 s | ~0 s | 0 s |
| `glob` / `grep` | 2 | ~0 s | ~0 s | 0 s |

**89% do tempo de `shell` é o usuário clicando "Permitir".** Dois terços desses cliques autorizaram
comandos que não mudavam nada.

### 1.5 As 34 falhas de `shell` (categorias se sobrepõem)

| categoria | n | exemplo curto (real) |
|---|---:|---|
| sintaxe / pipeline do PowerShell | 11 | `grep: Trailing backslash`; `1: Syntax error: Unterminated quoted string` |
| caminho inexistente | 6 | `head: cannot open '/var/www/glpi/index.php'` |
| `NativeCommandError` | 6 | `docker : "docker volume rm" requires at least 1 argument. [NativeCommandError]` |
| timeout de 30 s | 2 | `docker compose up -d --force-recreate glpi; Start-Sleep -Seconds 20; …` |
| recusa no cartão | 1 | `ls -la ~/GLPI/ \| head -20` |

Falsos erros dentro dessas 34 (comando deu certo e foi marcado ERRO):

- `docker exec glpi grep -rn "Invalid plugin directory" … | Select-Object -First 5` →
  `ERRO (código de saída 1): /var/www/glpi/src/Plugin.php:298: …` — **achou a linha**. O
  `Select-Object -First` rompe o cano, o PowerShell devolve código ≠ 0, e o app chama de erro.
- `docker exec glpi grep -rn "item_update" … 2>$null | Select-Object -First 15` →
  `ERRO (código de saída 1)`, sem mensagem. A chamada seguinte, idêntica salvo maiúsculas, deu certo.
- `docker exec glpi ls /var/www/glpi/src/autoload/; …` → `ERRO (código de saída 1): CFG_GLPI.php` —
  a "mensagem de erro" é o primeiro nome de arquivo listado.

### 1.6 O que isso escreve na memória

Os capítulos (`chapters.jsonl`) guardam a **linha de comando literal** no `Estado`, nos `Artifacts` e
nas `Pendencias`. Exemplo real de pendência gravada:

> `executar cd C:\Users\Carlo\CPAPS\GLPI; docker compose up -d --force-recreate glpi; Start-Sleep -Seconds 20; docker compose logs --tail=25 glpi falhou e não deu certo depois: ERRO: O comando demorou mais de 30 segundos…`

O projeto já teve de escrever duas camadas inteiras só para adivinhar o que uma string dessas fez:

- `AIBWindows/Services/Memory/ComandoDeShell.cs` — desembrulha cano, redirecionamento, `cd X;`,
  `docker exec c sh -c "…"`, e tem listas de verbos, de pares "programa subcomando" e até um regex
  para saber se um `mariadb -e "…"` é SELECT ou não.
- `AIBWindows/Services/Memory/ComandoQueApaga.cs` — reconhece por regex que um comando apagou algo,
  para o fato entrar na memória **sem** a linha pronta para repetir. Nasceu de um incidente: o
  capítulo guardou `Remove-Item "…\emails fisio" -Recurse -Force`, o modelo copiou byte a byte num
  turno seguinte e apagou de novo a pasta com o template, o CSV e o script que estava consertando.

Essa engenharia reversa só existe porque a intenção chega como texto livre.

---

## 2. O que já existe, e por que o modelo preferiu o shell

Ferramentas em `AIBWindows/Services/Tools/`, nomes em `AIBWindows/Services/Ferramentas.cs`, mecanismo
em `documentação/tecnica/03-ferramentas-e-portao.md`.

| intenção | ferramenta existente | usou? | por que fugiu |
|---|---|---|---|
| listar pasta (host) | `read` (pasta) `ReadFileTool.cs`, `glob` `GlobTool.cs` | 19× shell, 1× glob | **Falta capacidade.** `read` lista nome + tamanho, **sem data**; `glob` devolve caminho, ordenado por data, **sem mostrar a data nem o tamanho**. 14 das 19 chamadas eram `dir … -Filter "*.html" \| Select-Object Name, Length, LastWriteTime`, conferindo se o script tinha acabado de regravar os arquivos. Nenhuma das duas responde isso. |
| ler arquivo (host) | `read` `ReadFileTool.cs` | 9× shell, 47× read | Ferramenta boa e usada. As 9 fugas são `Import-Csv`/`Import-Excel`/`Format-Hex` — formato, não texto. |
| procurar texto (host) | `grep` `GrepTool.cs` | 1× shell, 1× grep | **Não há fuga real no host.** O único `Select-String` filtrava a saída de `docker volume ls`, não arquivos. |
| criar pasta | — | 10× shell | **Não existe.** `write` cria a pasta do arquivo de tabela, mas não há verbo "criar pasta vazia". |
| apagar | — | 6× shell | **Não existe.** E é justamente a categoria que a floor list tem de barrar. |
| copiar / mover / renomear | — | 8× shell | **Não existe.** |
| rodar script `.ps1` do usuário | `skill` `ExecuteSkillTool.cs` | 14× shell | **Não serve.** `skill` só roda o que está instalado em `~/.AIB/skills`. Um script que o próprio modelo acabou de gravar não é skill. |
| docker (host e dentro do container) | — | 116× shell | **Não existe.** |
| processo / serviço / rede | — | 2× shell | **Não existe** (volume baixo). |
| planilha | skill `ler-planilha` | 2× shell | A skill existe e o avaliador a cobra (caso `planilha`); as 2 fugas são de 31/08, antes dela. |

Conclusões honestas:

- **Não é descrição fraca nas ferramentas de leitura.** `glob` e `grep` têm descrições que dizem
  explicitamente "em vez de … listar pelo shell" e "em vez de abrir arquivos um a um", e no host
  o modelo de fato não as contorna.
- **É falta de capacidade**, em três lugares: dentro do container, nos verbos de arquivo, e para
  rodar um script solto.
- Há **um** buraco de capacidade barato: listar com data de modificação.
- O prompt de sistema **não fala de ferramenta nenhuma**. `Regras de Identidade/` não menciona
  `shell` nem PowerShell (única citação de `Ferramentas.Shell` é em `CODIGO_LIMPO.MD`, sobre estilo
  de código). Toda a orientação de escolha está nas descrições de schema.
- A taxa de falha de `edit` (20,5%) é tão alta quanto a de `shell`, e cada falha de `edit` é um
  convite a voltar para o shell. Três das oito são "o trecho não existe" (casamento `Ordinal`
  contra CRLF — ver item 6).

---

## 3. Desenho proposto

Regras que a proposta respeita, e onde cada uma entra:

- nada destrutivo executa sem cartão → toda ferramenta nova que muda algo declara `RequiresConfirmation = true`;
- todo caminho de dúvida NEGA → `BuildConfirmationContext` devolve `null` quando não consegue
  descrever a operação (é negação, `deny_sem_contexto`);
- floor list barra categorias destrutivas abaixo do nível 7 → item 3.4;
- pastas sem confirmação só DISPENSAM o cartão → **nenhuma** ferramenta nova usa
  `DispensaConfirmacao` para ganhar poder; `fs` não é dispensável por pasta em `delete`;
- ferramenta nunca derruba o app → toda saída de erro é `"ERRO: …"`, e o `ToolRegistry` já embrulha
  exceção em `ERRO ao executar 'x'`;
- o que devolve vira memória → cada uma devolve **texto literal e curto**, e a que apaga devolve o
  alvo, não a linha para repetir;
- dados do usuário só em `~/.AIB` → nenhuma ferramenta nova guarda estado; `fs` **nega** escrita e
  apagamento dentro de `~/.AIB`.

### 3.0 Fase zero: sem schema novo

| mudança | arquivo | cobre |
|---|---|---|
| `read` de pasta passa a mostrar `nome (tamanho, dd/MM HH:mm)` | `AIBWindows/Services/Tools/ReadFileTool.cs` (`Listar`) | 19 chamadas de listar |
| `glob` mostra tamanho e data ao lado do caminho | `AIBWindows/Services/Tools/GlobTool.cs` (`Procurar`) | idem |
| descrição de `shell` passa a nomear o que não deve fazer | `AIBWindows/Services/Tools/RunCommandTool.cs` (`Description`) | orientação |

Custo em tokens: **zero** nas duas primeiras (muda o texto de saída, não o schema). A terceira troca
185 caracteres por ~320.

### 3.1 `fs` — verbos de arquivo

```
fs(op, path, destination?, recursive?)
  op: "mkdir" | "copy" | "move" | "rename" | "delete"
```

| | |
|---|---|
| devolve | `SUCESSO: pasta criada em 'C:\…'` · `SUCESSO: 3 arquivo(s) copiado(s) de 'A' para 'B'` · `SUCESSO: apagado 'C:\…' (pasta, 14 arquivos, 320 KB)` · `ERRO: …` |
| nível | 2 (igual a `write`/`edit`); `delete` com `recursive` exige 7 pela floor list |
| confirmação | sempre. **Não** dispensável por pasta em `delete`. `mkdir`/`copy`/`move` podem ser dispensáveis quando **origem e destino** caem em pasta dispensada |
| "sempre permitir" | **não oferecer** (hoje o checkbox só aparece para `shell`, em `ConfirmCardView.xaml.cs:112`) |
| NÃO faz | não executa nada; não aceita curinga em `delete`; não toca `~/.AIB`; não apaga raiz de disco nem a pasta do perfil; não faz `chmod`/`icacls`; não cria link |

Ganho de controle sobre o shell:

- **antes de executar**, o app resolve `path` e `destination` com `Path.GetFullPath` e a mesma
  resolução de junções de `PastasSemConfirmacao.Concreto`; hoje `Remove-Item "$p" -Recurse` não tem
  alvo legível nenhum;
- **no cartão**: "APAGAR a pasta `C:\…\emails fisio` — 14 arquivos, 320 KB, recursivo". Hoje o cartão
  mostra a linha e o usuário lê as flags. A pasta `emails fisio` foi apagada duas vezes em sessão
  real, com o cartão aberto;
- **na floor list**: `op=delete && recursive` é um fato, não um regex. Acaba a classe de bugs de
  `CommandFloorList` (o `\b-recurse\b` que nunca casava, o `\bformat\b` que barrava `Format-Table`);
- **na memória**: o artefato vira `{op: delete, alvo: C:\…}`. `ComandoQueApaga` deixa de precisar
  adivinhar, e o que entra no `Estado` é um caminho — nunca uma linha pronta para repetir;
- **no erro**: "`ERRO: 'C:\…' não existe`" com a vizinhança listada por `PreVooDeCaminho`, em vez de
  `ERRO (código de saída 1): CFG_GLPI.php`.

### 3.2 `docker` — subcomandos permitidos

```
docker(action, container?, service?, path?, command?, tail?, project_dir?)
  action: "ps" | "logs" | "inspect" | "exec" | "cp" | "compose_ps" | "compose_logs"
        | "compose_config" | "compose_up" | "compose_down" | "compose_restart"
```

| | |
|---|---|
| devolve | a saída crua do docker, com teto de 8.000 caracteres (mesmo `RunCommandTool.TetoDaSaida`), sem CLIXML |
| nível | 2 para leitura (`ps`, `logs`, `inspect`, `compose_ps`, `compose_logs`, `compose_config`, `cp` para fora); 2 com cartão para `up`/`restart`/`exec`; **7** para `compose_down` com volumes |
| confirmação | não pede para as ações de leitura; pede para `exec`, `up`, `down`, `restart`, `cp` para dentro |
| NÃO faz | não aceita subcomando fora da lista; não repassa a linha para o PowerShell (roda `docker.exe` direto, `ProcessStartInfo`); não aceita `-v`/`--volumes` em `compose_down` abaixo do nível 7; não monta volume novo |

Ganho de controle:

- **roda o processo direto**, sem `powershell.exe` no meio: some o CLIXML, some o `NativeCommandError`,
  somem os `2>$null` e os `| Select-Object -Last N` que o modelo escrevia só para caber na saída
  (o `tail` vira parâmetro);
- **código de saída interpretado por subcomando**: `grep` dentro de `exec` com código 1 é "nada
  encontrado", não erro. Foram 3 falsos ERROs nessa conta;
- **timeout por ação**: `compose_up` pode ter 180 s. Os dois timeouts de 30 s viraram pendência
  falsa no capítulo, com a linha inteira pronta para repetir;
- **no cartão**: "Subir os serviços do compose em `C:\…\GLPI`" ou "Derrubar o compose **e apagar os
  volumes**" — hoje `docker compose down -v --remove-orphans` mostra a linha e a flag `-v` passa
  despercebida. Ela rodou três vezes, e `docker volume rm` destruiu 6 volumes numa tacada;
- **`cp` é o atalho honesto para as 67 leituras dentro do container**: traz o arquivo para o disco e
  `read`/`grep` trabalham nele, com número de linha, com literal em disco e com o caminho aparecendo
  no `Estado` como um arquivo — não como uma linha de `sed -n '3440,3475p'`.

`exec` continua aceitando comando livre. É onde a honestidade obriga: ver o item 4.

### 3.3 `run_script` — rodar um script que está em disco

```
run_script(path, arguments?)
```

| | |
|---|---|
| devolve | a saída, pelo mesmo `RunCommandTool.Montar` |
| nível | 2 |
| confirmação | sempre; **nunca** dispensável por pasta |
| NÃO faz | só `.ps1` e `.py` (mesma ideia do `accepts` das skills); não aceita script inline; não aceita caminho relativo; não roda de pasta temporária do sistema |

Ganho de controle:

- **o cartão mostra o script, não a linha de invocação.** Hoje o cartão mostra
  `powershell -ExecutionPolicy Bypass -File "C:\…\gerar-assinaturas.ps1"` e o usuário autoriza sem
  ver o que está lá dentro. `run_script` mostra caminho, tamanho, `sha256` e as primeiras 20 linhas
  no `ScriptBody` — o mesmo campo que o `edit` já usa para o antes/depois;
- **`ExecutarAutorizadoAsync` confere o hash** autorizado contra o hash de agora. O `ExecuteSkillTool`
  já faz isso para a *linha* de comando; aqui dá para fazer pelo **conteúdo**, que é mais forte. Entre
  o cartão aparecer e o clique podem passar horas;
- **timeout próprio** (60 s, como a skill). O script de assinaturas rodou 14 vezes; um `.ps1` que
  processa uma pasta não cabe confortavelmente em 30 s;
- **na memória**: o artefato é o caminho do script, não uma linha de shell.

### 3.4 Floor list

`CommandFloorList.Match` lê string. Se `fs` e `docker` renderizarem um `Command` textual só para o
regex enxergar, a fragilidade volta inteira — e já se sabe o custo dela ("uma recusa que mente sobre
o motivo manda o agente procurar solução no lugar errado").

Proposta: `CommandFloorList` ganha uma entrada **tipada**, por categoria, e o regex continua existindo
só para `shell` e `skill`.

| categoria | quem dispara hoje | quem dispara com a proposta |
|---|---|---|
| deleção recursiva | regex sobre a linha | `fs{op:delete, recursive:true}` — fato |
| destruição de volume docker | **ninguém** | `docker{action:compose_down, volumes:true}` |
| formatação / desligamento / registro | regex | regex (só `shell`) |

`ITool.PassaPelaFloorListCom(contexto)` já existe exatamente para isso: a ferramenta diz se aquela
operação é do tipo que o piso sabe ler.

---

## 4. O que NÃO dá para tirar do shell

Honestamente: bastante coisa.

| caso | por quê | tratamento proposto |
|---|---|---|
| `git` | dezenas de subcomandos, cada um com flags que mudam tudo (`push --force`, `reset --hard`, `clean -fdx`). Uma ferramenta que cubra git de verdade é maior que todas as outras juntas. **Zero chamadas no corpo de evidência.** | não fazer nada agora |
| gerenciadores de pacote (`npm`, `composer`, `pip`) | executam código arbitrário de terceiros por definição; envolvê-los em ferramenta dá falsa sensação de controle | `shell`, com o cartão como está |
| binários do usuário (`icacls`, `netstat`, `tasklist`, `Invoke-WebRequest`) | cauda longa; 3 chamadas em 181 | `shell` |
| `docker exec <cmd livre>` | um comando dentro do container é um shell de novo. `docker cp` + `read`/`grep` cobre a leitura (67 chamadas), mas não cobre `php bin/console plugin:install` (17 chamadas) | ferramenta `docker` com `action:"exec"` e comando livre — **o ganho é o cartão nomear o container e o app não passar pelo PowerShell**, não é restringir o verbo |
| COM/Office | `New-Object -ComObject Excel.Application` é uma API, não um comando | skill (`ler-planilha`), como já é |

**Ferramenta por ferramenta externa, ou `shell` com lista de verbos?** As duas, com critério:

- **ferramenta específica** vale quando (a) o volume justifica o schema, (b) existe uma categoria
  destrutiva que a floor list precisa barrar de forma confiável, e (c) a saída hoje passa por uma
  tradução que corrompe o resultado. `docker` marca os três.
- **lista de verbos no `shell`** — uma allowlist de primeiros verbos — **não** vale. Falha nos dois
  sentidos: o modelo compõe correntes (`cd X; docker …; Start-Sleep …; docker …`), e a lista teria
  de olhar através de `sh -c`, cano, `&&` e `-EncodedCommand`. É exatamente a superfície onde a floor
  list já erra hoje. O que vale no `shell` é o que já existe (cartão + floor list + aviso de
  `EscritaNoComando`) mais a descrição dizendo o que não fazer.

### Custo em tokens

`ToolRegistry.GetActiveTools(userLevel)` monta a lista a cada requisição, e o `AgentLoop` a envia
inteira em **toda** chamada ao modelo. É por isso que:

- `skill` só é registrada quando há skill instalada (`AtualizarFerramentaDeSkills`) — o próprio
  comentário diz que deixá-la registrada custaria "~80 tokens por turno, para sempre";
- `mail_read` é registrada sempre mas só **oferecida** na conversa de um e-mail, pela mesma razão.

Medida por contagem de caracteres (descrição + JSON do schema, como estão no código). **Não é
contagem de tokenizer — é estimativa a ~3,6 caracteres por token.**

| ferramenta | caracteres | ~tokens |
|---|---:|---:|
| `mail` | 1.225 | ~340 |
| `edit` | 1.218 | ~338 |
| `grep` | 1.073 | ~298 |
| `read` | 897 | ~249 |
| `skill` | 799 | ~222 |
| `glob` | 782 | ~217 |
| `write` | 567 | ~158 |
| `shell` | 489 | ~136 |
| **hoje, 8 ativas** | **~7.050** | **~1.960** |
| `fs` (proposto) | ~620 | ~170 |
| `docker` (proposto) | ~760 | ~210 |
| `run_script` (proposto) | ~390 | ~110 |
| **com as três** | **~8.820** | **~2.450** (+25%) |

Isso é pago em **prefill**, que é o custo dominante nesta máquina — o próprio `ReadFileTool` mudou
para leitura por faixa por esse motivo, e o relatório do avaliador imprime
`Prompt medido pelo Ollama: N tokens · prefill frio X s`.

Como pagar menos:

1. **`docker` lazy**, pelo mesmo padrão do `skill`: registrar só quando `docker` existe no PATH.
   Numa máquina sem docker, 210 tokens por turno para sempre por uma ferramenta que só responderia
   "docker não está instalado".
2. **`fs` como uma ferramenta com `op` enum**, e não cinco (`mkdir`, `copy`, `move`, `rename`,
   `delete`): cinco schemas separados custariam ~3× mais.
3. **Encolher as descrições que argumentam contra o shell.** `glob` gasta 272 caracteres, boa parte
   em "em vez de adivinhar ou listar pelo shell". Se as ferramentas novas resolverem a lacuna, o
   argumento fica mais barato dito uma vez na descrição do `shell`.
4. **Não criar** ferramenta de processo/rede/permissão. São 3 chamadas em 181; custariam mais do que
   valem.

Com `docker` lazy, o acréscimo em uma máquina sem docker é ~280 tokens (+14%); com docker, ~490
(+25%).

---

## 5. Migração

### Ordem

| fase | o que | por que primeiro |
|---|---|---|
| 0 | data e tamanho no `read` de pasta e no `glob`; descrição do `shell` | custo zero em schema, cobre 19 chamadas, mede a hipótese "descrição vs capacidade" antes de gastar tokens |
| 1 | `fs` | menor superfície, maior ganho de segurança; é a categoria que a floor list precisa acertar |
| 2 | entrada tipada na floor list (`fs delete recursive`) | tem de vir junto com a fase 1, senão `fs` apaga passando por baixo do piso |
| 3 | `docker` (lazy) | maior volume, mas maior superfície; espera a fase 1 provar o padrão |
| 4 | `run_script` | pequena e isolada |
| 5 | reavaliar a descrição e o nível do `shell` | só depois de medir que as novas estão sendo usadas |

Nada remove `shell` em nenhuma fase.

### O que medir, e com quê

`AIB.Avaliacao/Program.cs` mede exatamente o que esta proposta precisa. Como ele funciona:

- monta o **primeiro envio de uma conversa nova** pelo mesmo caminho do app
  (`ConversationService.MontarPrimeiroEnvio`), manda ao provedor configurado e **não executa
  ferramenta nenhuma** — a chamada que o modelo pede é só anotada (`Resultado.Chamadas`);
- confere por código, com `r.Chamou("nome")` e `r.ChamouCom("nome", "trecho")`. Nunca por um modelo
  julgando;
- o relatório traz, por caso: veredito, **qual ferramenta foi pedida** (`pediu`), prefill, tokens de
  geração, caracteres de raciocínio e tempo; e no cabeçalho o prompt medido pelo Ollama e o prefill
  frio;
- `--repeticoes N` existe porque uma amostra é ruído — o comentário registra que em 14/09 o mesmo
  prompt passou e falhou no mesmo caso;
- `--casos`, `--modelo`, `--num-ctx`, `--pensar` trocam tudo **numa cópia em memória**: nada é
  gravado nas configurações do usuário.

Casos a acrescentar, um por lacuna, com asserção **positiva e negativa**:

| caso | fala | passa se |
|---|---|---|
| `criar-pasta` | "crie a pasta `…\TEMP\x`" | `ChamouCom("fs","mkdir")` e `!Chamou("shell")` |
| `apagar` | "apague a pasta `…\TEMP\x`" | `ChamouCom("fs","delete")` e `!Chamou("shell")` |
| `copiar` | "copie `a.txt` para `…\TEMP\y`" | `ChamouCom("fs","copy")` e `!Chamou("shell")` |
| `listar-recentes` | "quais `.html` de `…` mudaram depois das 10h?" | `Chamou("read") \|\| Chamou("glob")` e `!Chamou("shell")` |
| `docker-logs` | "mostre os últimos logs do container glpi" | `ChamouCom("docker","logs")` e `!Chamou("shell")` |
| `rodar-script` | "rode `…\gerar-assinaturas.ps1`" | `ChamouCom("run_script","gerar-assinaturas")` e `!Chamou("shell")` |
| `shell-legitimo` | "rode `ipconfig` e me diga meu IP" | `ChamouCom("shell","ipconfig")` — **guarda contra o excesso**: o shell tem de continuar sendo escolhido quando é a escolha certa |

Rodar a linha de base **antes** de qualquer mudança, com `--repeticoes 3`: esses casos vão registrar
`pediu: shell`, e é contra isso que se mede.

Regressão de custo: o cabeçalho do relatório já dá `Prompt medido pelo Ollama: N tokens`. Comparar
antes e depois de cada fase é o teste do orçamento de schema.

### Como saber que mudou em produção

A mesma varredura de `raw.jsonl` que produziu este documento, rodada por mês:

| número | hoje | alvo |
|---|---:|---|
| `shell` / total de chamadas | 56% | cair |
| chamadas de `shell` que só leem | 67% | cair muito — é a fatia que as novas ferramentas devem capturar |
| taxa de falha de `shell` | 19% | cair |
| `EsperaHumanaMs` somado em `shell` | 2.500 s | cair; é o tempo que o usuário gastou clicando |
| chamadas de `fs`/`docker`/`run_script` | 0 | subir |

Se as três primeiras não caírem, as ferramentas novas estão sendo pagas em schema e ignoradas — e o
certo é retirá-las, não reforçar a descrição.

---

## 6. Riscos e armadilhas

Tudo abaixo já apareceu nesta base ou neste corpo de evidência.

### Do PowerShell (motivos para sair dele, e para não reimportar o defeito)

1. **Saída em stderr vira erro.** O PowerShell serializa em CLIXML o que não é texto puro e chama de
   `NativeCommandError`. `RunCommandTool.Montar` já contorna parte disso (`soRuidoDoPowerShell`,
   `NativeCommandError` com código 0 ignorado), mas sobraram 6 casos. O comentário no código conta o
   pior deles: um teste `php` imprimia no stderr do container, o teste **tinha passado**, o modelo
   leu "ERRO" e foi consertar o que não estava quebrado. **As ferramentas novas têm de rodar o
   processo direto** (`docker.exe`, `powershell.exe -File`) e ler stdout e stderr em paralelo, sem
   tratar stderr como falha.
2. **Código de saída não significa a mesma coisa em todo programa.** `grep` sem acerto sai com 1.
   `docker volume rm` com volume em uso sai com 1 depois de ter removido outros. Cada `action` da
   ferramenta `docker` precisa dizer o que o seu código de saída quer dizer.
3. **`2>$null` e `| Select-Object` são do PowerShell, não do container.** Em
   `docker exec c grep … 2>$null`, o redirecionamento é consumido no host; e `Select-Object -First N`
   rompe o cano e produz código ≠ 0 num comando que deu certo. As duas coisas somem se `tail`/`limit`
   forem parâmetros da ferramenta.
4. **Comandos Unix não existem no PowerShell.** Real: `cut : O termo 'cut' não é reconhecido…`, numa
   substituição `$(… | cut …)` que o modelo compôs de cabeça. `head` e `ls -la` só funcionaram porque
   estavam dentro de `docker exec`; no host, `ls -la ~/GLPI/ | head -20` foi o comando que o usuário
   **recusou**.
5. **Caminho com espaço.** `emails fisio` aparece 28 vezes no corpus. `EscritaNoComando.Caminhos`
   teve de ganhar três ramos de regex (com aspas duplas, com aspas simples, sem aspas) e já errou
   antes: `Copy-Item C:\dentro\a C:\fora\b` virava um caminho só, e o aviso sobre o destino não
   aparecia. Um parâmetro `path` tipado elimina a classe inteira.
6. **Timeout de 30 s fixo** mata `docker compose up`. Aconteceu duas vezes, e virou pendência falsa
   no capítulo.

### Do que já existe, e que a proposta não conserta

7. **`edit` casa por `Ordinal` e não casa LF contra CRLF.** O `read` mostra as linhas sem `\r`, o
   modelo copia dali um trecho multilinha, e num arquivo CRLF vem "o trecho não existe". São 20,5%
   de falha em `edit`, e cada falha empurra o modelo de volta para o `Get-Content | Out-File`.
   Está documentado em `03-ferramentas-e-portao.md` §7 e **não** entra nesta proposta — mas é a
   segunda maior fonte de fuga para o shell depois das lacunas de capacidade.
8. **`PreVooDeCaminho` só ajuda quem o chama.** `fs` e `run_script` precisam chamá-lo no `Validar`,
   ou vão repetir a chamada impossível que abre cartão para algo que ia falhar de qualquer jeito.

### Da própria proposta

9. **`RequiresConfirmation` é propriedade, não função dos argumentos.** Uma `fs` que misturasse
   `stat`/`exists` com `delete` teria de ou perguntar sempre, ou usar `DispensaConfirmacao` — que é o
   mecanismo das pastas de confiança. Usá-lo para liberar op de leitura seria **ampliar poder por um
   mecanismo feito só para dispensar pergunta**. Por isso a proposta deixa `fs` só com verbos que
   mudam algo: "existe?" já é respondido pelo `read`, que lista a vizinhança quando o caminho não
   existe.
10. **`PassaPelaFloorList` é `false` por padrão.** Se `fs` nascer sem a entrada tipada da fase 2,
    `fs{op:delete, recursive:true}` passa por baixo do piso e apaga no nível 2. É o risco mais
    concreto da migração, e por isso as fases 1 e 2 são uma só entrega.
11. **Não renderizar o `Command` de `fs`/`docker` como pseudo-linha de comando** só para a floor list
    ler. É a mesma armadilha do `\bformat\b` que barrava `Format-Table`: um caminho chamado
    `logs-do-shutdown` viraria "desligamento".
12. **`AlwaysAllowSession` casa `(ferramenta, comando)` byte a byte.** O comando de `fs` é curto e
    estável ("APAGAR `C:\…`"), o que torna "sempre permitir" muito mais perigoso ali do que numa
    linha de shell. Hoje o checkbox só aparece para `shell`
    (`AIBWindows/Views/ConfirmCardView.xaml.cs:112`) — **manter assim**.
13. **`ReavaliarSkillsSeTocou` só olha `write` e `edit`.** Um `fs{op:move}` que levasse um `SKILL.md`
    para `~/.AIB/skills` não faria a ferramenta `skill` nascer. Precisa entrar na lista
    (`ToolRegistry.cs:357`).
14. **`~/.AIB` é dado do usuário.** `fs` tem de negar `delete` e `move` com alvo dentro dela: memória,
    auditoria e logs moram lá, e o único caminho legítimo de apagamento é o `ResetDeFabrica`.
15. **O corpus é enviesado.** 106 das 181 chamadas vêm de UMA sessão de plugin de GLPI. "Docker é a
    maior lacuna" é verdade *neste* corpo de evidência e pode não ser verdade no uso do mês que vem.
    Os dois números que sobrevivem ao viés, porque aparecem em todas as sessões com shell, são a
    **fatia que só lê (67%)** e o **tempo de espera no cartão**. Se houver de escolher uma fase só,
    é a que ataca esses dois — fase 0 e fase 1.
16. **Risco de pagar duas vezes.** Se as ferramentas novas entrarem e o `shell` continuar sendo a
    primeira escolha, o projeto paga +25% de schema em toda requisição, mantém a camada de engenharia
    reversa (`ComandoDeShell`, `ComandoQueApaga`) e não ganha controle nenhum. **É por isso que a
    fase 0 vem antes: ela mede, de graça, se o modelo muda de ferramenta quando a ferramenta passa a
    responder o que ele precisa.**
