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

- A smaller supply chain around a service that holds credentials.
- The image and cold start gains are small. Referencing `ModelContextProtocol.AspNetCore` 2.2.0 (without using it)
  adds four assemblies, about 2.1 MB, to a 118.5 MB image (uncompressed). On Azure the app was listening about
  0.3 seconds after the container started, out of about 22 seconds from scheduling the replica, so almost all of the
  cold start happens before the app's code runs (one measurement each, 2026-10-14; see
  [Cold start and cost](../deployment.md#cold-start-and-cost)).
- The project owns protocol conformance. HTTP-level tests and a review against the MCP specification keep that
  honest.
- Revisit when the server needs resources, prompts, elicitation, progress notifications or SSE: that is the
  point where the SDK earns its weight.
