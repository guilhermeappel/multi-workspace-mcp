# 0004. Settings use the Options pattern and the standard .NET configuration layers

Status: accepted

## Context

The first version read flat, upper-case environment variables (`SIGNING_KEY`, `NOTION_TOKEN_WORK`) straight from
`IConfiguration`, and validated them in one hand-written method. `appsettings.json` held only logging, so there was
no place that showed which settings exist or what their defaults are, and the validation stopped at the first
mistake.

Nothing had been deployed yet, so renaming the settings cost only edits, not a breaking change for anyone.

## Decision

- Settings live in two sections bound to typed classes: `Server` (`ServerOptions`) and `Notion`
  (`NotionOptions`, whose `Workspaces` map is workspace name to integration token).
- The non-secret defaults are committed in `appsettings.json`. The two secrets (`Server:AuthPassword`,
  `Server:SigningKey`) and the workspace tokens are never stored there: user-secrets in development, environment
  variables in production (`Server__SigningKey`, `Notion__Workspaces__work`). The standard layering applies, so
  any key can be overridden by a later source.
- Validation lives in `IValidateOptions<T>` implementations and reports every problem at once. `Program` reads the
  options right after building the host and exits with one `Configuration error:` line per problem, before the
  server listens. `ValidateOnStart` stays on as a safety net.
- The rest of the app consumes `ServerSettings` and `WorkspaceRegistry`, the validated, ready-to-use forms (decoded
  key, trimmed lists), registered as singletons from the options.
- Token lifetimes became settings because they are the kind of value an operator may want to change. Limits that
  are part of the security behaviour and of the tests (login attempts, request body sizes, retry counts) stay
  constants.
- Guard tests keep the pieces together: every `ServerOptions` property is in the README table, `appsettings.json`
  declares every non-secret property with the value the code defaults to, and it contains no secret.

## Consequences

- One place shows what can be configured, with its default, and the command line, user-secrets and environment
  variables all work the way .NET developers expect.
- A blank or malformed value (`Server__TokenGeneration=two`, `Server__AuthDisabled=yes`) now stops the startup
  instead of being ignored or defaulted, which is what we want for revocation and authentication.
- Lists need one indexed variable per item (`Server__ExtraAllowedOrigins__0`) instead of a comma-separated string.
- Renaming a setting later is a breaking change for deployments; treat the key names as part of the public surface.
