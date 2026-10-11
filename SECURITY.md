# Security policy

## What this project is

multi-workspace-mcp is a **personal, single-user** server. You run your own copy, with your own password, your
own signing key and your own Notion integration tokens. There is no shared or hosted instance.

## Supported versions

Only the latest release, and the latest commit on `main`, are supported. Deploy from a release tag (for example `1.2.3`) or from a `sha-…` image tag built from `main`.

## Reporting a vulnerability

Please report vulnerabilities **privately** with
[GitHub private vulnerability reporting](https://github.com/guilhermeappel/multi-workspace-mcp/security/advisories/new)
and do not open a public issue. Include the affected commit, what an attacker can do and the steps to
reproduce. Never paste real tokens, passwords or signing keys: redact them.

You can expect an acknowledgement within a few days. This is a spare-time project, so fixes are best effort.

## Threat model

What the server protects, and how:

- **Access to your Notion data** is gated by an OAuth 2.1 flow (authorization code + PKCE S256) that ends at a
  password page. Tokens are stateless and HMAC-SHA256 signed; the purpose of each token is part of the signed
  data. The login locks after repeated wrong passwords.
- **Notion tokens** live only in environment variables / Azure secrets (or user-secrets in development), never in
  `appsettings.json`, and are never logged or returned.

What you must still do yourself:

- **Choose strong secrets.** `Server:AuthPassword` should be long and random (16+ characters) and `Server:SigningKey` must be
  32+ random bytes. Anyone who holds `Server:SigningKey` can forge access tokens; anyone who holds `Server:AuthPassword`
  can log in.
- **Give each Notion integration the smallest capabilities.** Use *Read content* only for a workspace that
  does not need writes, and share only the pages you want Claude to reach.
- **Mind prompt injection.** A page you let Claude read can contain text that tries to steer Claude, for
  example into writing to your other workspace. Read-only integrations (no write capability in Notion) are the
  strongest defence. As a second layer, `Notion:ReadOnlyWorkspaces` makes this server refuse every tool that changes
  something in the workspaces you list. It is enforced by this server, so it does not replace the integration
  capabilities, and it does not stop a page from being read.
- **Start the connector yourself.** The password page is the only consent step. Only enter the password for a
  connection you just started from your own Claude account.
- **Do not share the URL.** Do not expose `Server:AuthDisabled`; it only works when `ASPNETCORE_ENVIRONMENT` is
  `Development`, and Azure Container Apps must never run that way.

## Rotating secrets

| What leaked | What to do |
|---|---|
| `Server:AuthPassword` | Set a new `auth-password` secret and increment `Server:TokenGeneration` (this signs everybody out). |
| `Server:SigningKey` | Set a new `signing-key` secret. This invalidates every token **and** every client registration: remove the connector in Claude and add it again. |
| A Notion token | Revoke it in Notion (integration settings), then update the secret. |

Changing a secret does not restart running replicas. Updating an environment variable (for example
`Server:TokenGeneration`) creates a new revision, which picks the new secrets up. Details are in the [deployment guide](docs/deployment.md#rotating-secrets).

## Known, accepted limits

- The login throttle and the authorization-code ledger are in memory and per process, so the app must run
  with a single replica (`--max-replicas 1`). See [ADR 0003](docs/adr/0003-single-replica-ephemeral-state.md).
- The login throttle has a server-wide limit, so someone who attacks from enough networks can keep you from
  starting a new login for up to 15 minutes at a time. Existing sessions keep working. Nothing is leaked by this.
- Refresh tokens are stateless, so they cannot be revoked one by one. `Server:TokenGeneration` is the kill switch.
  See [ADR 0001](docs/adr/0001-stateless-hmac-tokens.md).
