#!/usr/bin/env bash
# Installs Ubuntu packages on a runner (arguments go to apt-get install): apt-get update (once per job) and install,
# each try cut off after eight minutes, three tries. A mirror that stopped answering once held a job's install for its
# whole 30 minutes; another served 200 KB/s. APT_ARCHIVES, when set, is the directory the .deb files are downloaded
# to and taken from (the action's cache).
set -euo pipefail
opts=(-q -o Acquire::Retries=3 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30)
if [[ -n ${APT_ARCHIVES:-} ]]; then
  mkdir -p "$APT_ARCHIVES/partial"
  opts+=(-o "Dir::Cache::Archives=$APT_ARCHIVES")
fi
marker="${RUNNER_TEMP:-/tmp}/apt-updated"
for try in 1 2 3; do
  if { [[ -f $marker ]] || sudo timeout 480 apt-get "${opts[@]}" update; } &&
    sudo timeout 480 apt-get "${opts[@]}" install -y "$@"; then
    touch "$marker"
    # The cache keeps the .deb files only: partial/ and the lock belong to apt and are not readable by the runner.
    [[ -z ${APT_ARCHIVES:-} ]] || sudo rm -rf "$APT_ARCHIVES/partial" "$APT_ARCHIVES/lock"
    exit 0
  fi
  echo "::warning::apt-get failed (try $try of 3)"
  sleep $((try * 10))
done
exit 1
