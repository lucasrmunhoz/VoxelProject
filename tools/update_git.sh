#!/usr/bin/env bash
# Atualiza o repo do projeto:
# 1) Duplica todos os .cs de Assets/_Scripts para .txt (recursivo)
# 2) Auto-stash -> pull --rebase -> stash pop
# 3) Commit e push (pede mensagem)

set -e

PROJ="/home/lucas/myunity/VoxelNightmarePrototype"
SCRIPTS_DIR="$PROJ/Assets/_Scripts"
LOG="$PROJ/tools/update_git_last.log"

cd "$PROJ"

echo "==== Início: $(date) ====" | tee "$LOG"

# -------------------------------
# 1) Duplicação .cs -> .txt
# -------------------------------
echo "== Duplicação .cs -> .txt ==" | tee -a "$LOG"

dup_fail=0
copied=0

if [ -d "$SCRIPTS_DIR" ]; then
  while IFS= read -r -d '' src; do
    dest="${src%.cs}.txt"
    if cp -f -- "$src" "$dest"; then
      copied=$((copied+1))
    else
      dup_fail=1
      echo "Falha ao copiar: $src" | tee -a "$LOG"
    fi
  done < <(find "$SCRIPTS_DIR" -type f -name "*.cs" -print0)

  if [ $dup_fail -eq 0 ]; then
    echo "processo de duplicacao para txt feito com sucesso ($copied arquivos copiados/atualizados)" | tee -a "$LOG"
  else
    echo "processo falhou" | tee -a "$LOG"
  fi
else
  echo "Aviso: pasta não encontrada: $SCRIPTS_DIR" | tee -a "$LOG"
fi

# -------------------------------
# 2) Fluxo Git com auto-stash
# -------------------------------

# Perguntar mensagem de commit
if [ -z "$1" ]; then
  read -p "Mensagem do commit: " MSG
else
  MSG="$1"
fi
[ -z "$MSG" ] && MSG="update"

# Função: há mudanças locais?
dirty_worktree() {
  # modificações não staged?
  if ! git diff --quiet; then return 0; fi
  # modificações staged?
  if ! git diff --cached --quiet; then return 0; fi
  # arquivos não rastreados?
  if [ -n "$(git ls-files --others --exclude-standard)" ]; then return 0; fi
  return 1
}

STASH_NAME="auto-stash: updater $(date +%Y-%m-%d_%H-%M-%S)"
NEEDS_STASH=0

if dirty_worktree; then
  echo -e "\n== Existem mudanças locais; salvando em stash temporário ==" | tee -a "$LOG"
  git stash push -u -m "$STASH_NAME" | tee -a "$LOG"
  NEEDS_STASH=1
fi

echo -e "\n== Pull (rebase) ==" | tee -a "$LOG"
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

