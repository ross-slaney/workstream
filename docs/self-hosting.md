# Running your own Workstream

This page covers running Workstream in your own Azure subscription, the command line, the checks and how releases work. [infra/README.md](../infra/README.md) has more detail on each Azure piece.

## Run your own in Azure

Your copy runs in your Azure subscription and deploys from your fork of this repository on GitHub. Nothing is made by hand in Azure except the model, if you use Azure's. You need:

- an Azure subscription where you are an Owner and a tenant administrator
- the Azure CLI (`az`) and the GitHub CLI (`gh`), both signed in
- an Anthropic API key, or Claude deployed in Microsoft Foundry ([infra/README.md](../infra/README.md#models))
- an email address for the system administrator

It costs about $100 to $125 a month in Azure with nobody using it, and then each desktop's hours and disk ([infra/README.md](../infra/README.md#what-it-costs-with-nobody-using-it)). It runs in West US 3.

1. Fork this repository on GitHub and clone your fork. GitHub turns Actions off on a new fork, so turn them on in the fork's **Actions** tab.

2. Raise your subscription's core limit. A new subscription has 10 cores in a region, which is two Standard desktops. When this was set up, Azure granted 40 at once and for free:

   ```bash
   SUB=$(az account show --query id -o tsv)
   for name in cores standardDasv7Family; do
     az quota update --resource-name $name --scope /subscriptions/$SUB/providers/Microsoft.Compute/locations/westus3 \
       --limit-object value=40 --resource-type dedicated
   done
   ```

3. Give GitHub the right to deploy to your subscription. Run this from your clone. It makes the GitHub environments `production` and `staging` on your fork and stores an Azure credential in each.

   ```bash
   ./scripts/setup-azure.sh
   ```

4. Tell your fork who the administrator is and which model to use.

   ```bash
   gh variable set ADMINISTRATOR_EMAIL --env production --body "you@example.com"
   gh variable set MODEL_ENDPOINT --env production --body "https://api.anthropic.com"
   gh secret set MODEL_KEY --env production   # paste your Anthropic API key when it asks
   ```

5. Publish a release. Its tag is `v` followed by the version in `client/app.json`, for example `v0.7.1`.

   ```bash
   VERSION=$(node -p "require('./client/app.json').expo.version")
   gh release create "v$VERSION" --title "Workstream $VERSION" --generate-notes
   ```

   This runs `.github/workflows/release.yml`. The first time takes about 45 minutes, because it builds the desktops' machine image. Follow it with `gh run watch`. When it finishes, the run's summary gives your two addresses: the web app at `https://workstream-web.<domain>` and the API at `https://workstream-api.<domain>`.

6. Open the web app and sign in with the administrator's email. Sign-in sends a code by email. The first emails come from a domain Azure manages and may land in spam.

7. Optionally, let people sign in with GitHub, so their desktops can push and open pull requests as them. Make a GitHub OAuth app with the two addresses from step 5, set its two values on your fork, and deploy the same release again. [infra/README.md](../infra/README.md#sign-in-with-github) has the steps.

To change your copy later, merge into `main` and publish the next version ([Releases](#releases)).

## The command line

```bash
node cli/workstream.mjs login --api http://localhost:5280      # opens the browser to allow it; remembered
node cli/workstream.mjs start --team "My team" --env Default "Fix the failing test, then open a pull request"
node cli/workstream.mjs status <task id or link>
node cli/workstream.mjs tasks
```

`login` prints a link and a code and opens the browser, where you sign in and allow the command line. It can then read what you can and start tasks, as you; it cannot change, stop or delete anything, or open a desktop. `start` prints the task's link and waits until its desktop is ready (`--no-wait` to return at once, `-` to read the prompt from standard input, `--json` for a script). `logout` signs it out. What it keeps is in `~/.config/workstream/`. A local Claude Code uses it through the `start-workstream-task` skill to hand a GitHub issue to Workstream.

## Checks

```bash
cd client && npm run check                 # types and tests
cd desktop/agent && npm test               # the agent's tests
node --test cli/*.test.mjs                 # the command line's arguments
dotnet test tests/Workstream.Tests         # journeys through the API (needs Docker for SQL Server)
az bicep build --file infra/main.bicep --stdout > /dev/null
```

`.github/workflows/ci.yml` runs the same on every pull request and every push to `main`.

One test talks to Azure itself and is skipped unless it is told where: `AzureSavedSetupLive` makes a machine from the desktops' image in a resource group of its own, saves its disk and makes a second machine from the save ([infra/README.md](../infra/README.md), "What it costs when people use it").

To try a running stack from outside, the way a browser would, including the desktop's display through the relay:

```bash
node scripts/try-stack.mjs --api http://localhost:5280 --web http://localhost:8091
```

It works against the local stack and against staging, which sign in with a password. Production signs in by emailed code, or with GitHub where its OAuth client is set ([infra/README.md](../infra/README.md), "Sign in with GitHub").

## Releases

Production ships only from a published GitHub release. Merging to `main` deploys nothing. The version is `client/app.json` → `expo.version`, mirrored in `client/package.json`; publishing the release `vX.Y.Z` runs `.github/workflows/release.yml`, which bakes the desktops' image when `desktop/` changed since the previous release, then builds both images from the tag's commit, deploys `infra/main.bicep`, and checks that both origins serve that exact commit.

[infra/README.md](../infra/README.md) has what Azure needs once (the CI identity, the models), what it costs, and how to read the database.

## Models

Every model call from a desktop goes through the API's gateway (`api/Model/ModelGateway.cs`): Workstream's own agent's, and those of any tool a person runs on the desktop, such as OpenCode, which every desktop has ready with no sign-in. A desktop never holds a model key. It holds only its own key, which reaches its own task and nothing else. The gateway sends each call on to the model endpoint you set, counts its tokens, and charges them in dollars against the team's monthly allowance, with the person and the tool it counts to. When the allowance is used up, the team's agents and tools stop until a system administrator raises it.

The endpoint is either Anthropic's API (`https://api.anthropic.com` and an Anthropic API key) or Claude deployed in Microsoft Foundry ([infra/README.md](../infra/README.md#models)). Both take the same requests. With Foundry, `./scripts/dev-env.sh` writes its endpoint and key into `.env` for your computer. There is no payment in Workstream yet, so an allowance is a limit, not a bill.
