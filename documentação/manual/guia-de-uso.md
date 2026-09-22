# Guia de uso do AIB

## O que é o AIB

O AIB é um assistente de IA que roda no Windows. Você conversa com ele numa janela que aparece
sobre a área de trabalho, e ele pode trabalhar no seu computador: ler e gravar arquivos, procurar
coisas, rodar comandos e ler a sua caixa de e-mail. Tudo o que muda alguma coisa no seu computador
passa antes por você, num cartão de confirmação.

A "inteligência" vem de um de dois lugares, à sua escolha:

- **Ollama (local):** o modelo roda no seu próprio computador. Nada sai da máquina e não há custo
  por mensagem. Você precisa ter o Ollama instalado, aberto e com pelo menos um modelo baixado.
- **OpenRouter (nuvem):** o modelo roda em servidores na internet. Precisa de uma conta e de uma
  chave do OpenRouter, cada mensagem é cobrada e o conteúdo da conversa sai do seu computador.

## Primeiro arranque

Na primeira vez que você abre a conversa, aparece um assistente de configuração:

1. **Boas-vindas**, com o aviso de que o AIB está em fase Beta e pode agir no seu computador.
2. **Escolha sua Inteligência:** escolha o personagem que vai conversar com você.
3. **Motor de Inteligência:** escolha **Ollama (Local)** ou **OpenRouter (Nuvem)**.
   - Com Ollama, escolha um dos modelos instalados. Se o Ollama não for encontrado, clique em
     "Tentar novamente" depois de abri-lo, ou use o modelo padrão sugerido.
   - Com OpenRouter, cole a sua chave (ela começa com `sk-or-`) e escolha um modelo. A lista só
     mostra modelos que sabem usar ferramentas; sem isso a IA não conseguiria ler nem gravar nada.
4. e 5. Duas telas explicando os níveis. Clique em **Concluir**.

A chave do OpenRouter é guardada no cofre do Windows, cifrada para a sua conta de usuário. Ela não
fica no arquivo de configurações e nunca é mostrada de volta na tela.

Se você clicar em **Sair** nesse assistente, o AIB fecha. Ele volta a aparecer da próxima vez,
e também sempre que faltar o provedor ou a chave do OpenRouter.

## Abrir e fechar

O AIB fica no **ícone da bandeja**, perto do relógio do Windows.

- **Ctrl+Shift+Espaço** abre e fecha a conversa, de qualquer programa.
- **Clique no ícone da bandeja** faz o mesmo.
- **Botão direito no ícone** mostra o menu: "Abrir Chat", "Shadow no desktop", "Ler os e-mails
  agora" e "Sair".

A janela aparece centralizada, logo acima da barra de tarefas. Ela **some sozinha** quando você
clica em outro programa, e a conversa continua do mesmo jeito quando você a chama de novo. Também
dá para esconder com **Esc** ou no X do canto. Fechar a janela não encerra o AIB; para encerrar, use
**Sair** no menu da bandeja.

Se a IA terminar de responder com a janela escondida, aparece uma notificação do Windows com o
começo da resposta.

## Conversar

Digite na caixa de baixo e aperte **Enter**. Para pular linha, use **Shift+Enter** ou
**Ctrl+Enter**. Se você copiar arquivos no Explorer e colar com **Ctrl+V** na caixa, o AIB cola os
caminhos deles.

Enquanto a IA pensa, aparecem três pontinhos. **A resposta aparece de uma vez, inteira**, quando
fica pronta; ela não é escrita letra por letra. Durante a resposta o AIB toca sons curtos, como
uma "voz" do personagem. Para interromper, clique no botão de enviar, que vira **parar** durante a
resposta.

Logo depois que o AIB inicia, o modelo é carregado. Enquanto isso a caixa de texto fica bloqueada
e a faixa acima dela avisa; na primeira vez pode levar alguns minutos.

No alto da janela ficam: o nome do personagem, o seu **nível**, a troca entre **Chat** e
**E-mail**, e os botões do painel lateral, das configurações, de **limpar a conversa** e de fechar.
Limpar começa uma conversa nova; a anterior é guardada no histórico.

No rodapé fica o contador de memória: quantos tokens (pedaços de texto) a conversa ocupa, o limite
do seu nível e, no OpenRouter, quanto a conversa já custou.

Os botões de câmera (ler a tela) e de microfone (falar) já aparecem na caixa de texto, mas ainda
não funcionam nesta versão.

### Comandos de barra

Digite `/` na caixa para ver a lista.

