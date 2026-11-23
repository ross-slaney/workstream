# Workstream

Workstream gives each task its own cloud desktop with an AI agent working on it. A person writes what they want done, a Linux desktop starts for that task, and the agent does the work there: it opens the browser, edits files in VS Code and runs commands in a terminal. The person watches the screen live in their browser, talks to the agent beside it, and takes the mouse whenever they want. Up to four tasks fit on one screen, side by side.

A team shares its tasks and its environments. One task can also be shared with someone outside the team, by email, for them to watch, to write to its agent too, or to use its desktop as well, for as long as the person sharing it chooses. An environment is what a desktop starts with: which apps, and optionally a repository, a setup script, variables and instructions for the agent. It can be kept in the repository as `workstream.yml`. A desktop has what a project's whole stack needs (Docker, .NET, Node, Java, the Android SDK and its emulator), and a container on it reaches the desktop as `host.docker.internal`, as on a Mac ([docs/architecture.md](docs/architecture.md), "A project's whole stack on a desktop"). Someone who signs in with GitHub picks the environment's repository from their own, and the desktop of each task they start pushes and opens pull requests as them; its agent finishes the work as a branch and a pull request ([docs/core-model.md](docs/core-model.md), "A desktop's GitHub token").

A desktop runs while someone is using it or its agent is working. One that nobody has used for half an hour is paused: its machine is released and its files are kept, and it resumes in about half a minute when someone clicks it or writes to its agent. One that stays paused for a week is archived: its disk is saved and the machine deleted, and it comes back in about a minute. A team's administrator sets both times on the team's page. Having a task open in a browser tab is not using it, and only deleting a task deletes its files ([docs/core-model.md](docs/core-model.md#pause-archive-delete)).

Making a machine takes Azure from fifteen seconds to a minute. While someone is in a team's workspace, Workstream keeps one desktop running ahead of them, so the task they start begins on it at once.

Workstream is open source under the [MIT license](LICENSE). You can run it on your own computer to try it, and run your own copy in your Azure subscription for a team.

## Try it on your computer

On your computer each desktop is a Docker container instead of an Azure machine. Everything else works the same way. You need about 10 GB of free disk space and:

- Docker
- the .NET 10 SDK
- Node 22
- an Anthropic API key from [console.anthropic.com](https://console.anthropic.com), for the agent

1. Install the web app's packages and build the desktop's image. The image takes several minutes the first time and is about 3 GB.

   ```bash
   cd client && npm ci && cd ..
   docker build -t workstream-desktop:local desktop
   ```

2. Copy `.env.example` to `.env` and set the agent's model to Anthropic's API.

   ```bash
   cp .env.example .env
   ```

   Then set these two lines in `.env`:

   ```
   WORKSTREAM_MODEL_ENDPOINT=https://api.anthropic.com
   WORKSTREAM_MODEL_KEY=<your Anthropic API key>
   ```

3. Start everything: SQL Server in a container, the API and the web app.

   ```bash
   dotnet run --project apphost
   ```

4. Open http://localhost:8091/sign-in?view=signup and create an account with any email and a password. Nobody needs an inbox on your computer.

5. Press **New task**, write what you want done, and watch its desktop start.

The API is at http://localhost:5280. The system administrator is `admin@workstream.test`, with the password in `.env.example`, and reaches `/admin`. To show **Continue with GitHub** on the sign-in page, make a GitHub OAuth app whose callback URL is `http://localhost:5280/sqlos/auth/oidc/callback` and put its two values in `.env` ([infra/README.md](infra/README.md#sign-in-with-github)).

## Run your own in Azure

Your copy runs in your Azure subscription and deploys from your fork of this repository on GitHub. Nothing is made by hand in Azure except the model, if you use Azure's. You need:

- an Azure subscription where you are an Owner and a tenant administrator
- the Azure CLI (`az`) and the GitHub CLI (`gh`), both signed in
- an Anthropic API key, or Claude deployed in Microsoft Foundry ([infra/README.md](infra/README.md#models))
- an email address for the system administrator

It costs about $100 to $125 a month in Azure with nobody using it, and then each desktop's hours and disk ([infra/README.md](infra/README.md#what-it-costs-with-nobody-using-it)). It runs in West US 3.

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

7. Optionally, let people sign in with GitHub, so their desktops can push and open pull requests as them. Make a GitHub OAuth app with the two addresses from step 5, set its two values on your fork, and deploy the same release again. [infra/README.md](infra/README.md#sign-in-with-github) has the steps.

To change your copy later, merge into `main` and publish the next version ([Ship it](#ship-it)).

## What is in this repository

| Folder | What it is |
| --- | --- |
| `api/` | The API: .NET 10 with SqlOS for sign-in and authorization. One process is the REST API, the relay that carries a desktop's display to the people watching, the gateway every agent's model calls go through, and the background jobs that make and remove desktops. |
| `client/` | The web app: Expo and expo-router, rendered on the server for public pages and in the browser for signed-in ones. The landing page, the docs (`/docs`, written in `client/content/docs`), sign-in, the workspace and the system administrator's page. |
| `cli/` | The command line: start and follow tasks from a terminal, as yourself, after signing in through the browser. Node 22 and nothing else. |
| `desktop/` | The desktop: the scripts that make the machine image, and the agent that runs on it (the Claude Agent SDK with its own window on the desktop). |
| `apphost/` | The .NET Aspire AppHost that runs the whole product on a laptop. |
| `infra/` | Bicep for everything in Azure. |
| `tests/` | Journeys through the API, with allow and deny cases for each kind of caller. |
| `docs/` | [The core model](docs/core-model.md) (who can do what to what, in SqlOS terms) and [how the pieces fit](docs/architecture.md). |

## Start a task from a terminal

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

One test talks to Azure itself and is skipped unless it is told where: `AzureSavedSetupLive` makes a machine from the desktops' image in a resource group of its own, saves its disk and makes a second machine from the save ([infra/README.md](infra/README.md), "What it costs when people use it").

To try a running stack from outside, the way a browser would, including the desktop's display through the relay:

```bash
node scripts/try-stack.mjs --api http://localhost:5280 --web http://localhost:8091
```

It works against the local stack and against staging, which sign in with a password. Production signs in by emailed code, or with GitHub where its OAuth client is set ([infra/README.md](infra/README.md), "Sign in with GitHub").

## Ship it

Production ships only from a published GitHub release. Merging to `main` deploys nothing. The version is `client/app.json` → `expo.version`, mirrored in `client/package.json`; publishing the release `vX.Y.Z` runs `.github/workflows/release.yml`, which bakes the desktops' image when `desktop/` changed since the previous release, then builds both images from the tag's commit, deploys `infra/main.bicep`, and checks that both origins serve that exact commit.

[infra/README.md](infra/README.md) has what Azure needs once (the CI identity, the models), what it costs, and how to read the database.

## Models

Every model call an agent makes goes through the API's gateway (`api/Model/ModelGateway.cs`). A desktop never holds a model key. It holds only its own key, which reaches its own task and nothing else. The gateway sends each call on to the model endpoint you set, counts its tokens, and charges them in dollars against the team's monthly allowance. When the allowance is used up, the team's agents stop until a system administrator raises it.

The endpoint is either Anthropic's API (`https://api.anthropic.com` and an Anthropic API key) or Claude deployed in Microsoft Foundry ([infra/README.md](infra/README.md#models)). Both take the same requests. With Foundry, `./scripts/dev-env.sh` writes its endpoint and key into `.env` for your computer. There is no payment in Workstream yet, so an allowance is a limit, not a bill.
