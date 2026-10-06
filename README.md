# AIB

Assistente de IA para o Windows. Uma janela de conversa flutua sobre a área de trabalho, chamada
pelo atalho `Ctrl+Shift+Space` ou pelo ícone da bandeja. A IA lê, grava e edita arquivos, roda
comandos no PowerShell e habilidades instaladas, e lê caixas de e-mail por IMAP (só leitura).
Toda ação que muda o computador passa antes por um cartão de confirmação.

## Requisitos

- Windows 10 ou 11 (x64).
- SDK do .NET 8.
- Um provedor de modelo, escolhido no primeiro arranque:
  - [Ollama](https://ollama.com/) rodando localmente, com pelo menos um modelo baixado; ou
  - uma chave do [OpenRouter](https://openrouter.ai/) (nuvem, cobrado por uso). A chave fica no
    cofre DPAPI do Windows, nunca em arquivo de configuração.

## Compilar e rodar

```
dotnet build AIBWindows\AIB.csproj
dotnet run --project AIBWindows\AIB.csproj
```

Os dados do usuário ficam em `%USERPROFILE%\.AIB`.

## Testes

Os testes ficam em `AIB.Tests` e a avaliação com modelo em `AIB.Avaliacao`. Como rodar, inclusive
com o app aberto: [documentação/tecnica/08-testes-e-avaliacao.md](documenta%C3%A7%C3%A3o/tecnica/08-testes-e-avaliacao.md).

## Documentação

- [Índice](documenta%C3%A7%C3%A3o/README.md)
- [Guia de uso](documenta%C3%A7%C3%A3o/manual/guia-de-uso.md), para quem usa o AIB
- [Documentação técnica](documenta%C3%A7%C3%A3o/tecnica/), para quem mexe no código
- [Regras de Identidade](Regras%20de%20Identidade/): segurança, código limpo e visual
