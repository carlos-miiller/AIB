#!/bin/bash
# Roda o AI Screen Assistant sem precisar ativar o venv manualmente

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$DIR/.venv/bin/python3" "$DIR/main.py" "$@"
