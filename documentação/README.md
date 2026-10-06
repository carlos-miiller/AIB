# Documentação do AIB

Tudo aqui descreve o código **como ele está**. Quando o código e um documento discordarem, vale o
código — e o documento está errado e precisa ser corrigido no mesmo commit que mudou o
comportamento.

## Para quem usa

- [Guia de uso](manual/guia-de-uso.md) — primeiro arranque, conversa, o cartão de confirmação,
  memória, e-mail, configurações e onde ficam os seus dados.

## Para quem mexe no código

Leia na ordem, pelo menos o 01, antes de mudar qualquer coisa.

| # | Documento | Do que trata |
|---|---|---|
| 01 | [Arquitetura](tecnica/01-arquitetura.md) | Projetos, composição no `App.xaml.cs`, mapa das pastas, o caminho de uma mensagem |
| 02 | [Turno e provedores](tecnica/02-turno-e-provedores.md) | `ConversationService`, `AgentLoop`, Ollama e OpenRouter, janela, keep-alive, cache |
| 03 | [Ferramentas e portão](tecnica/03-ferramentas-e-portao.md) | As ferramentas, o cartão de confirmação, floor list, pastas sem confirmação, auditoria |
| 04 | [Memória](tecnica/04-memoria.md) | Capítulos, atos, cota, Estado, Pendente, `/compact` |
| 05 | [E-mail](tecnica/05-email.md) | Contas, vigia, triagem, conversas, e a regra do corpo que nunca vai para o disco |
| 06 | [Interface](tecnica/06-interface.md) | Janelas, cartão, orbe, tema e tokens |
| 07 | [Configurações e dados](tecnica/07-configuracoes-e-dados.md) | `~/.AIB`, `SettingsService`, perfis, cofre, níveis, personagens |
| 08 | [Testes e avaliação](tecnica/08-testes-e-avaliacao.md) | `AIB.Tests`, como rodar com o app aberto, `AIB.Avaliacao` |

## Regras que não se negociam

Em [`Regras de Identidade/`](../Regras%20de%20Identidade/):

- [SEGURANCA.MD](../Regras%20de%20Identidade/SEGURANCA.MD) — portão fail-closed, chaves só no
  cofre, e-mail só leitura e corpo nunca em disco.
- [CODIGO_LIMPO.MD](../Regras%20de%20Identidade/CODIGO_LIMPO.MD) — como o código é organizado e
  escrito.
- [VISUAL.MD](../Regras%20de%20Identidade/VISUAL.MD) — identidade visual e o uso dos tokens.

## Fora do escopo

[`ideias_futuras/`](../ideias_futuras/) guarda ideias, não comportamento. Nada ali existe no
código até um documento desta pasta dizer o contrário.
