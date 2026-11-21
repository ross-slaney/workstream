# Infrastructure

Everything in Azure is declared here and deployed by `.github/workflows/release.yml`. Nothing is made by hand, with one exception written down below: the Foundry resource and its model deployments.

## What is deployed

`main.bicep` is one subscription-scoped deployment in East US 2.

| Group | What | File |
| --- | --- | --- |
| `workstream` | Container registry, Log Analytics, the private network with its NAT gateway, the container apps environment inside that network, Communication Services for email, the gallery that holds the desktops' image | `foundation.bicep` |
| `workstream` | The runtime identity, the SQL server and database | `data.bicep` |
| `workstream` | The API and the web app | `api.bicep`, `web.bicep` |
| `workstream-desktops` | One virtual machine per task, with its disk and network interface. The API makes and removes them; Bicep declares only the API's right to. | `desktops.bicep` |
| `workstream-ai` | The Foundry resource. Made by hand, see "Models". | |

The two origins are under the container apps environment's own domain: `https://workstream-web.<domain>` and `https://workstream-api.<domain>`. A domain of our own is not set up yet.

### The network

`workstream-net` (10.60.0.0/16) has two subnets. `container-apps` holds the container apps environment. `desktops` holds every task's machine. A desktop has no public address and reaches the internet through the NAT gateway. The subnet's rules admit one thing in, the API's relay to a desktop's display on port 5900 from the `container-apps` subnet, and refuse every other machine on the network, other desktops included. Outbound to the network is refused too. A desktop calls the API at its public address like anything else.

### What the API may do in Azure

The API and the web app run as one user-assigned identity, `workstream-runtime`. It pulls both images (`AcrPull` on the registry), owns the database's schema (it is in the SQL administrators group), and makes desktops: `Virtual Machine Contributor` on the `workstream-desktops` group and on the desktops' subnet, and `Reader` on the gallery. It has no other role.

## Setting up a subscription

Once, by an owner of the subscription who is also a tenant administrator, signed in with `az login` and `gh auth login`:

```bash
./scripts/setup-azure.sh
```

For each of the GitHub environments `production` and `staging` it makes an app registration (`workstream-github-production`, `workstream-github-staging`) and gives its service principal Contributor on the subscription, the right to assign exactly the three roles above, and Microsoft Graph `Group.ReadWrite.All` for the SQL administrators groups. It stores each credential as that environment's `AZURE_CREDENTIALS` secret. It is safe to run again.

Then set, on the GitHub environment `production`:

| Name | Kind | What |
| --- | --- | --- |
| `ADMINISTRATOR_EMAIL` | variable | The one address that reaches `/admin`. They sign in by emailed code like everyone else. Changing it moves the role at the next deploy; without it nobody is the administrator. |
| `MODEL_ENDPOINT` | variable | The Foundry resource's Anthropic endpoint, `https://<resource>.services.ai.azure.com/anthropic`. |
| `MODEL_KEY` | secret | The Foundry resource's key. |

Without the two model values the product deploys and agents say models are not set up. The `staging` environment takes the same two model values, and `ADMINISTRATOR_PASSWORD` as a secret.

## Releasing

Publishing the GitHub release `vX.Y.Z` runs `release.yml` (the `release` skill does the whole thing):

1. **Resolve.** The tag must be `v` + `client/app.json`'s version, on `main`, and published.
2. **Web + API.** On a fresh installation the job first deploys `foundation.bicep` by itself, because the images need the registry and the web image needs the API's address. It builds both images, deploys `main.bicep`, and checks that both origins serve the exact commit.
3. **Desktops' image.** When `desktop/` changed since the previous release, or the gallery is empty, the job makes a Debian 13 machine in a group of its own, runs `desktop/image/bake.sh` on it, captures it into the gallery as version `X.Y.Z`, keeps the three newest versions and deletes the builder. New desktops use the newest version; running and stopped desktops keep the disk they have.

To deploy a release again, run the workflow by hand with its tag. If a release fails, fix the cause and re-run its jobs; a fix to the workflow or the Bicep needs a new tag, because the workflow runs from the tag's commit.

## Staging

Production signs in by emailed code, so trying a change there needs a real inbox. Staging is a second copy for that: its own database, its own two apps and its own desktops (`workstream-staging`, `workstream-staging-desktops`) on production's shared foundation, signing in with an email and a password and sending no email. It is the same `main.bicep` with `stage=staging`.

```bash
gh workflow run staging.yml -f action=deploy -f ref=main     # or a branch, a tag, a commit
gh workflow run staging.yml -f action=remove
```

The run's summary gives its two addresses. Its administrator is `admin@workstream.test` with the password in the `staging` environment's `ADMINISTRATOR_PASSWORD` secret. Remove it when you are done: while it exists it costs what its two apps and its database cost, and its desktops count against the same cores.

## Models

Agents call Claude on Microsoft Foundry through the API's gateway. The Foundry resource and its deployments are made by hand, once, because deploying a model from a partner means accepting that partner's terms through Azure Marketplace, which is a person's decision and not a pipeline's.

```bash
az group create -n workstream-ai -l eastus2
az cognitiveservices account create -g workstream-ai -n <name> -l eastus2 --kind AIServices --sku S0 --custom-domain <name>
```

Then one deployment per model, each named after its model: `claude-opus-5-5`, `claude-sonnet-5-5` and `claude-haiku-5-5`. Deploying a Claude model asks for the organization's name, country and industry and accepts Anthropic's terms. `./scripts/dev-env.sh` reads the endpoint and key for local development; the same two values go in the `production` environment as `MODEL_ENDPOINT` and `MODEL_KEY`.

The deployment names are the defaults in `ModelOptions` (`api/Model/ModelGateway.cs`), with what each costs.

## Database access

The SQL server's one administrator is the Entra group `workstream-sql-admins`. The runtime identity is in it; a person who needs to read or fix data is added to it:

```bash
az ad group member add --group workstream-sql-admins --member-id $(az ad signed-in-user show --query id -o tsv)
```

Add a firewall rule for your address, connect with Entra sign-in, and remove both when you are done.

## What it costs with nobody using it

An estimate from list prices in East US 2, not a bill: about $105 a month at most, less while the two apps sit idle, because an idle replica is charged a lower rate.

| | A month |
| --- | --- |
| NAT gateway and its address | about $36 |
| API, one replica of 0.5 vCPU and 1 GB kept warm | up to about $39 |
| Web app, one replica of 0.25 vCPU and 0.5 GB kept warm | up to about $20 |
| SQL database, Basic | $4.90 |
| Container registry, Basic | $5 |
| Logs, email, the image in the gallery | a few dollars |

A running desktop is its machine: a Standard desktop (`Standard_D4as_v7`, 4 cores, 16 GB) is about $0.20 an hour. A stopped desktop costs only its 64 GB disk, about $5 a month, until its task is deleted.

## Limits to know

- **A provider.** A container apps environment inside a virtual network needs the `Microsoft.ContainerService` resource provider registered on the subscription. Without it the environment sits in `Waiting` and never finishes. `release.yml` registers it before a fresh installation's first deployment.

- **Cores.** A new subscription has 10 cores in a region. Two Standard desktops use 8; baking an image uses 4 while it runs. More desktops at once needs a quota request in the Azure portal.
- **Email.** The domain Azure manages sends a few dozen emails an hour and may land in spam. A domain of our own replaces it.
- **Model throughput.** A Foundry deployment has a tokens-a-minute limit. An agent that reaches it waits and tries again.
