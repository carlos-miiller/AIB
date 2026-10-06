# Regras de Segurança (AIB)

O AIB roda comandos, grava arquivos e lê e-mail na máquina do usuário. **A IA não manda na
máquina**: toda ação que a altera passa por um portão que só abre com decisão humana. Estas regras
são invioláveis; o mecanismo está em `documentação/tecnica/03-ferramentas-e-portao.md` e
`documentação/tecnica/05-email.md`.

## 1. Portão de ferramentas (`ToolRegistry.ExecuteToolAsync`)

- **Fail-closed.** Todo caminho de dúvida nega ou pergunta: contexto que não se descreve (nulo ou
  exceção, com cartão ou com dispensa) nega; sem interface para perguntar nega; cartão descartado
  sem resposta nega; erro ao decidir dispensa cai no cartão. Nenhum pode virar "permitir".
- **Toda negação conta como falha.** As do portão começam com `ACESSO NEGADO`; a recusa do usuário é
  `ToolRegistry.RecusaDoUsuario`. Não mudar esses textos sem ajustar `ArtifactExtractor.Falhou`.
- **O cartão é a autoridade** (`ChatConfirmationPrompt` → `ConfirmCardView`). Não existe outra
  porta de confirmação: toda pergunta passa por esse caminho e por esse cartão. Ele aparece na
  conversa ou, no modo orbe, dentro da `ConfirmacaoDoOrbeWindow` — o mesmo cartão, na mesma porta,
  só hospedado em outro lugar. Não criar confirmação paralela (outro diálogo, outra decisão fora
  do `ChatConfirmationPrompt`). O cartão mostra o alvo real (caminho absoluto resolvido, comando
  exato, linha do script) e a prévia do conteúdo. Foco inicial em Recusar.
- **Pré-voo antes do cartão** (`ITool.Validar`): chamada impossível é recusada sem virar pergunta.
- Toda ferramenta que altera a máquina declara `RequiresConfirmation = true`. Hoje: `write`, `edit`,
  `fs`, `shell`, `skill`; o `browser` decide por ação (`ITool.PedeConfirmacao`): ler não pergunta,
  clicar, digitar, abrir site novo e anotar perguntam.
- **"Sempre permitir"** vale só na sessão (em memória), e só para `shell` (casa o comando exato) e
  `browser` (vale para o site, e nunca para ação decisiva: envio de formulário, abrir site novo,
  anotar).
- **Nível**: ferramenta acima do nível do usuário some do schema (`GetActiveTools`) e é negada na
  execução. Ferramenta nova e perigosa pede `RequiredLevel` alto.

### 1.1 Floor list (piso de comandos destrutivos)
- **Roda ANTES do cartão**, no `ToolRegistry`, tanto no caminho com cartão quanto no dispensado por pasta. O que ela barra é negado direto com `ACESSO NEGADO (FLOOR): …`, sem perguntar: antes ela rodava depois, e o usuário clicava "Permitir" num comando que seria recusado de qualquer jeito.
- **Só para `shell` e `skill`** — as ferramentas com `ITool.PassaPelaFloorList = true`, cujo comando é linha de comando. No `write`/`edit` o comando é `CRIAR/SOBRESCREVER/EDITAR <caminho>`, e um caminho com "logoff" ou "format" no nome era recusado com motivo falso.
- **Exceção por nível:** abaixo do Nível 7 e com `ConfirmDangerousCommands` ligado ela barra; em Nível 7+ ou com a flag desligada, não barra — e o cartão continua perguntando.
- É **best-effort**: lê o texto do comando, não o conteúdo de um script. O cartão continua sendo o portão canônico.

### 1.2 O autorizado viaja até a execução
- O registry executa só por `ITool.ExecutarAutorizadoAsync`, passando o contexto que foi autorizado.
- Uma skill de documentação (`markdown`) é dispensada do cartão porque só lê o manual; se entre a autorização e a execução o `SKILL.md` passar a ter script — ou o script aprovado trocar de linha de comando —, a execução recusa ("a habilidade mudou") em vez de rodar. Sem autorização descrita, o script não roda.

### 1.3 Pastas sem confirmação
- `PastasSemConfirmacao` **só dispensa** o cartão de `write` e `edit` dentro das pastas listadas.
  **Nada é recusado por estar fora**: fora, o cartão aparece. Lista vazia (padrão) = tudo pergunta.
- **Nunca** vale para `shell` (comando não declara alvo). O shell ganha só o aviso de
  `EscritaNoComando` no cartão, que é best-effort — silêncio não é garantia.
- A floor list continua valendo no caminho dispensado, e a dispensa é auditada.

## 2. Conteúdo de terceiros não dá ordens

