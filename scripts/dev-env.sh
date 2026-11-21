#!/usr/bin/env bash
# Writes the agent's model settings into the root .env for local development: the endpoint and key of
# Workstream's Foundry resource (infra/README.md, "Models"). Run it signed in with `az login`. The AppHost reads
# .env, which git ignores. Other lines of an existing .env are kept.
#
#   GROUP  the Foundry resource's group (default workstream-ai)
#   NAME   the Foundry resource (default: the only one in the group)
set -euo pipefail
cd "$(dirname "$0")/.."

GROUP=${GROUP:-workstream-ai}
NAME=${NAME:-$(az cognitiveservices account list -g "$GROUP" --query "[0].name" -o tsv)}
[ -n "$NAME" ] || { echo "No Foundry resource in $GROUP. Make one first (infra/README.md, \"Models\")." >&2; exit 1; }

endpoint="https://$NAME.services.ai.azure.com/anthropic"
key=$(az cognitiveservices account keys list -g "$GROUP" -n "$NAME" --query key1 -o tsv)

touch .env
grep -v -E '^WORKSTREAM_MODEL_(ENDPOINT|KEY)=' .env > .env.next || true
{
  echo "WORKSTREAM_MODEL_ENDPOINT=$endpoint"
  echo "WORKSTREAM_MODEL_KEY=$key"
} >> .env.next
mv .env.next .env
chmod 600 .env
echo "Wrote the model endpoint ($endpoint) and its key to .env. Restart the AppHost."
