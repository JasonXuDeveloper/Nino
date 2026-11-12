#!/usr/bin/env bash
# Commits the given paths as github-actions[bot] and pushes the commit to main, rebasing on a main that moved
# (report.yml's README updates: the .NET numbers in publish, the build numbers in publish-generator). Run from a
# checkout of origin/main with credentials. Nothing to commit is no error; a push that fails three times is a warning.
#
# usage: push_main.sh <commit message> <path>...
set -euo pipefail
message=$1
shift
git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
git add -- "$@"
if git diff --cached --quiet; then
  echo "⏭️ Nothing changed: $message"
  exit 0
fi
git commit -m "$message"
for _ in 1 2 3; do
  if git push origin HEAD:main; then
    echo "✅ Pushed: $message"
    exit 0
  fi
  git pull --rebase origin main
done
echo "::warning::Could not push to main: $message"
