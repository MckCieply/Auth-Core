---
title: Comparison
description: Auth-Core next to Better Auth, Keycloak, Clerk and WorkOS, with a source for every fact.
---

The same alternatives as the README, in the same order.

## When to pick which

|  | Pick it when | Auth-Core instead when |
|---|---|---|
| **Better Auth** | Your backend is TypeScript and you want auth as a library inside the app, with 2FA and passkeys. | Your backend is in another language, or you want auth apart from the app, with its own database. |
| **Keycloak** | You need SSO across products, SAML or LDAP, or the broadest set of standards. | You want a small service for one product, with companies and roles built in. |
| **Clerk** | You want ready-made sign-in screens and MFA today, as a service. | You want to host it yourself, keep the users' data on your server, and pay a flat yearly price. |
| **WorkOS** | You sell to enterprises and need SSO and directory sync per customer. | Your customers sign in with a password or Google, Microsoft or GitHub, and you want to host it yourself. |
| **Auth-Core** | You want one self-hosted service per product, isolated from the app, any backend language. | |

Auth-Core does not fit when you need MFA today, SSO across products, SAML or SCIM, ready-made screens, or a TypeScript backend that
wants auth inside the app.

## The facts

|  | How it runs | Licence | Companies and roles | Sign-in screens | MFA today | SSO between products, SAML | How you pay |
|---|---|---|---|---|---|---|---|
| **Better Auth** | a TypeScript framework in your codebase[^ba-docs] | MIT[^ba-licence] | a plug-in, with roles[^ba-org] | not stated (read: documentation introduction)[^ba-docs] | yes, a plug-in[^ba-2fa] | a plug-in; SAML in it[^ba-sso] | free of charge under the MIT licence[^ba-licence] |
| **Keycloak** | a server you install and run[^kc-start] | Apache 2.0[^kc-licence] | an organizations feature you enable[^kc-admin] | login pages from Keycloak, with themes[^kc-admin] | yes[^kc-admin] | yes; yes[^kc-home] | open source[^kc-home] |
| **Clerk** | a service you subscribe to[^cl-terms] | proprietary[^cl-terms] | organizations with roles[^cl-org] | prebuilt components[^cl-docs]; pages hosted by Clerk[^cl-portal] | yes[^cl-mfa] | enterprise connections; SAML in them[^cl-sso] | per monthly retained user, and per extra SSO connection[^cl-pricing] |
| **WorkOS** | a hosted service[^wo-terms] | proprietary[^wo-terms] | organizations with roles[^wo-roles] | a hosted, pre-built UI (AuthKit)[^wo-ui] | yes[^wo-mfa] | yes; yes[^wo-sso] | per monthly active user, and per connection[^wo-pricing] |
| **Auth-Core** | a service you host | BSL 1.1 | built in | your own | planned, no date | no; no | EUR 490 a year per product, flat |

The Auth-Core row is the README at the build commit.

Checked: 26 Nov 2026.

[^ba-docs]: Better Auth, documentation: <https://www.better-auth.com/docs/introduction>
[^ba-licence]: Better Auth, licence: <https://github.com/better-auth/better-auth/blob/main/LICENSE.md>
[^ba-org]: Better Auth, organization plug-in: <https://www.better-auth.com/docs/plugins/organization>
[^ba-2fa]: Better Auth, two-factor plug-in: <https://www.better-auth.com/docs/plugins/2fa>
[^ba-sso]: Better Auth, SSO plug-in: <https://www.better-auth.com/docs/plugins/sso>
[^kc-home]: Keycloak, home page: <https://www.keycloak.org/>
[^kc-start]: Keycloak, getting started: <https://www.keycloak.org/getting-started/getting-started-zip>
[^kc-licence]: Keycloak, licence: <https://github.com/keycloak/keycloak/blob/main/LICENSE.txt>
[^kc-admin]: Keycloak, Server Administration Guide: <https://www.keycloak.org/docs/latest/server_admin/index.html>
[^cl-docs]: Clerk, documentation: <https://clerk.com/docs>
[^cl-portal]: Clerk, Account Portal: <https://clerk.com/docs/guides/account-portal/overview>
[^cl-terms]: Clerk, terms of service: <https://clerk.com/legal/terms>
[^cl-org]: Clerk, organizations: <https://clerk.com/docs/organizations/overview>
[^cl-mfa]: Clerk, sign-up and sign-in options: <https://clerk.com/docs/authentication/configuration/sign-up-sign-in-options>
[^cl-sso]: Clerk, enterprise connections: <https://clerk.com/docs/authentication/enterprise-connections/overview>
[^cl-pricing]: Clerk, pricing: <https://clerk.com/pricing>
[^wo-terms]: WorkOS, terms of service: <https://workos.com/legal/terms-of-service>
[^wo-roles]: WorkOS, roles and permissions: <https://workos.com/docs/authkit/roles-and-permissions>
[^wo-ui]: WorkOS, hosted UI: <https://workos.com/docs/authkit/hosted-ui>
[^wo-mfa]: WorkOS, multi-factor authentication: <https://workos.com/docs/authkit/mfa>
[^wo-sso]: WorkOS, single sign-on: <https://workos.com/docs/sso>
[^wo-pricing]: WorkOS, pricing: <https://workos.com/pricing>

[Is it for me?](/Auth-Core/fit/ "Is it for me?") lists what Auth-Core needs and where it stops.
