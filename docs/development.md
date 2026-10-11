# Development guide

Architecture, project rules and code standards for working on this repository. Contributing workflow:
`CONTRIBUTING.md`. Usage guide: `README.md`. Deployment and operations: `docs/deployment.md`. Diagrams:
`docs/architecture.md`.

The server is a remote MCP server (Streamable HTTP) in .NET 10 that exposes **several Notion workspaces through a
single Claude connector**. It is deployed to Azure Container Apps from a container image on GHCR.

## Commands

```bash
dotnet build NotionMultiMcp.slnx
dotnet test NotionMultiMcp.slnx
dotnet format NotionMultiMcp.slnx --verify-no-changes   # style check (CI runs it); drop the flag to fix
dotnet run --project src/NotionMultiMcp          # http://localhost:8080 (Development)
docker build -t multi-workspace-mcp .
```

Local secrets go in `dotnet user-secrets` of the `src/NotionMultiMcp` project
(`Notion:Workspaces:<name>`, `Server:AuthPassword`, `Server:SigningKey`). To test without OAuth, run with
`Server__AuthDisabled=true` (environment variable) or `--Server:AuthDisabled=true`; this only works in
Development. Settings follow the standard .NET layering; see the Settings section of the README.

## Architecture

```
src/NotionMultiMcp/
  Program.cs              composition only: AddServerConfiguration(), AddOAuthServer(), AddMcpServer(), AddNotionClient(), pipeline
  appsettings.json        the committed, non-secret defaults of the Server section (a test keeps them equal to the code)
  ServerSettings.cs       the validated, ready-to-use form of ServerOptions (decoded key, trimmed lists)
  StartupConfiguration.cs validates the options at startup; each mistake becomes one clear line and exit code 1
  StartupLog.cs           what the app logs when it starts
  GlobalExceptionHandler.cs  last-resort exception handling in each endpoint's own error shape
  HealthEndpoints.cs      GET/HEAD / and /healthz
  Json.cs                 tolerant J.* helpers for JsonNode
  ToolException.cs        usage error shown to the model as a tool result
  Configuration/          the Options pattern: ServerOptions and NotionOptions (bound sections), their validators,
                          SigningKeyFormat, AddServerConfiguration()
  Auth/                   single-user OAuth 2.1 server
    OAuthEndpoints.cs     maps the endpoint classes below
    DiscoveryEndpoints.cs, RegistrationEndpoint.cs, AuthorizeEndpoints.cs, TokenEndpoint.cs
    AuthServiceCollectionExtensions.cs  AddOAuthServer()
    OAuthPaths.cs, OAuthConstants.cs    paths, GrantType, OAuthErrorCode, OAuthProtocol (RFC wire vocabulary)
    TokenPurpose.cs                     enum: why a token was signed (mapped to its signed text in TokenService)
    OAuthResults.cs, HtmlPage.cs, AuthorizeRequest.cs, RedirectPolicy.cs, PkceVerifier.cs
    TokenService.cs       stateless tokens (JSON + HMAC-SHA256), payload records
    LoginThrottle.cs      per-client and overall limits on password attempts (in memory)
    AuthorizationCodeLedger.cs  single-use enforcement for authorization codes (in memory)
  Mcp/                    the MCP protocol
    McpEndpoint.cs        POST /mcp (Origin, auth, version, JSON-RPC; no SSE, no session)
    McpHandler.cs         initialize / ping / tools/list / tools/call, JSON-RPC message validation
    OriginPolicy.cs, JsonRpc.cs, JsonRpcException.cs, McpServiceCollectionExtensions.cs (AddMcpServer())
    Tools/                the tools
      ToolCatalog.cs      tool definitions, workspace injection into schemas, instructions
      ToolDispatcher.cs   runs a call: failures to results, output bound, logging
      ReadTools.cs, WriteTools.cs   the eight handlers
      ToolContext.cs, ToolArguments.cs, ToolText.cs, DataSourceResolver.cs, Tool.cs   shared pieces
  Notion/
    NotionClient.cs       typed HttpClient for the REST API (pinned Notion-Version; retries 429, and 5xx/network
                          errors for reads only)
    NotionApiException.cs, NotionServiceCollectionExtensions.cs (AddNotionClient())
    WorkspaceRegistry.cs  Notion:Workspaces:<name> -> workspace "<name>"
    BlockRenderer.cs      blocks -> Markdown (recursive, with a block budget)
    MarkdownToBlocks.cs   Markdown -> blocks
    RichText.cs           rich_text <-> inline Markdown
    Properties.cs         property reading and simple values -> Notion format conversion
    NotionIds.cs, BlockTypes.cs, PropertyTypes.cs, NotionObjectTypes.cs   the Notion vocabulary as constants
tests/NotionMultiMcp.Tests/  xUnit; pure components, stubbed-HTTP tests and in-memory HTTP tests of the whole app
  Support/                  McpAppFactory (fake clock + fake Notion API), McpTestClient (plays Claude)
  RepositoryConventionsTests.cs   guards: typed results, documented settings and tools, English
deploy/azure-setup.ps1       one-time Azure infrastructure setup
docs/architecture.md         Mermaid diagrams (components, OAuth flow, request pipeline, deployment)
docs/adr/                    architecture decision records (why, not how)
```

