---
name: run-local-stack
description: Run the whole Workstream product on this computer (SQL Server, the API, the web app, and desktops as Docker containers) and use it in a browser. Use whenever you need the app running locally: to check a change in the real app, start a task and watch its desktop, seed a team, or capture screens.
---

# Run Workstream locally

```bash
cd client && npm ci && cd ..                       # once
docker build -t workstream-desktop:local desktop   # once, and again after a change under desktop/
dotnet run --project apphost
```

The AppHost starts SQL Server in a container that keeps its data, the API on http://localhost:5280 and the web app on http://localhost:8091. It prints its dashboard's address with a sign-in token. Ports and the rest are in `.env.example`; copy it to `.env` to change one.

## Signing in

- Create an account at http://localhost:8091/sign-in?view=signup with any `*.test` address and a password. Locally sign-in takes a password, so nobody needs an inbox.
- The system administrator is `admin@workstream.test` with the password in `.env.example`. They reach http://localhost:8091/admin.
- An invitation shows its link in the dialog after you invite someone, because no email is sent locally. Open the link signed out to accept it as a new person.

## Desktops

Locally a desktop is a container named `ws-<task id>` from the image `workstream-desktop:local`. It is the same two scripts the Azure image is baked with, less what a container cannot use (`WORKSTREAM_SLIM` in `desktop/image/install.sh`): no Docker inside, no .NET SDK, no LibreOffice.

- A new task's desktop is ready in a few seconds.
- `docker logs ws-<id>` shows the agent's own log. Inside the container, `~/.workstream/` has the display's log, the agent's state and the conversation as the desktop saw it.
- Deleting a task removes its container. `docker ps --filter name=ws-` lists what is running.
- The image is about 3 GB. If Docker's disk is full the build fails with "no space left on device"; clear your own build cache with `docker builder prune`, and leave other projects' containers and volumes alone.

## The agent's model

The agent calls Claude through the API's gateway, which needs Workstream's Foundry endpoint and key:

```bash
./scripts/dev-env.sh
```

It writes `WORKSTREAM_MODEL_ENDPOINT` and `WORKSTREAM_MODEL_KEY` into `.env` from Azure (you must be signed in with `az login`). Restart the AppHost afterwards. Without them everything else works and the agent answers that models are not set up. Never copy that key anywhere else: not into `client/`, a log, an issue or a reply.

## Try the whole thing in one command

```bash
node scripts/try-stack.mjs --api http://localhost:5280 --web http://localhost:8091
```

It signs up a new person, starts a task, waits for the desktop, opens its display through the relay, reads what the agent answered, checks a stranger is refused, then stops, starts and deletes the task. `--keep` leaves the task running and prints how to sign in to look at it.

## Things that catch people out

- The API does not pick up a code change by itself. Stop the AppHost, `dotnet build api`, start it again. The web app reloads on its own.
- The first load of a page is slow while Metro bundles it. Wait and load it again.
- The database survives a restart. To start clean, stop the AppHost and remove the SQL Server container and its volume, both named for this checkout's AppHost.
- The tests (`dotnet test tests/Workstream.Tests`) start their own SQL Server on port 14454 and their own API on 15280, so they run beside this stack.
