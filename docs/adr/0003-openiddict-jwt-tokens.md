# ADR 0003 — OpenIddict issuing standard JWTs

- **Status:** Accepted
- **Date:** 2026-01-03
- **Deciders:** Aleksander Torka

## Context

The service must issue access tokens that a Python (FastAPI) consumer can verify
offline, plus handle refresh rotation, revocation and key publication. Options
considered:

- **`MapIdentityApi`** (ASP.NET Core Identity's built-in endpoints): its bearer
  tokens are an opaque, .NET-specific format that non-.NET consumers cannot verify.
  Rejected.
- **Duende IdentityServer:** requires a commercial license. Rejected.
- **Hand-rolled JWT issuing:** re-implements rotation, revocation and JWKS — the
  exact things that are easy to get wrong. Rejected.

## Decision

Use **OpenIddict 7** (Apache 2.0) to issue standard **RS256 JWT** access tokens and
manage refresh flows, rotation, revocation and JWKS. ASP.NET Core Identity handles
passwords, lockout and email/reset tokens underneath.

## Consequences

- **Positive:** standard JWTs verifiable in any language via a cached JWKS;
  rotation, revocation and key handling are not hand-rolled; permissive license.
- **Negative:** OpenIddict has a learning curve, especially for a custom
  `POST /auth/login` endpoint (see the week-1 spike risk).
- **Must configure:** `DisableAccessTokenEncryption()` (OpenIddict encrypts access
  tokens by default, which Python cannot read) and `RegisterAudiences(...)`
  (OpenIddict 7 rejects unregistered audiences).
