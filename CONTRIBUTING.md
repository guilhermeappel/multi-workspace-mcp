# Contributing

Thanks for your interest. The short version is below; [docs/development.md](docs/development.md) has the architecture, the project rules
and the code standards.

## Setup

Fork the repository and clone your fork (a pull request cannot come from a branch named `main`). You need the .NET 10 SDK (see `global.json`).

```bash
dotnet build NotionMultiMcp.slnx
dotnet test NotionMultiMcp.slnx
dotnet format NotionMultiMcp.slnx --verify-no-changes
```

The tests need no Notion account and no network: the app runs in memory against a fake Notion API. CI uploads a
coverage report; locally use `dotnet test --collect:"XPlat Code Coverage"`.

## Workflow

- Branch from `main` as `<type>/<kebab-case-description>` with type `feature`, `fix`, `hotfix`, `refactor`,
  `test`, `docs` or `chore`. CI rejects other names. One branch, one kind of change.
- Use [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `refactor:`, `test:`,
  `docs:`, `chore:`). The pull request title follows the same format.
- Every behaviour change adds or updates a test. Bug fixes start with a test that fails.
- Code, comments, documentation, log messages and commit messages are in English.
- Code style is enforced by the build and by `dotnet format`; run it before you push.
- Pin any new GitHub Action to a commit SHA and keep workflow `permissions` minimal.
- Update `README.md`, `docs/development.md` and `docs/architecture.md` when you change what they describe. Diagrams are
  Mermaid.
- Decisions with trade-offs get a short ADR in `docs/adr`.

## Project rules worth knowing first

- The server has **no NuGet packages** (only the ASP.NET Core shared framework). Do not add one without a real
  need; test packages are fine and live in `Directory.Packages.props`.
- No state that must survive a restart: the app scales to zero.
- Never log tokens, codes or passwords.

## Security

Do not report vulnerabilities in public issues. See [SECURITY.md](SECURITY.md).
