# 9. Integração com Google AI Studio (Gemini API)

Estudo técnico, arquitetura de integração e análise de custos para a adição do **Google AI Studio (Gemini API)** como o terceiro provedor de IA nativo da AIB, com foco no aproveitamento do crédito promocional de US$ 300 do Google.

Data do levantamento: **Outubro de 2026**  
Status: **Implementado em 23946d4**  
Escopo do documento: Análise arquitetural, mapeamento de código, pesquisa web oficial, mitigação de riscos e comparação de custos.

> Levantamento feito pelo Antigravity e revisado depois da implementação. Foram reconferidos na
> fonte: a regra do crédito, a tabela de preços e o que o endpoint compatível documenta. **Não
> foram reconferidos:** os limites de requisição (1.3), os limiares e o desconto do cache
> implícito (1.4) e a exigência de OpenAPI 3.0 nos esquemas (1.5).

---

## Resumo Executivo e Recomendação

### 1. O Crédito de US$ 300 do Google
O proprietário do projeto obteve um crédito promocional de US$ 300 do Google Cloud. Os pontos relativos ao faturamento são:
* **Fato Confirmado:** Conforme a documentação oficial da API Gemini ([Google AI Studio Billing](https://ai.google.dev/gemini-api/docs/billing)), o crédito de boas-vindas e o crédito do teste gratuito do Google Cloud **não pagam a Gemini API nem o Google AI Studio**. Desde março de 2026, a Gemini API está expressamente excluída do teste gratuito do Google Cloud. Usuários que receberam créditos promocionais antes de março de 2026 podem utilizá-los até a data de expiração (90 dias). Requisições faturadas no AI Studio cobram diretamente a forma de pagamento cadastrada no faturamento da nuvem.
* **Hipótese Não Confirmada:** Não é fato confirmado que esse crédito possa ser abatido utilizando modelos Gemini via **Vertex AI**. A página de recursos gratuitos do Google Cloud ([Google Cloud Free Program Features](https://docs.cloud.google.com/free/docs/free-cloud-features)) exclui o AI Studio e modelos de parceiros (*partner models*), mas não afirma que os modelos Gemini na Vertex AI estejam cobertos pelo crédito do teste gratuito.
* **Vertex AI com a mesma base de código:** Trata-se de hipótese não verificada. A Vertex AI pode exigir autenticação via Google Cloud IAM (OAuth2 ou contas de serviço), em vez de uma chave de API direta em cabeçalho `Authorization: Bearer`. Além disso, a URL do provedor na interface da AIB é fixa.

### 2. Caminho de Integração Adotado
O caminho implementado utiliza o **Endpoint Compatível com OpenAI (`/v1beta/openai/chat/completions`)** por meio da classe `GoogleProvider`.
* A API nativa do Gemini (`generateContent`) utiliza formato próprio de mensagens (`contents`, `parts`, `functionDeclarations`, `functionResponse`), o que exigiria duplicar os mecanismos de serialização e leitura de stream já existentes na AIB.
* O endpoint compatível com OpenAI permite reaproveitar o protocolo SSE (`text/event-stream`), chamadas de ferramenta e agregação de deltas estruturados na AIB, isolando na classe `GoogleProvider` apenas as particularidades do Gemini (como o tratamento de `extra_content` e o parâmetro `reasoning_effort`).

---

## O que foi implementado

A integração do Google AI Studio como terceiro provedor foi realizada no commit `23946d4`:

* **O que entrou:**
  * **Endpoint compatível com OpenAI:** requisições diretas via HTTP POST para `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` com streaming SSE.
  * **Classe `GoogleProvider`:** implementa a interface `IChatProvider`, registrada sob a constante `ProvedoresDeIa.Google`. Modelo padrão: `gemini-flash-latest`.
  * **Cofre de credenciais:** chave armazenada no cofre DPAPI do Windows sob o sistema `google`.
  * **Interface gráfica:** painel próprio na aba Conexão LLM (`SettingsWindow.xaml`) exibindo o endereço fixo da API, campo protegido de chave e seleção de modelo com sugestões editáveis. A URL é exibida como somente leitura (`IsReadOnly="True"`) e não pode ser editada na tela.
  * **Triagem de e-mail:** inclusão do provedor Google na lista de provedores elegíveis para triagem em segundo plano.
  * **Regras de nuvem (`ProvedoresDeIa.EhNuvem`):** centralização da identificação de provedores remotos (`OpenRouter` e `Google`), aplicando automaticamente limites de nuvem (`LimitesDoProvedor.Nuvem`), teto de tokens do capítulo sem cortes locais e opções de raciocínio.
  * **Aquecimento de modelo:** o serviço `WarmupService` ignora provedores de nuvem, evitando requisições pagas de heartbeat no Google.
  * **Assinaturas de pensamento:** o campo `extra_content` de cada chamada de ferramenta é lido do stream, armazenado por ID em `_extraPorChamada` e devolvido no turno seguinte dentro da ferramenta.
  * **Controle de raciocínio:** envio de `reasoning_effort` (`none`, `low`, `medium`, `high`). Caso um modelo Pro recuse `none` com erro 400, a classe reenvia sem o campo e memoriza o comportamento na flag `_naoDesliga`.
  * **Chamadas de ferramenta paralelas:** o componente `GoogleProvider.JuntaDeChamadas` inicia chamadas por nome de função e gera identificadores (`gg_...`) quando o Gemini omite o ID ou repete índices.
  * **Contagem de tokens:** leitura de `prompt_tokens`, `completion_tokens` e `prompt_tokens_details.cached_tokens`.

* **O que ficou de fora:**
  * **Vertex AI:** não suportado; a URL é fixa para o endpoint do Google AI Studio.
  * **Primeiro arranque:** a tela `FirstRunWindow` permanece apenas com as opções Ollama e OpenRouter; a configuração do Google ocorre na tela de configurações.
  * **Texto de raciocínio na tela:** o texto do raciocínio intermediário não é repassado ao canal visível (o indicador de pensamento da interface não acende).
  * **Filtros de segurança (*safety settings*):** não há configuração de parâmetros de segurança nas requisições.
  * **Catálogo dinâmico de modelos:** não há consulta à API de modelos em tempo de execução; a lista da interface baseia-se em sugestões estáticas.

---

## 1. Pesquisa Web e Evidências Oficiais

### 1.1 Crédito Promocional de US$ 300: AI Studio vs Vertex AI
* **Fontes Oficiais:** [Google AI Studio Billing Guide](https://ai.google.dev/gemini-api/docs/billing) e [Google Cloud Free Program Features](https://docs.cloud.google.com/free/docs/free-cloud-features) (Consultado em Outubro de 2026).
* **Fatos Confirmados:**
  1. A página oficial de faturamento da API Gemini explicita que créditos do teste gratuito do Google Cloud não cobrem o uso da Gemini API nem do Google AI Studio.
  2. Desde março de 2026, a Gemini API está expressamente excluída do teste gratuito do Google Cloud. Usuários que ativaram créditos antes desse período podem utilizá-los dentro da validade de 90 dias.
  3. No Google AI Studio, contas no regime pago faturam diretamente o cartão de crédito associado à conta de faturamento do GCP.
* **Hipóteses Não Confirmadas:**
  1. Não há confirmação documental de que o crédito de US$ 300 do teste gratuito do GCP cubra a execução de modelos Gemini na Vertex AI. A documentação do programa gratuito lista exclusões expressas (como o AI Studio e modelos de terceiros), mas não especifica a inclusão de Gemini na Vertex AI.
  2. A compatibilidade da Vertex AI com a mesma base de código trocando apenas a URL é uma hipótese não verificada. A Vertex AI emprega mecanismos de autenticação baseados no Google Cloud IAM, exigindo tokens OAuth2 ou credenciais de conta de serviço, o que difere do cabeçalho de chave de API estática do AI Studio.

---

### 1.2 Comparativo: API Nativa vs Endpoint OpenAI

* **Fonte Oficial:** [Gemini API OpenAI Compatibility](https://ai.google.dev/gemini-api/docs/openai) (Consultado em Outubro de 2026).

| Recurso | API Nativa (`generateContent` / `streamGenerateContent`) | Endpoint Compatível OpenAI (`/v1beta/openai/`) | Situação na AIB |
| :--- | :--- | :--- | :--- |
| **Protocolo & Transporte** | REST com streaming SSE proprietário (`data: { candidates: [...] }`). | REST com streaming SSE padrão OpenAI (`data: { choices: [{ delta: ... }] }`). | A AIB reaproveita a leitura de stream SSE já testada no `OpenRouterProvider`. |
| **Chamadas de Ferramenta Paralelas** | Suporte nativo via `functionDeclarations` e `functionCalls`. | Suporte via array `tools` e blocos `tool_calls` nos deltas. | Tratado por `GoogleProvider.JuntaDeChamadas`. |
| **Raciocínio (Thinking)** | Configuração via `thinkingConfig: { thinkingBudget: N }`. | Configuração via parâmetro `reasoning_effort` (`none`, `low`, `medium`, `high`). | Alinhado com `PerfilDeProvedor.Raciocinio`. |
| **Cache de Contexto** | Suporta Cache Implícito e criação explícita via `cachedContents`. | Suporta **Cache Implícito** automático no backend do Google. | O Cache Implícito opera sem chamadas adicionais de criação de cache. |
| **Assinatura de Raciocínio (Thought Signatures)** | Campo nativo `thoughtSignature`. | A documentação diz que o Gemini 3 as suporta neste endpoint, sem mostrar o campo. | A AIB guarda e reenvia o `extra_content` de cada chamada de ferramenta. |

---

### 1.3 Modelos Gemini Atuais, Preços e Limites (Outubro 2026)

* **Fontes Oficiais:** [Gemini API Pricing](https://ai.google.dev/gemini-api/docs/pricing) e [Gemini Rate Limits](https://ai.google.dev/gemini-api/docs/rate-limits) (Consultado em Outubro de 2026).

#### Tabela de Preços (Valores por 1 Milhão de Tokens)

| Modelo | Entrada (Prompt) | Entrada em Cache | Saída (Geração) | Context Window |
| :--- | :--- | :--- | :--- | :--- |
| **Gemini 2.5 Flash-Lite** | $0.10 | $0.010 | $0.40 | 1.048.576 |
| **Gemini 2.5 Flash** | $0.30 | $0.030 | $2.50 | 1.048.576 |
| **Gemini 2.5 Pro** (até 200k tokens) | $1.25 | $0.125 | $10.00 | 2.097.152 |
| **Gemini 3.1 Flash-Lite** | $0.25 | $0.025 | $1.50 | 1.048.576 |
| **Gemini 3.1 Pro Preview** (até 200k tokens) | $2.00 | $0.200 | $12.00 | 2.097.152 |
| **Gemini 3.5 Flash-Lite** | $0.30 | $0.030 | $2.50 | 1.048.576 |
| **Gemini 3.5 Flash** | $1.50 | $0.150 | $9.00 | 1.048.576 |
| **Gemini 3.8 Flash** *(até 31/12/2026)* | $0.75 | $0.075 | $3.75 | 1.048.576 |
| **Gemini 3.8 Flash** *(a partir de 01/01/2027)* | $1.50 | $0.150 | $7.50 | 1.048.576 |

> [!NOTE]
> Conforme a tabela oficial do Google, os valores do **Gemini 3.8 Flash** possuem vigência promocional até 31/12/2026, dobrando a partir de 01/01/2027 para $1.50 (entrada), $0.15 (cache) e $7.50 (saída).

#### Limites de Taxa de Requisição

| Regime | Modelo | RPM (Req/min) | TPM (Tokens/min) | RPD (Req/dia) |
| :--- | :--- | :--- | :--- | :--- |
| **Free Tier** | Gemini 2.5 Flash | 15 RPM | 250.000 TPM | 250 RPD |
| **Free Tier** | Gemini 2.5 Pro | 5 RPM | 250.000 TPM | 100 RPD |
| **Pay-as-you-go (Tier 1)** | Gemini 2.5 Flash | 1.000 RPM | 4.000.000 TPM | Ilimitado |
| **Pay-as-you-go (Tier 1)** | Gemini 2.5 Pro | 360 RPM | 2.000.000 TPM | Ilimitado |

> [!WARNING]
> No Free Tier, o teto diário de 250 requisições pode ser consumido rapidamente em sessões com múltiplos turnos e voltas de ferramentas sucessivas.

---

### 1.4 Cache de Contexto: Implícito vs Explícito

* **Fonte Oficial:** [Gemini API Context Caching](https://ai.google.dev/gemini-api/docs/caching) (Consultado em Outubro de 2026).

#### Funcionamento:
1. **Cache Implícito:**
   * O sistema do Google detecta automaticamente repetições de prefixos idênticos no histórico.
   * **Limiar mínimo exigido:**
     * Modelos **Gemini 2.5**: **2.048 tokens**.
     * Modelos **Gemini 3.8**: **4.096 tokens**.
   * Não há cobrança horária de armazenamento para o cache implícito; a leitura de tokens cacheados recebe desconto de 75% a 90% conforme o modelo.
2. **Cache Explícito:**
   * Requer chamada prévia para criação de recurso via API com tempo de vida definido (TTL) e gera custo horário de armazenamento.

#### Relação com a AIB:
* Na AIB, o prefixo estável enviado ao modelo (instruções de sistema, definições de ferramentas e memórias) é estimado em cerca de 3.500 tokens (estimativa não medida).
* Para modelos Gemini 2.5, esse prefixo supera o piso de 2.048 tokens, tornando o cache implícito elegível desde o primeiro turno. Para modelos Gemini 3.8 (piso de 4.096 tokens), o cache implícito depende do acúmulo de turnos no histórico.
* **Ressalva:** A suposição de que o índice de 96% de acerto observado no OpenRouter se manterá no Gemini não é garantida; o aproveitamento do cache implícito depende exclusivamente da política de retenção interna dos servidores do Google.

---

### 1.5 Particularidades no Laço de Ferramentas

1. **Assinaturas de Pensamento (*Thought Signatures*):**
   * A documentação oficial do Google afirma que o **Gemini 3** suporta assinaturas de pensamento no endpoint compatível com OpenAI.
   * O formato **não é** o `reasoning_details` do OpenRouter. A página do endpoint compatível não mostra o campo; a implementação assume o `extra_content` em cada chamada de ferramenta.
   * O `GoogleProvider` armazena o conteúdo desse campo pelo identificador da chamada (`_extraPorChamada`) e o reenvia no turno seguinte como `c["extra_content"]` dentro do bloco `tool_calls` da mensagem do assistente. Sem essa devolução, espera-se que modelos que raciocinam recusem o turno subsequente (HTTP 400). O dono confirmou que o provedor funciona com a API real; um turno com ferramenta e raciocínio ligado ainda não foi confirmado.
2. **Esquema JSON das Ferramentas:**
   * A API Gemini exige compatibilidade estrita com OpenAPI 3.0, rejeitando construções como `$schema` e esquemas com tipagem ambígua.
3. **Filtros de Segurança (*Safety Filters*):**
   * A documentação do endpoint compatível com OpenAI menciona limiares de segurança apenas para tarefas de geração de imagem. A existência e o funcionamento de filtros de segurança para chat de texto nesse endpoint constituem hipótese não confirmada. Esse parâmetro não foi incluído na implementação.
4. **Erros e Retentativas:**
   * Respostas com códigos 408, 429 ou 5xx são tratadas com retentativas automáticas antes do início da resposta em streaming, respeitando eventuais cabeçalhos `Retry-After` até o teto de 30 segundos.

---

## 2. Arquivos Modificados na Implementação

Conforme registrado no commit `23946d4` (`git show --stat 23946d4`), os seguintes arquivos integraram a implementação:

1. **[`AIBWindows/Services/Ai/GoogleProvider.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Services/Ai/GoogleProvider.cs) (Novo):**
   * Implementação de `IChatProvider` para o endpoint `/v1beta/openai/chat/completions` com HTTP direto e streaming SSE.
   * Armazenamento e devolução de `extra_content` por chamada de ferramenta.
   * Tratamento de `reasoning_effort` (`none`, `low`, `medium`, `high`) com fallback para modelos Pro (`_naoDesliga`).
   * Agrupamento de chamadas paralelas via `JuntaDeChamadas` e retentativas em falhas transitórias (408, 429, 5xx).
2. **[`AIB.Tests/GoogleProviderTests.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIB.Tests/GoogleProviderTests.cs) (Novo):**
   * Testes unitários com rede simulada cobrindo registro de provedor, validação de chaves, corpo da requisição, reenvio de `extra_content`, processamento de stream e retentativas.
3. **[`AIBWindows/Services/ProvedoresDeIa.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Services/ProvedoresDeIa.cs):**
   * Definição da constante `Google`, `UrlDoGoogle`, `ModeloPadraoDoGoogle` (`gemini-flash-latest`), lista `ModelosDoGoogle`, inclusão em `Todos`, método auxiliar `EhNuvem`, cofre `google`, validação de chave e limites de nuvem.
4. **[`AIBWindows/Services/Ai/ChatProviderFactory.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Services/Ai/ChatProviderFactory.cs):**
   * Construção e reaproveitamento em cache da instância de `GoogleProvider`.
5. **[`AIBWindows/Services/Agent/WarmupService.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Services/Agent/WarmupService.cs):**
   * Verificação via `ProvedoresDeIa.EhNuvem` para ignorar aquecimento em provedores de nuvem.
6. **[`AIBWindows/Services/ConversationService.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Services/ConversationService.cs):**
   * Uso de `ProvedoresDeIa.EhNuvem` no teto de tokens do capítulo e no repasse das opções de raciocínio.
7. **[`AIBWindows/Views/SettingsWindow.xaml`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Views/SettingsWindow.xaml) e [`.xaml.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/Views/SettingsWindow.xaml.cs):**
   * Adição do painel `PainelGoogle` na aba Conexão LLM com URL estática em modo somente leitura, armazenamento de chave no cofre DPAPI e seleção de modelo com sugestões editáveis; integração com a triagem de e-mail.
8. **[`AIBWindows/App.xaml.cs`](file:///C:/Users/Carlo/CPAPS/AIB/AIBWindows/App.xaml.cs):**
   * `NeedsFirstRun` só exige chave para o OpenRouter: a tela de primeiro arranque não sabe pedir a do Google.
9. **Documentação e Instruções:**
   * Atualização de `CLAUDE.md`, `01-arquitetura.md`, `02-turno-e-provedores.md` e `07-configuracoes-e-dados.md`.

---

## 3. Comparação de Custo com o Uso Real

O provedor e modelo utilizados atualmente na AIB em produção é o **DeepSeek V4.1 Flash via OpenRouter**.

### 3.1 Uso Real Medido na AIB
No período de **29/09/2026 a 07/10/2026**, o uso levantado foi:
* **Total de requisições:** 265
* **Volume total de entrada:** 19,5 milhões de tokens
* **Taxa de reaproveitamento de cache:** 96%
* **Custo total efetivo pago:** **US$ 0,49**

Com base nesses dados medidos, a composição da entrada foi:
* **Entrada em cache (96%):** 19.500.000 × 0,96 = 18.720.000 tokens (18,72 milhões)
* **Entrada sem cache (4%):** 19.500.000 × 0,04 = 780.000 tokens (0,78 milhão)

---

### 3.2 Cálculo Comparativo de Custo de Entrada

A comparação a seguir calcula **exclusivamente o custo dos tokens de entrada** para o mesmo volume de 19,5 milhões de tokens sob as taxas oficiais de cada modelo Gemini. O volume de saída não foi medido no período e, portanto, não compõe o cálculo abaixo.

#### 1. Gemini 2.5 Flash-Lite
* Entrada sem cache: 0,78 × US$ 0,10 = US$ 0,0780
* Entrada em cache: 18,72 × US$ 0,01 = US$ 0,1872
* **Total de entrada:** US$ 0,0780 + US$ 0,1872 = **US$ 0,2652** (~US$ 0,27)

#### 2. Gemini 2.5 Flash
* Entrada sem cache: 0,78 × US$ 0,30 = US$ 0,2340
* Entrada em cache: 18,72 × US$ 0,03 = US$ 0,5616
* **Total de entrada:** US$ 0,2340 + US$ 0,5616 = **US$ 0,7956** (~US$ 0,80)

#### 3. Gemini 3.8 Flash (preço até 31/12/2026)
* Entrada sem cache: 0,78 × US$ 0,75 = US$ 0,5850
* Entrada em cache: 18,72 × US$ 0,075 = US$ 1,4040
* **Total de entrada:** US$ 0,5850 + US$ 1,4040 = **US$ 1,9890** (~US$ 1,99)

Para comparar: o período inteiro custou US$ 0,49 no DeepSeek, entrada e saída somadas. Só o
Flash-Lite fica abaixo disso na entrada.

---

## 4. Riscos, Incertezas e Suposições vs Fatos Confirmados

| Tópico | Classificação | Detalhes |
| :--- | :--- | :--- |
| **Abatimento do Crédito de US$ 300 no AI Studio** | **Fato Confirmado** | Conforme documentação de faturamento do Google, o crédito de boas-vindas do GCP não cobre a Gemini API nem o AI Studio (excluído desde março de 2026). O uso gera cobrança direta no cartão cadastrado. |
| **Abatimento do Crédito de US$ 300 na Vertex AI** | **Hipótese Não Confirmada** | A documentação do Google Cloud Free Program lista exclusões, mas não confirma expressamente que modelos Gemini na Vertex AI estejam cobertos pelo crédito promocional. |
| **Vertex AI com a mesma base trocando a URL** | **Hipótese Não Verificada** | A autenticação na Vertex AI depende tipicamente de credenciais IAM/OAuth2 e a URL na interface da AIB é fixa. |
| **Assinaturas de Pensamento no Gemini 3** | **Parcialmente Confirmado** | O suporte no endpoint compatível está documentado; o campo não. Implementado guardando e devolvendo o `extra_content`; falta confirmar num turno real com ferramenta e raciocínio. |
| **Filtros de Segurança para Chat no Endpoint OpenAI** | **Hipótese Não Confirmada** | A documentação oficial menciona parâmetros de segurança apenas para geração de imagens; não foi implementado no código. |
| **Manutenção da Taxa de 96% de Cache** | **Suposição Não Confirmada** | O funcionamento do cache implícito no Google é automático, mas não há garantia de que atingirá a mesma taxa de acerto observada no OpenRouter. |
| **Tamanho do Prefixo Fixo (3.500 tokens)** | **Estimativa Não Medida** | Valor de referência baseado na composição de instruções e ferramentas, não medido diretamente via tokenizador específico. |
| **Limitações do Free Tier do AI Studio** | **Fato Confirmado** | Limites de 15 RPM e 250 RPD no Gemini 2.5 Flash restringem sessões com múltiplos turnos no agente. |

---

## 5. Esforço de Implementação

A implementação foi concluída no commit `23946d4`.

---

## Conclusão

1. **Uso dos Créditos de US$ 300:**
   * A documentação oficial confirma que a Gemini API no Google AI Studio não consome o crédito de boas-vindas do Google Cloud.
   * O abatimento desse crédito via Vertex AI permanece como hipótese não confirmada documentalmente.
2. **Solução em Operação:**
   * A integração foi efetivada pelo provedor `GoogleProvider` utilizando o endpoint compatível com OpenAI do Google AI Studio, preservando o histórico de ferramentas com `extra_content`, aplicando limites de nuvem e operando com credenciais isoladas no cofre do sistema.