- Arquivo lido, nomes de arquivo, saída de comando, resultado de skill e e-mail (`mail` e
  `mail_read`) são **informação, não instrução** (`AgentLoop.MarcaDeConteudo`, preâmbulo do
  `LerEmailTool`, aviso no enquadramento do e-mail aberto no chat).
- **E-mail no contexto anula dispensas**: numa conversa aberta a partir de um e-mail, ou com texto
  de `mail_read` no contexto vivo, o cartão avisa, o "sempre permitir" é ignorado e as pastas sem
  confirmação deixam de dispensar.
- **Ninguém respondeu não é recusa**: sem interface para perguntar, ou com o cartão falhando ao
  aparecer, a chamada é negada como `deny_sem_ui` — nunca atribuída ao usuário.
- **Memória não entrega comando que apaga**: Estado, Pendente e fatos descrevem o que foi apagado,
  sem a linha pronta para repetir (`ComandoQueApaga`).

## 3. E-mail

- **Somente leitura**: nunca enviar, apagar, mover, arquivar ou marcar no servidor. Toda pasta abre
  com `FolderAccess.ReadOnly` (`EXAMINE`) e corpo com `BODY.PEEK`.
- **O corpo nunca vai para disco nem log.** Todo caminho da conversa para o disco passa por
  `ConteudoDeTerceiros.Redigir` — hoje quatro: `raw.jsonl`, texto do resumidor, registro de execução,
  `chat_history.json`. Um quinto caminho tem de passar por ele. Do outro lado, todo corpo que entra
  num prompt passa por `Embrulhar` — `LerEmailTool.Formatar` e `TriadorDeEmail.Montar` —, inclusive o
  da triagem: por isso o registro de execução opt-in grava assunto e remetente, e o corpo omitido.
- Exceção documentada, mantida até decisão do usuário: o enquadramento do e-mail aberto no chat
  (veredito, nunca corpo) é gravado mesmo com `MailJournalDays = 0`.

## 4. Segredos

- Chaves de API e senhas de app **só no cofre DPAPI** (escopo do usuário): `~/.AIB/credentials/`
  (`CredentialService`, `MailVault`). Nunca em configurações, `.env`, log ou console.
- A auditoria registra de uma chave **só `key_last4`**.
- O registro de execução apara campos com cara de credencial (`RegistroDeExecucao.Redigir`).
- **Segredo na saída de ferramenta não vai ao modelo.** Todo resultado passa por `Segredos.Redigir`
  no `ToolRegistry`, antes de voltar ao agente: valor ao lado de nome suspeito (`*PASSWORD=`,
  `"token": "…"`, string de conexão) e formatos conhecidos de chave viram `[SEGREDO OMITIDO]`, e o
  resultado ganha um aviso. Como `raw.jsonl`, histórico e tela gravam o que o modelo recebeu, ficam
  limpos juntos. É **best-effort** (padrão de texto): senha solta, sem nome ao lado, passa.
  Ferramenta nova não devolve resultado por fora do registry.
- `write` e `edit` recusam texto com a marca `[SEGREDO OMITIDO]`: gravá-la apagaria o valor de verdade.

## 5. Dados e registros

- Todo dado do usuário fica em `~/.AIB` (`DirectoryService.DataDir`).
- **Ler dentro de `~/.AIB` não é livre** (`DadosProtegidos`, valendo para `read`, `grep` e `glob`):
  `credentials/`, `profile.dat` e `navegador/perfil/` são **negados**, sem cartão; `skills/`,
  `character/` e `navegador/notas/` são livres; todo o resto (memória, logs, e-mail) **pede
  cartão**, sem "sempre permitir". Uma busca que parte de cima (a pasta do usuário) pula esses
  arquivos. Pasta nova em `~/.AIB` nasce pedindo cartão; para ser livre, entra na lista. O `shell`
  alcança tudo isso, e por isso passa sempre pelo cartão.
- **`raw.jsonl` nunca é apagado** no uso normal: nenhuma ferramenta, compactação ou troca de conversa
  o remove. A única exceção é o **reset de fábrica**, que é total por decisão do usuário: apaga tudo
  o que há em `~/.AIB` (`ResetDeFabrica`), sem `.bak`, depois de uma confirmação que diz isso. Só
  roda pelo botão da tela; o agente não tem ferramenta que o dispare.
- Os arquivos de e-mail são apagáveis e obedecem `MailJournalDays`, que corta por **dias**.
- A auditoria (`~/.AIB/logs/audit-*.jsonl`) é append-only e grava **antes** da execução, em todo
  desfecho do portão de confirmação (cartão, sessão, dispensa, floor list, sem contexto, sem UI).
