# Infrastructure

Everything in Azure is declared here and deployed by `.github/workflows/release.yml`. Nothing is made by hand, with two exceptions written down below: the model, when it is Claude in Microsoft Foundry rather than Anthropic's API, and the GitHub OAuth client people sign in with. The README's "Run your own in Azure" is the short version of this page, in order.

## What is deployed

`main.bicep` is one subscription-scoped deployment in West US 3. (East US 2 was tried first: on the day this was built it left a network-integrated container apps environment waiting for twenty minutes without making it, twice, where West US 3 made one in under four.)

| Group | What | File |
| --- | --- | --- |
| `workstream` | Container registry, Log Analytics, the private network with its NAT gateway, the container apps environment inside that network, Communication Services for email, the gallery that holds the desktops' image (`gallery.bicep`: the definition `machine`, and `machine-hibernate` for desktops that hibernate) | `foundation.bicep` |
| `workstream` | The runtime identity, the SQL server and database | `data.bicep` |
| `workstream` | The API and the web app | `api.bicep`, `web.bicep` |
| `workstream-desktops` | One virtual machine per task, with its disk and network interface. The API makes and removes them; Bicep declares only the API's right to. | `desktops.bicep` |
| `workstream-ai` | The Foundry resource, when the model is Claude in Foundry. Made by hand, see "Models". | |

The two origins are under the container apps environment's own domain: `https://workstream-web.<domain>` and `https://workstream-api.<domain>`. A domain of our own is not set up yet.

### The network

`workstream-net` (10.60.0.0/16) has two subnets. `container-apps` holds the container apps environment. `desktops` holds every task's machine. A desktop has no public address and reaches the internet through the NAT gateway. The subnet's rules admit one thing in, the API's relay to a desktop's display on port 5900 from the `container-apps` subnet, and refuse every other machine on the network, other desktops included. Outbound to the network is refused too. A desktop calls the API at its public address like anything else.

### What the API may do in Azure

The API and the web app run as one user-assigned identity, `workstream-runtime`. It pulls both images (`AcrPull` on the registry), owns the database's schema (it is in the SQL administrators group), and makes desktops: `Virtual Machine Contributor` on the `workstream-desktops` group and on the desktops' subnet, `Disk Snapshot Contributor` on the `workstream-desktops` group for environments' saved setups, and `Reader` on the gallery. It has no other role.

## Setting up a subscription

Once, by an owner of the subscription who is also a tenant administrator, signed in with `az login` and `gh auth login`:

```bash
./scripts/setup-azure.sh
```

For each of the GitHub environments `production` and `staging` it makes an app registration (`workstream-github-production`, `workstream-github-staging`) and gives its service principal Contributor on the subscription, the right to assign exactly the four roles above, and Microsoft Graph `Group.ReadWrite.All` for the SQL administrators groups. It stores each credential as that environment's `AZURE_CREDENTIALS` secret. It is safe to run again, and it is run again when the runtime identity is given a new role, because the CI identity cannot widen what it may assign: an assignment whose condition is out of date is made again.

Then set, on the GitHub environment `production`:

| Name | Kind | What |
| --- | --- | --- |
| `ADMINISTRATOR_EMAIL` | variable | The one address that reaches `/admin`. They sign in by emailed code like everyone else. Changing it moves the role at the next deploy; without it nobody is the administrator. |
| `MODEL_ENDPOINT` | variable | `https://api.anthropic.com`, or a Foundry resource's Anthropic endpoint, `https://<resource>.services.ai.azure.com/anthropic`. |
| `MODEL_KEY` | secret | An Anthropic API key, or the Foundry resource's key. |

Without the two model values the product deploys and agents say models are not set up. The `staging` environment takes the same two model values, and `ADMINISTRATOR_PASSWORD` as a secret.

For signing in with GitHub, the same environment takes two more, made when the OAuth client is (see "Sign in with GitHub" below):

| Name | Kind | What |
| --- | --- | --- |
| `GH_OAUTH_CLIENT_ID` | variable | The OAuth client's Client ID. |
| `GH_OAUTH_CLIENT_SECRET` | secret | A client secret of the OAuth client. |

Without both, the sign-in page shows no GitHub button and people sign in by emailed code, as before.

## Releasing

Publishing the GitHub release `vX.Y.Z` runs `release.yml` (the `release` skill does the whole thing):

1. **Resolve.** The tag must be `v` + `client/app.json`'s version, on `main`, and published.
2. **Desktops' image.** When `desktop/` changed since the previous release, or the gallery's definition is empty, the job makes a Debian 13 machine in a group of its own (`image-builder.bicep`), runs `desktop/image/bake.sh` on it, captures it into the gallery as version `X.Y.Z` (about half an hour), keeps the three newest versions and deletes the builder. Otherwise it does nothing. Either way it says which version this release makes its desktops from: its own, or the newest before it.
3. **Web + API.** On a fresh installation the job first deploys `foundation.bicep` by itself, because the images need the registry and the web image needs the API's address. It builds both images, deploys `main.bicep` with the image version from the step before, and checks that both origins serve the exact commit.

