# 07 — Configurações e dados

Onde o AIB guarda o que é do usuário, como as configurações são carregadas e saneadas, onde moram as credenciais, como funcionam os níveis e de onde vêm os personagens.

## A regra: `~/.AIB` é a verdade

Tudo o que é do usuário mora em `%USERPROFILE%\.AIB` (`DirectoryService.DataDir`). A pasta do programa — onde está o executável — é só o **padrão de fábrica** e o fallback de leitura. Isso vale para configurações, memória, histórico, credenciais, e-mail, skills e personagens.

Consequências práticas:

- Nenhum código grava fora de `~/.AIB` (ou da pasta temporária do sistema, para caches). Um arquivo novo do usuário ganha caminho derivado de `DirectoryService`.
- Quando algo existe nos dois lugares, a versão de `~/.AIB` manda. A cópia da instalação só é lida se a do usuário não existir.
- Os testes nunca escrevem no `~/.AIB` real: toda classe que grava em disco aceita uma raiz alternativa (ver [08-testes-e-avaliacao.md](08-testes-e-avaliacao.md)).

O histórico de conversas foi o último arquivo a sair de `%APPDATA%\AIB` para `~/.AIB`; o arquivo antigo foi descartado de propósito, sem migração (comentário em `ChatHistoryService`).

## `DirectoryService`

`AIBWindows/Services/DirectoryService.cs` é estático e centraliza os caminhos.

| Membro | Caminho |
|---|---|
| `DataDir` | `~/.AIB` |
| `SkillsDir` | `~/.AIB/skills` |
| `MemoryDir` | `~/.AIB/memory` |
| `LogsDir` | `~/.AIB/logs` |
| `CharactersDir` | `~/.AIB/character` |
| `SettingsPath` | `~/.AIB/profile.dat` |

O AIB não usa pasta temporária.

