# 6. Side-Panel de Contexto Visível (Estilo Windows 11)

## O Conceito
Inspirado na aba lateral do Menu Iniciar do Windows 11 (usada para conexão com o celular), o AIB ganharia um **Painel Expansível** acoplado à sua cápsula principal. O objetivo é tirar o "estado de memória" da caixa preta da IA e dar controle visual ao usuário.

## A Ação (O Dashboard Lateral em 3 Seções)
Quando a gaveta desliza para fora da interface principal, ela não mostrará apenas arquivos, mas funcionará como um **Dashboard de Controle** dividido em 3 seções expansíveis (estilo *Accordion*):

### Seção 1: Lembretes Ativos (Reminders)
- **Funcionalidade:** O usuário diz "Me lembre de ligar para o cliente X daqui a 2 horas".
- **Visualização:** A seção lista os lembretes pendentes ou alertas programados para o dia.
- **Ação:** O AIB pode interagir nativamente com a API de Notificações do Windows (Toast Notifications) para disparar o alarme na tela do usuário no momento exato. O usuário pode cancelar ou adiar os lembretes clicando neles no painel.

### Seção 2: Memória Recente (Long-term DB)
- **Funcionalidade:** Uma listagem das últimas 5 inserções que o AIB fez no banco vetorial de longo prazo (`LiteDB`).
- **Visualização:** O usuário consegue ler de forma rápida que o AIB arquivou o fato de que "O ramal da TI mudou para 9999" hoje de manhã.
- **Ação:** Dá transparência ao usuário de que a IA está aprendendo coisas novas, permitindo que ele clique num ícone de "lixeira" se a IA tiver memorizado algo indesejado ou errado, limpando a tabela do banco na hora.

### Seção 3: Arquivos em Contexto (Short-term Working Memory)
- **Funcionalidade:** Exibe ícones visuais (Miniaturas de PDF, Excel, Word) de todos os arquivos que a IA está "segurando" no momento para o ReAct loop.
- **Controles (Remover):** O usuário clica num "X" no arquivo para expurgá-lo da memória imediata, economizando a barra de Nível de Tokens.
- **Controles (Adicionar):** O painel aceita a ação de Arrastar e Soltar (Drag & Drop) do Windows Explorer.
- **Ação Rápida (Abrir):** Se o usuário não lembrar do que se trata aquele arquivo que o AIB está usando, ele dá um clique simples no ícone. O AIB roda `Process.Start(caminho)` e o Windows abre o arquivo nativamente no Word/Acrobat para o humano ler.

## Benefício Corporativo
O vendedor do Call Center tem certeza absoluta sobre *quais* tabelas de preços a IA está lendo naquele momento, evitando misturar regras antigas com tabelas novas, e ganhando um controle tátil do que o assistente sabe ou não sabe.
