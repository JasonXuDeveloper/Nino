#!/usr/bin/env bash
# Installs Ubuntu packages on a runner (arguments go to apt-get install): apt-get update (once per job) and install, each try cut off after five
# minutes and tried three times. A mirror that stops answering once held a job's install for its whole 30 minutes.
set -euo pipefail
opts=(-q -o Acquire::Retries=3 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30)
for try in 1 2 3; do
  if { [[ -f ${RUNNER_TEMP:-/tmp}/apt-updated ]] || sudo timeout 300 apt-get "${opts[@]}" update; } &&
    sudo timeout 300 apt-get "${opts[@]}" install -y "$@"; then
    touch "${RUNNER_TEMP:-/tmp}/apt-updated"
    exit 0
  fi
  echo "::warning::apt-get failed (try $try of 3)"
  sleep $((try * 10))
done
exit 1
