# ADR 0004 — Same-origin cookie for refresh, access token in memory

- **Status:** Accepted
- **Date:** 2026-01-03
- **Deciders:** Aleksander Torka

## Context

The refresh token is long-lived and high-value; the access token is short-lived.
Storing either in `localStorage` exposes it to XSS. A separate Backend-for-Frontend
(BFF) would add a moving part we do not need, because the service is already
same-origin with the consumer (see ADR 0002).

## Decision

- Store the **refresh token** in an `HttpOnly; Secure; SameSite=Strict; Path=/auth`
  cookie. It is never readable by JavaScript and is only ever sent to `/auth`.
- Keep the **access token in memory** in the SPA, with a 5–10 minute lifetime.
- On PWA/app start, the frontend performs a silent `POST /auth/refresh` to obtain a
  fresh access token from the cookie.
- Refresh uses **rotation with reuse detection**; `POST /auth/logout` revokes.

## Consequences

- **Positive:** no tokens in `localStorage`; no separate BFF; `SameSite=Strict`
  plus `Path=/auth` limits CSRF surface; reuse detection catches stolen refresh
  tokens.
- **Negative:** access token is lost on full page reload until the silent refresh
  completes; cookie/proxy/iOS-PWA behaviour differs on real phones, so it is tested
  on a real device in week 4, not week 5.
