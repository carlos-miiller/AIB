# Regras de Identidade Visual (AIB)

Este documento dita as leis imutáveis de Design e Experiência de Usuário (UX) do projeto. **Toda e qualquer modificação futura na interface (WPF/XAML) deve obrigatoriamente respeitar estas diretrizes**, para garantir que o AIB nunca perca sua identidade premium.

## 1. O Conceito Fundamental (Aesthetics)
O AIB é um assistente de classe *Premium*. A interface não é uma "janela de programa" tradicional do Windows. 
- Ele deve parecer um "Sussurro Digital" ou um "Holograma Tátil".
- A inspiração primordial é o paradigma de *Bottom-Sheet* móvel (ex: Google Gemini Mobile) traduzido para Desktop.
- A interface é caracterizada por fundos altamente escuros e translúcidos (*Glassmorphism* mascarado), cantos perfeitamente arredondados e a completa ausência de bordas ríspidas (hard-edges).

## 2. A Janela Principal (Window Anatomy)
- **Formato Físico**: A janela não possui barra de títulos, não possui botões de minimizar/fechar tradicionais do OS (`WindowStyle="None"`, `AllowsTransparency="True"`).
- **Ancoragem Espacial**: O app jamais deve brotar no meio da tela atrapalhando a visão. O código-fonte calcula e ancora a janela exatos **45 pixels acima da bandeja do sistema (Taskbar)**, perfeitamente centralizada no eixo horizontal da tela principal.
- **O Modo Stealth (Ghosting)**: A janela é invasiva porém extremamente educada. O evento de desfoque (`Deactivated`) garante que a IA desapareça instantaneamente se o usuário clicar no seu navegador ou em outro aplicativo. Ela só existe quando chamada.

## 3. Paleta de Cores e Sombreado
- **Fundo Sólido Translúcido**: Para evitar falhas nativas de Blur no Windows, o "vidro fumê" não usa filtros blur instáveis. Usa-se uma cor física sólida quase preta com 90% de opacidade (`#E5121216` ou `WColor.FromArgb(230, 18, 18, 22)`).
- **Gradiente Neon**: A única iluminação vívida da aplicação fica confinada a um discreto *Stroke* (Borda) em degradê. Essa borda mescla variações de Roxo (`#9B51E0`), Azul Ciano (`#3182CE`), Magenta (`#D53F8C`) e Laranja (`#E28743`). 
- **Sombras**: O `DropShadowEffect` é obrigatório nos cantos externos da janela. Sem a sombra, a janela parece colada no fundo. A sombra garante o efeito 3D de "Flutuação sobre as outras janelas do PC".

## 4. O Sistema de Balões (Bubbles)
O chat não é uma tela de texto plano (como o Discord ou Slack), ele é pautado em *Bolhas de Mensagens* contidas:
- **Tamanho e Padding**: O `MaxWidth` dos balões está travado em cerca de `420px`. As margens internas (Padding) devem garantir que o texto respire sem sufocar nas bordas.
- **Bordas Arredondadas**: O raio padrão da aplicação (Corner Radius) gira entre `19` e `22`. Pontas quadradas são estritamente proibidas.
- **Cores por Papel (Role)**:
  - **Usuário**: Alinhamento obrigatório à Direita. Cor de fundo puxada para um roxo/azul translúcido que reflete as intenções orgânicas de comando.
  - **AIB (IA)**: Alinhamento obrigatório à Esquerda. Cor de fundo neutra e opaca, emitindo a resposta do sistema.

## 5. Leis de Animação e Renderização
- **Proibido Letra a Letra**: É expressamente proibido fazer a interface renderizar o fluxo de streaming da OpenAI quebrando as letras uma a uma. Isso quebra o compilador de Markdown no WPF e cria um *flickering* horrível.
- **Loading Progressivo**: A UI deve obrigatoriamente mostrar o clássico balão com os três pontos intermitentes (`● ● ●` gerados via Keyframes). O balão de carregamento existe para tranquilizar o usuário de que a IA não travou.
- **Movimento Sutil (Fade-Up)**: Nenhuma interface "brota" de forma dura na tela. Todo `UIElement` adicionado ao ChatWindow sofre uma Injeção C# de `DoubleAnimation`. A opacidade vai de 0 a 1 em 400ms, enquanto o eixo Y sofre uma tradução (*TranslateTransform*) de 20 pixels para baixo até 0. Isso faz a mensagem "deslizar e acender".

## 6. A Cápsula de Entrada (Input Box)
A cápsula inferior onde o usuário digita é o coração tátil do aplicativo.
- O campo de texto deve ser simétrico e não possuir rolagem vertical invasiva que quebre a simetria da pílula.
- Ícones de ações físicas (Microfone, Lente OCR, Enviar) devem habitar o espaço direito, usando a cor base secundária que não chame mais atenção do que o texto sendo digitado.
- Botões de ações do sistema não devem poluir a área da cápsula. Se houver comandos novos, eles habitam menus secundários ou atalhos (Popups).

---
*Assinatura Sistêmica:* Se você (Programador ou IA) está projetando um botão, painel ou tela nova para o AIB, assegure-se de perguntar: *"Essa nova UI parece mágica, suave e discreta?"*. Se a resposta for não, refaça o código XAML.
