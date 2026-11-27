#!/usr/bin/env bash
# check-worktree.sh — READ-ONLY review helper for agent worktrees (never touches index/branches).
# Usage (run from repo root or anywhere inside the repo):
#   scripts/check-worktree.sh <worktree-path>   detail view (Hard Rule 10 pre-check / review)
#   scripts/check-worktree.sh --all             one line per worktree + cross-worktree overlap check
# Env: BASE=develop (compare branch), MAX=40 (max listed files in detail view)
# Exit: 0 ok, 1 usage/path error, 2 secret-like file found (Hard Rule 4).
set -euo pipefail

BASE="${BASE:-develop}"
MAX="${MAX:-40}"
SECRET_RE='(^|/)(\.env(\.[^/]*)?|[^/]*\.tfvars|[^/]*\.tfstate[^/]*|[^/]*kubeconfig[^/]*|[^/]*\.pem|[^/]*\.key)$'
rc=0

die() { echo "error: $*" >&2; exit 1; }

# Tracked changes vs HEAD + untracked (non-ignored) files, one path per line.
changed_files() {
  { git -C "$1" diff --name-only HEAD; git -C "$1" ls-files --others --exclude-standard; } | sort -u
}

secrets_in() { grep -E "$SECRET_RE" | grep -vE '(^|/)\.env\.example$' || true; }

ahead_behind() {  # prints "+ahead/-behind" vs BASE
  local c; c=$(git -C "$1" rev-list --left-right --count "$BASE...HEAD" 2>/dev/null || echo "? ?")
  set -- $c; echo "+$2/-$1"
}

check_secrets() {  # $1 = label, stdin = file list
  local s; s=$(secrets_in)
  if [ -n "$s" ]; then echo "  !! secret-like files ($1): $(echo $s)"; rc=2; fi
}

detail() {
  local wt="$1"
  [ -d "$wt" ] || die "path does not exist: $wt"
  git -C "$wt" rev-parse --is-inside-work-tree >/dev/null 2>&1 || die "not a git worktree: $wt"
  local branch files n untracked
  branch=$(git -C "$wt" branch --show-current)
  files=$(changed_files "$wt")
  n=$(printf '%s' "$files" | grep -c . || true)
  echo "=== $wt  [$branch @ $(git -C "$wt" rev-parse --short HEAD), $(ahead_behind "$wt") vs $BASE]"
  [ "$branch" = "develop" ] || [ "$branch" = "main" ] && echo "  !! on $branch — Hard Rule 1: work in a feature worktree"

  echo "--- status ($n changed)"
  git -C "$wt" status --short --untracked-files=all | head -n "$MAX"
  [ "$n" -gt "$MAX" ] && echo "  ... (+$((n - MAX)) more)"

  echo "--- tracked diff vs HEAD"
  local stat; stat=$(git -C "$wt" diff HEAD --stat | tail -n 1); echo "${stat:-  (none)}"
  untracked=$(git -C "$wt" ls-files --others --exclude-standard)
  if [ -n "$untracked" ]; then
    echo "--- untracked: $(printf '%s\n' "$untracked" | grep -c .) files, $(cd "$wt" && printf '%s\n' "$untracked" | tr '\n' '\0' | xargs -0 cat 2>/dev/null | wc -l | tr -d ' ') lines"
  fi

  case "$branch" in *-proto) ;; *) printf '%s\n' "$files" | grep -q '^proto/' && echo "  !! proto/ changed outside a *-proto branch (Hard Rule 2)";; esac
  check_secrets "$wt" <<<"$files"
  return 0
}

all() {
  TMP=$(mktemp); trap 'rm -f "$TMP"' EXIT
  local main_root wt branch first=1
  printf '%-42s %-32s %-11s %s\n' WORKTREE BRANCH "vs $BASE" CHANGES
  while read -r key val; do
    case "$key" in
      worktree) wt="$val" ;;
      branch)
        branch="${val#refs/heads/}"
        if [ $first -eq 1 ]; then main_root="$wt"; first=0; continue; fi   # skip main checkout
        local files mod unt rel="${wt#"$main_root"/}"
        files=$(changed_files "$wt")
        mod=$(git -C "$wt" diff --name-only HEAD | grep -c . || true)
        unt=$(git -C "$wt" ls-files --others --exclude-standard | grep -c . || true)
        printf '%-42s %-32s %-11s %s\n' "$rel" "$branch" "$(ahead_behind "$wt")" "${mod} modified, ${unt} untracked"
        printf '%s\n' "$files" | grep . | sed "s|^|$rel	|" >> "$TMP" || true
        check_secrets "$rel" <<<"$files"
        ;;
    esac
  done < <(git worktree list --porcelain)

  local overlap; overlap=$(cut -f2 "$TMP" | sort | uniq -d)
  if [ -n "$overlap" ]; then
    echo "!! files changed in more than one worktree (merge-conflict risk, check disjoint ownership):"
    for f in $overlap; do echo "   $f <- $(awk -F'\t' -v f="$f" '$2==f{print $1}' "$TMP" | tr '\n' ' ')"; done
  else
    echo "ok: no file is changed in more than one worktree"
  fi
}

case "${1:-}" in
  --all|-a) all ;;
  ""|-h|--help) sed -n '2,7p' "$0"; exit 1 ;;
  *) detail "$1" ;;
esac
exit "$rc"