Decisions with trade-offs are recorded as ADRs in `docs/adr`. Diagrams are Mermaid. When you change the OAuth
flow, the token format or the `/mcp` pipeline, update `docs/architecture.md` in the same change.

## Project rules

- **The server uses no NuGet packages**, only the ASP.NET Core shared framework. The MCP protocol is
  hand-written (stateless, JSON). Do not add packages without a real need. The test project may use
  packages, with versions in `Directory.Packages.props`.
- **No in-memory state that must survive a restart.** The container scales to zero. Client IDs, auth
  codes and tokens are signed with `Server:SigningKey`, and `Server:TokenGeneration` revokes every access and
  refresh token (changing `Server:SigningKey` invalidates everything). The only
  acceptable exception is ephemeral state, such as code anti-replay and the login throttle. Both are per
  process, so the app must keep `--max-replicas 1`.
- **Notion API version:** `NotionClient.ApiVersion` (`2026-03-11`). It uses `data_sources` (not
  `databases`) for schema and query, and `in_trash` (not `archived`). Read the upgrade guide on
  developers.notion.com before bumping the version.
- **Tools:** every tool that reaches Notion has `NeedsWorkspace: true`. The `workspace` parameter (an enum
  of the configured names) is injected into the schema automatically. Tool descriptions and messages are
  read by the model, so write them for it.
- **Retries:** a write must never be repeated after a server error or a network failure, because Notion may
  have applied it. Use `PostAsync`/`PatchAsync` for writes and `GetAsync`/`PostReadOnlyAsync` (search, query)
  for reads. A 429 is retried for everything. When a multi-request write fails half way (`create_page` with more than
  100 blocks, `append_content`), report what already exists and how many blocks landed; never a bare error.
- **Protocol:** `/mcp` follows the MCP Streamable HTTP transport and JSON-RPC 2.0 strictly: it validates `Origin` and
  `MCP-Protocol-Version`, answers a malformed message with `-32600` and HTTP 400, and returns the `error="invalid_token"`
  challenge only when a token was actually sent. Keep OAuth and JSON-RPC error bodies in their RFC shapes
  (snake_case `error`/`error_description`, numeric JSON-RPC codes); do not reshape them.
- **Startup validation:** a settings mistake stops the app with one clear line (`Configuration error: …`) and exit code 1,
  never a silent default: revocation (`Server:TokenGeneration`), the password (16+ characters) and the signing key (32+
  bytes with real variety) are validated by `ServerOptionsValidator`, the workspaces by `NotionOptionsValidator`. Report
  every problem, not only the first. A new setting is a property on `ServerOptions` (secret or not), a default in
  `appsettings.json` if it is not a secret, a validator rule and a row in the README Settings table; guard tests
  fail if one of those is missing.
- **Login throttle:** count an attempt *before* checking the password and refund it on success; never gate on a
  separate check-then-record.
