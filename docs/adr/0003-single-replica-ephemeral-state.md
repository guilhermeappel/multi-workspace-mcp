# 0003. One replica, with ephemeral in-memory state

Status: accepted

## Context

Tokens are stateless (ADR 0001), but two small pieces of state are useful and cheap to keep in memory:
single use of authorization codes (anti-replay) and the login throttle. Sharing them across replicas would need
an external store.

## Decision

Both live in memory, per process (`AuthorizationCodeLedger`, `LoginThrottle`), and the app is deployed with
`--min-replicas 0 --max-replicas 1`.

The login throttle gives each client 10 attempts per 15 minutes and the whole server 60. A client is an IPv4 address or
an IPv6 /64 network, because one subscriber usually controls a whole /64 and single addresses would give an attacker a
fresh budget each. An attempt is
counted under a lock *before* the password is checked, and refunded when the password is right, so concurrent
guesses cannot slip through. The client address comes from `X-Forwarded-For`: the app trusts that header because
it is only reachable through the Container Apps ingress, which sets it. A single global counter was rejected: with
`/register` open to anyone, anybody who knows the URL could lock the owner out of the login with ten wrong
passwords. The server-wide limit that does exist has the same cost on a smaller scale (see the consequences).

## Consequences

- Anti-replay is best effort: it is lost on restart. That is acceptable because codes live for five minutes and
  are also bound to a PKCE verifier.
- Running the app anywhere that does not sit behind a trusted proxy lets a client choose its own address through
  `X-Forwarded-For`. The server-wide limit still applies, but the per-client one is then easy to evade.
- The server-wide limit lets an attacker who uses at least six networks use up the budget and keep the owner from
  starting a new login for up to 15 minutes at a time. Sessions that already exist are not affected, because refresh
  tokens do not go through the throttle, and the attacker learns nothing. It is accepted: the alternative is no
  protection against guesses spread over many addresses, and a second factor is out of scope for a single-user server.
- The throttle resets when the app scales to zero, so it slows guessing down but does not stop a patient
  attacker: a long, random `Server:AuthPassword` is the real protection.
- Raising `--max-replicas` breaks both guarantees (each replica would count on its own). If that is ever
  needed, move them to a shared store first.
