# Deployment guide

How to run your own copy of multi-workspace-mcp on Azure Container Apps, which scales to zero and stays within
the free grant for personal use. Any host that runs a container works too; the Azure script is a convenience,
not a requirement.

Before you start, read [Who this is for](../README.md#who-this-is-for) and [SECURITY.md](../SECURITY.md). This
is a personal, single-user server: you deploy **your own copy** with **your own** secrets, and you do not
share its URL.

## Overview

| # | Step | Time | Where |
|---|------|------|-------|
| 1 | Create an integration in each Notion workspace | 5 min | notion.so |
| 2 | Run and test locally ([Quick start](../README.md#quick-start)) | 10 min | your machine |
| 3 | Fork the repository and push to `main`: CI builds, tests and publishes the image | 5 min | GitHub |
| 4 | Create the Container App on Azure with the script | 10 min | PowerShell + Azure CLI |
| 5 | Create a US$ 1 budget alert | 3 min | Azure portal |
| 6 | Add the connector in Claude | 2 min | claude.ai |

**Prerequisites:** the .NET 10 SDK, PowerShell 7, the
[Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), a GitHub account and an Azure account (a
card is required, but usage stays within the free grant). Local Docker is optional.

## 1. Notion integrations

Repeat these steps for **each** workspace:

1. Go to <https://www.notion.so/profile/integrations>, click **New integration**, pick the right workspace
   and the **Internal** type.
2. Under **Capabilities**, check *Read content*, *Update content* and *Insert content*.
   For a read-only workspace, check *Read content* only.
3. Copy the **Internal Integration Secret** (`ntn_…`).
4. **Share pages with the integration.** An integration only sees what was shared with it. Two ways:
   - on the integration's **Access** tab, select the root pages (their children come along); or
   - on each page, open `•••` → **Connections** and add the integration.

A workspace's name on the server comes from its setting name: `Notion:Workspaces:work` becomes `work` and
`Notion:Workspaces:personal` becomes `personal`. You can have as many workspaces as you like. See
[Settings](../README.md#settings) for how these keys map to environment variables.

## 2. CI and the container image

The workflow `.github/workflows/ci.yml` does the following:

- on every pull request and on pushes to `main`: the style check (`dotnet format --verify-no-changes`),
  `dotnet build` and `dotnet test`;
- on pull requests: checks that the branch name follows `<type>/<kebab-case-description>` and builds the
  image without publishing it, so a Dockerfile error shows up before the merge;
- on a push to `main`, if the tests pass: builds the image and publishes it to
  `ghcr.io/<owner>/multi-workspace-mcp` with the tags `latest` and `sha-xxxxxxx`.

Pushing a version tag such as `v1.2.3` runs `.github/workflows/release.yml` instead: it tests the code,
publishes the tags `1.2.3` and `1.2` and creates a GitHub release. To run a released version, use its tag
(for example `:1.2.3`) as the image.

Follow it on the **Actions** tab. In a fork, GitHub may leave Actions disabled until you enable it there. Each run's summary shows the deploy command with the right tag.

Check the visibility of the package under GitHub → your profile → **Packages** → `multi-workspace-mcp` →
**Package settings**. The image contains no secrets (they are injected at deploy time), so the simplest option is
to make it **public**: Azure then pulls it without any credential. A public package cannot be made private again.

If you prefer to keep it private, Azure needs a credential to pull it. Create a **classic PAT** under
GitHub → Settings → Developer settings → Personal access tokens → **Tokens (classic)**, tick **only** the
`read:packages` scope and set a long expiry (GHCR does not accept fine-grained tokens for this), and pass
`-RegistryUsername <owner>` to the script in the next step.

## 3. Azure Container Apps

```powershell
az login
./deploy/azure-setup.ps1 -Image ghcr.io/<owner>/multi-workspace-mcp:sha-xxxxxxx
```

Options: `-RegistryUsername <owner>` for a private image (the script then asks for the PAT),
`-EnableLogAnalytics` to keep the logs (see below) and `-DryRun` to print every `az` command, with the
secrets hidden, without running any of them (the script still asks for the tokens and the password).

Use the `sha-xxxxxxx` tag from the CI job summary rather than `latest`: Container Apps does not pull a
mutable tag again, so `latest` would never update.

The script asks for the Notion tokens and the password (at least 16 characters, with no spaces, quotes,
backticks, commas or percent signs) in the terminal, plus the GitHub PAT when `-RegistryUsername` is given. Nothing is
written to a file. The values are passed to `az` as arguments, so they are visible to other processes on
your machine while the script runs. It then:

1. Registers the `Microsoft.App` and `Microsoft.OperationalInsights` providers.
2. Creates the resource group `rg-notion-mcp` in `eastus`.
3. Creates the environment **without Log Analytics** (`--logs-destination none`), the item that usually
   causes cost, unless you pass `-EnableLogAnalytics` (30 days of retention and a daily ingestion cap of
   0.1 GB, set with `-LogDailyCapGb`).
4. Creates the app with **0 to 1 replica, 0.25 vCPU and 0.5 GiB**. The tokens, the password and the PAT (if
   any) are stored as *secrets*.
5. Generates a random `Server:SigningKey` and sets `Server:PublicBaseUrl` to the app's address.
6. Prints the connector URL: `https://notion-mcp.<something>.eastus.azurecontainerapps.io/mcp`.

**Why `eastus` and not a region near you?** The callers are Claude's servers, and the Notion API is also in
the US. You only reach the server directly when typing the password. To change the region, use
`-Location <region>`. For other workspace names: `-Workspaces work,personal,client-x`.

**Keep it at one replica.** The login throttle and the authorization-code ledger live in memory, so the app
must run with `--max-replicas 1` (see [ADR 0003](adr/0003-single-replica-ephemeral-state.md)).

Check that it is up: open `https://<fqdn>/healthz` (it should answer `ok`). The first call after a long idle
period is slower because of the cold start; see [Cold start and cost](#cold-start-and-cost).

## 4. Cost alert (do not skip)

Azure portal → **Cost Management** → **Budgets** → **+ Add**:

- Scope: your subscription
- Amount: **US$ 1**, monthly
- Alert conditions: *Actual* 100% and *Forecasted* 100%, with your e-mail

With the app scaling to zero, usage stays far below the monthly free grant (180,000 vCPU-seconds and
2 million requests). At 0.25 vCPU that is about 200 hours of active replica per month. The alert is there
to warn you if some setting changes. If you keep a replica running, raise the amount (see
[Cold start and cost](#cold-start-and-cost)).

## Cold start and cost

With `--min-replicas 0` (what the script creates) Azure removes the replica after a few minutes without
traffic, and the next request starts a new one. On the deployment this section is based on (`eastus`, 0.25 vCPU),
the replica was scheduled at the first request and the container was running about 23 seconds later. Pulling the
image took under 3 of those seconds. Your numbers may differ.

The connector may give up before that and show "Couldn't connect to the server". Nothing is broken at that
point: the replica is already starting, so connecting again a minute later works. Tokens are signed, not stored,
so you are not logged out by a cold start.

You have two options:

- **Accept it.** Keep scale to zero and reconnect when the first attempt after a long idle period fails. Cost
  stays within the free grant.
- **Keep one replica running.** The first call is then fast every time:

  ```powershell
  az containerapp update -n notion-mcp -g rg-notion-mcp --min-replicas 1 --max-replicas 1
  ```

  This creates a new revision with the same image and secrets. Keep `--max-replicas 1` (see above). The trade-off
  is cost: 0.25 vCPU running all month is about 650,000 vCPU-seconds, above the 180,000 free vCPU-seconds, so
  expect a small monthly charge. Idle replicas are billed at a lower rate than active ones; check the
  [Azure Container Apps pricing](https://azure.microsoft.com/pricing/details/container-apps/) for your region and
  raise the budget alert above to match.

## 5. Connect in Claude

1. claude.ai → **Settings** → **Connectors** → **Add custom connector**.
2. Name: `Notion (work + personal)`. URL: `https://<fqdn>/mcp`, **exactly** as the script printed it.
3. Click **Connect**. The server's page opens: type the password and click **Authorize**.
4. In a conversation, enable the connector from the tools menu. It also shows up on desktop and mobile.

Phrases to try:

- "list my Notion workspaces"
- "search the work Notion for the onboarding page"
- "create a page 'Trip' in my personal Notion with a packing checklist"

If you like, disconnect the official Notion connector so Claude does not confuse the two.

## Day-to-day operations

```powershell
$rg = "rg-notion-mcp"; $app = "notion-mcp"

# Update the code: push to main, wait for the Action and use the sha-xxxxxxx tag from the job summary
az containerapp update -n $app -g $rg --image ghcr.io/<owner>/multi-workspace-mcp:sha-xxxxxxx

# When the GitHub PAT expires (private image only)
$pat = Read-Host "New PAT" -MaskInput   # keeps the value out of your shell history
az containerapp registry set -n $app -g $rg --server ghcr.io --username <owner> --password $pat

# Live logs (work without Log Analytics)
az containerapp logs show -n $app -g $rg --type console --follow

# Failed logins and rejected requests of the last 7 days (only with -EnableLogAnalytics)
$workspaceId = az monitor log-analytics workspace show -g $rg -n law-notion-mcp --query customerId -o tsv
az monitor log-analytics query -w $workspaceId --analytics-query "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(7d) | where Log_s contains 'rejected' | project TimeGenerated, Log_s | order by TimeGenerated desc"

# Add a third workspace
az containerapp secret set -n $app -g $rg --secrets notion-client=ntn_...
az containerapp update -n $app -g $rg --set-env-vars Notion__Workspaces__client=secretref:notion-client

# Change the password AND invalidate every session (Claude asks you to log in again)
$password = Read-Host "New password" -MaskInput
az containerapp secret set -n $app -g $rg --secrets "auth-password=$password"
az containerapp update -n $app -g $rg --set-env-vars Server__TokenGeneration=2

# Delete everything
az group delete -n $rg
```

## Rotating secrets

| What leaked | What to do |
|---|---|
| `Server:AuthPassword` | Set a new `auth-password` secret and increment `Server:TokenGeneration` (signs everybody out). |
| `Server:SigningKey` | Set a new `signing-key` secret. Every token and client registration becomes invalid: remove the connector in Claude and add it again. |
| A Notion token | Revoke it in Notion, then update the secret. |

Changing a secret does not restart running replicas. Updating an environment variable (for example
`Server:TokenGeneration`) creates a new revision, which picks the new secrets up.

## Running somewhere else

The image is a plain ASP.NET Core container listening on port 8080. On any other host, provide the same
settings as environment variables (see [Settings](../README.md#settings)), put it behind HTTPS, and set
`Server__PublicBaseUrl` to the public `https://` address. Keep a single instance, for the reason given above.
