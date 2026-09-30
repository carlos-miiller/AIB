# Regras de Identidade Visual (AIB)

Leis de aparência do AIB. Valem para qualquer mudança em XAML ou em elemento montado por código.
Fonte dos valores: `AIBWindows/Themes/Cores.Escuro.xaml` (cores do tema escuro, o original),
`Cores.Claro.xaml` (o par claro, mesmas chaves), `Tokens.xaml` (raios, fontes, medidas) e
`Controls.xaml` (estilos), que convertem o §1 das specs em `refactor-interface/` (`tela-chat-v3.html`, `tela-configuracoes.html`,
`shadow-assistant.html`). Detalhe de cada janela: `documentação/tecnica/06-interface.md`.

## 0. Mudar a interface não muda o padrão
- Mudanças na interface não mudam o padrão visual. Tela, botão ou painel novo nasce no estilo que
  já existe: mesmas superfícies, raios, tipografia e estilos de `Controls.xaml`.
- O que for adicionado é conciso. Nada de enfeite, cor nova ou texto que não informe.
- Cor, raio, sombra e tamanho saem dos tokens. Hex literal em View é proibido: em XAML use
  `{StaticResource Chave}`; em código, `FindResource` ou `Ui/PincelDoTema.De(chave, hex)`, com o hex
  de reserva igual ao do token (o do escuro). Token que falta é criado nos arquivos de tema — cor
  nos DOIS `Cores.*.xaml`, o resto em `Tokens.xaml` —, não escrito na tela. Hex na tela, além de sair de sincronia, não muda com o tema.

## 1. Janela
- Sem barra de título nem botões do sistema: `WindowStyle="None"`, `AllowsTransparency="True"`.
- Fundo é o "vidro" `GlassBrush`: cor sólida quase preta a 90% no tema escuro, quase branca no claro. Blur nativo
  (Gaussian/Acrylic/DWM) é proibido: dá falha de renderização e custa quadro.
- Moldura com `NeonBrush` (roxo, azul, magenta, laranja). É a única cor saturada da janela.
- `WindowShadow` na moldura externa é obrigatória: sem ela a janela não flutua sobre o desktop.
- A conversa aparece centralizada na tela principal, logo acima da barra de tarefas, e some ao
  perder o foco para outro programa. Modal do AIB usa `ModalGuard` para não disparar isso.

## 2. Conversa
- Bolhas com raio 18 uniforme (`RadiusBubble`). Nada de canto em "rabinho" nem canto reto.
- Usuário: à direita, degradê `PrimaryBrush`, sem sombra e sem brilho.
- IA: à esquerda, `SurfaceCardBrush` com borda `BorderCardBrush` e `CardShadow` numa camada irmã
  do texto (efeito sobre o texto desliga o ClearType).
- Largura máxima da bolha: 74% da lista. A hora fica fora da bolha e aparece só no hover.
- Ações da IA colapsam em ícones num único chip (`ToolChainView`); falha fica visível em vermelho.
- Ação destrutiva pede o cartão de confirmação antes de executar, com "Recusar" em foco e
  "Permitir" em `DangerFilledButton`.

## 3. Movimento e renderização
- O balão da IA só é desenhado com a fala completa. Texto brotando letra a letra é proibido: quebra
  o Markdown e faz o balão tremer.
- Enquanto a IA pensa, três pontos lilases pulsam numa bolha da IA. Espera longa vai para a faixa
  de sistema com anel girando, tempo decorrido e saída.
- Nada surge seco: bolhas entram com fade e deslize curto (20 px); giros usam 0,9 s linear.
- O orbe avisa pulsando e nunca abre balão sozinho.

## 4. Cápsula de entrada
- Moldura `InputFrameBrush`, fundo `SurfaceInputBarBrush`.
- O botão primário é ENVIAR (aviãozinho). Microfone e leitura de tela são botões separados à
  esquerda dele, em `TextSecondaryBrush`, sem competir com o texto digitado.
- Comandos novos não ganham botão na cápsula: vão para o popup de `/`, menus ou configurações.

## 5. Tipografia
- Segoe UI, o padrão do WPF; não declarar fonte de UI.
- Mono (`MonoFontFamily`) só para o que se confere caractere por caractere: código, caminho, comando,
  endereço, chave.
- Tamanhos pelos tokens `FontSize*`. Rótulo de campo sempre visível; placeholder não substitui rótulo.

---
Pergunta de conferência antes de entregar uma tela: ela usa só tokens e estilos existentes, e parece
parte da mesma peça que o resto do AIB? Se não, refaça.
