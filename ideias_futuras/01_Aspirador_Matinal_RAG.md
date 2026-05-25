# 1. Aspirador Proativo Matinal (RAG Automático)

## O Conceito
A ideia central é transformar o AIB de um sistema **reativo** para um assistente **proativo**. O AIB acorda antes do funcionário e prepara o terreno intelectual do dia.

## A Ação (Background Worker)
- Um gatilho temporal (ex: 07:50 AM) ou o evento de inicialização do Windows acorda o AIB invisivelmente.
- O sistema monitora uma pasta de rede compartilhada (`\\Servidor\Tabelas-do-Dia`) ou um e-mail específico corporativo.
- Utiliza a ferramenta nativa `ReadFileTool` para devorar PDFs e Excels atualizados de regras de negócio.
- Converte os dados em matrizes e injeta no banco vetorial persistente `LiteDB` através do motor `SmartComponents.LocalEmbeddings`.

## O Resultado (UX e Geração de Valor)
Quando o funcionário finalmente abre a interface para trabalhar, a primeira mensagem do dia não é um espaço em branco, mas sim um aviso da IA:
> *"Bom dia! Já baixei e vetorizei as 3 planilhas de hoje. Notei de relance que a taxa da geladeira X caiu 2%. Como vamos bater a meta hoje?"*

O vendedor começa o dia com um resumo executivo cirúrgico, pulando etapas burocráticas de leitura manual de comunicados e focando diretamente em vender.
