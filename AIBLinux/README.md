# AIB Linux (Python Version)

Esta é a versão original do AIB, construída em Python. É uma implementação flexível, ideal para desenvolvimento, testes e ambientes Linux.

## 🛠️ Tecnologias Utilizadas

- **Linguagem:** Python 3.10+
- **Bibliotecas:** `openai`, `python-dotenv`, `requests`
- **Automação:** Scripts Bash para build e execução.

## 📋 Pré-requisitos

1. **Python 3.10+**: Certifique-se de ter o Python instalado.
2. **Pip**: Gerenciador de pacotes do Python.
3. **Ollama**: Rodando como servidor local.

## 🚀 Como Rodar (Localmente)

### 1. Preparar Ambiente
Recomendamos o uso de um ambiente virtual (venv):
```bash
python -m venv venv
source venv/bin/activate  # Linux/Mac
# ou .\venv\Scripts\activate no Windows (se for rodar o python puro)
pip install -r requirements.txt
```

### 2. Configuração
Configure o arquivo `.env` na raiz do projeto com suas credenciais.

### 3. Execução
Execute o script principal:
```bash
python main.py
```
Ou use o script de conveniência:
```bash
bash run.sh
```

## 📦 Scripts de Build

- `build.sh`: Script para preparar o ambiente.
- `build_universal.sh`: Script para compilação universal (se aplicável).
- `verify_ollama.py`: Pequeno utilitário para garantir que o seu servidor Ollama está acessível e configurado corretamente.

---
*Nota: Atualmente, o desenvolvimento principal está focado na versão Windows (AIBWindows), mas esta versão permanece funcional para interações via terminal e scripts.*
