# 08 — Testes e avaliação

O AIB tem duas formas de verificação, com papéis diferentes:

- **`AIB.Tests`** — a suíte xUnit. Roda sem modelo, sem rede e sem tocar os dados do usuário. É a rede de proteção de toda mudança.
- **`AIB.Avaliacao`** — um console que manda casos ao modelo de verdade e confere o que ele pediu. Mede o prompt; não é teste de regressão, leva minutos e pode custar dinheiro.

## `AIB.Tests`

`AIB.Tests/AIB.Tests.csproj`: `net8.0-windows10.0.19041.0`, WPF ligado, xUnit 2.5, FluentAssertions 8, Moq e coverlet. Referencia `AIBWindows/AIB.csproj` e testa as classes diretamente.

### Convenções

**Testes nunca escrevem no `~/.AIB` real.** Um teste que grava ali contamina o que o usuário tem de mais importante — foi o que aconteceu com a auditoria: o log de produção ganhou 47 entradas fabricadas por teste, como `Remove-Item -Recurse C:\dados`, e deixou de valer como evidência. Por isso toda classe que grava em disco tem uma porta para outra raiz, e os testes usam pastas temporárias (`Path.GetTempPath()` + GUID):

| Onde | Porta |
|---|---|
| Auditoria | `AuditLogService.LogDirectoryOverride` |
| Histórico de conversas | `ChatHistoryService.HistoryDirectoryOverride` |
| Skills | `SkillService.SkillsDirectoryOverride` |
| Configurações | `new SettingsService(caminho)` |
| Memória da conversa | último argumento do construtor de `ConversationService` (`memoryRootOverride`) |
| Sessão e fatos | `new SessionMemory(id, raiz)`, `new FactStore(raiz)` |
| E-mail | parâmetro `raizDeDados` de `MailVault`, `DiarioDeTriagem`, `ArquivoDeConversas`, `ConversasIgnoradas`, `EstadoDasCaixas`, `MarcoDoVigia`, `VigiasDoEmail`, `RegrasDoVigia.CaminhoPadrao` |
| Chave do provedor | parâmetro `chaveDe` de `ChatProviderFactory` (não toca o cofre) |
| Registro de execução | parâmetro `pastaDeLogs` de `RegistroDeExecucao.Iniciar` |

Os dois primeiros são desviados **antes de qualquer teste rodar**, por `[ModuleInitializer]` em `AIB.Tests/TestAuditRedirect.cs`. Não é fixture porque o xUnit roda classes em paralelo e um desvio por classe deixaria janelas em que outra classe ainda escreve no caminho real. O histórico entra junto porque fechar uma `ChatWindow` de teste arquiva a conversa.

Ao escrever uma classe nova que grava em disco, dê a ela a mesma porta — e use-a no teste.

**Janela de teste pronta.** `AIB.Tests/JanelaDeEnsaio.cs` monta uma `ChatWindow` descartável: `SettingsService` em pasta temporária, `ToolRegistry` sem portão (recusa tudo que pede confirmação), provider mudo e memória em pasta temporária. Antes cada classe copiava sessenta linhas de montagem, e cada cópia era um lugar por onde a memória podia escapar para o `~/.AIB`.

**WPF numa thread STA só: `WpfHost.EmSta`.** O WPF exige STA e o xUnit roda em MTA. `AIB.Tests/WpfHost.cs` cria **uma** thread STA para o processo inteiro, dona do `Application` e do `Dispatcher`, e `WpfHost.EmSta(acao)` roda a ação nela, propagando a exceção como falha do teste. Uma thread por teste falhava de forma intermitente: só pode existir um `Application` por AppDomain, e objeto WPF criado numa thread não pode ser tocado em outra. O `Application` nasce com `ShutdownMode.OnExplicitShutdown`, como o de produção — senão o WPF encerrava ao fechar a última janela e os testes seguintes eram abortados. `WpfHost.GarantirRecursos` carrega `Themes/Controls.xaml`, sem o qual nenhum `StaticResource` resolve.

**Coleções para estado estático.** O xUnit roda classes em paralelo; estado estático compartilhado só convive com isso numa coleção declarada. Uma coleção com nome que ninguém definiu não isola nada — o xUnit cria uma ad hoc, em silêncio.

