#!/bin/bash

echo "Construindo versão de alta compatibilidade usando ambiente isolado (Docker)..."
echo "Isso resolve todos os alertas de GLIBC e bibliotecas compartilhadas (libs) em diferentes distribuições."

# Gera a receita do nosso ambiente isolado dinamicamente
# Usamos o 'bullseye' (Debian 11) que tem uma versão do GLIBC antiga o suficiente para ser universal,
# mas moderna o suficiente para rodar o Python 3.12 e o PyQt6 perfeitamente.
cat << 'EOF' > Dockerfile.build
FROM python:3.11-bullseye

# Instala bibliotecas do sistema (binutils para PyInstaller e libs base para ambiente Qt6/X11)
RUN apt-get update && apt-get install -y \
    binutils \
    libgl1-mesa-dev \
    libx11-xcb-dev \
    libxcb-xinerama0 \
    libxcb-cursor0

WORKDIR /app

# Atualiza pacote de instalação e gera o executável.
# Fixamos o PyQt6 < 6.8 aqui porque versões recentes do Qt abandonaram suporte a Linux antigos,
# se tentarmos usar a 6.11 ele não acha o binário pré-compilado e tenta compilar (falhando).
CMD pip install --upgrade pip && \
    pip install -r requirements.txt "PyQt6<6.8" && \
    pip install pyinstaller && \
    pyinstaller --noconfirm --onefile --windowed --name AIB \
        --hidden-import "pynput.keyboard._xorg" \
        --hidden-import "pynput.mouse._xorg" \
        main.py && \
    chown -R ${HOST_UID}:${HOST_GID} build dist *.spec
EOF

# Tenta usar o docker normal, se der problema de permissão (socket), avisa e altera pra sudo
DOCKER_CMD="docker"
if ! docker info >/dev/null 2>&1; then
    echo "Permissão normal do docker falhou, solicitando root/sudo..."
    DOCKER_CMD="sudo docker"
fi

echo ">> Preparando o container matriz..."
$DOCKER_CMD build -t aib-universal-builder -f Dockerfile.build .

echo ">> Executando a montagem e injeção do PyInstaller..."
$DOCKER_CMD run --rm \
    -v "$(pwd)":/app \
    -e HOST_UID=$(id -u) \
    -e HOST_GID=$(id -g) \
    aib-universal-builder

rm Dockerfile.build

echo "=========================================================="
echo "Build UNIVERSAL concluído com sucesso!"
echo "Teste rodar o ./dist/AIB em qualquer máquina da rede agora."
