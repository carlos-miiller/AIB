#!/bin/bash

# Ativa o ambiente virtual local
source .venv/bin/activate

# Garante que o PyInstaller está instalado
pip install pyinstaller

echo "Iniciando o processo de build do AIB AI..."

# Gera o executável único usando o PyInstaller
# As flags incluem:
# --noconfirm   : Substitui builds anteriores sem perguntar
# --onefile     : Gera um único arquivo executável
# --windowed    : Oculta a janela de terminal do sistema (roda direto no modo gráfico)
# --name AIB    : Define o nome final do binário
pyinstaller --noconfirm --onefile --windowed --name AIB main.py

echo "Build concluído!"
echo "O seu executável está na pasta: dist/"
echo "Não esqueça: você precisa copiar o arquivo .env para a mesma pasta onde for rodar o executável."
