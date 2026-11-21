# Workstream

Workstream gives each task its own cloud desktop with an AI agent working on it. A person writes what they want done, a Linux desktop starts for that task, and the agent does the work there: it opens the browser, edits files in VS Code and runs commands in a terminal. The person watches the screen live in their browser, talks to the agent beside it, and takes the mouse whenever they want. Up to four tasks fit on one screen, side by side.

A team shares its tasks and its environments. One task can also be shared with someone outside the team, by email, for them to watch, to write to its agent too, or to use its desktop as well, for as long as the person sharing it chooses. An environment is what a desktop starts with: which apps, and optionally a repository, a setup script, variables and instructions for the agent.

Making a machine takes Azure from fifteen seconds to a minute. While someone is in a team's workspace, Workstream keeps one desktop running ahead of them, so the task they start begins on it at once.

## What is in this repository

| Folder | What it is |
| --- | --- |
| `api/` | The API: .NET 10 with SqlOS for sign-in and authorization. One process is the REST API, the relay that carries a desktop's display to the people watching, the gateway every agent's model calls go through, and the background jobs that make and remove desktops. |
| `client/` | The web app: Expo and expo-router, rendered on the server for public pages and in the browser for signed-in ones. The landing page, sign-in, the workspace and the system administrator's page. |
| `desktop/` | The desktop: the scripts that make the machine image, and the agent that runs on it (the Claude Agent SDK with its own window on the desktop). |
| `apphost/` | The .NET Aspire AppHost that runs the whole product on a laptop. |
| `infra/` | Bicep for everything in Azure. |
| `tests/` | Journeys through the API, with allow and deny cases for each kind of caller. |
| `docs/` | [The core model](docs/core-model.md) (who can do what to what, in SqlOS terms) and [how the pieces fit](docs/architecture.md). |

## Run it locally

You need the .NET 10 SDK, Node 22 and Docker.

```bash
cd client && npm ci && cd ..
docker build -t workstream-desktop:local desktop
dotnet run --project apphost
```

The web app is at http://localhost:8091 and the API at http://localhost:5280. Create an account at `/sign-in?view=signup` with any email and a password; locally nobody needs an inbox. The system administrator is `admin@workstream.test` with the password in `.env.example`, and reaches `/admin`.

Locally a desktop is a Docker container made from `desktop/Dockerfile`, which runs the same two scripts the Azure image is baked with. The first build takes several minutes and about 3 GB.

The agent needs a model. Run `./scripts/dev-env.sh` once, signed in to Azure: it writes the Foundry endpoint and key into `.env`, which the AppHost reads. Without them everything else works and the agent says models are not set up.

## Checks

```bash
cd client && npm run check                 # types and tests
cd desktop/agent && npm test               # the agent's tests
dotnet test tests/Workstream.Tests         # journeys through the API (needs Docker for SQL Server)
az bicep build --file infra/main.bicep --stdout > /dev/null
```

`.github/workflows/ci.yml` runs the same on every pull request and every push to `main`.

To try a running stack from outside, the way a browser would, including the desktop's display through the relay:

```bash
node scripts/try-stack.mjs --api http://localhost:5280 --web http://localhost:8091
```

It works against the local stack and against staging, which sign in with a password. Production signs in by emailed code.

## Ship it

Production ships only from a published GitHub release. Merging to `main` deploys nothing. The version is `client/app.json` → `expo.version`, mirrored in `client/package.json`; publishing the release `vX.Y.Z` runs `.github/workflows/release.yml`, which bakes the desktops' image when `desktop/` changed since the previous release, then builds both images from the tag's commit, deploys `infra/main.bicep`, and checks that both origins serve that exact commit.

[infra/README.md](infra/README.md) has what Azure needs once (the CI identity, the models), what it costs, and how to read the database.

## Models

Every model call an agent makes goes through the API's gateway (`api/Model/ModelGateway.cs`) to Workstream's own Microsoft Foundry resource. A desktop never holds a model key: it holds only its own key, which lets it reach its own task and nothing else. The gateway counts each call's tokens and charges them, in dollars, against the team's monthly allowance; when the allowance is used up the team's agents stop until a system administrator raises it.

There is no bring-your-own-key and no payment yet. An allowance is a limit, not a bill.