The image comes first because the API and the image are released together: a deployment makes its desktops from one image version, the one it was deployed with, and never from whatever is newest. So the API never meets a desktop newer than itself, and deploying an older release again gets that release's image, as long as it is one of the three kept. Running and stopped desktops keep the disk they have.

The image is specialized (`infra/gallery.bicep`, `desktop/image/seal.sh`): a machine made from it starts as it is, with nothing to provision, and reads what it is for from its user data.

To deploy a release again, run the workflow by hand with its tag. If a release fails, fix the cause and re-run its jobs; a fix to the workflow or the Bicep needs a new tag, because the workflow runs from the tag's commit.

## Staging

Production signs in by emailed code, so trying a change there needs a real inbox. Staging is a second copy for that: its own database, its own two apps and its own desktops (`workstream-staging`, `workstream-staging-desktops`) on production's shared foundation, signing in with an email and a password and sending no email. It is the same `main.bicep` with `stage=staging`.

```bash
gh workflow run staging.yml -f action=deploy -f ref=main     # or a branch, a tag, a commit
gh workflow run staging.yml -f action=deploy -f ref=my-branch -f desktop-image=true   # a change under desktop/
gh workflow run staging.yml -f action=remove
```

Staging makes its desktops from the newest release's image. With `desktop-image=true` it bakes one of its own from the ref first, as version `0.0.N` in the same gallery; no release ever chooses a `0.0` version, only the newest is kept, and removing staging deletes it.

The run's summary gives its two addresses. Its administrator is `admin@workstream.test` with the password in the `staging` environment's `ADMINISTRATOR_PASSWORD` secret. Remove it when you are done: while it exists it costs what its two apps and its database cost, and its desktops count against the same cores.

## Hibernation

Built, measured, and off. [docs/hibernation.md](../docs/hibernation.md) has what Azure needs for it, what was measured and how to turn it on.

## Models

