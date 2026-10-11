# Architecture decision records

Short records of decisions that have trade-offs. Each one says what the situation was, what was decided and
what it costs. Add a new numbered file when you make a decision like these, and update the index below.

| ADR | Decision |
|---|---|
| [0001](0001-stateless-hmac-tokens.md) | Tokens, client ids and codes are stateless, HMAC-signed documents |
| [0002](0002-no-nuget-and-hand-written-mcp.md) | No NuGet packages in the server; the MCP protocol is hand-written |
| [0003](0003-single-replica-ephemeral-state.md) | One replica, with ephemeral in-memory state for anti-replay and login throttling |
| [0004](0004-options-pattern-and-layered-settings.md) | Settings use the Options pattern and the standard .NET configuration layers |

## Template

```markdown
# NNNN. Title

Status: accepted | superseded by NNNN

## Context
What forces are at play.

## Decision
What we do.

## Consequences
What gets easier, what gets harder, and what to revisit.
```
