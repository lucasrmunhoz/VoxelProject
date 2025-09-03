#!/usr/bin/env bash
# Atualiza o repo do projeto: auto-stash -> pull --rebase -> re-aplica -> commit -> push

set -e

PROJ="/home/lucas/myunity/VoxelNightmarePrototype"
LOG="$PROJ/tools/update_git_last.log"

cd "$PROJ"

# Perguntar mensagem de commit se não for passada como argumento
if [ -z "$1" ]; then
  read -p "Mensagem do commit: " MSG
else
  MSG="$1"
fi
[ -z "$MSG" ] && MSG="update"

echo "==== Início: $(date) ====" | tee "$LOG"

# Detectar se há mudanças (tracked ou untracked)
dirty_worktree() {
  # mudanças em arquivos já versionados?
  if ! git diff --quiet; then return 0; fi
  # mudanças staged?
  if ! git diff --cached --quiet; then return 0; fi
  # arquivos não versionados?
  if [ -n "$(git ls-files --others --exclude-standard)" ]; then return 0; fi
  return 1
}

STASH_NAME="auto-stash: updater $(date +%Y-%m-%d_%H-%M-%S)"
NEEDS_STASH=0

if dirty_worktree; then
  echo "== Existem mudanças locais; salvando em stash temporário =="? | tee -a "$LOG"
  git stash push -u -m "$STASH_NAME" | tee -a "$LOG"
  NEEDS_STASH=1
fi

echo -e "\n== Pull (rebase) ==" | tee -a "$LOG"
# não falhar o script se o pull der conflito; avisar e sair limpo
if ! git pull --rebase origin main | tee -a "$LOG"; then
  echo -e "\n❌ Conflito no rebase. Resolva no Git/Unity e rode o app de novo." | tee -a "$LOG"
  read -p "Pressione Enter para fechar..."
  exit 1
fi

if [ "$NEEDS_STASH" -eq 1 ]; then
  echo -e "\n== Reaplicando mudanças locais (stash pop) ==" | tee -a "$LOG"
  if ! git stash pop | tee -a "$LOG"; then
    echo -e "\n⚠️ Conflitos ao reaplicar o stash."
    echo "Abra o Unity/IDE, resolva os conflitos, faça commit e rode o app de novo." | tee -a "$LOG"
    read -p "Pressione Enter para fechar..."
    exit 1
  fi
fi

echo -e "\n== Add e Commit ==" | tee -a "$LOG"
git add -A
if git diff --cached --quiet; then
  echo "Nenhuma alteração para commitar." | tee -a "$LOG"
else
  git commit -m "$MSG" | tee -a "$LOG"
fi

echo -e "\n== Push ==" | tee -a "$LOG"
git push | tee -a "$LOG"

echo -e "\n✅ OK: Projeto atualizado! Mensagem: $MSG" | tee -a "$LOG"

# Notificação de desktop
if command -v notify-send >/dev/null 2>&1; then
  notify-send "VoxelProject" "Atualização finalizada: $MSG"
fi

read -p "Pressione Enter para fechar..."

