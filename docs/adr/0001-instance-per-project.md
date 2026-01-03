# ADR 0001 — One instance per consumer project

- **Status:** Accepted
- **Date:** 2026-01-03
- **Deciders:** Aleksander Torka

## Context

Auth-Core will be adopted by several unrelated projects over time. There is no
requirement for single sign-on between them and no shared user base. The
alternative would be a single multi-tenant deployment with a "realm" dimension
threaded through every table, query and cache key.

## Decision

Each consumer project runs its **own instance** of Auth-Core, with its own
database, its own users and its own signing keys.

## Consequences

- **Positive:** physical isolation between consumers; independent upgrades; no
  realm dimension in code, so queries and the schema stay simple; a security
  incident in one instance cannot leak another's data.
- **Negative:** more instances to deploy and operate; no cross-project SSO (this
  is explicitly a non-goal).
- Organizations still exist *inside* an instance for multi-company tenancy of a
  single consumer (see ADR on tenancy in a later record).
