"""Cliente OpenAI com suporte a streaming e histórico de conversa."""
import os
from typing import Generator, Optional
from openai import OpenAI


SYSTEM_PROMPT = """Você é um assistente AI inteligente, direto e prestativo integrado ao desktop do usuário.
Quando uma imagem de tela é fornecida, analise-a cuidadosamente para dar contexto às perguntas do usuário.
Responda sempre no mesmo idioma que o usuário usar.
Seja conciso e objetivo, mas completo quando necessário."""


class OpenAIClient:
    def __init__(self):
        api_key = os.getenv("OPENAI_API_KEY")
        if not api_key:
            raise ValueError("OPENAI_API_KEY não definida. Configure o arquivo .env")
        
        base_url = os.getenv("URL")
        # Se URL estiver vazia ou não definida, OpenAI usa o padrão (api.openai.com)
        self.client = OpenAI(api_key=api_key, base_url=base_url)
        self.model = os.getenv("MODEL", "gpt-4o")
        self.history: list = []

    def send_message(
        self, text: str, image_base64: Optional[str] = None
    ) -> Generator[str, None, None]:
        """Envia mensagem (com imagem opcional) e faz streaming da resposta."""
        content = []

        if image_base64:
            content.append({
                "type": "image_url",
                "image_url": {
                    "url": f"data:image/jpeg;base64,{image_base64}",
                    "detail": "high",
                },
            })

        content.append({"type": "text", "text": text})
        self.history.append({"role": "user", "content": content})

        stream = self.client.chat.completions.create(
            model=self.model,
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                *self.history,
            ],
            stream=True,
            max_tokens=2048,
        )

        full_response = ""
        for chunk in stream:
            delta = chunk.choices[0].delta.content
            if delta:
                full_response += delta
                yield delta

        # Salva resposta no histórico (sem imagem pra economizar tokens)
        self.history.append({"role": "assistant", "content": full_response})

    def clear_history(self):
        self.history = []

    @property
    def message_count(self) -> int:
        return len(self.history)
