import os
import sys

# Adiciona o diretório atual ao path para importar 'app'
sys.path.append(os.getcwd())

# Mock do environment
os.environ["OPENAI_API_KEY"] = "dummy_key"
os.environ["URL"] = "http://localhost:11434/v1"
os.environ["MODEL"] = "llama3.2"

try:
    from app.openai_client import OpenAIClient
    client = OpenAIClient()
    
    print(f"Client initialized with model: {client.model}")
    print(f"Base URL in client: {client.client.base_url}")
    
    if str(client.client.base_url) == "http://localhost:11434/v1/":
        print("SUCCESS: Python client base URL correctly set.")
    else:
        print(f"FAILURE: Python client base URL is {client.client.base_url}")

    if client.model == "llama3.2":
        print("SUCCESS: Python client model correctly set.")
    else:
        print(f"FAILURE: Python client model is {client.model}")

except Exception as e:
    print(f"ERROR: {e}")
