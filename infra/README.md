# Infrastructure

Everything in Azure is declared here and deployed by `.github/workflows/release.yml`. Nothing is made by hand, with three exceptions written down below: the Foundry resource and its model deployments, Workstream's GitHub App, and the GitHub OAuth client people sign in with.

## What is deployed

`main.bicep` is one subscription-scoped deployment in West US 3. (East US 2 was tried first: on the day this was built it left a network-integrated container apps environment waiting for twenty minutes without making it, twice, where West US 3 made one in under four.)

| Group | What | File |
| --- | --- | --- |
| `workstream` | Container registry, Log Analytics, the private network with its NAT gateway, the container apps environment inside that network, Communication Services for email, the gallery that holds the desktops' image (`gallery.bicep`: the definition `machine`, and `machine-hibernate` for desktops that hibernate) | `foundation.bicep` |
| `workstream` | The runtime identity, the SQL server and database | `data.bicep` |
| `workstream` | The API and the web app | `api.bicep`, `web.bicep` |
| `workstream-desktops` | One virtual machine per task, with its disk and network interface. The API makes and removes them; Bicep declares only the API's right to. | `desktops.bicep` |
| `workstream-ai` | The Foundry resource. Made by hand, see "Models". | |

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
| `MODEL_ENDPOINT` | variable | The Foundry resource's Anthropic endpoint, `https://<resource>.services.ai.azure.com/anthropic`. |
| `MODEL_KEY` | secret | The Foundry resource's key. |

Without the two model values the product deploys and agents say models are not set up. The `staging` environment takes the same two model values, and `ADMINISTRATOR_PASSWORD` as a secret.

For GitHub, the same environment takes six more, all made when the GitHub App is (see "GitHub" below). GitHub refuses names that start with `GITHUB_`, so they start with `GH_APP_`:

| Name | Kind | What |
| --- | --- | --- |
| `GH_APP_ID` | variable | The app's App ID. |
| `GH_APP_SLUG` | variable | The app's name in its address, `https://github.com/apps/<slug>`. |
| `GH_APP_CLIENT_ID` | variable | The app's Client ID. |
| `GH_APP_CLIENT_SECRET` | secret | A client secret of the app. |
| `GH_APP_PRIVATE_KEY` | secret | A private key of the app: the whole `.pem` file GitHub downloads. |
| `GH_APP_WEBHOOK_SECRET` | secret | Optional. The app's webhook secret. |

Without the first five, teams see that GitHub is not set up, and everything else works.

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

Built, measured, and off. A paused desktop loses every program; a hibernated one writes its memory to its disk first, and resuming brings everything back as it was (`docs/architecture.md`, "Hibernating a paused desktop"). Whether to turn it on is a decision about Trusted Launch, below.

### What Azure needs, as it was found

Tried in West US 3 on 2026-11-21, in a scratch group since deleted:

- **A size that hibernates.** `az vm list-skus` says `HibernationSupported` for `Standard_D2as_v6`, `D4as_v6` and `D8as_v6` and not for the Dasv7 sizes desktops use today. The v6 sizes have the same cores and memory, and the same list price to the tenth of a cent ($0.0908, $0.182 and $0.363 an hour). `/dev/kvm` is there on them, with hibernation on too. The `standardDav6Family` limit is 40 cores, raised from 10 the way "Limits to know" says.
- **No Trusted Launch.** Azure refuses: "Trusted launch is not supported for Linux VM with Hibernation capability", with Secure Boot on or off and with the virtual TPM on or off. (Under Secure Boot the Debian kernel also locks hibernation out by itself.) A machine's security type cannot be changed afterwards, in either direction. So a desktop that hibernates is made with neither Secure Boot nor a virtual TPM.
- **An image definition that says so.** A machine with hibernation is made only from a gallery definition with the feature `IsHibernateSupported`. It cannot be added to a definition that exists (the update is refused), so there is a second one, `machine-hibernate`. A disk built with Trusted Launch cannot go into it, and one built without cannot go into `machine`, so its versions are baked on a builder without Trusted Launch (`image-builder.bicep`, `trustedLaunch=false`).
- **The machine itself.** Nothing of Azure's: no extension and no agent. `desktop/image/hibernate.sh` makes a swap file as large as the machine's memory at first boot (a third of a second, beside the desktop; 8, 16 or 32 GB of the disk by size) and answers Azure's request by telling the kernel to hibernate, and `resume-here.sh` lets only the machine that hibernated wake up from its disk. On a machine made without hibernation it does nothing, so every image has it.

