# Regras de Segurança, Privacidade e Limites (AIB)

Como o AIB atua dentro de máquinas locais, muitas vezes contendo credenciais corporativas e poderes de Shell do Windows, ele possui a faca e o queijo para se tornar uma ameaça de cibersegurança caso as suas barreiras caiam. Este documento define as linhas vermelhas do projeto (Zero Trust).

## 1. Regra de Sandboxing do Terminal (Zero-Trust)
O assistente é capaz de rodar código real de infraestrutura no seu computador. Mas **A IA NÃO MANDA NA SUA MÁQUINA**.
- **Comandos Silenciosos são Proibidos:** O AIB **jamais** pode executar nativamente comandos Shell/PowerShell/CMD sem a interação humana física.
- **A Janela Modal:** Toda chamada para a tool `windows_console_execution` é obrigatoriamente roteada para parar a thread e abrir a `CommandConfirmationWindow`. A interface deve cuspir o script perigoso explícito para o usuário e forçar um Clique Físico Humano em "Aprovar" ou "Rejeitar".
- Qualquer bypass ou "jeitinho" pra pular essa tela modal será considerado violação crítica da arquitetura.

### 1.1 Floor list (piso de comandos destrutivos)
- **Roda ANTES do cartão**, no `ToolRegistry`, tanto no caminho com cartão quanto no dispensado por pasta. O que ela barra é negado direto com `ACESSO NEGADO (FLOOR): …`, sem perguntar: antes ela rodava depois, e o usuário clicava "Permitir" num comando que seria recusado de qualquer jeito.
- **Só para `shell` e `skill`** — as ferramentas com `ITool.PassaPelaFloorList = true`, cujo comando é linha de comando. No `write`/`edit` o comando é `CRIAR/SOBRESCREVER/EDITAR <caminho>`, e um caminho com "logoff" ou "format" no nome era recusado com motivo falso.
- **Exceção por nível:** abaixo do Nível 7 e com `ConfirmDangerousCommands` ligado ela barra; em Nível 7+ ou com a flag desligada, não barra — e o cartão continua perguntando.
- É **best-effort**: lê o texto do comando, não o conteúdo de um script. O cartão continua sendo o portão canônico.

### 1.2 Fail-closed no portão
- Toda negação do portão começa com `ACESSO NEGADO` e conta como **falha** (chip, memória, bloqueio de repetição).
- Sem contexto para descrever a operação (nulo ou exceção ao montá-lo) → **nega**, com cartão ou com dispensa de pasta.
- **O autorizado viaja até a execução** (`ITool.ExecutarAutorizadoAsync`). Uma skill de documentação (`markdown`) é dispensada do cartão porque só lê o manual; se entre a autorização e a execução o `SKILL.md` passar a ter script — ou o script aprovado trocar de arquivo —, a execução recusa ("a habilidade mudou") em vez de rodar. Sem autorização descrita, o script não roda.

## 2. Tratamento Criptográfico de Segredos (O Cofre)
Você quer que a Inteligência saiba todas as suas senhas de sites para logar você, mas você não pode jogar suas senhas num banco de dados texto.
- **Não Salvar em TXT/LiteDB:** É proibido armazenar credenciais, Tokens JWT ou senhas em texto plano no banco de dados vetorial ou em arquivos da pasta `%appdata%`.
- **A API DPAPI (CredentialService):** Segredos corporativos devem usar as rotinas específicas de Vault (ex: `manage_vault` Tool). Essa classe amarra-se ao *Data Protection API* do Windows nativamente. Os dados salvos no HD são criptografados de modo que **apenas o seu usuário físico, logado no seu computador, possua a chave mestre** para abri-los. Um hacker não conseguiria ler suas senhas nem copiando o arquivo de banco de dados por um Pendrive.
- **Chave da OpenAI (canônico):** A chave da OpenAI é armazenada exclusivamente no cofre DPAPI em `~/.AIB/credentials/openai.bin`, nunca em `.env` nem em texto plano no `profile.dat`. O valor persistido em `settings.ApiKey` é o sentinela literal `"use-vault"`, que instrui o `OpenAIService` a consultar `CredentialService.RetrieveCredential("openai", "ApiKey")` em cada construção do `ChatClient`. A entrada inicial acontece via `FirstRunWindow`, aberta automaticamente no primeiro atalho global (`Ctrl+Shift+Space`) após a instalação ou após uma rotação de chave (detector `App.NeedsFirstRun`). O caminho de re-key fora desse evento é o botão "Alterar chave" na `SettingsWindow`, que reabre `FirstRunWindow` como diálogo sem encerrar o aplicativo no Cancel.

## 3. O Escudo de Gamificação (Level como Firewall)
Modelos de Nível não servem só para limitar Tokens da OpenAI, mas funcionam como *Rights Management* (Controle de Acesso).
- O desenvolvedor de uma `ITool` nova, principalmente se tiver impactos perigosos no sistema de arquivos local, **deve estipular um `RequiredLevel` alto**.
- O `ToolRegistry` se encarrega de amputar cirurgicamente a descrição dessa ferramenta do Payload JSON que é mandado pra IA, caso o nível do usuário seja baixo (Recém-chegado). Isso cria uma barreira *Air-Gapped*: Se o sistema sofre uma invasão nas primeiras horas (Prompt Injection no usuário de Call Center novato), a IA invasora sequer sabe que tem poderes para apagar pastas do Windows, não podendo gerar danos maciços.

## 4. Privacidade de Dados Sensíveis e Cloud (RAG Local)
O AIB lê arquivos confidenciais do usuário (PDFs da empresa). Esses fatos longos não podem ir para modelos de Nuvem desnecessariamente.
- A vetorização matemática (Criação de Embeddings) **sempre rodará de forma 100% offline** (Atualmente usando CPU com o ONNX BGE-Micro-V2 provido pelo `SmartComponents.LocalEmbeddings`). 
- Isso significa que dados que o AIB decidiu jogar na "memória eterna" nunca escoam pela conexão com a Internet.
- **O Payload da OpenAI:** A única coisa que efetivamente vai para os servidores da OpenAI (Nuves) é o pedaço de texto estrito da *Janela de Contexto de Curto Prazo* da conversa ativa atual. Dados passados do LiteDB só entram no contexto se forem perfeitamente fisgados e relevantes.
