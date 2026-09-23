# Protótipo do navegador — v1.0

Sem IA e sem custo. Serve para responder, numa página de verdade (o Bitrix, por exemplo),
qual forma de leitura a IA deveria receber: a árvore inteira, a **vista** (só o que está na
tela) ou o OCR da tela — e quanto cada uma custaria em tokens.

## Rodar

```
cd prototipos/ProtoNavegador
dotnet run
```

Abre o Edge com um perfil próprio em `~/.AIB/navegador/perfil`. Na primeira vez, logue no site
pela janela: o login fica guardado nesse perfil, separado do seu Edge. `--perfil <pasta>` usa
outro perfil.

## Roteiro de teste no Bitrix

```
abrir <endereço do bitrix>/company/personal/user/<seu id>/tasks/
medir               ← árvore × vista × OCR × ocultos, em tokens
vista               ← é isto que a IA "veria"
achar Fernando      ← pesquisa na página guardada, sem ler tudo
tabela              ← lista as tabelas; 'tabela 1' mostra a primeira
clicar s1e40        ← age por ref; ref de leitura antiga é recusada
rolar / voltar / ler
```

Se navegar pela janela com o mouse, use `ler` para o protótipo ler a página nova.

## O que observar

- A vista mostra o suficiente para saber **onde está** e **o que dá para fazer**?
- A tabela de tarefas sai legível em `tabela`? (Grades feitas de `div`, sem papel de tabela,
  não saem — anote se for o caso do Bitrix.)
- O OCR traz algo que a vista não traz?
- Quanto sobra em `ocultos` — é o texto que a vista esconde de propósito.

## Limites conhecidos da v1

- Texto da mesma cor do fundo não conta como oculto.
- Quadros (iframe) são lidos, com refs `sNfKeM`; a posição "na tela" é relativa ao quadro.
- Nada da página vai para o disco; só o perfil do navegador (cookies do login).