### What was measured

Machines made the way the API makes them, without Trusted Launch, with a desktop session, the agent exchanging with a stand-in API, Chromium with a page open, VS Code, a node server, a counter and a Docker container running. Seconds, from the request to Azure.

| | Standard (`D4as_v6`, 16 GB) | Large (`D8as_v6`, 32 GB) |
| --- | --- | --- |
| Pause: until Azure has released the machine | 27 to 38, middle 28 (5 times) | 37 to 41, middle 39 (3 times) |
| Resume: until the display answers | 27 to 42, middle 33 (6 times) | 33 to 35, middle 34 (3 times) |
| Resume: until the agent has exchanged with the API again | 25 to 39, middle 31 | 32 to 34, middle 34 |
| The same machine started after a plain pause, for comparison | 22 to the display, 23 to the exchange (once), with nothing running | |

Every resume brought back the same boot, the same processes (the session, the agent, Chromium and its pages, VS Code, the node server, the counter, the container) and a working network, with the clock right to the second. The agent's exchange recovered by itself each time.

Those desktops had about 1.7 GB in use. With a program holding 10 GB more on the Large one, half of it the kind that does not compress, the pause took 97 and 90 seconds and the resume 109 and 103 seconds to the display: about 7 seconds more for each gigabyte in use, each way. A real project's stack (a database in a container, a bundler, a browser) holds several gigabytes, so its desktop resumes in a minute or two rather than half a minute.

A new machine is no slower on v6 than on v7: five each from the release image, alternating, showed their display after 22 to 25 seconds on v6 (middle 23) and 24 to 52 on v7 (middle 40), which is Azure's own spread that hour and not the size. Five more made to hibernate, from an image baked with these scripts on a builder without Trusted Launch, showed theirs after 24 to 28 seconds (middle 25): the room for memory is made beside the desktop, not before it.

Two things to know before relying on it:

- **A resume is slower to a screen than a plain start, and slower the more memory is in use.** What programs hold is read back from a disk that gives 60 MB a second. The kernel is told to write what programs hold and let go of cached files first; left to shrink the image as far as it could, it pushed programs' memory to swap, and the desktop hardly answered for a minute and a half after it was back.
- **A machine that does not hibernate is stopped the plain way, ten minutes late.** Azure waited ten minutes for the machine and left it running; a plain stop asked for in the meantime was taken and carried out only then. The API waits that out and then deallocates, and the desktop comes back as from any pause. It happened in testing while `hibernate.sh` still went through `systemctl hibernate`, which refused twice: for a minute after a resume, and on the boot in which the swap file was made again. It tells the kernel directly now.

### Turning it on

One line: `DEFINITION: machine-hibernate` in `release.yml`. The next release bakes the image into that definition, because it is empty, on a builder without Trusted Launch, and deploys the API with `desktopsHibernate=true` (`main.bicep`): `Workstream:Desktops:Azure:Hibernate`, the three v6 sizes, and the image from that definition. Staging first: `gh workflow run staging.yml -f action=deploy -f ref=main -f desktop-image=true -f hibernate=true`.

Desktops that exist keep what they have. A paused one is started as the machine it is, with Trusted Launch on a v7 size, and is paused the plain way from then on. An archived one comes back on a machine like the one its disk was made on, because a disk keeps its security type and Azure takes it only on a machine of the same kind: with Trusted Launch and without hibernation (`AzureDesktopHost.CreateAsync` reads it off the archive). Only desktops made after the change hibernate. Saved setups are made again by themselves, since a save is for one image version. Turning it off again is the same line back, and the same holds the other way round.

What is given up is Trusted Launch on desktops: Secure Boot, which stops a desktop booting a kernel or boot loader that is not signed, and the virtual TPM. A desktop is already untrusted, its person has root, it holds one key that reaches its own task, and it has no way to another machine (`docs/architecture.md`, "What a desktop can and cannot do"); Trusted Launch protects a desktop's own boot from what runs on it, and nothing else from the desktop. That is the owner's call, which is why this is off.