| Coleção | Definida em | Quem entra |
|---|---|---|
| `ContextoGlobal` (`DisableParallelization = true`) | `ContextServiceTests.cs` | Toda classe que escreve (inclusive limpa) em `ContextService` ou `ActionLogService`, e as que montam janelas: `ConversationServiceTests`, `WindowSmokeTests`, `ModalGuardTests`, entre outras. |
| `Escrita` | `ColecaoDeEscrita.cs` | Quem mexe em `PastasSemConfirmacao.Configurar` (a dispensa é estática; ligada por uma classe, faria outra executar sem perguntar). |
| `Historico` | `ColecaoDeHistorico.cs` | Quem grava no histórico de conversas: o arquivo temporário é um só para a suíte e o ler-modificar-gravar perdia entradas. |
| `Skills` | `ColecaoDeSkills.cs` | Quem mexe em `SkillService.SkillsDirectoryOverride` ou depende das skills vistas pelo registry (o registro da ferramenta `skill` depende da contagem). |

**Um teste para cada correção.** Os testes do projeto carregam no comentário o caso real que motivou a correção. Siga o padrão: o teste reproduz o defeito, falha antes da correção e passa depois.

**Guarda dos temas.** `ThemeResourcesTests` lê todos os `.xaml` de `AIBWindows/` como texto e cobra que toda chave de `{StaticResource …}` esteja definida em algum `x:Key`. Uma chave errada compila e só explode quando a tela (ou o template) é criado. O teste acha a raiz do repositório **subindo a partir da pasta do binário** até encontrar `AIBWindows/` — por isso a pasta de saída do build precisa estar dentro do repositório (ver abaixo).

### Opt-in: integração e imagens

- **Integração com o Ollama real.** `MemoriaIntegracaoTests` tem `[Trait("Categoria", "Integracao")]` e só roda com `AIB_INTEGRACAO=1`. Modelo e URL vêm de `AIB_MODELO` e `AIB_URL` (padrões `qwen3.5:9b` e `http://localhost:11434`). Sem a variável, os testes passam em branco e imprimem `PULADO` — o xUnit 2.5 não tem skip dinâmico. Escreve só em pasta temporária.

  ```powershell
  $env:AIB_INTEGRACAO = "1"
  dotnet test AIB.Tests --filter Categoria=Integracao -l "console;verbosity=detailed"
  ```

- **Imagens das telas.** Com `AIB_UI_PNG=1`, `WindowSmokeTests` salva um PNG de cada tela montada, em `AIB_UI_PNG_DIR` (padrão: a pasta temporária). Sem a variável, as telas são medidas e arranjadas, mas nada é gravado.

### Como rodar

Da raiz do repositório:

```powershell
dotnet test AIB.Tests
```

Um grupo só: `dotnet test AIB.Tests --filter "FullyQualifiedName~ToolRegistryTests"`.

**Com o AIB aberto.** O `AIB.exe` em execução trava os arquivos da pasta de saída do app, e o build do projeto de testes falha ao tentar sobrescrevê-los. Em vez de fechar o app, mande a saída para outra pasta **dentro do repositório** e desligue o self-contained:

```powershell
dotnet test AIB.Tests "-p:OutDir=AIB.Tests\bin\san\" -p:SelfContained=false
```

A pasta precisa ficar dentro do repositório: com `OutDir` fora dele, `ThemeResourcesTests` não encontra `AIBWindows/` subindo a partir do binário e falha.

## `AIB.Avaliacao`

`AIB.Avaliacao/Program.cs`. Mede o prompt contra o modelo configurado. Para cada caso, monta o **primeiro envio de uma conversa nova** pelo mesmo caminho do app (`ConversationService.MontarPrimeiroEnvio`), manda ao provider por streaming e confere a resposta **por código**. Nenhuma ferramenta é executada: a chamada pedida pelo modelo só é anotada, então "crie um arquivo" não cria nada.

Existe porque mudança de prompt sem medida já custou caro: uma reestruturação do prompt de sistema que parecia melhor fez 27/33 contra 33/33 do prompt atual, ficou 60% mais lenta e foi desfeita (o registro está no comentário de `SYSTEM_PROMPT` em `ConversationService`). A regra ali é: antes de mexer no prompt, meça.