- **Tool errors:** throw `ToolException` for usage errors. `NotionApiException` becomes a message with a
  hint. Both return `isError: true` and do not become JSON-RPC errors.
- **JsonNode:** a node can only have one parent. Use `DeepClone()` when reusing nodes. The
  `J.ObjectArg`/`J.ArrayArg` helpers already return copies.
- **Notion limits:** 100 blocks per request (`AppendBlocksAsync` batches them), 2 nesting levels per
  request and 2000 characters per text object (`RichText` already splits).
- **Input validation:** a property value that cannot be converted (a number that is not a number, a checkbox
  that is not true/false) is a `ToolException`, never a silent clear of the field. Request bodies are capped
  (16 KiB on the OAuth endpoints, 2 MiB overall).
- **Security:** never log tokens, codes or the password. Failed logins, lockouts and rejected `/mcp`
  requests are logged at `Warning`, without secrets. ASP.NET and `HttpClient` logs stay at `Warning` so
  the `/authorize` query string and Notion request URLs are not leaked.

## Code standards

Conventions of this repository. `.editorconfig` covers formatting, style and naming in the editor; what it
cannot express is here.

### Approach

- Prefer reusing and updating the existing helpers (`J.*`, `RichText`, `Properties`, `NotionIds`) over
  creating new ones. Keep changes focused, consistent with what exists and covered by tests.
- Run the narrowest test, build or check that can falsify a change before widening the scope.
- Deployment is a manual operation (see CI/CD below); a merged change is not deployed by itself.

### Language

Everything in this repository is written in **English**: code, identifiers, comments, documentation, UI
text (such as the login page), log messages, error messages, scripts, commit messages and pull request
text.

### Tests, commits and git

- Every behaviour change adds or updates a test (xUnit). CI collects coverage (coverlet) and uploads the Cobertura
  report as an artifact; use it to find untested behaviour. Pure components: no network. HTTP flows (OAuth and
  `/mcp`): `WebApplicationFactory`.
