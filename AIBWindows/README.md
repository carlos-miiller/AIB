# AIB Windows (WPF Version)

Esta é a implementação nativa para Windows do assistente AIB. Focada em altíssima performance, integração profunda com o sistema e uma UI premium utilizando o design de transparência do Windows 10/11.

## 🛠️ Tecnologias Utilizadas

- **Framework:** .NET 8.0 (WPF)
- **Design:** Acrylic Blur (via Win32 interop)
- **Voz:** [Whisper.net](https://github.com/silentorbit/whisper.net) para transcrição e [NAudio](https://github.com/naudio/NAudio) para captura.
- **Teclas de Atalho:** NHotkey para ativação global.

## 📋 Pré-requisitos

1. **.NET 8 SDK:** Necessário para compilar e rodar.
2. **Ollama:** Rodando localmente (se for usar modelos locais).
3. **GPU (Opcional):** Para aceleração do Whisper (Runtime Clblast incluído).

## 🚀 Como Rodar

### 1. Configuração do `.env`
Certifique-se de que o arquivo `.env` na raiz do projeto (ou nesta pasta) contém:
```env
OPENAI_API_KEY=sua_chave (ou 'ollama' para local)
URL=http://localhost:11434/v1 (Para Ollama)
MODEL=qwen3:4b
```

### 2. Compilação
Abra o terminal nesta pasta e execute:
```bash
dotnet build
dotnet run
```
*Ou simplesmente abra o projeto no Visual Studio 2022.*

## 🎙️ AIB Live (Modo de Voz)

O AIB Windows inclui um motor de voz local. Na primeira execução, ele baixará o modelo `ggml-base.bin` automaticamente. 
- **Ativação:** Clique no ícone de microfone.
- **Uso:** Fale com a IA. Ela detectará o fim da sua fala automaticamente e enviará a mensagem.
- **Privacidade:** O processamento de áudio é 100% offline.

## 📷 Visão e OCR

O sistema utiliza o OCR nativo do Windows 10/11 (`Windows.Media.Ocr`), o que garante velocidade e precisão sem necessidade de APIs externas pagas.
