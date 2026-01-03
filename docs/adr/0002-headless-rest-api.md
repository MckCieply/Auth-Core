# ADR 0002 — Headless REST API, consumer owns the UI

- **Status:** Accepted
- **Date:** 2026-01-03
- **Deciders:** Aleksander Torka

## Context

Consumers are first-party applications that already have their own frontends and
design systems. A hosted login UI (the classic OIDC redirect dance) would add
redirects, a second origin, CORS and a foreign look and feel. Passkeys and cookies
also behave best when bound to the app's own domain.

## Decision

Auth-Core exposes a **headless REST API** only. Each consumer builds its own login,
reset, invite and verification screens and calls the API directly. The service is
mounted at `/auth` on the consumer's own domain via a reverse proxy, so everything
is same-origin.

## Consequences

- **Positive:** native UX with no redirects; no CORS; passkeys and cookies bind to
  the app's domain; any language can call plain REST.
- **Negative:** each consumer re-implements auth screens (mitigated later by
  optional shared packages); no out-of-the-box hosted login page for third-party
  tools (a hosted OIDC module is a "Beyond MVP" item).
- The API is described in OpenAPI so typed clients can be generated later.
