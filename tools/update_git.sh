#!/usr/bin/env bash
# Atualiza o repo do projeto:
# 1) Duplica todos os .cs de Assets/_Scripts para .txt (recursivo)
# 2) Gera/atualiza INDEX.md com links RAW para todos os .txt
# 3) Pergunta mensagem de commit
# 4) Auto-stash -> pull --rebase -> stash pop -> add/commit/push

set -e

PROJ="/home/lucas/myunity/VoxelNightmarePrototype"
SCRIPTS_DIR="$PROJ/Assets/_Scripts"
INDEX_FILE="$SCRIPTS_DIR/INDEX.md"
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
# 2) Gerar/atualizar INDEX.md (links RAW)
# -------------------------------
echo -e "\n== Gerando INDEX.md de links RAW ==" | tee -a "$LOG"

# Detectar owner/repo a partir do remote
REMOTE_URL="$(git config --get remote.origin.url || true)"
OWNER=""
REPO=""

if [[ "$REMOTE_URL" =~ ^git@github\.com:([^/]+)/([^\.]+)\.git$ ]]; then
  OWNER="${BASH_REMATCH[1]}"
  REPO="${BASH_REMATCH[2]}"
elif [[ "$REMOTE_URL" =~ ^https://github\.com/([^/]+)/([^\.]+)\.git$ ]]; then
  OWNER="${BASH_REMATCH[1]}"
  REPO="${BASH_REMATCH[2]}"
fi

# Fallback seguro (se parsing falhar)
[ -z "$OWNER" ] && OWNER="lucasrmunhoz"
[ -z "$REPO" ] && REPO="VoxelProject"

# Branch atual (fallback para main)
BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)"
[ -z "$BRANCH" ] && BRANCH="main"

RAW_BASE="https://raw.githubusercontent.com/$OWNER/$REPO/$BRANCH/Assets/_Scripts"
TREE_LINK="https://github.com/$OWNER/$REPO/tree/$BRANCH/Assets/_Scripts"

# Criar/limpar arquivo
{
  echo "# Scripts RAW Index"
  echo
  echo "- **Árvore no GitHub**: $TREE_LINK"
  echo "- **Gerado em**: $(date '+%Y-%m-%d %H:%M:%S')"
  echo
} > "$INDEX_FILE"

if [ -d "$SCRIPTS_DIR" ]; then
  # Entrar na pasta para facilitar paths relativos
  pushd "$SCRIPTS_DIR" >/dev/null

  # Vamos agrupar por diretório (ordem determinística)
  current_dir="__NONE__"

  # Encontrar todos .txt, ordenar, e escrever seções
  while IFS= read -r -d '' rel; do
    # Ex.: ./Voxels/BaseVoxel.txt  -> DIR="Voxels"  FILE="BaseVoxel.txt"
    #      ./BaseRoomGenerator.txt -> DIR="."       FILE="BaseRoomGenerator.txt"
    clean="${rel#./}"
    DIR="$(dirname "$clean")"
    FILE="$(basename "$clean")"

    # Se mudou de diretório, abre um cabeçalho
    if [ "$DIR" != "$current_dir" ]; then
      echo >> "$INDEX_FILE"
      if [ "$DIR" = "." ]; then
        echo "## (raiz de _Scripts)" >> "$INDEX_FILE"
      else
        echo "## $DIR" >> "$INDEX_FILE"
      fi
      echo >> "$INDEX_FILE"
      current_dir="$DIR"
    fi

    # Monta URL RAW
    if [ "$DIR" = "." ]; then
      RAW_URL="$RAW_BASE/$FILE"
    else
      RAW_URL="$RAW_BASE/$DIR/$FILE"
    fi

    echo "- [$FILE]($RAW_URL)" >> "$INDEX_FILE"
  done < <(find . -type f -name "*.txt" -print0 | LC_ALL=C sort -z)

  popd >/dev/null
  echo "INDEX.md gerado em: $INDEX_FILE" | tee -a "$LOG"
else
  echo "Aviso: não foi possível gerar INDEX.md (pasta não encontrada: $SCRIPTS_DIR)" | tee -a "$LOG"
fi

# -------------------------------
# 3) Mensagem de commit
# -------------------------------
if [ -z "$1" ]; then
  read -p "Mensagem do commit: " MSG
else
  MSG="$1"
fi
[ -z "$MSG" ] && MSG="update"

# -------------------------------
# 4) Fluxo Git com auto-stash
# -------------------------------
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
if ! git pull --rebase origin "$BRANCH" | tee -a "$LOG"; then
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