Agents call Claude through the API's gateway, at the endpoint in `MODEL_ENDPOINT` with the key in `MODEL_KEY`. The simplest is Anthropic's own API: `https://api.anthropic.com` and an API key from [console.anthropic.com](https://console.anthropic.com). Nothing else is needed.

The other is Claude deployed in your own Microsoft Foundry resource, which keeps the model calls in Azure. The Foundry resource and its deployments are made by hand, once, because deploying a model from a partner means accepting that partner's terms through Azure Marketplace, which is a person's decision and not a pipeline's.

```bash
az group create -n workstream-ai -l eastus2
az cognitiveservices account create -g workstream-ai -n <name> -l eastus2 --kind AIServices --sku S0 --custom-domain <name>
```

Then one deployment per model, each named after its model: `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`. Deploying a Claude model asks for the organization's name, country and industry and accepts Anthropic's terms.

Deploy **version 2** of each model, the one hosted on Azure, which is what the Foundry portal picks by default. Version 1 is hosted by Anthropic and bought through Azure Marketplace; on this subscription (pay-as-you-go, an individual billing account) that purchase is refused with "This purchase cannot be completed", and the deployment ends in `Failed`. A failed deployment has to be deleted before it can be made again. From the command line:

```bash
az rest --method put \
  --url "https://management.azure.com/subscriptions/<subscription>/resourceGroups/workstream-ai/providers/Microsoft.CognitiveServices/accounts/<name>/deployments/claude-opus-5-5?api-version=2025-10-01-preview" \
  --body '{"sku": {"name": "GlobalStandard", "capacity": 40}, "properties": {"model": {"format": "Anthropic", "name": "claude-opus-5-5", "version": "2"}, "modelProviderData": {"organizationName": "<organization>", "countryCode": "US", "industry": "technology"}}}'
``` `./scripts/dev-env.sh` reads the endpoint and key for local development; the same two values go in the `production` environment as `MODEL_ENDPOINT` and `MODEL_KEY`.

The deployment names are the defaults in `ModelOptions` (`api/Model/ModelGateway.cs`), with what each costs.

## Sign in with GitHub

People can sign in with GitHub beside the emailed code. It is SqlOS's GitHub connection, seeded by the API at start from two settings (`SqlOS:Oidc:GitHub:ClientId` and `SqlOS:Oidc:GitHub:ClientSecret`; `api/Workstream.cs`, `SeedGitHubSignIn`). It needs a GitHub OAuth client, which GitHub calls an "OAuth App". It is made by hand because GitHub has no way to make one without a person filling its form. The same sign-in is how desktops reach GitHub: SqlOS keeps the token GitHub gives it, and a task's desktop pushes with the token of the person who started the task ([docs/core-model.md](../docs/core-model.md), "A desktop's GitHub token").

### Making the OAuth client

Make it after the first release has deployed, because it needs your two addresses. The release run's summary gives them: the web app at `https://workstream-web.<domain>` and the API at `https://workstream-api.<domain>`.

1. On GitHub, open **Settings**, **Developer settings**, **OAuth Apps**, **New OAuth App**. Use your organization's settings if people from an organization will sign in, or your own otherwise.
2. **Application name**: `Workstream`. People see it on GitHub's page when they sign in.
3. **Homepage URL**: your web app's address, `https://workstream-web.<domain>`.
4. **Authorization callback URL**: `https://workstream-api.<domain>/sqlos/auth/oidc/callback`. It is on the API, not the web app, and it must match exactly.
5. Leave **Enable Device Flow** off and press **Register application**.
6. Copy the **Client ID**. Press **Generate a new client secret** and copy the secret, because GitHub shows it only once.
7. Put the two values on your repository's `production` environment:

```bash
gh variable set GH_OAUTH_CLIENT_ID --env production --body "<client id>"
gh secret set GH_OAUTH_CLIENT_SECRET --env production   # paste the secret when asked
```

8. Deploy the current release again so the API gets them: `gh workflow run release.yml -f tag=vX.Y.Z` with the tag you published. The next release you publish also passes them on (`.github/actions/deploy`, `infra/api.bicep`). The sign-in page then shows **Continue with GitHub**.

The client asks GitHub for a person's profile, their email addresses and their repositories (`read:user`, `user:email`, `repo`; the scopes are in code, `SeedGitHubSignIn`). GitHub shows them on its page the first time. A person needs a verified primary email address on GitHub; an address that already has a Workstream account signs in to that account.

To check it once deployed: sign out, press **Continue with GitHub**, approve on GitHub, and you are in your workspace. Do it once with a GitHub account whose address already has a Workstream account, and once with one that has none, which gets a team of its own.

To turn it off after it has been on, taking the two values away is not enough: SqlOS keeps a connection it was once given, with its button, until the connection is disabled. That is its row in SqlOS's `SqlOSAuthOidcConnections` table, `IsEnabled` (see "Database access"); development has SqlOS's own dashboard for it at `/sqlos`.

### Staging and development

A GitHub OAuth client sends people back to one callback address, so each place needs a client of its own. Staging: the same steps with `Workstream staging` as its name and staging's two addresses from its run's summary (`https://workstream-staging-web.<domain>` and `https://workstream-staging-api.<domain>/sqlos/auth/oidc/callback`), and the same two names on the `staging` environment. Your computer: homepage `http://localhost:8091`, callback `http://localhost:5280/sqlos/auth/oidc/callback`, and `WORKSTREAM_GITHUB_OAUTH_CLIENT_ID` and `WORKSTREAM_GITHUB_OAUTH_CLIENT_SECRET` in `.env` (`.env.example`). The two are set together or not at all: the API refuses to start with only one.

### Rotating

Generate a new client secret on the OAuth client's page beside the old one, set `GH_OAUTH_CLIENT_SECRET` to it, deploy the current release again, then delete the old one on GitHub. Nobody is signed out: sessions are SqlOS's own.

## Database access

The SQL server's one administrator is the Entra group `workstream-sql-admins`. The runtime identity is in it; a person who needs to read or fix data is added to it:

```bash
az ad group member add --group workstream-sql-admins --member-id $(az ad signed-in-user show --query id -o tsv)
```

Add a firewall rule for your address, connect with Entra sign-in, and remove both when you are done.

## What it costs with nobody using it

An estimate from list prices, not a bill: about $105 a month at most, less while the two apps sit idle, because an idle replica is charged a lower rate.

| | A month |
| --- | --- |
| NAT gateway and its address | about $36 |
| API, one replica of 0.5 vCPU and 1 GB kept warm | up to about $39 |
| Web app, one replica of 0.25 vCPU and 0.5 GB kept warm | up to about $20 |
| SQL database, Basic | $4.90 |
| Container registry, Basic | $5 |
| Logs, email, the image in the gallery | a few dollars |

None of the rest costs anything while nobody is using the product.

## What it costs when people use it

The prices are Azure's list prices in West US 3, kept in one place (`DesktopOptions.PriceOptions`, `api/Desktops/DesktopHosts.cs`). Every hour a machine runs is written down with what it ran for (`DesktopRuns`), and the system administrator's page adds it up by purpose for the month.

| | |
| --- | --- |
| A task's desktop, running | $0.091 (Small), $0.182 (Standard) or $0.364 (Large) an hour |
| A desktop's disk, running or paused, until it is archived or its task is deleted | $4.80 a month (Standard SSD, 64 GB: Small and Standard) or $9.60 (128 GB: Large) |
| An archived desktop's archive, in place of its disk | $0.05 a month for each GB that was on the disk (a full snapshot on standard storage, billed on the used size) |
| A spare, while someone is in a team's workspace | the same hourly price as a desktop of its size |
| A machine preparing an environment's setup | the same hourly price as a desktop of its size, until its disk is saved |
| An environment's saved setup | $0.05 a month for each GB on it (a full snapshot on standard storage, billed on the used size) |

Spares are the one cost that is not somebody's task. Their limits are two settings in `infra/api.bicep`: `sparesAtOnce` (2) and `spareHoursADay` (6). At those, spares cost at most 6 × $0.182 = $1.09 a day, about $33 in a month in which the limit is reached every day, and nothing on a day nobody opens the app. A spare nobody takes is removed after 15 minutes, which is $0.05.

A saved setup is an environment's repository cloned and setup script run once, on a machine made only for that, whose disk is then kept as a snapshot that tasks' disks are made from (`EnvironmentSetup`, `api/Desktops/DesktopJobs.cs`). An environment keeps one: a new one replaces the old, deleting the environment deletes it, and one nothing has started from for 30 days is deleted; the sweep deletes any snapshot no environment knows. A desktop with 10 GB on its disk makes a save of about $0.50 a month. The preparing machines are limited by `Workstream:Desktops:Saves` (`api/Desktops/DesktopHosts.cs`, `SaveOptions`): one at a time over every team, four hours of preparing a day, each attempt as long as the setup script may run (`Workstream:Desktops:SetupMinutes`, 45) and half an hour more, so at most 4 × $0.182 = $0.73 a day for Standard desktops. A save older than a week is made again when a task next starts from it.

The API's identity can do all of it with the two roles it holds on the desktops' group (`infra/desktops.bicep`): Virtual Machine Contributor makes the disk from a snapshot and the machine that takes it, and Disk Snapshot Contributor makes, reads and deletes the snapshots.

To check the Azure host's part against Azure itself, in a resource group that is not the desktops' own, sign in with `az login` and run the one test that does it. It reads the gallery image and changes nothing outside the group it is given, which needs a virtual network with a subnet; it removes what it makes, and the group is yours to delete afterwards.

```bash
export WORKSTREAM_LIVE_AZURE_SUBSCRIPTION=<subscription id>
export WORKSTREAM_LIVE_AZURE_GROUP=<a resource group for this check alone>
export WORKSTREAM_LIVE_AZURE_SUBNET=<the id of a subnet in it>
export WORKSTREAM_LIVE_AZURE_IMAGE=<the gallery image version's id, as Workstream:Desktops:Azure:ImageId>
export WORKSTREAM_LIVE_AZURE_SIZE=large          # small, standard (when not said) or large
export WORKSTREAM_LIVE_AZURE_FILL_GB=10          # gigabytes the "setup" writes, to time a real save; none when not said
dotnet test tests/Workstream.Tests --filter "FullyQualifiedName~AzureSavedSetupLive" --logger "console;verbosity=detailed"
```

Without those variables the test is skipped. What it measured is in [docs/architecture.md](../docs/architecture.md), "A saved setup".

A pool of stopped machines was measured and not built: Azure takes as long to start a stopped machine as to make a new one (a middle of about 17 seconds for either, and up to a minute), so its disks would have bought nothing.

## Limits to know

- **A provider.** A container apps environment inside a virtual network needs the `Microsoft.ContainerService` resource provider registered on the subscription. Without it the environment sits in `Waiting` and never finishes. `release.yml` registers it before a fresh installation's first deployment.

- **Cores.** A new subscription has 10 cores in a region: two Standard desktops use 8, and baking an image uses 4 while it runs. This subscription has 40 in West US 3 (the regional limit and the `StandardDasv7Family` limit), which Azure granted at once and without charge: `az quota update --resource-name cores --scope /subscriptions/<id>/providers/Microsoft.Compute/locations/westus3 --limit-object value=40 --resource-type dedicated`, and the same for `standardDasv7Family` and, for the sizes that hibernate, `standardDav6Family`.
- **How long Azure takes.** Making a machine, or starting a stopped one, takes Azure between 15 seconds and a minute before the machine begins to boot, and it varies by the hour. Nothing in this repository shortens that; a machine then shows its screen about 4 seconds after it begins to boot.
- **Azure's limit on questions.** Azure answers about 15 questions about operations in 25 seconds, across the whole subscription. The API never waits on an operation: it asks about the machine itself, which is counted per machine (`api/Desktops/DesktopHosts.cs`).
- **Email.** The domain Azure manages sends a few dozen emails an hour and may land in spam. A domain of our own replaces it.
- **Model throughput.** A Foundry deployment has a tokens-a-minute limit. An agent that reaches it waits and tries again.
