# 0001. Tokens, client ids and codes are stateless, HMAC-signed documents

Status: accepted

## Context

The server is a single-user OAuth 2.1 authorization server, and it runs on Azure Container Apps with the
replica count scaled to zero when idle. Anything kept in memory is lost on every cold start, and a database
would add cost, latency and an operational surface for one user.

## Decision

Client registrations (the `client_id` itself), authorization codes, access tokens and refresh tokens are JSON
documents signed with HMAC-SHA256 using `Server:SigningKey`:

```
base64url(json) "." base64url(HMAC-SHA256(Server:SigningKey, purpose + "|" + base64url(json)))
```

The purpose (`client`, `code`, `access`, `refresh`) is part of the signed data, so a token issued for one purpose
is rejected for any other. Signatures are compared in constant time and a payload is only deserialised after
its signature is verified.

A refresh token lives 90 days and slides forward every time it is used, but never past the end of the session it
belongs to: a session starts at the login and lasts at most 180 days (`SessionStartedAt` is carried inside the
refresh token), so a stolen refresh token cannot be kept alive for ever by using it.

Revocation has two levers: `Server:TokenGeneration` (a number inside access and refresh tokens; incrementing it
invalidates all of them) and `Server:SigningKey` (changing it invalidates everything, including client registrations).

## Consequences

- No database, and the container can scale to zero without signing Claude out.
- A token cannot be revoked on its own, and refresh tokens are not one-time use: rotation and reuse detection
  need state. A stolen refresh token works until `Server:TokenGeneration` is incremented or the token expires.
- The key is the crown jewel: whoever holds `Server:SigningKey` can forge access tokens. It must be random, long and
  kept in a secret store.
- Revisit this if the server ever serves more than one user, or if strict refresh-token rotation is required.
