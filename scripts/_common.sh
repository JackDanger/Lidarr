#!/usr/bin/env bash
# Shared config + helpers for the lidarr ops scripts. Source this from each.
# Variables can be overridden via env: HOST=foo CT=200 ./scripts/lidarr-log

set -euo pipefail

: "${HOST:=diligence}"                       # SSH host running Proxmox
: "${CT:=163}"                               # LXC container ID running Lidarr
: "${LIDARR_URL:=https://lidarr/api/v1}"     # API base reachable from this machine
: "${LIDARR_LOG:=/mnt/LidarrData/logs/lidarr.txt}"
: "${LIDARR_REPO:=/mnt/LidarrData/lidarr}"
: "${LIDARR_CONFIG:=/var/lib/lidarr/config.xml}"

# API key cache: avoid hitting the container every invocation.
_KEY_CACHE="$HOME/.cache/lidarr-apikey"

lidarr_apikey() {
  if [[ -s "$_KEY_CACHE" ]]; then
    cat "$_KEY_CACHE"
    return
  fi
  mkdir -p "$(dirname "$_KEY_CACHE")"
  ssh "$HOST" "pct exec $CT -- grep -oP '(?<=<ApiKey>)[^<]+' $LIDARR_CONFIG" \
    | tr -d '\r\n' | tee "$_KEY_CACHE"
  chmod 600 "$_KEY_CACHE"
}

# Run a command inside the container. Quoting via single arg to avoid escape hell.
ct_exec() {
  ssh "$HOST" "pct exec $CT -- bash -c $(printf '%q' "$*")"
}

# Hit the Lidarr API. First arg = path (with leading /), remaining = curl args.
lidarr_curl() {
  local path="$1"; shift
  curl -sS --fail-with-body "$LIDARR_URL$path" \
    -H "X-Api-Key: $(lidarr_apikey)" \
    "$@"
}

# Colors (only when stdout is a tty)
if [[ -t 1 ]]; then
  C_RED=$'\033[31m'; C_YEL=$'\033[33m'; C_GRN=$'\033[32m'
  C_CYA=$'\033[36m'; C_DIM=$'\033[2m'; C_RST=$'\033[0m'
else
  C_RED=''; C_YEL=''; C_GRN=''; C_CYA=''; C_DIM=''; C_RST=''
fi
