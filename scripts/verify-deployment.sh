#!/usr/bin/env bash
# Checks that the deployed product runs the exact revision. Used by .github/workflows/release.yml after the
# Bicep deployment.
#   scripts/verify-deployment.sh api <api origin> <sha> [minutes to wait for /ready]
#   scripts/verify-deployment.sh web <web origin> <sha> [minutes to wait for /healthz]
set -euo pipefail
kind=$1
case "$kind" in
  api)
    api=$2 sha=$3 minutes=${4:-10}
    deadline=$(( $(date +%s) + minutes * 60 ))
    until curl -fsS --max-time 15 "$api/ready" 2>/dev/null | jq -e --arg sha "$sha" '.status == "ready" and .revision == $sha' > /dev/null; do
      [ "$(date +%s)" -lt "$deadline" ] || { echo "::error::$api never reported revision $sha as ready"; exit 1; }
      sleep 5
    done
    echo "$(date -u +%T) $api is serving $sha"
    curl -fsS --max-time 30 -o /dev/null "$api/sqlos/auth/.well-known/openid-configuration" || { echo "::error::OpenID discovery failed"; exit 1; }
    [ "$(curl -s --max-time 30 -o /dev/null -w '%{http_code}' "$api/api/me")" = 401 ] || { echo "::error::Anonymous /api/me must return 401"; exit 1; }
    [ "$(curl -s --max-time 30 -o /dev/null -w '%{http_code}' "$api/api/desktop/setup")" = 401 ] || { echo "::error::/api/desktop/setup without a key must return 401"; exit 1; }
    [ "$(curl -s --max-time 30 -o /dev/null -w '%{http_code}' -X POST "$api/api/model/anthropic/v1/messages")" = 401 ] || { echo "::error::The model gateway without a key must return 401"; exit 1; }
    dashboard=$(curl -sL --max-time 30 -o /dev/null -w '%{http_code}' "$api/sqlos")
    [[ "$dashboard" == 403 || "$dashboard" == 404 ]] || { echo "::error::SqlOS's development dashboard must be unavailable (received $dashboard)"; exit 1; }
    ;;
  web)
    origin=$2 sha=$3 minutes=${4:-5}
    deadline=$(( $(date +%s) + minutes * 60 ))
    until curl -fsS --max-time 30 "$origin/healthz" 2>/dev/null | jq -e --arg sha "$sha" '.status == "ok" and .revision == $sha' > /dev/null; do
      [ "$(date +%s)" -lt "$deadline" ] || { echo "::error::$origin never served revision $sha"; exit 1; }
      sleep 5
    done
    for route in / /sign-in /invite; do
      curl -fsS --max-time 30 -o /dev/null "$origin$route" || { echo "::error::$route failed"; exit 1; }
    done
    echo "$(date -u +%T) $origin is serving $sha"
    ;;
  *) echo "usage: $0 api|web ..." >&2; exit 2 ;;
esac
