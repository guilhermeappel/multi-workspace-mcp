# 0002. No NuGet packages in the server; the MCP protocol is hand-written

Status: accepted

## Context

The server holds Notion integration tokens, scales to zero (so cold start and image size matter) and is small:
it needs four MCP methods (`initialize`, `ping`, `tools/list`, `tools/call`). The official
`ModelContextProtocol.AspNetCore` SDK exists. It is not an OAuth authorization server, so `/authorize`,
`/token` and `/register` (most of the protocol code) would stay hand-written either way. The SDK's attribute
model also fights the per-workspace `enum` that is injected into every tool schema.

## Decision

The server uses only the ASP.NET Core shared framework, with no NuGet packages. The MCP Streamable HTTP
transport, in stateless JSON mode, is implemented by hand (`Mcp/`). Test projects may use packages, with
versions in `Directory.Packages.props`.

## Consequences

- A smaller supply chain around a service that holds credentials, a smaller image and a faster cold start.
- The project owns protocol conformance. HTTP-level tests and a review against the MCP specification keep that
  honest.
- Revisit when the server needs resources, prompts, elicitation, progress notifications or SSE: that is the
  point where the SDK earns its weight.
