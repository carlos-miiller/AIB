# 05. Banco de Dados e Memória (RAG e Longo Prazo)

A inteligência de um modelo de linguagem (LLM) baseia-se de forma fixa em dados pré-treinados limitados até uma data de corte. Para o AIB "lembrar-se" das nuances pessoais do usuário (nome do pet, regras de programação fixas da empresa, piadas, caminhos de diretórios frequentes), a aplicação descarta o ato de vomitar todo o histórico num único prompt titânico (o que destruiria o limite de context tokens abordado em *Core Services*).

Em vez disso, a arquitetura injeta **RAG (Retrieval-Augmented Generation)** de maneira estritamente offline, local e isolada, sem trafegar histórico privado na Web. 

## 1. O Motor Vetorial Neural (`SmartComponents.LocalEmbeddings`)

Tipicamente, converter frases humanas em Matemática Semântica (Embeddings) exige tráfego REST severo com APIs da OpenAI (`text-embedding-3`). O AIB estraçalha esse gargalo integrando o pacote `SmartComponents.LocalEmbeddings` do ecossistema .NET:
- **Rede Neural Local (ONNX)**: O pacote hospeda e carrega instantaneamente em runtime o modelo open-source `bge-micro-v2` (via ONNX runtime). Ele performa a geração do Vetor estritamente na CPU (e GPU/NPU se presente) consumindo míseros ~80MB de RAM do sistema principal.
- Em resumo: O `MemoryService.cs` recebe o texto bruto da IA e cospe silenciosamente um Array de *Floats* de altíssima dimensão estrutural representando o 'Significado Espacial' daquela frase, de graça, e sem latência de ping.

## 2. Persistência de Dados (NoSQL via `LiteDB`)

Para gerir a escalabilidade dos vetores, a persistência abandona simples strings num `.txt` e avança para a fundação do **LiteDB**, um robusto *Document-Store* relacional embarcado nativo de C#.
- Reside na pasta encriptada de dados (`%appdata% / AIB / DB / memory.db`). O banco roda com modo `Shared` invisível para não criar deadlocks operacionais no Windows caso os processos sejam engasgados.
- **Estrutura Bruta de Coleção (`memories`)**:
  - `Id` (GUID Único Transacional)
  - `Content` (String contendo a lição ou fato armazenado pela IA na tool)
  - `CreatedAt` (DateTime - Carimbo da Memória)
  - `Embedding` (O array neural denso `float[]` gerado no passo anterior)

## 3. A Matemática de Recuperação (Cosine Similarity)

O trunfo final ocorre quando o LLM sente a necessidade de buscar fatos antigos e aciona a tool `recall` (Ex: *"{ "query": "Projeto de design Figma do ano passado" }"*):
1. O texto pesquisado pelo robô sofre Vetorização imediata pelo motor BGE local.
2. O `MemoryService` executa um *dump* de leitura brutal no LiteDB puxando todos os documentos.
3. Para cada documento, ele aplica a fórmula clássica de **Similaridade do Cosseno**: um Produto Escalar entre o Vetor novo (da Query) e os velhos da memória, divididos pelas Magnitudes combinadas.
   - **Resultado ~ 1.0**: Significado Semântico absolutamente idêntico.
   - **Resultado ~ 0.0**: Palavras e intenções alienígenas e não-correlatas.
   - **Resultado < 0.0**: Abordagens antagonistas ou diametralmente opostas.
4. **Filtro de Ruído (Threshold Cutoff)**: Para o AIB não poluir a janela de contexto curta do Agente, o sistema C# descarta categoricamente qualquer acerto menor que `0.2` (20% de aderência de relevância espacial), agrupa as notas mais altas (List OrderBy) e ejeta apenas os **Top 3** fatos irrefutáveis de volta para o Loop ReAct em questão de ~100 milissegundos.

Com esse maquinário, o AIB dispõe de uma memória eterna assustadoramente barata, veloz e autônoma, superando abordagens clássicas e custosas de "Banco de Dados como API".
