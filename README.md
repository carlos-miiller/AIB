# AIB - AI Personal Assistant

O **AIB** é um assistente pessoal inteligente e multimodal, projetado para ser integrado diretamente ao fluxo de trabalho do usuário no desktop. Ele combina capacidades de **Visão Computacional**, **Reconhecimento de Voz Local** e **Processamento de Linguagem Natural** para criar uma experiência de assistência nativa e ágil.

## 🚀 Capacidades Principais

- **🧠 Multimodalidade Local:** Utiliza LLMs locais (via Ollama) como o Qwen3 para responder perguntas com contexto total de privacidade.
- **👁️ Visão de Tela:** Capaz de capturar e analisar o conteúdo da tela do usuário utilizando OCR nativo e processamento de imagem.
- **🎙️ AIB Live (Voz Local):** Reconhecimento de voz contínuo e local utilizando **Whisper.net**. Funciona no estilo "Live Chat", detectando automaticamente quando o usuário termina de falar.
- **🎨 Design Premium:** Interface moderna com suporte a efeitos de transparência (Acrylic/Blur) e animações suaves.

## 📁 Estrutura do Projeto

O repositório está organizado em duas implementações principais:

### [AIB Windows](./AIBWindows/)
Implementação nativa para Windows 10/11 construída com **.NET 8 e WPF**. É a versão mais completa e performática, focada em integração com o sistema operacional e baixa latência de voz.

### [AIB Linux](./AIBLinux/)
Versão original baseada em **Python**, ideal para experimentação e ambientes Linux. Suporta execução local e via scripts de automação.

---

## 🛠️ Requisitos Globais

- [Ollama](https://ollama.com/) rodando localmente.
- Modelo recomendado: **`gemma4:e2b`** (Extremamente otimizado, veloz e com suporte multimodal) ou superior.
- O sistema possui **Warmup e Trava de Memória (Heartbeat)** no boot para mascarar a compilação de gramática.
- As skills customizadas (em Python) ficam salvas em `~/.AIB/skills` e são chamadas sob demanda (Lazy Loading).
- Arquivo `.env` configurado na raiz com as chaves de API necessárias (caso não use Ollama).

## 📄 Licença

Este projeto é desenvolvido para uso pessoal e demonstração de capacidades de IA integrada.

---
*Desenvolvido com foco em privacidade e performance.*