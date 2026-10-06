# qwen3:4b · descartado · 15/09/2026

As duas rodadas foram **interrompidas de propósito** por inviabilidade. Não geraram relatório
completo; os números abaixo saíram da saída do roteiro de medições. Mesmo prompt, mesmos 15
casos, mesma máquina (CPU, sem GPU).

## Com raciocínio a critério do modelo (`--pensar modelo`, num_ctx 32768)

- Carga: **7,04 GB** em RAM (qwen3.5:4b: 4,2 GB). Com o Chrome aberto sobrou 1 GB livre.
- Aquecimento ("oi"): prefill frio 89,5s, **total 627s**. Geração a ~3 tok/s (qwen3.5:4b: 4,6).
- Três primeiras respostas a "oi": passaram, em **194s, 261s e 350s** (qwen3.5:4b: 13–16s).
- Projeção da rodada de 45 casos: ~3,5 h. Interrompida.

## Sem raciocínio (`--pensar nao`, num_ctx 16384)

- Aquecimento: prefill frio 82,8s, total 241s.
- O `think: false` **não foi respeitado**: o raciocínio saiu DENTRO da resposta, em inglês —
  "Okay, the user said "oi". Let me check the system requirements…" — em **476s e 160s**.
- O Ollama 0.33.1 renderiza este modelo pelo template Go antigo (log: `selected=go_template`),
  o caminho em que a pesquisa encontrou falhas de template com tools e thinking. Não confirmei a
  causa exata; o sintoma basta para descartar.

## Conclusão

Inviável no AIB nos dois modos: minutos por mensagem e, sem raciocínio, o que ele pensa vaza
para o usuário. O custo de cache do qwen3.5:4b (~35s por fala nova, por ser híbrido) continua
muito menor que o custo de geração do qwen3:4b.
