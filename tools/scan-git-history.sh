#!/usr/bin/env bash
# Scan all reachable git history for secrets with a pinned gitleaks release.
# Runs from the target repository cwd (worktree or bare mirror). Prints only a
# generic PASS/FAIL; all scanner output, findings, filenames and values stay suppressed.
set -euo pipefail

GITLEAKS_VERSION='8.30.1'
GITLEAKS_SHA256='551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb'
GITLEAKS_URL="https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz"

say_fail() { printf 'history-secrets: FAIL\n'; }
say_pass() { printf 'history-secrets: PASS\n'; }

# Require a real commit and complete history, including in a bare mirror.
export GIT_NO_REPLACE_OBJECTS=1 GIT_NO_LAZY_FETCH=1
git rev-parse --verify 'HEAD^{commit}' >/dev/null 2>&1 || { say_fail; exit 1; }
[[ "$(git rev-parse --is-shallow-repository 2>/dev/null)" == false ]] || { say_fail; exit 1; }

work=$(mktemp -d) || { say_fail; exit 1; }
trap 'rm -f -- "$work/gitleaks.tar.gz" "$work/gitleaks" "$work/scan.toml" "$work/.gitleaksignore"; rmdir -- "$work"' EXIT

# Official pinned Linux x64 binary; extract only gitleaks, on PATH, no artifacts.
# A matching pinned binary already on PATH is used as-is; anything else is replaced
# by a verified download into the scratch dir.
have_pinned=0
if command -v gitleaks >/dev/null 2>&1 \
  && [ "$(gitleaks version 2>/dev/null | tr -d '[:space:]')" = "$GITLEAKS_VERSION" ]; then
  have_pinned=1
fi
if [ "$have_pinned" -eq 0 ]; then
  if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    printf 'Gitleaks %s must be installed on PATH for this platform.\n' "$GITLEAKS_VERSION"
    say_fail
    exit 1
  fi
  curl -fsSL --max-time 120 -o "$work/gitleaks.tar.gz" "$GITLEAKS_URL" \
    || { say_fail; exit 1; }
  printf '%s  %s\n' "$GITLEAKS_SHA256" "$work/gitleaks.tar.gz" | sha256sum -c - >/dev/null 2>&1 \
    || { say_fail; exit 1; }
  tar xzf "$work/gitleaks.tar.gz" -C "$work" gitleaks || { say_fail; exit 1; }
  export PATH="$work:$PATH"
fi

# Trusted scratch config: default rules only; contributor .gitleaks.toml is never read.
printf '[extend]\nuseDefault = true\n' > "$work/scan.toml"
touch "$work/.gitleaksignore"

# CI fetches PR head/merge refs; its full checkout supplies branches and tags.
# Local scans use the refs already prepared for publication.
if [[ "$#" -eq 1 && "$1" == --fetch-pull-refs ]]; then
  if ! git fetch --no-tags --refmap= origin \
    '+refs/pull/*/head:refs/history-scan/pull/*/head' \
    '+refs/pull/*/merge:refs/history-scan/pull/*/merge' >/dev/null 2>&1; then
    say_fail; exit 1
  fi
elif [[ "$#" -ne 0 ]]; then
  say_fail; exit 1
fi

# Include the checked-out commit even if no local ref names it.
if gitleaks git \
  --log-opts='--all HEAD --full-history -m' \
  --config="$work/scan.toml" \
  --redact=100 \
  --ignore-gitleaks-allow \
  --gitleaks-ignore-path="$work/.gitleaksignore" \
  --no-banner --no-color --exit-code=1 \
  . >/dev/null 2>&1; then
  say_pass
  exit 0
fi
say_fail; exit 1
