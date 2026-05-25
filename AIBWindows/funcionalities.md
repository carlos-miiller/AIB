# ✦ AIB - Mapa de Funcionalidades e Arquitetura

Este documento detalha todas as funcionalidades nativas e comportamentos esperados do AIB (Autonomous Intelligence for Business), servindo como guia para restauração e validação do sistema.

---

## 1. Interface Premium (UI/UX)

### 1.1 Efeito Visual Acrylic/Blur
- **Descrição:** A janela deve possuir um fundo translúcido que borra o conteúdo atrás dela (estilo Windows 11 Acrylic).
- **Implementação:** Utiliza a API `DwmSetWindowAttribute` via P/Invoke para habilitar o `DWMWA_SYSTEMBACKDROP_TYPE` como `DWMSBT_TRANSIENTWINDOW` (Mica/Acrylic).
- **Comportamento:** O fundo deve ser levemente escurecido (`#EE121214`) para garantir legibilidade.

### 1.2 Gerenciamento de Visibilidade Inteligente
- **Auto-Hide:** A janela deve se esconder automaticamente (`Hide()`) quando o usuário clica fora dela (evento `Deactivated`).
- **Escape Key:** Pressionar `ESC` deve fechar/esconder a janela instantaneamente.
- **Transição Suave:** Uso de animações de opacidade (DoubleAnimation) para entrada e saída suave (Fade In/Out).
- **Foco Automático:** Sempre que a janela é mostrada, o cursor deve ser forçado para o `InputBox`.

### 1.3 Chat de Alta Fidelidade
- **Markdig Renderer:** Suporte completo a Markdown, incluindo:
    - Tabelas formatadas com bordas sutis.
    - Blocos de código com fundo escuro e fontes mono (Cascadia Code/Consolas).
    - Destaque de sintaxe (Inline code).
- **Bolhas Premium:** Estilos distintos para Usuário (Alinhado à direita, gradiente azul/roxo) e IA (Alinhado à esquerda, borda sutil).
- **Auto-Scroll:** A área de chat deve rolar automaticamente para o final conforme novas mensagens ou chunks de texto chegam.

---

## 2. Motor de Inteligência (Agente Autônomo SOTA)

### 2.1 Arquitetura ReAct (Reasoning + Acting)
- **Descrição:** O AIB segue o padrão das IAs mais modernas, onde cada ação é precedida por um pensamento explícito e seguida por uma observação.
- **Loop de Pensamento Crítico:** 
    - **Thought:** O agente planeja o próximo passo com base no objetivo final.
    - **Action:** Seleciona e executa a ferramenta/skill mais adequada.
    - **Observation:** Analisa o resultado (sucesso ou erro) e decide o próximo curso de ação.
- **Autocorreção (Self-Correction):** Se uma ferramenta falhar (ex: erro de permissão ou parâmetro incorreto), o agente deve analisar o erro e tentar uma abordagem alternativa automaticamente sem interromper o usuário.

### 2.2 Aprendizado Dinâmico de Skills (In-Context Learning)
- **Evolução Contínua:** O AIB não é estático. Ele pode aprender novas funcionalidades "on-the-fly".
- **Materialização Evolutiva:** Ao criar uma skill (`materialize_skill`), o agente deve considerar as melhores práticas de codificação atuais (ex: tratamento de erros robusto, logs verbosos, e modularização).
- **Auto-Documentação:** Toda skill materializada deve vir acompanhada de metadados que permitam ao agente "lembrar" como usá-la em conversas futuras através da memória persistente.

### 2.3 Orquestração Multi-Ferramenta (Parallel & Sequential Execution)
- **Chamadas em Cadeia:** Capacidade de resolver problemas complexos que exigem dados de múltiplas fontes (ex: buscar um lead no Bitrix, pesquisar o site da empresa no Google, e salvar um resumo na memória).
- **Refinamento de Contexto:** Uso de técnicas de "Prompt Compression" ou "Summary Buffer" para garantir que o histórico longo de execuções técnicas não degrade a qualidade do raciocínio (evitando o esquecimento do objetivo inicial).

### 2.3 Gerenciamento de Memória e Credenciais
- **Memory (RAG Local):** Sistema de "Remember" e "Recall" usando arquivos de texto locais para persistência de fatos sobre o usuário ou empresa.
- **Cofre de Credenciais (DPAPI):** Armazenamento criptografado de chaves (Bitrix, Telegram, OpenAI) em `%AppData%\AIB\credentials\`.
- **Busca Global Resiliente:** Se o agente solicitar uma chave de um sistema (ex: `bitrix`) mas ela estiver em outro (ex: `telegram`), o `CredentialService` deve fazer uma varredura em todos os arquivos `.bin` para encontrar a chave.

---

## 3. Central de Controle e Voz

### 3.1 Gerenciamento Dinâmico de Configurações
- **Settings Window:** Interface dedicada para alteração de parâmetros em tempo real sem necessidade de reiniciar o app.
- **Provedores de IA:** Alternância entre Ollama (Local), OpenAI e Google Gemini.
- **Configuração de Modelos:** Seleção de modelos específicos (ex: `gpt-4o`, `qwen2.5:7b`) com validação de conexão.
- **Gestão de Path:** Configuração de diretórios customizados para Memória e Skills.

### 3.2 AIB Live (Interface de Voz)
- **Whisper Integration:** Transcrição de áudio local usando `Whisper.net`.
- **Modo "Talk to Agent":** Botão de microfone com feedback visual (pulso de animação) para interação mãos-livres.
- **Ativação por Voz:** (Opcional) Detecção de silêncio para envio automático.

---

## 4. Integrações de Sistema

### 3.1 Bot do Telegram (Modo Híbrido)
- **Descrição:** O AIB deve responder tanto pela janela local quanto via Telegram.
- **Polling Ativo:** Uso de `StartReceiving` (v22+) para escutar mensagens em tempo real sem bloquear a interface WPF.
- **Streaming Remoto:** Envio de mensagens para o Telegram com o mesmo motor de IA da interface local.

### 3.2 Atalhos e Notificações (System Tray)
- **Hotkey Global:** `Ctrl + Shift + Space` para alternar a visibilidade da janela em qualquer lugar do Windows.
- **Taskbar Icon:** Ícone na bandeja do sistema com menu de contexto (Abrir, Sair) e notificações de balão para status de execução.

### 3.3 Bitrix24 Agentic Skill
- **Call Command:** Comando genérico no script `bitrix.ps1` que permite à IA chamar qualquer endpoint da REST API do Bitrix enviando parâmetros JSON.

---

## 4. Diagnóstico e Performance
- **Streaming de Baixa Latência:** Uso de `IAsyncEnumerable<string>` para renderização tipo "máquina de escrever".
- **Logs Técnicos:** Mensagens de depuração (`[DEBUG-IA]`, `[PERF]`) exibidas no terminal para monitoramento de TTFT (Time to First Token) e execução de ferramentas.
- **Sanitização de Saída:** Remoção de blocos de raciocínio interno (Thought/Reasoning) da visualização final do usuário para manter o chat limpo.