| Comando | O que faz |
|---|---|
| `/compact` | Resume agora a parte antiga da conversa, em vez de esperar o momento automático. |
| `/memoria` | Mostra como a memória da conversa está organizada e quanto ela está economizando. |
| `/skills` | Lista as habilidades que a IA sabe usar. |

## O que a IA pode fazer no seu computador

| Ação | Pede confirmação? |
|---|---|
| Ler arquivos de texto e listar o conteúdo de pastas | Não |
| Procurar arquivos por nome e buscar texto dentro deles | Não |
| Consultar e ler os seus e-mails (se você conectou uma caixa) | Não |
| Criar ou sobrescrever um arquivo | Sim |
| Editar um trecho de um arquivo | Sim |
| Rodar um comando no PowerShell | Sim |
| Rodar uma habilidade (skill) | Sim |

**Habilidades** são pequenos programas instalados na pasta `skills` dos seus dados (veja "Onde
ficam os seus dados"). Cada uma tem uma descrição, e a IA escolhe quando usá-la. Habilidades que são
só um manual de instruções, sem nada para executar, são lidas sem perguntar.

As ações aparecem na conversa como uma fileira de ícones embaixo da fala da IA. Passe o mouse sobre
um ícone para ver o que foi feito. Ação que falhou ou que você recusou fica em vermelho. O painel
lateral guarda o histórico completo de ações da conversa.

## O cartão de confirmação

Quando a IA quer mudar alguma coisa, a conversa para e aparece um cartão com:

- a pergunta ("Criar este arquivo?", "Sobrescrever este arquivo?", "Editar este arquivo?",
  "Executar este comando?", "Executar esta habilidade?");
- o que vai acontecer de verdade, e o aviso de que o AIB não desfaz;
- o caminho do arquivo ou o comando exato;
- uma prévia do conteúdo que será gravado ou do trecho que será trocado;
- avisos, quando houver: por que um comando foi bloqueado, se há texto de e-mail na conversa (o
  pedido pode ter vindo de alguém que escreveu o e-mail), ou se o comando mexe fora das pastas que
  você liberou.

Você escolhe:

- **Permitir:** a ação acontece.
- **Recusar:** a ação não acontece, e a IA fica sabendo que você recusou. É o botão que já vem
  selecionado; apertar Enter sem ler recusa.
- **Sempre permitir este comando:** aparece só para comandos. Marque antes de clicar em Permitir, e
  aquele comando exato, letra por letra, não pergunta mais até você fechar o AIB. Se houver texto de
  e-mail na conversa, o cartão pergunta de novo mesmo assim.

A lista do que você marcou como "sempre permitir" aparece em **Configurações › Ferramentas ›
Autorizações desta sessão**, com o botão "Esquecer todas".

Se a conversa for limpa ou o AIB fechar com um cartão aberto, a resposta vale como **recusa**.

### Pastas sem confirmação

Em **Configurações › Ferramentas › Pastas sem confirmação** você lista pastas (uma por linha, com
o caminho completo). Criar, gravar e editar arquivos **dentro** delas acontece direto, sem cartão.
Fora delas o cartão aparece como sempre. A lista vem vazia: tudo pergunta.

Isso **não** vale para comandos: comando sempre pergunta. E, se houver texto de e-mail na conversa,
as pastas deixam de dispensar o cartão.

## Níveis

O seu nível sobe conforme você conversa. Cada resposta completa da IA conta um ponto de
experiência. Passe o mouse sobre o nível, no alto da janela, para ver quanto falta.

| Nível | Mensagens | O que muda |
|---|---|---|
| 1 | 0 | Ler, procurar, buscar e consultar e-mails. |
| 2 | 20 | Libera gravar, editar, rodar comandos e rodar habilidades (sempre com cartão). |
| 3 a 6 | 50, 100, 200, 350 | Mais espaço de memória para a conversa. |
| 7 | 600 | Comandos perigosos deixam de ser bloqueados antes do cartão (continuam pedindo confirmação). |
| 8 e 9 | 1000, 1500 | Mais espaço de memória. O nível 9 é o último. |

"Comandos perigosos" são apagar pastas inteiras, formatar ou particionar disco, desligar ou
reiniciar o computador e apagar coisas do registro do Windows. Até o nível 6, com **Confirmar
comandos perigosos** ligado (o padrão), esses comandos são recusados antes mesmo de virarem
pergunta.

Em nenhum nível a IA age sozinha: o cartão de confirmação aparece em todos.

## Memória da conversa

Uma conversa longa não cabe inteira no modelo. O AIB resolve isso resumindo a parte antiga:

- Quando a conversa passa de um certo tamanho, os turnos mais antigos viram um **capítulo**: um
  resumo com o que foi pedido, os arquivos envolvidos e o que ficou decidido.
- Quando os capítulos se acumulam, vários deles se juntam num **ato**, um resumo de resumos.
- A parte recente continua inteira, palavra por palavra.

O registro completo de cada conversa continua gravado no seu computador; o resumo muda só o que é
enviado ao modelo.

Resumir pode levar alguns minutos com modelo local. Enquanto isso, a faixa acima da caixa de texto
mostra "Compactando a memória", o tempo que já passou e o botão **Interromper**.

- `/compact` resume agora.
- `/memoria` mostra os capítulos e atos e quanto está sendo poupado.
- O rodapé mostra o tamanho atual; quando há resumo, o tamanho que a conversa teria sem ele aparece
  riscado ao lado. Passe o mouse para ver a conta.

O comportamento da memória se ajusta em **Configurações › Memória**.

## Painel lateral

O botão do painel abre, ao lado da conversa, três abas:

- **Histórico de chats:** as conversas anteriores. Um clique traz o conteúdo de uma conversa
  antiga para dentro da atual. Com o botão direito você também pode **Abrir conversa** (troca a
  atual pela antiga, que continua de onde parou) ou **Excluir** (sem desfazer).
- **Arquivos no contexto:** os arquivos que a IA leu ou criou nesta conversa, e os que você
  acrescentar em "+ Adicionar".
- **Histórico de ações:** tudo o que a IA fez, com detalhes ao passar o mouse.

## E-mail

O AIB **só lê** os seus e-mails. Ele não envia, não apaga, não move e não marca nada como lido.

### Conectar uma caixa

Em **Configurações › E-mail › Adicionar conta**, informe o endereço e uma **senha de app**. Não
use a senha normal da sua conta: gere uma senha de app no site do seu provedor (Gmail, Outlook,
Yahoo, iCloud e outros oferecem isso). O AIB descobre sozinho o servidor dos provedores mais
comuns. A senha de app fica no cofre do Windows, cifrada para a sua conta de usuário.

Você pode ter várias caixas; uma delas é a **principal**. Em cada conta dá para trocar a senha,
zerar a leitura (a próxima passada relê a janela de dias) ou remover.

### Triagem

Para o AIB acompanhar a caixa sozinho, ligue em **Configurações › Shadow** as opções **Mostrar o
Shadow no desktop** e **Deixar o Shadow tratar os e-mails**. A partir daí:

- a cada 20 minutos ele confere se chegou algo, sem usar o modelo;
- três vezes por dia (8h25, 12h55 e 16h55) o modelo lê o que chegou, resume e classifica por
  urgência. Se o computador estava desligado no horário, isso acontece quando ele ligar.

"Ler os e-mails agora", no menu da bandeja, faz essa leitura na hora.

### Na janela de conversa

Troque para **E-mail** no alto da janela. Aparece a caixa de entrada, com os mais urgentes em
cima. Clique num e-mail para ver o resumo e os botões:

- **Abrir com o personagem:** traz o e-mail para uma conversa. A IA recebe o remetente, o assunto,
  a data, a urgência e o resumo. Se precisar do texto original, ela o busca no servidor. Voltar ao
  mesmo e-mail depois retoma a mesma conversa.
- **Abrir no Gmail / Outlook / cliente:** abre a mensagem no navegador.
- **Ignorar:** tira a conversa da lista do AIB e do orbe até chegar mensagem nova nela. Nada é
  apagado.
- **Descartar a conversa:** apaga do histórico a conversa que você teve com a IA sobre aquele
  e-mail. O e-mail não é tocado.

Na leitura de um e-mail, **Recarregar** relê a mensagem no servidor e refaz o resumo.

## O orbe (Shadow)

Com **Shadow no desktop** ligado, uma pequena bola fica sobre a área de trabalho, acima da barra de
tarefas. Ela some quando a conversa está aberta e volta quando a conversa sai.

- **Clique no orbe** para ele virar uma barra de texto. Escreva e aperte Enter: o pedido vai para a
  conversa, que trabalha escondida, e a resposta aparece acima da barra. **Esc** ou clicar fora
  volta à bola.
- Quando o AIB tem algo a dizer (por exemplo, o resumo dos e-mails), o orbe **pulsa**, em
  vermelho se houver algo urgente. Ele não abre nada sozinho: clique quando quiser ver.
- Durante a leitura de e-mails, aparece um ícone de caixa de entrada no lugar da estrela.

## Personagens

O AIB vem com os personagens Ayano, Kai, Ren e Sora. Cada um tem a própria personalidade. Troque em
**Configurações › Identidade**. Trocar de personagem começa uma conversa nova (a anterior vai para o
histórico).

A personalidade de cada um está no arquivo `SOUL.MD`, na pasta `character` dos seus dados. Você pode
editar esse arquivo; atualizações do AIB não sobrescrevem personagens que já existem ali.

## Configurações

Abra pelo botão de engrenagem no alto da conversa. O botão **Salvar** só acende quando algo mudou,
e grava as mudanças de todas as páginas de uma vez. Cada página tem "Restaurar padrões desta
página".

| Página | O que você ajusta |
|---|---|
| Identidade | O personagem. |
| Conexão LLM | Ollama ou OpenRouter. Cada um guarda a própria configuração, então trocar e voltar não perde nada. Ollama: endereço, modelo e quanto tempo o modelo fica carregado. OpenRouter: chave (botão "Alterar"), modelo, "Só provedores que não guardam dados" (ligado por padrão) e provedor preferido. Nos dois: tamanho da janela de contexto e raciocínio do modelo. |
| E-mail | Contas, quantos dias para trás ler, tempo limite, quantos dias guardar o que a triagem leu, e qual provedor e modelo fazem a triagem. |
| Shadow | Mostrar o orbe, deixar o Shadow tratar os e-mails e quantos e-mails aparecem na fala do orbe. |
| Ferramentas | Ligar ou desligar as ferramentas (desligadas, a IA só conversa), confirmar comandos perigosos, máximo de etapas por resposta, pastas sem confirmação e autorizações desta sessão. |
| Memória | Como e quando a conversa é resumida, e onde ela fica gravada. |
| Avançado | Se o raciocínio do modelo volta para ele nas etapas seguintes. |
| Logs | Registros para diagnóstico e "Simular o primeiro envio". |

**Reset de Fábrica**, no rodapé, apaga as configurações, o histórico de conversas, tudo o que está
no cofre (chave do OpenRouter e senhas de e-mail), a memória das conversas e os arquivos de e-mail,
e fecha o AIB. Não há desfazer. Na próxima abertura, o primeiro arranque aparece de novo.

Duas coisas **não** são apagadas: a transcrição crua de cada conversa e o seu `facts.md`. As duas
são renomeadas, com a data e a hora no nome e a terminação `.bak`, e continuam na pasta `memory`.
O AIB deixa de lê-las; você continua podendo abri-las.

## Onde ficam os seus dados

Tudo fica na pasta `.AIB` dentro da sua pasta de usuário (`C:\Users\<você>\.AIB`):

| Pasta ou arquivo | O que guarda |
|---|---|
| `profile.dat` | As configurações, cifradas para a sua conta do Windows. |
| `credentials` | O cofre: a chave do OpenRouter e, em `credentials\mail`, as senhas de app. Tudo cifrado. |
| `chat_history.json` | A lista de conversas do histórico. |
| `memory` | O registro completo de cada conversa, os capítulos e os atos. |
| `email` | O que a triagem apurou (remetente, assunto, resumo), o progresso de leitura e os e-mails ignorados. |
| `logs` | O registro de auditoria das ações e, se você ligar, os registros de diagnóstico. |
| `skills` | As habilidades instaladas. |
| `character` | Os personagens. |

O que **nunca** é gravado:

- **O texto dos seus e-mails.** Quando a IA lê um e-mail, o texto fica só na conversa aberta; nos
  arquivos fica apenas a marca de que ele foi omitido. Duas exceções que você controla: ao abrir um
  e-mail com a IA, o remetente, o assunto, a data, a urgência e o resumo entram na conversa e são
  gravados com ela; e o **registro de execução** (Configurações › Logs, desligado por padrão)
  grava tudo o que passa pelo console, inclusive conteúdo de e-mails. Confira antes de
  compartilhar esses arquivos.
- **A chave do OpenRouter e as senhas de e-mail fora do cofre.** O registro de auditoria guarda no
  máximo os quatro últimos caracteres da chave.

## Custo no OpenRouter

Com o OpenRouter, cada mensagem é cobrada pela conta que você tem lá. O AIB mostra o gasto:

- no **rodapé** da conversa, o valor em dólares ao lado dos tokens;
- no **`/memoria`** e na dica do contador (passe o mouse), o gasto da conversa;
- em **Configurações › Conexão LLM**, o preço de cada modelo, vindo do catálogo do OpenRouter.

A triagem de e-mail também usa o modelo, três vezes por dia, e pode ter provedor e modelo próprios
(Configurações › E-mail). Com Ollama, nada é cobrado.
