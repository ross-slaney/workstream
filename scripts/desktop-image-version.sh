#!/usr/bin/env bash
# Prints the version of the desktops' image a deployment makes its desktops from: the newest one a release
# baked, and no newer than the release being deployed when one is named. A rollback therefore gets the image
# its release ran on, as long as that version is still kept.
#
#   scripts/desktop-image-version.sh           the newest release's image (a stage with no image of its own)
#   scripts/desktop-image-version.sh 0.2.1     the newest that is not newer than 0.2.1 (a release)
#
# Versions 0.0.x are staging's own bakes (.github/workflows/staging.yml) and are never chosen here.
# Prints nothing, and fails, when the gallery has no such version.
set -euo pipefail
most="${1:-}"
versions=$(az sig image-version list -g workstream --gallery-name workstream --gallery-image-definition machine \
  --query "[?provisioningState=='Succeeded'].name" -o tsv | grep -v '^0\.0\.' | sort -V || true)
chosen=""
for version in $versions; do
  if [ -z "$most" ] || [ "$(printf '%s\n%s\n' "$version" "$most" | sort -V | tail -n 1)" = "$most" ]; then chosen="$version"; fi
done
[ -n "$chosen" ] || { echo "The gallery has no desktops' image${most:+ for release $most}. A release bakes one." >&2; exit 1; }
echo "$chosen"
