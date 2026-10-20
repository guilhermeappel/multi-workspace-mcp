# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Versions are cut by pushing a `vX.Y.Z` tag (see
`.github/workflows/release.yml`).

## [Unreleased]

### Added

- `delete_block`: moves one content block of a page, with the blocks nested in it, to the trash. It takes a block id or
  a block link (a page URL ending in `#<block id>`), refuses pages and databases, also when they are nested inside the
  block, and is refused in read-only workspaces.
- `fetch_page` takes `include_block_ids`, which writes the id of each block before it, so `delete_block` can be pointed
  at one.

## [0.3.0] - 2026-10-20

### Added

- `move_page`: moves a page, with its subpages, under another page or into a database. A database id or URL is
  resolved to its data source, a page cannot be moved under itself, and the move is refused in read-only workspaces.

## [0.2.0] - 2026-10-11

### Added

- `Notion:ReadOnlyWorkspaces`: workspaces in which the tools that create, append or change are refused before Notion is
  called. `list_workspaces` shows which ones are read-only, and a name that is not a configured workspace stops the
  startup, so a typo cannot leave a workspace writable.

### Security

- Inline Markdown parsing has a match time limit and a total time budget per conversion, falls back to plain text
  when either is hit, and the Markdown of one call is limited to 100,000 characters. A long run of `**a ` made
  parsing take minutes and could stall the whole app.
- A client of the login throttle is now an IPv4 address or an IPv6 /64, so one network cannot get a fresh budget per
  address. The documentation now states that the server-wide limit can keep the owner from starting a new login.
- `Server:SigningKey` rejects a passphrase that is valid base64; use 32 random bytes (`openssl rand -hex 32`).
- A JSON-RPC batch is limited to 20 messages.
- `deploy/azure-setup.ps1` rejects passwords with `%`.

### Changed

- The GitHub Actions used by the CI and the release workflow were updated (checkout 7, metadata-action 6, among
  others), and the release workflow was checked with a pre-release tag.

## [0.1.0] - 2026-10-11

### Added

- A remote MCP server (Streamable HTTP, .NET 10) that exposes several Notion workspaces through one connector,
  with eight tools to search, read, query, create and update pages and databases.
- Single-user OAuth 2.1 (authorization code with PKCE S256, dynamic client registration) behind a password page,
  with a login throttle. Tokens are HMAC-signed, so there is no database.
- Validated settings: a configuration mistake stops the app with one clear line per problem.
- `deploy/azure-setup.ps1` to create the Azure Container Apps resources, and a deployment guide.
- Architecture diagrams, architecture decision records, a development guide and community files.
- Release workflow that publishes a versioned container image and creates a GitHub release.

### Changed

- The deployment guide and the README now explain the cold start after scaling to zero and the option to keep
  one replica running.
- Notion block, property and object types are named constants, `TokenPurpose` is an enum, and the .NET SDK
  analyzers run at the Recommended set.

### Fixed

- `deploy/azure-setup.ps1` installs the `containerapp` Azure CLI extension only when `az containerapp` is not
  already built in.
