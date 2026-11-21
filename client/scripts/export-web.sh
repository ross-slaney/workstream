#!/usr/bin/env sh
# Exports the web app (Expo's server output: every page rendered per request, plus the API routes) into
# dist/, which Dockerfile packages. Run from client/:
#   EXPO_PUBLIC_API_URL=https://api.example.com npm run export:web
# The API's origin is inlined into the bundle, so each environment exports its own.
# In CI, EXPO_METRO_CACHE_RESTORE_DIR and EXPO_METRO_CACHE_OUTPUT_DIR carry Metro's cache between runs.
set -eu
[ -n "${EXPO_PUBLIC_API_URL:-}" ] || { echo "EXPO_PUBLIC_API_URL is required" >&2; exit 1; }
export CI=1 EXPO_PUBLIC_SQLOS_CLIENT_ID="${EXPO_PUBLIC_SQLOS_CLIENT_ID:-workstream-app}"
rm -rf dist
npx expo export --platform web --output-dir dist