To check the host against Azure without deploying anything, `tests/Workstream.Tests/AzureDesktopHostLive.cs` makes, pauses, resumes, archives, brings back and deletes one machine in a resource group you name.

## Models

Agents call Claude on Microsoft Foundry through the API's gateway. The Foundry resource and its deployments are made by hand, once, because deploying a model from a partner means accepting that partner's terms through Azure Marketplace, which is a person's decision and not a pipeline's.

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

## GitHub

A team connects its GitHub account or organization by installing Workstream's GitHub App on it, from the team's page. Each desktop then asks the API for a token for its own environment's repository (`GET /api/desktop/github/token`), which GitHub makes good for one hour; git and gh on the desktop ask for one whenever they need it, and nothing stores it ([docs/core-model.md](../docs/core-model.md), "Connect GitHub"). The app itself is made by hand, once, because GitHub has no way to make an app without a person clicking through its form. It is the second thing made by hand, after the Foundry resource.

### Making the app for production

1. Open https://github.com/organizations/agenetix/settings/apps/new (the organization that owns Workstream; any account can own the app).
2. **GitHub App name**: `Workstream`. GitHub makes the slug from it (`workstream`); if the name is taken, pick another and note the slug GitHub shows.
3. **Homepage URL**: `https://workstream-web.happyhill-808cd2a4.westus3.azurecontainerapps.io`
4. Under **Identifying and authorizing users**:
   - **Callback URL**: `https://workstream-api.happyhill-808cd2a4.westus3.azurecontainerapps.io/api/github/callback`
   - Tick **Request user authorization (OAuth) during installation**. This is required: GitHub then hands back a code for the person who installed the app, and the API uses it to check that their GitHub account really reaches the installation they came back with. Without it anyone could name another account's installation.
   - Leave **Expire user authorization tokens** as it is and **Enable Device Flow** off.
5. Under **Post installation**: GitHub greys out **Setup URL** once the box above is ticked, because it then sends people to the callback URL instead. If it can still be filled, give it the same callback address and tick **Redirect on update**.
6. Under **Webhook**: keep **Active** ticked.
   - **Webhook URL**: `https://workstream-api.happyhill-808cd2a4.westus3.azurecontainerapps.io/api/github/webhook`
   - **Secret**: make one with `openssl rand -hex 32` and keep it for `GH_APP_WEBHOOK_SECRET`.
   - With it, removing the app on GitHub disconnects every team that was connected to it. To go without, untick **Active** and skip that secret.
7. Under **Permissions**, **Repository permissions** only:
   - **Contents**: Read and write
   - **Pull requests**: Read and write
   - **Metadata**: Read-only (GitHub sets it)
   - Nothing under **Organization permissions** or **Account permissions**.
8. Under **Subscribe to events**: nothing. GitHub sends an app's installation events whether or not any are ticked.
9. **Where can this GitHub App be installed?** **Any account**, so every team can install it on its own organization.
10. **Create GitHub App**.

Then, on the app's settings page:

11. Note the **App ID** and the **Client ID** at the top of the page.
12. Under **Client secrets**, **Generate a new client secret** and copy it.
13. Under **Private keys**, **Generate a private key**. GitHub downloads a `.pem` file.
14. Put the six values on the `production` environment:

```bash
gh variable set GH_APP_ID --env production --body <App ID>
gh variable set GH_APP_SLUG --env production --body workstream
gh variable set GH_APP_CLIENT_ID --env production --body <Client ID>
gh secret set GH_APP_CLIENT_SECRET --env production            # paste the client secret
gh secret set GH_APP_PRIVATE_KEY --env production < ~/Downloads/workstream.<date>.private-key.pem
gh secret set GH_APP_WEBHOOK_SECRET --env production --body <the openssl secret>
```

15. Publish the next release. Its deploy passes them on (`.github/actions/deploy`, `infra/api.bicep`): the private key goes as its PEM in base64, and the API reads either form. The team's page then offers **Connect GitHub** to administrators.

To check it once deployed: on a team's page, connect GitHub to a test organization with one private repository, make an environment that picks that repository, start a task, and ask the agent to change a line and open a pull request. The pull request shows the app as its author.

### Staging and development