- `DirectoryService.EnsureDirectories` roda no arranque, antes de tudo. Se existe a pasta antiga `%APPDATA%\AIB` e ainda não existe `~/.AIB`, copia a antiga. Depois cria as pastas e semeia os personagens que faltam (ver [Personagens](#personagens)). Nunca lança: falha vai para o console.
- `DirectoryService.ApplyFromSettings` troca `DataDir` se `UserAppSettings.DataDirectory` estiver preenchido, e roda `EnsureDirectories` de novo. Não há campo na tela para ele. Depois disso o `App` chama `SettingsService.InvalidateCache`, porque o caminho das configurações pode ter mudado.
- `DirectoryService.FailsafeCharactersDir` acha a pasta `character` da instalação: primeiro ao lado do executável (é a que vale em produção; o `.csproj` copia `character\**` para lá), depois subindo três ou quatro níveis, o que cobre a execução a partir da árvore de build.

## Estrutura de `~/.AIB`

```
~/.AIB/
├── profile.dat                      configurações (JSON cifrado com DPAPI)
├── chat_history.json                conversas arquivadas, para o painel (cifrado)
├── lembretes.json                   lembretes únicos pendentes (ferramenta remind; cifrado)
├── iniciativa.dat                   iniciativa: faixas de horário e contagens do dia (DPAPI)
├── credentials/
│   ├── openrouter.bin               chave do OpenRouter (DPAPI)
│   └── mail/<sha256>.bin            senha de app de cada caixa (DPAPI)
├── character/<Nome>/                personagens: SOUL.MD, info.json, vinculo.dat (o vínculo com ele, DPAPI)
├── skills/<skill>/SKILL.md          skills instaladas
├── memory/
│   ├── facts.md                     fatos duráveis (do usuário; cifrado por linha, editado na aba Memória)
│   ├── facts.index.jsonl            registro do que já foi promovido (da máquina; cifrado por linha)
│   ├── shadow/                      a conversa do orbe, uma só para sempre (mesmos arquivos de uma sessão)
│   │   └── status.json              atributos dos personagens como estão agora; nasce vazio, ganha cada um quando aparece
│   └── sessions/<id>/
│       ├── raw.jsonl                todos os turnos, crus — nunca apagado (cifrado por linha)
│       ├── chapters.jsonl           capítulos (resumos de turnos; cifrado por linha)
│       ├── acts.jsonl               atos (resumos de capítulos; cifrado por linha)
│       ├── turno-aberto.json        o turno em curso, regravado a cada passo (cifrado)
│       └── compactacao.log          diário da compactação (opt-in)
├── email/
│   ├── estado.json                  por caixa: validade, último UID, quando
│   ├── ultimo-digest.txt            instante do último digest
│   ├── vigias.json                  conversas sob vigilância
│   ├── regras.md                    regras do vigia, editadas à mão
│   ├── ignoradas.json               conversas que o usuário mandou ignorar
│   ├── diario/diario-AAAA-MM-DD.json   o que a triagem decidiu, por dia
│   └── conversas/<hash>/            triagem.jsonl e chave.txt, por conversa de e-mail
├── navegador/
│   ├── perfil/                      perfil do Edge da ferramenta browser (cookies do login)
│   ├── sites-liberados.txt          um domínio por linha, editável à mão
│   └── notas/<domínio>/             anotações: _site.md e um .md por tipo de página
└── logs/
    ├── audit-AAAA-MM-DD.jsonl       auditoria (UTC)
    ├── execucao-AAAA-MM-DD-HHmmss.log   espelho do console (opt-in)
    └── prompt-AAAA-MM-DD-HHmmss.txt     "Imprimir o prompt" da tela de configurações
```

Detalhes que importam:

- **`memory/sessions/<id>/`** (`SessionMemory`). O id é o instante de abertura, `yyyyMMdd-HHmmss-fff`; os milissegundos existem porque duas sessões no mesmo segundo misturariam dois `raw.jsonl`. O id passa por `Path.GetFileName`, para não virar caminho fora da pasta. Os JSONL são UTF-8 **sem BOM** (o BOM estragaria a primeira linha para qualquer parser) e sem escapar acentos. Gravar nunca lança: falha de disco vira `false` e linha no console, porque a memória é um acréscimo e não pode derrubar a conversa. O formato e o papel de cada arquivo estão em [04-memoria.md](04-memoria.md).
- **`raw.jsonl` nunca é apagado.** Resumo é perda irreversível, e um resumo errado aqui não gera só incoerência: gera um agente agindo sobre informação errada com shell na mão. O cru sai do prompt, não sai do disco. Nenhum código do uso normal apaga esse arquivo. A única exceção é o reset de fábrica, que por decisão do usuário é total e apaga `~/.AIB` inteira.
- **`compactacao.log`** (`RegistroDaCompactacao`) só é escrito com `CompactionLogging` ligado. Guarda contagens e tempos, não o texto dos resumos. A pasta é resolvida a cada escrita, porque a sessão troca quando a conversa é zerada ou restaurada.
- **`facts.md`** é do usuário: a AIB só acrescenta linhas no fim, nunca reescreve nem apaga (só o reset de fábrica o leva, com todo o resto). `facts.index.jsonl` é append-only e impede que um fato apagado pelo usuário volte na próxima promoção.
- **`chat_history.json`** (`ChatHistoryService`) é regravado a cada turno, com a conversa viva por cima da própria entrada. Guarda até 50 conversas do usuário e, num teto à parte, até 50 conversas nascidas de e-mail — com um teto só, uma caixa movimentada expulsava as conversas que o usuário começou. Cada `ChatSession` guarda `MemorySessionId`, que liga a entrada à pasta da sessão, e `MailThreadKey` quando nasceu de um e-mail.
- **`email/`**: o corpo de uma mensagem **nunca** é gravado. O diário guarda remetente, assunto e o resumo de uma frase; `MailJournalDays` controla a retenção e em zero desliga o diário. A pasta de cada conversa de e-mail tem nome de hash, para o assunto não vazar em listagens. Ver [05-email.md](05-email.md).
- **`navegador/`** (`NavegadorService`, `SitesLiberados`): o perfil é do Edge, e guarda o que um navegador guarda — cookies e sessão dos sites em que o usuário logou pela janela. **Nada das páginas** é gravado pelo AIB: a leitura fica na RAM e sai para o modelo embrulhada por `ConteudoDeTerceiros` (ver [03](03-ferramentas-e-portao.md)). Apagar `sites-liberados.txt` faz o primeiro acesso a cada site voltar a perguntar; apagar `perfil/` desloga de tudo. O driver do Playwright vive em `.playwright/`, **ao lado do `AIB.exe`** (a publicação em arquivo único não o embute): mover o exe sem essa pasta desliga o navegador.
- **`logs/audit-*.jsonl`** (`AuditLogService`): append-only, um arquivo por dia (data UTC), sem BOM, gravado **antes** da execução da ação auditada. Falha ao auditar vai para o console e não derruba a conversa. Nos testes, `AuditLogService.LogDirectoryOverride` desvia tudo para uma pasta temporária.
- **`logs/execucao-*.log`** (`RegistroDeExecucao`): só com `ExecutionLogging` ligado. Espelha o console, que inclui os prompts inteiros — e o da triagem leva assunto e remetente dos e-mails. O **corpo, não**: ele desce para o prompt embrulhado por `ConteudoDeTerceiros` e `RegistroDeExecucao.Redigir` o troca pelo aviso de omissão antes de a linha chegar ao arquivo (era a última exceção documentada à regra 3, e deixou de ser). Ainda assim nasce desligado, e o cabeçalho do arquivo diz o que ele contém. Guarda os 20 mais recentes e só apaga arquivos com o próprio prefixo, porque a pasta também guarda a auditoria.
- **`logs/prompt-*.txt`** (`RetratoDoEnvio.Gravar`): prefixo diferente de propósito, para a poda do registro de execução não apagá-lo.

### Cifra dos dados (`ArquivoCifrado`, `CifraDaMemoria`)

Conversas, fatos e lembretes são gravados cifrados pelo DPAPI da conta do Windows, como o
`profile.dat` e os cofres. Protege do disco copiado, do backup e do outro usuário da máquina;
**não** protege de programa rodando na mesma conta.

- **Binário** (`ArquivoCifrado.Gravar`): arquivo novo, extensão `.dat` — `iniciativa.dat`,
  `character/<Nome>/vinculo.dat`.
- **Por linha** (`ArquivoCifrado.Cifrar`/`Acrescentar`/`Linhas`): cada linha vira
  `aib1:<base64>`. É o dos arquivos em que só se acrescenta (`raw.jsonl`, `chapters.jsonl`,
  `acts.jsonl`, `facts.md`, `facts.index.jsonl`): continuam arquivos de linhas, e linha em texto
  claro (de antes da cifra) convive com linha cifrada. Linha que não abre — outra conta, ou
  cortada — é pulada.
- **Inteiro** (`ArquivoCifrado.GravarTexto`): o arquivo todo numa linha cifrada, por temporário —
  `chat_history.json`, `lembretes.json`, `turno-aberto.json`.
- `ArquivoCifrado.Ler` abre os três e o texto claro.

**Migração** (`CifraDaMemoria.Migrar`, no arranque, antes de qualquer serviço abrir a memória):
regrava cifrado o que ainda tem texto claro. É a única vez que o AIB reescreve um `raw.jsonl`:
escreve num `.cifrando`, confere que ele aberto dá as mesmas linhas do original e só então troca;
se não der, o original fica. Depois da primeira vez, só confere.

**Exportação** (`CifraDaMemoria.Exportar`, botão "Exportar em texto claro…" na aba Memória): a
cifra é da conta do Windows e **não sobrevive a reinstalação nem a troca de máquina**. A
exportação copia tudo em texto claro, com a mesma árvore, para a pasta escolhida; é o backup.

`read` e `grep` abrem a linha cifrada (a leitura dentro de `~/.AIB/memory` já passou pelo cartão
de `DadosProtegidos`). O `facts.md` deixou de ser editável no bloco de notas: a edição é o campo
"Fatos guardados" da aba Memória (`FactStore.Texto`/`Regravar`).

Continuam em texto claro: os logs e a auditoria, as anotações de site do navegador, os arquivos
de e-mail e o `compactacao.log`.

### Reset de fábrica

É **total**, por decisão do usuário: nada do que o AIB guardou em `~/.AIB` fica, nem cópia `.bak`. A versão anterior varria só `memory/` e `email/` e renomeava `raw.jsonl` e `facts.md`; skills, personagens, logs e o perfil do navegador (com os logins) nem eram tocados.

`SettingsWindow.WipeData_Click`, depois de uma confirmação que lista tudo o que some:

1. `App.SoltarArquivos` fecha o Edge do navegador (que segura `navegador/perfil`) e o registro de execução (que segura o próprio arquivo em `logs/`), e tira o ícone da bandeja.
2. `CredentialService.WipeAllCredentials` e `ChatHistoryService.ClearHistory`.
3. `ResetDeFabrica.Limpar` esvazia a raiz de dados.
4. Mostra o resultado, roda `ResetDeFabrica.Limpar` **de novo** e sai com `Environment.Exit`. A segunda passada pega o que um turno em curso ou o vigia de e-mail gravaram enquanto a mensagem estava na tela. `Exit` em vez de `Shutdown` porque o encerramento normal fecha a conversa, e fechar a conversa a arquiva: recriava `chat_history.json` e a pasta da sessão logo depois do reset.

Não há linha de auditoria do reset: ela recriaria `logs/`. O próximo arranque não acha configuração e abre o primeiro arranque; os personagens de fábrica são semeados de novo.

`ResetDeFabrica` (`AIBWindows/Services/ResetDeFabrica.cs`) vive fora da tela porque apagar arquivo precisa de ensaio:

| | |
|---|---|
| Apaga | Tudo o que há dentro da raiz, recursivamente, inclusive arquivos só-leitura (o perfil do Edge tem). |
| Fica | A pasta raiz, vazia. Sem ela, `EnsureDirectories` migraria de volta o que houver em `%AppData%\AIB` (o local antigo). |
| Atalho de pasta | Junção ou link sai sozinho, sem descer: o destino fica fora da raiz e não é do AIB. |
| Recusa | `RaizSegura`: a raiz de dados é configurável (`DataDirectory`), então recusa a raiz de uma unidade e qualquer pasta que contenha o perfil do usuário ou o próprio programa. |
| Falha | Vira linha `[RESET]` no console e entra na contagem, que a mensagem final mostra. **Nunca lança e nunca aborta o resto.** |

Ensaios em `AIB.Tests/ResetDeFabricaTests.cs`, sempre em raiz temporária.

## Configurações: `SettingsService` e `UserAppSettings`

`AIBWindows/Services/SettingsService.cs` guarda as duas classes.

### Arquivo e cache

- O arquivo é `~/.AIB/profile.dat`: JSON indentado, cifrado com DPAPI no escopo do usuário (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`).
- Arquivo ausente: nasce com os padrões e é gravado. Arquivo que não decifra: tenta ler como JSON em texto claro (legado) e regrava cifrado. Nada disso lança.
- O caminho é resolvido **a cada operação** (`SettingsService.ResolvePath`). Um `static readonly` antigo congelava o caminho e impedia `ApplyFromSettings` de realocar o arquivo.
- `LoadSettings` serve de um cache em memória e devolve **sempre uma cópia** (`UserAppSettings.Clone`). A cópia é profunda para `MailAccounts` e `Perfis`: com a cópia rasa, quem mexesse na lista recebida mexia no cache e nas outras janelas.
- O construtor `SettingsService(string? settingsPath)` fixa o caminho; é o que os testes usam.

### Perfis por provedor

Os campos `AiProvider`, `ApiUrl`, `ModelName`, `KeepAlive`, `ContextWindow` e `Reasoning` são o provedor **ativo** da conversa, e é o que o resto do programa lê. `Perfis` guarda um `PerfilDeProvedor` por provedor (`AIBWindows/Services/ProvedoresDeIa.cs`), para trocar e voltar sem perder modelo, janela e keep-alive.

| Membro | O que faz |
|---|---|
| `UserAppSettings.PerfilAtivo` | Monta o perfil do ativo a partir dos campos da conversa. |
| `UserAppSettings.PerfilDe(provedor)` | O perfil do ativo, o guardado, ou o de fábrica (`ProvedoresDeIa.PerfilPadrao`). |
| `UserAppSettings.Ativar(provedor, perfil)` | Guarda o perfil do ativo anterior e torna `provedor` o ativo. |
| `UserAppSettings.AplicarPerfis(escolhido, perfis)` | O "Salvar" da tela: **ativa primeiro**, depois grava os outros perfis. Na ordem inversa, `Ativar` guardava o perfil do anterior a partir dos campos antigos e passava por cima do que a tela acabara de gravar — mudar o modelo do Ollama e trocar para o OpenRouter no mesmo Salvar perdia o modelo novo. |
| `UserAppSettings.ParaTriagem` | Uma cópia com o provedor e o modelo **da triagem de e-mail** no lugar dos da conversa. A triagem tem provedor próprio (`MailTriageProvider`, `MailTriageModel`) para poder conversar pelo OpenRouter e manter a leitura dos e-mails no Ollama. |

`PerfilDeProvedor` não tem chave de API: o perfil vai para o arquivo de configurações, a chave vai para o cofre.

`PerfilDeProvedor.Sanear(provedor)`: janela entre 8.192 e 262.144 (padrão 32.768); raciocínio só entre as opções do provedor (`ProvedoresDeIa.OpcoesDeRaciocinio`), senão `off`; no OpenRouter a URL é fixa e não há keep-alive; no Ollama o keep-alive é `1m`, `5m`, `30m` ou `-1` (padrão `-1`, sempre carregado).

### `Sanear`: números dentro de faixas

`UserAppSettings.Sanear` roda ao ler do disco. Um valor fora da faixa vira o mais próximo válido — o usuário perde o exagero, não as configurações inteiras. Corrigir na entrada é mais barato que espalhar defesa por cada consumidor.

| Campo | Faixa | Padrão |
|---|---|---|
| `MaxTurnIterations` | 1–60 | 18 |
| `CompactionTrigger` | 0,50–0,99 | 0,85 |
| `MemoryFraction` | 0,05–0,60 | 0,25 |
| `TurnosSoltos` | 4–200 (≤ 0 volta ao padrão) | 20 |
| `TokensSoltos` | 8.000–1.000.000 (≤ 0 volta ao padrão) | 100.000 |
| `TurnosPorCapitulo` | 2–20 (≤ 0 volta ao padrão) | 15 |
| `TokensPorCapitulo` | 4.000–60.000 (≤ 0 volta ao padrão) | 20.000 |
| `CapitulosPorAto` | 0 = automático; senão 2–24 | 0 |
| `EsconderResultadosDepoisDe` | 0 = desligado; senão 2–50 | 0 |
| `ShadowMailPreviewCount` | 1–10 | 3 |
| `MailWindowDays` | 1–30 | 3 |
| `MailTimeoutSeconds` | 5–120 | 15 |
| `MailJournalDays` | 0–90 | 7 |
| `MensagensPorDia` | 1–20 | 6 |
| `NomeDoUsuario` | espaços repetidos juntados; até 40 caracteres | vazio |
| `SilencioInicio` / `SilencioFim` | "HH:mm"; ilegível volta ao padrão; iguais desligam o silêncio | 22:00 / 08:00 |

Cada padrão é uma `const` nomeada em `UserAppSettings` (`PadraoDeIteracoes`, `PadraoDoGatilhoDeCompactacao`…), lida pelo inicializador, pelo "Restaurar padrões" da tela e pelos testes. Número digitado em três lugares sai de sincronia na primeira mudança.

`Sanear` também normaliza provedores (`SanearProvedores`): nomes antigos (`OpenAI`, `Anthropic`, `LmStudio`) viram OpenRouter se a URL for do OpenRouter e Ollama no resto; perfis de provedor inexistente saem. Uma **migração única** por arquivo (`PerfisMigrados`) preserva o que de fato acontecia antes: o keep-alive `5m` nunca chegava ao Ollama e vira `-1`, e a triagem de e-mail herda o provedor e o modelo da conversa.

### `AplicarEmVigor`: estáticos que valem já

Alguns valores são lidos por código que não carrega configurações. `SettingsService.AplicarEmVigor` roda a cada `LoadSettings` do disco e a cada `SaveSettings`, e atualiza:

- `ChatRequestOptions.JanelaAtual` (janela do provedor ativo, base dos orçamentos por nível);
- `LimitesDoProvedor.Atual` (`Local` para Ollama, `Nuvem` para OpenRouter: linhas por leitura, itens por pasta, tetos do e-mail, alvo pós-compactação, capítulos por ato);
- `ChatRequestOptions.JanelaDoOllama` e `ChatRequestOptions.KeepAliveAtual`, tirados do perfil **do Ollama** qualquer que seja o provedor da conversa, porque a triagem pode continuar no Ollama;
- `FiltroDeSaida.Ligado`, de `FiltrarSaidaDeComandos` (padrão ligado; página Ferramentas, "Enxugar saída de comandos"). Desligado, a saída de comando volta ao corte cego antigo — existe para dar para comparar.

Além disso, `LoadSettings` e `SaveSettings` chamam `PastasSemConfirmacao.Configurar`, para a lista de pastas dispensadas valer no instante em que o usuário salva.

### Chaves que nascem desligadas

Várias opções nascem desligadas por decisão registrada no comentário de cada uma: `ShadowAssistantEnabled` e `ShadowHandlesMail` (ninguém ganha um programa lendo o próprio e-mail por ter atualizado; e querer o orbe não é querer que ele abra a caixa), `ModelThinking` (medido: em CPU, turnos inteiros terminavam só pensando), `ThinkingInHistory`, `MailTriageThinking`, `ExecutionLogging`, `CompactionLogging`, `EsconderResultadosDepoisDe`. `OpenRouterSemColetaDeDados` nasce **ligada**, porque o prompt leva arquivos e e-mails. `KeepAssistantSpeech` nasce ligada porque o contrário é defeito.

## Credenciais

### Chave do provedor: `CredentialService`

`AIBWindows/Services/CredentialService.cs`. Um arquivo por sistema em `~/.AIB/credentials/<sistema>.bin`, com um dicionário JSON cifrado por DPAPI (usuário atual).

- Hoje só o OpenRouter usa chave: `ProvedoresDeIa.SistemaDaChave` devolve `"openrouter"` para ele e `null` para o Ollama; o nome da chave é `ProvedoresDeIa.NomeDaChave` (`"ApiKey"`).
- `CredentialService.StoreCredentialAsync` grava; devolve texto começando com `ERRO` em falha, e quem chama confere.
- `CredentialService.LerDoSistema(sistema, chave)` lê **só o arquivo daquele sistema**. Uma busca global devolveria a chave de mesmo nome de outro serviço (por exemplo, uma chave da OpenAI gravada por versão antiga) e mandaria a requisição com a credencial errada.
- `ChatProviderFactory.ChaveDe(provedor)` é quem o app usa para obter a chave de um provedor; devolve vazio se não há chave, e a requisição sai sem autorização até o 401 explicar onde configurar. A **credencial efetiva** entra na chave do cache da fábrica: sem isso, trocar a chave no cofre continuava usando o cliente com a chave velha até reiniciar.
- A chave é gravada no cofre na hora em que o usuário a confirma (`SettingsWindow.GuardarChave_Click`, `FirstRunWindow`), sem esperar o "Salvar". O formato é checado por `ProvedoresDeIa.ChaveValida` (`sk-or-` e ao menos 20 caracteres), o mesmo critério nas duas telas.

### Senhas de e-mail: `MailVault`

`AIBWindows/Services/Mail/MailVault.cs`. Uma senha de app por caixa, em `~/.AIB/credentials/mail/<sha256 do endereço>.bin`, cifrada com DPAPI.

- O nome do arquivo é o hash do endereço em minúsculas, para a lista de endereços não ficar legível numa listagem de pasta.
- Fica dentro de `credentials/` para o reset de fábrica levá-la junto.
- `MailVault.Ler` devolve `null` quando não há senha ou o blob não decifra (outra máquina, outro usuário) — nunca uma string de erro, que passaria por qualquer checagem de preenchimento e iria ao servidor como senha.
- `MailVault.Remover` é chamado pelo mesmo comando que tira a conta da lista, para não deixar credencial órfã.
- `UserAppSettings.MailAccounts` guarda endereço, host, porta, SSL e conta principal — **sem senha**, e sem o status de conexão, que é de tempo de execução.
- O construtor aceita uma raiz alternativa para os testes.

### Regras invioláveis

- Chave ou senha **nunca** vai para `profile.dat`, log, console ou interface. `UserAppSettings` não tem campo `ApiKey` de propósito; um campo antigo desse nome é ignorado ao carregar e some no próximo `SaveSettings`.
- Na auditoria, só os quatro últimos caracteres: `key_last4` (eventos `firstrun_saved` e `chave_guardada`).
- O registro de execução não recebe senha nem chave porque elas não passam pelo console. `RegistroDeExecucao.Redigir` é a segunda linha de defesa: troca por `[REDIGIDO]` o resto da linha depois de `"api_key"`, `"apiKey"`, `"password"`, `"senha"` e `Authorization:`, e tira o corpo de e-mail lido pela conversa (`ConteudoDeTerceiros.Redigir`).

## Níveis: `LevelService`

`AIBWindows/Services/LevelService.cs`. O XP é `UserAppSettings.MessageCount`, somado um a cada turno que termina sem erro (`ChatWindow.RefreshLevelUI(true)`, no fim do envio). Há nove níveis:

| Nível | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
|---|---|---|---|---|---|---|---|---|---|
| XP mínimo | 0 | 20 | 50 | 100 | 200 | 350 | 600 | 1000 | 1500 |

`LevelService.GetXPForNextLevel` no nível 9 extrapola o último degrau (2000), para a barra de progresso não dividir por zero.

O que o nível libera:

| Nível | O que muda |
|---|---|
| 1 | `read`, `glob`, `grep`, `mail` e `mail_read` (esta só na conversa de um e-mail) — `RequiredLevel => 1`. |
| 2 | `write`, `edit`, `shell` e `skill` — `RequiredLevel => 2`. Todas pedem confirmação; `skill` só existe com alguma skill instalada. |
| 7 | A floor list de comandos destrutivos (`CommandFloorList.Match`) deixa de barrar `shell` e `skill`; o cartão de confirmação passa a ser a única autoridade. Abaixo de 7, com `ConfirmDangerousCommands` ligado (padrão), deleção recursiva, formatação, desligamento, registro destrutivo e `-EncodedCommand` são recusados antes do cartão. |

`ToolRegistry.GetActiveTools` só oferece ao modelo as ferramentas do nível, e `ToolRegistry.ExecuteToolAsync` recusa com `ACESSO NEGADO` a que estiver acima dele. Detalhes em [03-ferramentas-e-portao.md](03-ferramentas-e-portao.md).

O nível também dimensiona a memória: `LevelService.GetMaxTokensForLevel(nivel, janela)` vai de um quarto da janela (nível 1) a três quartos (nível 9), em oito passos iguais. É **derivado** da janela, e não uma tabela, porque a tabela antiga ficou para trás quando a janela subiu e o nível 9 passou a usar 37% do que o modelo aguentava. O teto de 75% deixa espaço para a geração. Mudar `num_ctx` no meio da sessão faz o Ollama recarregar o modelo e perder o cache — medido: prefill de 343 ms voltou a 204 s.

O comando `/unlock_level N` no chat ajusta `MessageCount` para o piso do nível `N` (1 a 9).

## Personagens

Cada personagem é uma pasta `character/<Nome>/` com:

- `SOUL.MD` — a persona, em texto. O marcador `{{usuario}}` é trocado pelo nome de usuário do Windows ao carregar.
- `info.json` — o cartão da tela de escolha, desserializado em `AgentProfile`: `Name`, `Description`, `Personality`, `Sample-speech` e `Stats` (`Assertiveness`, `Usefulness`, `Humanity`), e `Atributos` (`Iniciativa`, `Resiliencia`, `Constancia`, `Curiosidade`, de 1 a 5, e `Afeto`, de -5 a 5). As estrelas são só da tela de escolha; os atributos são os padrões de fábrica do personagem, copiados para o `memory/shadow/status.json` quando ele aparece (ver "Iniciativa" em `06-interface.md`).

Os de fábrica estão em `AIBWindows/character/` (`Ayano`, `Ellen`, `Kai`, `Sora`). O ativo é `UserAppSettings.ActiveCharacter` (padrão `Ayano`).

De onde são lidos:

- **Semeadura.** `DirectoryService.EnsureDirectories` copia para `~/.AIB/character` cada personagem da instalação que **ainda não existe lá**, item a item e sem sobrescrever. A versão antiga copiava a pasta inteira uma única vez, e um personagem novo nunca chegava a quem já tinha o app; sobrescrever atropelaria um `SOUL.MD` editado pelo usuário.
- **Alma.** `ConversationService.LoadActiveCharacterSoul` lê `~/.AIB/character/<Nome>/SOUL.MD`; se não existir, lê da pasta da instalação (`FailsafeCharactersDir`) e avisa no console. O nome passa por `Path.GetFileName`, para um valor corrompido não virar caminho arbitrário. Alma ilegível não derruba a conversa: segue sem persona.
- **Listas das telas.** `FirstRunWindow.LoadAgents` lista as pastas de `~/.AIB/character` que têm `info.json` válido. `SettingsWindow.LoadCharacters` lista as pastas de `~/.AIB/character` e garante `Ayano` na lista.
