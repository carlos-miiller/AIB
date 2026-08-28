# Estado — ato 3 (capítulos 9–12, turnos 71–96)

## Máquina
- projeto: C:\Users\Carlo\CPAPS\AIB — git, branch feature/redesign-first-run
- verdade dos dados: ~/.AIB — a pasta do programa é só failsafe
- Ollama em localhost:11434 — qwen3.5:9b e qwen3.5:4b, 100% CPU
- 15,7 GB de RAM; os dois modelos residentes ocupam 10,2 GB
- falha nesta máquina: heredoc `python - <<'EOF'` pelo bash
- comando que funciona: `dotnet test AIB.Tests`

## Recusado
- apagar raw.jsonl — negado, sempre
- mudar o padrão visual da interface — negado; acrescentar no mesmo estilo, sim
- teste escrevendo em ~/.AIB/memory — negado; usar memoryRootOverride

## Medido
- resumidor com think:false: 286,6s → 14,7s (qwen3.5:4b, 5 turnos)
- prefix cache: anexar no fim 2,7ms / mudar o começo 50,5ms
- qwen3.5:4b em CPU: prefill 34 tok/s, geração 7,7 tok/s
- capítulo renderizado ≈ 230 tokens; cota de capítulos com a Ayano = 538

## Agora
Fase 3 da memória hierárquica implementada e commitada. Suíte offline
356/356. Capítulo validado contra o Ollama real; ato não. Próximo passo:
rodar o ensaio que fecha quatro capítulos e promove o primeiro ato.

## Incerto
- facts.md nunca foi produzido por execução real, só por teste unitário
- num_ctx pedido é 16384, mas o nível 1 orça 8192 — KV dobrado, não medido
- gatilho do ato é por contagem (4); a cota de capítulos estoura em 2

## Tentado
- ✗ subir o timeout do resumidor de 2 para 4 min — trata o sintoma. O custo
  eram 1.738 tokens de `<think>` gerados e jogados fora. Resolveu: think:false.
- ✗ prever no ensaio quantos turnos o gatilho custa — os números mudam com o
  modelo. Resolveu: o ensaio espera o gatilho e usa teto só como rede.
- ✓ truncar o texto do usuário em 800 — só depois de truncar assistente e
  ferramenta também; sozinho não bastava.

---
---

*Daqui para baixo não entra no prompt. É a legenda do mockup.*

## Como cada campo é escrito

| campo | quem escreve | política | morre quando |
|---|---|---|---|
| cabeçalho | código | reescrito | — |
| Máquina | código (artefatos `CommandRun`, `Failed`, caminhos) | anexa, corte por volume | usuário apaga |
| Recusado | código (artefatos `Denied`) | anexa, promove na 1ª vez | usuário apaga |
| Medido | modelo | anexa | usuário apaga |
| Agora | modelo | **sobrescreve** | próximo ato |
| Incerto | modelo | **sobrescreve** | próximo ato |
| Tentado | modelo + código (`Failed`) | anexa | o objetivo em `Agora` muda |

Só três campos custam geração de LLM. Dois deles são sobrescritos, então não
crescem nunca. Os dois primeiros já saem do `ArtifactExtractor` +
`ArtifactDigest.Condense`, que existem e são testados.

Nenhum literal — caminho, comando, negação — passa pelo resumidor. Se o modelo
errar `Agora`, perde-se uma frase e nada mais.

## O que este arquivo NÃO tem, de propósito

- **narrativa**: "o usuário pediu X e o agente fez Y" não ajuda ninguém. Fica em
  `chapters.jsonl`, no disco, para quem quiser reconstituir.
- **bloco por entidade**: funciona no arquivo do jogo porque o elenco é finito.
  Aqui as entidades são ilimitadas — todo arquivo, todo comando. O corte por
  volume substitui o elenco fixo.
- **julgamento**: nenhum campo pergunta ao modelo "isto vale guardar?".

## Ordem dos campos

Do mais estável para o mais volátil, para o prefix cache sobreviver: `Máquina` e
`Recusado` quase nunca mudam e ficam no topo; `Agora` e `Incerto` mudam a cada
ato e ficam no fim, onde reescrever custa 2,7 ms em vez de 50,5 ms.
