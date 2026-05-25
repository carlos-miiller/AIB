# 2. Restauração de Workspace e Auto-Login

## O Conceito
Uma infraestrutura de orquestração local (SSO Simulado) para acabar com a perda de tempo na preparação do ambiente de trabalho do Call Center.

## A Ação (Orquestração de Processos)
- Após o bom dia matinal do AIB, o assistente pergunta: *"Deseja reabrir os sistemas de ontem?"*.
- O AIB acessa o Cofre Local Criptografado (`CredentialService` com *DPAPI* do Windows) para resgatar silenciosamente as senhas complexas exigidas pela TI corporativa.
- O sistema utiliza `System.Diagnostics.Process` ou protocolos avançados de navegador (CDP / PuppeteerSharp) para iniciar o Google Chrome ou Edge de forma limpa.

## A Execução Automática (O Setup em Segundos)
1. Abre as abas corretas e fixas (CRM, ERP da Empresa, Webmail corporativo).
2. O AIB, atuando como as "mãos" do usuário, injeta as credenciais de autenticação recuperadas do Vault nos sites de forma segura.
3. Faz o login automático em 3, 4 ou 5 sistemas simultaneamente.

## Benefício Corporativo
O humano levou apenas 5 segundos para aprovar a ação. O gargalo matinal ("Logar em tudo e abrir todas as planilhas"), que consome cerca de 10 minutos de 50 funcionários todos os dias, é pulverizado. Economiza horas de pagamento para digitação de senhas e arrumação de janelas.
