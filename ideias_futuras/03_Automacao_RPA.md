# 3. Automação Visceral de UI (RPA Nativo)

## O Conceito
Levar a inteligência artificial da abstração do texto para a execução motora. Eliminar o clique mecânico.

## A Ação (RPA e Controle Win32)
O AIB integrará a tecnologia da **Windows UI Automation API**, a mesma engine usada por leitores de tela para localizar componentes na tela sem depender de visão computacional pesada de pixel.

## A Execução
1. O humano dá a ordem falada ao AIB: *"Lance a venda desse cliente no sistema ERP"*.
2. A inteligência do AIB questiona a API do Windows sobre os elementos abertos da aba do CRM e recupera a árvore de propriedades (Botões e TextBoxes de formulários).
3. O LLM mapeia os campos lógicos que ele tem salvo na memória de curto prazo (Ex: *"Nome do Cliente" = "João"*) contra a UI estática retornada pelo Windows.
4. O AIB assume o controle do ponteiro do mouse (`mouse_event`) e teclado (`SendKeys`), digitando os dados rapidamente e clicando no botão de Enviar.

## Potencial
O ser humano descansa a mão e o pulso. O número de erros de digitação (Lançamento de Vendas em Sistema Errado ou com vírgulas faltando) cai drasticamente.
