#!/bin/bash
echo "FROM python:3.12-slim-bullseye" > Dockerfile.build
echo "RUN apt-get update && apt-get install -y binutils" >> Dockerfile.build
echo "WORKDIR /app" >> Dockerfile.build

docker build -t aib-builder -f Dockerfile.build .
docker run --rm -v $(pwd):/app -e UID=$(id -u) -e GID=$(id -g) aib-builder bash -c "\
    pip install -r requirements.txt && \
    pip install pyinstaller && \
    pyinstaller --noconfirm --onefile --windowed --name AIB main.py && \
    chown -R \${UID}:\${GID} build dist *.spec"
