---
name: release
description: Ship Workstream to production. Bumps the version (client/app.json, mirrored in client/package.json), opens and squash-merges the version PR, publishes the GitHub release vX.Y.Z, and watches the Release workflow deploy the web app and the API and bake the desktops' image from that tag. Use when asked to release, ship, cut a version, tag, deploy to production, or roll back. Merging to main deploys nothing; the published release is what ships.
---

# Release

Take what is already on `origin/main` to production through a version PR and a published GitHub release. The published release `vX.Y.Z` is what `.github/workflows/release.yml` deploys: both images from the tag's commit, one Bicep deployment of the web app and the API, and the desktops' image when `desktop/` changed since the previous release. No other path reaches production.

Asking to release, ship, cut a version or deploy authorizes the commit, push, PR, merge and `gh release create` for this workflow only. Asking to roll back authorizes `gh workflow run release.yml`.

## Non-negotiables

- Release only `origin/main` plus the version bump. Unfinished work stays out.
- Do not create the tag or the release until the version PR is merged and `origin/main` carries the new version.
- Tag `vX.Y.Z`, title `Workstream X.Y.Z`, published (not a draft, not a prerelease), targeted at `main`. The workflow refuses a tag that is not `v` + `client/app.json`'s `expo.version`, a tag whose commit is not on `main`, and a `client/package.json` version that differs.
- Never retag, move or delete a published tag. If a release is bad, deploy the previous one again and publish a patch.
- The green `Checks` run on the merge commit is the evidence that it works. If it is red, find the cause and fix it first.

## Before a release that changes the desktop or the infrastructure

Try it on staging first: `gh workflow run staging.yml -f action=deploy -f ref=main`, sign in at the address in the run's summary with a password, start a task and watch its desktop. Remove staging afterwards with `-f action=remove`. Production signs in by emailed code, so it cannot be tried without a real inbox.

## 1. Establish the release

```bash
git status --short --branch
git fetch --prune origin
git log --oneline origin/main -20
gh release list --limit 5
gh run list --workflow Checks --branch main --limit 3
```

The version is `client/app.json` → `expo.version`, mirrored in `client/package.json` → `version`. Pick the next one: a patch for fixes, a minor for something people will notice.

## 2. The version PR

```bash
git switch -c release/vX.Y.Z origin/main
# set the version in client/app.json and client/package.json, then:
(cd client && npm install --package-lock-only --no-audit --no-fund)
git commit -am "Release Workstream X.Y.Z"
git push -u origin release/vX.Y.Z
gh pr create --title "Release Workstream X.Y.Z" --body "The version bump for vX.Y.Z."
gh pr checks --watch
gh pr merge --squash --delete-branch
```

Watch the checks before merging; the repository has no required checks, so `--auto` would merge at once.

## 3. Publish and watch

```bash
git fetch origin && git log --oneline origin/main -1
gh release create vX.Y.Z --target main --title "Workstream X.Y.Z" --generate-notes
gh run watch "$(gh run list --workflow Release --limit 1 --json databaseId -q '.[0].databaseId')" --exit-status
```

The run has three jobs: it resolves the tag, deploys the web app and the API and checks both origins serve the tag's commit, then bakes the desktops' image if `desktop/` changed (about half an hour; the builder machine is deleted when it ends, whether it worked or not). Read the log of anything red. Report the two origins from the run's summary.

A fresh installation needs `scripts/setup-azure.sh` run once and the `production` environment's `ADMINISTRATOR_EMAIL`, `MODEL_ENDPOINT` and `MODEL_KEY` (infra/README.md).

## If the run fails

- Fix the cause, then re-run the failed jobs of that same run: `gh run rerun <id> --failed`.
- A fix to the workflow, the Bicep or the bake script needs a new tag, because the run uses the files at the tag's commit. Publish the next patch.
- Never leave a builder behind: `az group list --tag purpose=image-build -o table` should be empty when no release is running.

## Roll back

```bash
gh workflow run release.yml -f tag=vX.Y.Z   # the release to go back to
```

It deploys that release's images again. The desktops' image is not rolled back by this: new desktops use the newest version in the gallery. To go back there too, delete the bad version: `az sig image-version delete -g workstream --gallery-name workstream --gallery-image-definition desktop --gallery-image-version X.Y.Z`.
