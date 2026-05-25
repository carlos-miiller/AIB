# AIB Project Context (GRAVITY.MD)

## Visão Geral
O **AIB (AIBWindows)** é um assistente de IA autônomo e de desktop para Windows, desenvolvido em **.NET 8 (C# WPF)**. O objetivo principal do projeto é prover um agente inteligente em um formato "flutuante" e nativo, integrando fortemente o controle e leitura do ambiente de trabalho do usuário. 

A interface atual é profundamente inspirada no "Google Gemini Mobile", trazendo uma cápsula escura minimalista, efeitos de *Glassmorphism* (fundo translúcido), e interações fluidas estilo chat (com balões de conversa simétricos animados e entrada de texto arredondada com botões rápidos).

A aplicação foi rigorosamente limpa e focada. Integrações legadas com Telegram ou interfaces secundárias foram inteiramente removidas.

---

## 1. Arquitetura de Interface e UX (`ChatWindow.xaml` / `.xaml.cs`)

### 1.1 Estilo Visual
- **Janela Flutuante**: O app roda sem bordas clássicas do Windows (`WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`).
- **Sombreamento / Glassmorphism**: Utiliza `DropShadowEffect` para separar a UI do plano de fundo.
- **Cápsula de Input**: Um `Border` de entrada estilizado com gradiente translúcido (borda externa com `CornerRadius="22"`) e borda interna preta sólida (`CornerRadius="19"`).  
- **Balões de Mensagem**: 
  - **Usuário**: Alinhado à direita com borda roxa/azul translúcida.
  - **Agente (LLM)**: Alinhado à esquerda.
  - Ambos possuem limites rígidos e iguais de largura (`MaxWidth="420"`, `Margin="4,3,4,3"`) e suporte completo a formatação via **Markdown** usando a biblioteca genérica `Markdig.Wpf` no `MarkdownViewer`.

### 1.2 Interações e Animações
- **Envio de Mensagem Silencioso**: A transmissão de texto (Streaming) da OpenAI não pipoca letra a letra na tela. Em vez disso, a interface apresenta uma animação estática de três pontos "● ● ●" piscando, e revela o texto Markdown da resposta inteira de uma só vez quando o loop termina, prevenindo bugs visuais de markdown quebrado.
- **Animações de Entrada**: Toda mensagem que entra no chat utiliza um `DoubleAnimation` disparado pelo motor em `ChatWindow.xaml.cs`, fazendo a bolha de texto escorregar sutilmente de baixo para cima (20px `TranslateTransform`) enquanto aumenta sua opacidade de `0` para `1`.
- **Smart Clipboard (Colar Arquivos)**: O `InputBox` intercepta `CTRL+V` (`InputBox_PreviewKeyDown`). Se o usuário copiar arquivos no Windows Explorer e colar no chat, o sistema extrai os caminhos absolutos transparentemente e os insere envoltos em aspas duplas, resolvendo fricção de caminhos com espaços.

---

## 2. Arquitetura Base (Backend do Agente)

### 2.1 ReAct Loop & OpenAIService (`OpenAIService.cs`)
- O motor utiliza a API da OpenAI.
- O Agente implementa um "loop ReAct" padrão (Raciocínio + Ação). O serviço avalia a intenção, processa ferramentas se necessário, e continua repetindo até não ter mais chamadas de ferramentas a fazer. O streaming técnico e o rastreamento das ferramentas funcionam nos bastidores.

### 2.2 Sistema de Progressão (Gamificação e Tokens)
- O sistema foi alterado para um modelo de "Level" baseado no uso do usuário. O Nível 1 fornece 3072 tokens de contexto base.
- Conforme as interações acumulam, o Nível sobe e libera exponencialmente mais tokens de contexto limitados (+2048 do nível 2 ao 3; +5120 do nível 4 ao 6; +10240 a partir do nível 7).
- **Feedback Visual da UI**: O contador na interface colore-se de Verde (bom), Amarelo (atenção), Laranja (alerta) e Rosa/Vermelho (crítico) conforme o consumo se aproxima do teto. Acima de 5120, a interface condensa os números usando o sufixo numérico "k" (ex: `6.5k / 12k tokens`).
- **Comando de Debug**: Existe um atalho `/unlock_level #` para pular níveis manualmente (ocultado propositalmente no popup de auto-completar).

### 2.3 RAG (Retrieval-Augmented Generation) com Embeddings Locais
- **Vetorização Offline**: Integração com `SmartComponents.LocalEmbeddings` (modelo `bge-micro-v2` ONNX). Transforma textos em vetores localmente consumindo meros ~80MB de RAM.
- **Armazenamento e Similaridade**: Os dados de longo prazo estão salvos no `LiteDB` (`memory.db`). A ferramenta `recall` vasculha a base rodando *Cosine Similarity* para recuperar as informações mais contextualmente relevantes sem custo extra de chamadas de API.

### 2.4 Ferramentas e Capabilities (Tool Registry)
- **ToolRegistry**: Todas as "Skills" implementam a interface `ITool`. Se a LLM falhar no schema JSON oficial, uma Regex detecta a intenção no meio da string bruta para forçar a execução (Fallback Mode).
- **Integrações Nativas de I/O**:
  - **Visão e Microfone**: Botões na UI injetam captura de tela via OCR e áudio via Whisper de forma silenciosa e responsiva no input.
  - **Ferramenta Universal `read_file`**: Ferramenta nativa do C# projetada para extrair dados sem envolver o terminal de comandos Windows. Consegue ler arquivos texto nativamente, documentos **.pdf** complexos via `UglyToad.PdfPig`, além de documentos **.docx** ou **.xlsx** extraindo texto e estruturas tabulares via `DocumentFormat.OpenXml` — operando de forma perfeitamente limpa sem necessitar do Pacote Office instalado.

---

## 3. Resolvendo "Gotchas" e Ambiguidade Técnica
Qualquer IA editando a base de código a partir deste ponto precisa ter ciência destas decisões consolidadas:
- **Ambiguidade de Namespaces**: Foram criados aliases nas declarações das classes para o compilador não confundir `System.Drawing` e `System.Windows.Media`. Mais importante, classes como `System.Windows.Clipboard` ou `System.Windows.DragEventArgs` exigem o namespace inteiro fully-qualified no code-behind de `ChatWindow.xaml.cs` para evitar conflitos diretos com o legado WinForms (`System.Windows.Forms`).
- **Caminho de Propriedades Animadas (Storyboard)**: No WPF para .NET 8, não passe o alvo como `"UIElement.Opacity"` em `PropertyPath`, pois causa exceção em tempo de execução. A sintaxe de texto solta `"Opacity"` é obrigatória.
- **LetterSpacing CSS**: Tal formatação não se aplica bem para `TextBlock` base em WPF. Controle os espaços físicos visualmente.
- **Compilação Contínua em C#**: Sempre destrave o executável para build executando `Stop-Process -Name AIB -Force` via PowerShell (ou equivalente). A janela tendencialmente segura locks nos assemblies compilados mesmo em plano de fundo ou na System Tray.

---

## Resumo de Estado
A Arquitetura inteira (Ferramentas, Agente React, Banco Vetorial Local, e UI) atinge agora um momento de plena maturidade e solidez tecnológica. O App não requer grandes reconstruções em infraestrutura. 
Desenvolvimentos a partir de agora devem focar primordialmente na experiência: polir as capacidades semânticas das **Skills**, balancear o consumo inteligente de tokens com prompts menores, ou injetar minúcias de Qualidade de Vida (QoL) para o usuário.