### O que lê e o que grava

- Lê as configurações reais (provedor, modelo, persona), a alma, as skills e o diário de triagem do usuário. Nunca chama `SaveSettings`: as opções de linha de comando mudam só a cópia em memória.
- A memória da conversa vai para uma pasta temporária, apagada no fim.
- Monta seus próprios `ToolRegistry` (sem portão), `AgentLoop` e `ChatProviderFactory`.
- O relatório é um Markdown `<rótulo>-<AAAAMMDD-HHmm>.md`. Sem pasta de saída, vai para `resultados/` ao lado do binário; os relatórios guardados no repositório estão em `AIB.Avaliacao/resultados/`, então passe essa pasta explicitamente.

### Como rodar

```powershell
dotnet run --project AIB.Avaliacao -- <rótulo> AIB.Avaliacao\resultados [opções]
```

| Opção | Efeito |
|---|---|
| `--casos a,b` | Só esses casos. |
| `--repeticoes N` | Cada caso N vezes. Uma amostra é ruído: o mesmo prompt já passou e falhou no mesmo caso. |
| `--modelo X` | Outro modelo, só nesta execução. |
| `--pensar sim\|nao\|modelo` | Raciocínio ligado, desligado ou a critério do modelo. Sem a opção, segue `ModelThinking`. |
| `--num-ctx N` | Outra janela, só nesta execução. |
| `--sem-recado-de-falha` | Casos `falha-*` sem o recado que o laço acrescenta ao erro (`AgentLoop.RecadoDeFalha`) — a linha de base. |
| `--sem-pendencias` | Caso `continuar` sem a seção de pendências na memória. |
| `--afeto N` | O desvio do afeto (agora menos o de fábrica), para medir a linha "Convivência" do prompt: 0.75, 1.5, -0.75, -1.5. Sem a opção, zero: a linha não entra. |
| `--conversa <pasta>` | Acrescenta os casos `mem-*`, sobre a memória gravada daquela sessão (`chapters.jsonl` e `acts.jsonl`, lidos de uma cópia). Para antes de chamar o modelo se a memória renderizar vazia. |
| `--mostrar-memoria` | Com `--conversa`: imprime o bloco de memória e sai, sem chamar o modelo. |

Os casos cobrem saudação, identidade da persona, data, conta, cada ferramenta (`write`, `read`, `glob`, `grep`, `skill`, `mail`, `shell`, `edit`), honestidade quando não há ferramenta (`sem-envio`, `sem-lembrete`), a segunda volta depois de uma falha ou recusa (`falha-edit`, `falha-recusa`), retomar depois da compactação (`continuar`) e obediência a ordem escondida num arquivo lido (`injecao-arquivo`). Alguns casos apontam para caminhos da máquina do dono (por exemplo, a pasta do repositório e uma planilha em Downloads) — como nada é executado, basta o modelo pedir a chamada certa.

Antes dos casos há um aquecimento fora da conta, que carrega o modelo e o prefixo e mede o custo frio do prompt. Cada caso tem teto de 12 minutos. O relatório traz casos aprovados, tokens do prompt, prefill, geração, caracteres de raciocínio e tempo de cada caso, e as respostas.

### Cuidado com custo

A avaliação usa o provedor que estiver configurado no app. Se for o OpenRouter com um modelo pago, **cada caso é uma requisição cobrada**, e o total é casos × repetições + o aquecimento. A avaliação não mostra o custo; faça a conta antes:

- estime o prompt (o relatório de uma rodada anterior traz os tokens do primeiro envio, ou use `--mostrar-memoria` nos casos `mem-*`) e multiplique pelo número de requisições e pelo preço do modelo;
- comece com `--casos` e poucas repetições, e só amplie se o sinal justificar;
- prefira `--pensar nao`: raciocínio gera tokens de saída pagos e, no Ollama em CPU, custa minutos por caso;
- use `--modelo` para medir um modelo sem trocá-lo no app.

No Ollama local o custo é tempo e memória da máquina, não dinheiro.
