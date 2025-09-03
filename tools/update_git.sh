#!/usr/bin/env bash
# Atualiza o repo do projeto e pede mensagem de commit se não for informada

set -e

PROJ="/home/lucas/myunity/VoxelNightmarePrototype"
LOG="$PROJ/tools/update_git_last.log"

cd "$PROJ"

# Perguntar mensagem de commit se não foi passada como argumento
if [ -z "$1" ]; then
  read -p "Mensagem do commit: " MSG
else
  MSG="$1"
fi

{
  date
  echo "== Pull (rebase) =="
  git pull --rebase origin main || true
  echo

  echo "== Add =="
  git add -A
  echo

  echo "== Commit =="
  if git commit -m "$MSG"; then
    echo "Commit feito: $MSG"
  else
    echo "Nenhuma alteração para commitar."
  fi
  echo

  echo "== Push =="
  git push
  echo

  echo "✅ OK: Projeto atualizado!"
} | tee "$LOG"

# Notificação de desktop (mint usa notify-send)
if command -v notify-send >/dev/null 2>&1; then
  notify-send "VoxelProject" "Atualização finalizada: $MSG"
fi

# Pausar no final se rodar em terminal gráfico
read -p "Pressione Enter para fechar..."

