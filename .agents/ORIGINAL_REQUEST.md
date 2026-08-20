# Original User Request

## 2026-08-19T18:31:43Z

# Teamwork Project Prompt — Draft

> Status: Launched
> Goal: Craft prompt → get user approval → delegate to teamwork_preview
> Requested team: [none — teamwork routes from the description]

Avaliar de forma abrangente o projeto AIB (Assistente Inteligente Baseado em IA), realizando uma análise estática profunda do código-fonte para caçar erros e problemas arquiteturais que normalmente passariam despercebidos.

Working directory: c:\Users\Carlo\CPAPS\AIB
Integrity mode: development

## Requirements

### R1. Análise Estática Profunda
Executar uma varredura rigorosa no projeto `AIBWindows` e em seus testes (`AIB.Tests`). A avaliação deve cobrir a arquitetura C#, padrões de projeto (MVVM, injeção de dependências), estrutura de UI (XAML) e boas práticas de código limpo.

### R2. Caça a Bugs Críticos e Gargalos
Identificar potenciais *race conditions*, *deadlocks* em chamadas assíncronas (especialmente nas lógicas de streaming do `OpenAIService` e `OllamaNativeClient`), vazamentos de memória (eventos não desinscritos em WPF) e vulnerabilidades de segurança ou tratamento inadequado de exceções.

### R3. Relatório Detalhado
Produzir um documento unificado contendo todas as descobertas, categorizadas por severidade e área de impacto, sugerindo arquiteturas ou refatorações para cada problema encontrado.

## Acceptance Criteria

### Relatório Final
- [ ] O arquivo `relatorio_auditoria.md` deve ser gerado no diretório atual (C:\Users\Carlo\CPAPS\AIB).
- [ ] O relatório deve conter no mínimo 4 seções distintas de análise (ex: Arquitetura, Assincronismo/Concorrência, UI/XAML, Lógica de Negócios).
- [ ] Todos os problemas relatados devem referenciar o arquivo exato e, sempre que possível, a linha (ou classe/método) onde o problema se encontra.
- [ ] Todo problema levantado deve estar acompanhado de uma sugestão técnica acionável de resolução (um snippet de código ou descrição do padrão a ser adotado).
