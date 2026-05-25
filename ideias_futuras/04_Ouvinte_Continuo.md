# 4. Modo Ouvinte Contínuo (Passive Transcription)

## O Conceito
Transformar o AIB de uma barra de chat para um "Treinador Whisperer" que assopra conhecimento na orelha do vendedor durante a ligação, sem que o humano tenha pedido.

## A Arquitetura (Leveza para Edge Computing)
A latência e o consumo de máquina são mitigados:
1. O Serviço de Voz lê a interface de áudio passivamente num loop local.
2. O arquivo binário roda localmente um modelo *Speech-to-Text* ultraleve, como o `Whisper Tiny` (via C# ONNX Runtime), pesando míseros ~70MB.
3. Se e somente se o cliente disparar Palavras-Chave (Triggers) programadas na lógica, ex: "muito caro", "procon", "cancelar pedido", "concorrente tem desconto", o sistema desperta o Raciocínio (ReAct).

## A Execução
Somente nesse momento crasso da ligação, o AIB envia esse micro-trecho focado de texto para o Servidor Central de LLM (Ollama). O Servidor devolve instantaneamente um pop-up na tela do vendedor:
> *"O AIB Sugere: Argumente que nossa entrega é 3 dias mais veloz que a do Concorrente e ofereça a extensão de garantia gratuita."*

O vendedor soa implacável ao telefone.
