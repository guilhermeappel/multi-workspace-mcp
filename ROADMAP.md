# Roadmap

These are ideas, not promises. Nothing here has a date. If one of them matters to you, open an issue to say so
and why: that is how an idea moves up, and a pull request is the fastest way to move it.

## Near term

- **A cold start option in `deploy/azure-setup.ps1`.** Today the script always creates the app with zero to one
  replica. A parameter for the minimum number of replicas would make the choice described in
  [Cold start and cost](docs/deployment.md#cold-start-and-cost) part of the setup.
- **More Notion coverage.** Comments, moving a page, and editing a specific block are not supported (see
  [Limitations](README.md#limitations)). Each one is a self-contained tool: see "How to add a tool" in
  [docs/development.md](docs/development.md).
- **Deployment guides for other hosts.** The image is a plain container, so the guide could also cover
  other platforms that run one. The constraint to keep is a single instance
  ([ADR 0003](docs/adr/0003-single-replica-ephemeral-state.md)).

## Open question

- **Other services besides Notion.** The server is built around Notion today, and a second service would need a
  provider abstraction in the tool layer. That is a design change, so it starts as an ADR proposal and a
  discussion, not as a pull request. Interest from people who would use it is what decides whether it happens.

## Not planned

- A shared or hosted instance. The server is deliberately single-user (see
  [Who this is for](README.md#who-this-is-for)).
- A database. Statelessness is what lets the app scale to zero
  ([ADR 0001](docs/adr/0001-stateless-hmac-tokens.md)).