An app sends people back to one callback address, so staging needs an app of its own to connect GitHub: the same steps with `Workstream staging` as its name, `https://workstream-staging-api.happyhill-808cd2a4.westus3.azurecontainerapps.io/api/github/callback` as its callback and `…/api/github/webhook` as its webhook, and the same six names on the `staging` environment. Without them staging shows that GitHub is not set up. For a laptop, an app whose callback is `http://localhost:5280/api/github/callback` and the `WORKSTREAM_GITHUB_*` values in `.env` (`.env.example`); its webhook cannot reach a laptop, so leave it inactive.

### Rotating

A new client secret or private key is added on the app's page beside the old one; set the GitHub secret to the new value, publish a release, then delete the old one on GitHub. Tokens already handed to desktops last out their hour.

## Sign in with GitHub

People can sign in with GitHub beside the emailed code. It is SqlOS's GitHub connection, seeded by the API at start from two settings (`SqlOS:Oidc:GitHub:ClientId` and `SqlOS:Oidc:GitHub:ClientSecret`; `api/Workstream.cs`, `SeedGitHubSignIn`). It needs a GitHub OAuth client, which GitHub calls an "OAuth App". This is not Workstream's GitHub App above: that one reaches repositories, this one only says who a person is. It is the third thing made by hand, because GitHub has no way to make one without a person filling its form.

### Making the OAuth client for production

1. Open https://github.com/organizations/agenetix/settings/applications/new (the organization's **Settings**, **Developer settings**, **OAuth Apps**, **New OAuth App**).
2. **Application name**: `Workstream`. People see it on GitHub's page when they sign in.
3. **Homepage URL**: `https://workstream-web.happyhill-808cd2a4.westus3.azurecontainerapps.io`
4. **Authorization callback URL**: `https://workstream-api.happyhill-808cd2a4.westus3.azurecontainerapps.io/sqlos/auth/oidc/callback`. It is SqlOS's address on the API, not the web app's, and it must match exactly.
5. Leave **Enable Device Flow** off. **Register application**.
6. Copy the **Client ID**. **Generate a new client secret** and copy it; GitHub shows it once.
7. Put the two values on the `production` environment:

```bash
gh variable set GH_OAUTH_CLIENT_ID --env production --body "<client id>"
gh secret set GH_OAUTH_CLIENT_SECRET --env production   # paste the secret when asked
```

8. Publish the next release. Its deploy passes them on (`.github/actions/deploy`, `infra/api.bicep`), and the sign-in page then shows **Continue with GitHub**.

The client asks GitHub for a person's profile and email addresses (`read:user`, `user:email`) and nothing else. A person needs a verified primary email address on GitHub; an address that already has a Workstream account signs in to that account.

To check it once deployed: sign out, press **Continue with GitHub**, approve on GitHub, and you are in your workspace. Do it once with a GitHub account whose address already has a Workstream account, and once with one that has none, which gets a team of its own.

To turn it off after it has been on, taking the two values away is not enough: SqlOS keeps a connection it was once given, with its button, until the connection is disabled. That is its row in SqlOS's `SqlOSAuthOidcConnections` table, `IsEnabled` (see "Database access"); development has SqlOS's own dashboard for it at `/sqlos`.

### Staging and development

A GitHub OAuth client sends people back to one callback address, so each place needs a client of its own. Staging: the same steps with `Workstream staging` as its name, `https://workstream-staging-web.happyhill-808cd2a4.westus3.azurecontainerapps.io` as its homepage and `https://workstream-staging-api.happyhill-808cd2a4.westus3.azurecontainerapps.io/sqlos/auth/oidc/callback` as its callback, and the same two names on the `staging` environment. A laptop: homepage `http://localhost:8091`, callback `http://localhost:5280/sqlos/auth/oidc/callback`, and `WORKSTREAM_GITHUB_OAUTH_CLIENT_ID` and `WORKSTREAM_GITHUB_OAUTH_CLIENT_SECRET` in `.env` (`.env.example`). The two are set together or not at all: the API refuses to start with only one.

### Rotating

Generate a new client secret on the OAuth client's page beside the old one, set `GH_OAUTH_CLIENT_SECRET` to it, publish a release, then delete the old one on GitHub. Nobody is signed out: sessions are SqlOS's own.

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