- Conventional Commits: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`. The pull request title
  uses the same format.
- **Branches:** `<type>/<kebab-case-description>`, always from `main` (or from the previous branch when the
  work is stacked). CI rejects pull requests from branches outside this pattern (Dependabot branches are allowed). One
  branch, one kind of change. The description says what the branch contains, not the language or the tooling.

  | Branch | When | Commit |
  |---|---|---|
  | `feature/` | new functionality (tool, endpoint) | `feat:` |
  | `fix/` | bug fix | `fix:` |
  | `hotfix/` | urgent fix to what is already in production | `fix:` |
  | `refactor/` | restructuring without changing behaviour | `refactor:` |
  | `test/` | tests only | `test:` |
  | `docs/` | documentation only | `docs:` |
  | `chore/` | build, CI, dependencies, configuration | `chore:` |

### C#

- Always use braces on control structures. Comment only to explain intent or the reason behind a decision.
- No *magic strings* or *magic numbers* in logic: extract named constants (see `OAuthErrorCode`, `BlockTypes`,
  `JsonRpcErrorCode`). Not magic strings: JSON field names in switches and schemas, model-facing text, and log
  and error messages.
- A closed set the server owns is an `enum` (`TokenPurpose`, `MessageKind`); when it also has a wire form, one
  `switch` maps it to the text and throws on an unknown value, and a test pins that text if tokens already
  issued depend on it. A set someone else owns and can extend (Notion types, RFC values) stays a class of
  constants.
- **Naming:** private fields `_camelCase`; constants `PascalCase` at every visibility; interfaces prefixed
  with `I`; async methods end with `Async` and take `CancellationToken cancellationToken` as the last
  parameter, **required** in internal code (a forgotten token is a silent bug). `= default` only on public
  contracts consumed by other callers. Test method names are sentences and are exempt from the `Async` suffix.
- **Types:** default visibility is `internal sealed`, unless the type is exported on purpose. The test
  project sees internal types through `InternalsVisibleTo`. `sealed record` for immutable
  `Request`/`Response`/`Result`/`Dto` types; `sealed class` for `Settings` and stateful services. Do not use
  `Model` as a generic suffix.
- Do not zero-initialise non-collection properties: use `required`, nullable types or empty collections.
- **Always `var`** for local variables, with no exceptions. If a variable has to exist before a
  `try/catch`, extract the `try/catch` into a private method that returns the value instead of declaring an
  explicit type.
- Compare strings with `string.Equals(a, b, StringComparison.X)`, not `==`/`!=`.
- Mapping between types is manual and explicit. No AutoMapper/Mapster; if the repetition becomes a real
  problem, use Mapperly (source generator, compile-time errors).
- String-to-enum/status mappings use a `switch` that **throws** on an unknown value, never a silent default.
- Expected errors do not become generic exceptions: here they are `ToolException` / `NotionApiException`.
  Result libraries (`ErrorOr` and similar) do not apply, because the server uses no NuGet packages.

### .NET 10 and C# 14

- Target `net10.0`, `LangVersion latest`, SDK pinned in `global.json`. Use the current language features:
  collection expressions (`[]`), primary constructors, file-scoped namespaces, the `field` keyword instead
  of a manual backing field, `System.Threading.Lock` instead of locking on an `object`,
  `[GeneratedRegex]`, `FrozenDictionary`/`FrozenSet`/`SearchValues` for static read-only sets, and
  `TypedResults` in endpoints.
- The current time comes from `TimeProvider`, never from `DateTimeOffset.UtcNow`/`DateTime.UtcNow`
  directly, so the code is testable.
- `TreatWarningsAsErrors` is on. Test package versions live in `Directory.Packages.props` (central package
  management); `.csproj` files carry no `Version`.
- Style is enforced. `EnforceCodeStyleInBuild` is on, so `.editorconfig` rules with `warning` or `error`
  severity fail the build, and CI also runs `dotnet format --verify-no-changes` (formatting, naming and the
  rules the build does not report). Fix violations with `dotnet format` or by hand; do not lower a severity
  to make a check pass.
- The .NET SDK analyzers run at the `Recommended` set (`AnalysisLevel`/`AnalysisMode` in `Directory.Build.props`,
  no NuGet package). The few rules turned off are listed in `.editorconfig`, each with its reason; a new
  exception needs the same.
- **Notion vocabulary:** block, property and object type names are constants (`BlockTypes`, `PropertyTypes`,
  `NotionObjectTypes`), never literals in a `switch`. They stay strings, not enums, because Notion adds types
  over time and an unknown one must fall through to a fallback arm instead of throwing. Enums are for closed sets
  the server owns (for example `MessageKind`); see the rule on closed sets in the C# section.

## How to add a tool

1. Implement the handler `Task<string> XxxAsync(JsonObject args, CancellationToken cancellationToken)` in
   `ReadTools` (it only reads) or `WriteTools` (it changes something), using `context.Token(args)`,
   `ToolArguments.Required(...)` and `NotionIds.Parse(...)`.
2. In `ToolCatalog.BuildTools()`, add a `yield return new Tool(...)` with the JSON schema (without `workspace`),
   the annotations (`ReadOnly`, `Destructive`, `Idempotent`) and the handler.
3. If there is pure logic (conversion or parsing), put it in `Notion/` and cover it with a test.
4. Update the tools table in the README (`RepositoryConventionsTests` fails until you do).

## CI/CD

`.github/workflows/ci.yml` runs on pull requests and pushes to `main`. On pull requests it checks the branch
name; it always checks the code style, builds and tests, and builds the image (without publishing it) so a Dockerfile
error fails the pull request; on `main` it publishes the image
`ghcr.io/<owner>/multi-workspace-mcp` with the tags `latest` and `sha-xxxxxxx`. Deployment is manual:
`az containerapp update -n notion-mcp -g rg-notion-mcp --image ghcr.io/<owner>/multi-workspace-mcp:sha-xxxxxxx`.

Supply chain: third-party actions are pinned to a commit SHA (the tag stays in a comment), the Dockerfile pins
its base images by digest, `nuget.config` maps every package to nuget.org, and Dependabot (`.github/dependabot.yml`)
keeps actions, packages and base images current. When you add a workflow step, pin its action by SHA and keep
`permissions` minimal (`packages: write` only on the publish job).
