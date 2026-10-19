# Spec 0014 — An AI-agent kit for products built on Auth-Core

- **Status:** Draft, for the owner's review
- **Date:** 2026-10-19
- **Author:** Alex
- **Milestone:** Direction A, wave 1 — *a developer's coding agent sets up, connects and reviews a product on Auth-Core*.
- **Context:** specs [0005](0005-tenancy-and-rbac.md) (the manifest, the company API, the operator's commands),
  [0006](0006-python-consumer-package.md) (the Python package), [0007](0007-angular-sample.md) (the Angular sample),
  [0008](0008-hardening-and-release.md) (the image, the release, the secret scan), [0009](0009-self-service-signup.md),
  [0010](0010-private-accounts.md) and [0011](0011-google-and-microsoft-sign-in.md) (the manifest keys `signup`, `private`
  and `providers`). This spec adds one command to the server and changes no endpoint, no manifest rule and no setting.
  Slice 0013 of `docs/design.md` (threat model, guides and runbooks for the new flows; the release `v0.2.0`) is a different
  slice. **This slice merges to `main` only after `v0.2.0` is tagged**, and is released as `0.3.0`.

## Goal

A developer builds a product that signs its people in with Auth-Core. They work with a coding agent: Claude Code, OpenAI
Codex, GitHub Copilot or Cursor. They install the kit once and their agent can then:

- ask them, one plain question at a time, who gets an account, which sign-in buttons the product shows, what it protects
  and who may do what, and write the product's `auth.yaml` and an environment template with no secret value in it;
- connect a FastAPI backend and an Angular frontend the way the guides and samples do, and add a smoke test;
- add a role or a permission and guard an endpoint, with a test that a caller without the permission gets `403`;
- review the product and write a report of what is unsafe, worst first, each finding with its place and its fix.

Two hooks run in the developer's project whatever the agent is asked to do: one stops a secret value from being written into
a file git would commit, and one checks `auth.yaml` after every edit with the server's own validator, run from the image of
the developer's server version. Where a tool does not run hooks, the kit says so plainly and the agent runs the same two
checks by hand with the same scripts.

Concretely, this is met when this works in a fresh copy of the sample product, with Docker and Node on the machine, once
`v0.3.0` is tagged (before the tag, the same with `skills` as the source in place of the URL, from a clone of the release
branch):

```
npx skills add https://github.com/MckCieply/Auth-Core/tree/v0.3.0/skills -a claude-code -y
claude                                   # "set up Auth-Core" -> the quiz -> auth.yaml, auth-core.local.env.example,
                                         #   .auth-core/, AGENTS.md section, CLAUDE.md, hook configs for the four tools
# the agent writes AUTH_SMTP_PASSWORD=<a value> into a tracked file -> the write is refused with a message
# the agent writes `default_roles: {}` into auth.yaml -> the agent is told
#   "Auth-Core manifest check: auth.yaml is not valid for Auth-Core 0.3.0: default_roles must hold between 1 and 100 roles. ..."
#   and fixes the file

# the same check by hand (POSIX shell)
docker run --rm --network none --mount type=bind,source="$PWD/auth.yaml",target=/manifest/auth.yaml,readonly \
  ghcr.io/mckcieply/auth-core:0.3.0 validate-manifest /manifest/auth.yaml
                                         # -> exit 0, "valid" and a summary; nothing else configured
```

On Windows, in Git Bash, write `MSYS_NO_PATHCONV=1` before `docker` (Git Bash would turn `/manifest/auth.yaml` into a path of
its own) and `source="$(cygpath -m "$PWD/auth.yaml")"`, as `scripts/e2e-prod.sh` does; in PowerShell write the whole value
quoted, `"type=bind,source=$PWD\auth.yaml,target=/manifest/auth.yaml,readonly"`.

`0.3.0` is the release that first contains the kit: the one after `0.2.0`, which slice 0013 releases before this slice
merges. `validate-manifest` is in no image before it. The skills carry the number of the release that ships them (Settled 21), so if the kit ships under
another number, every `0.3.0` in this spec is that number.

## In scope

- `skills/` in this repository, under the MIT License: four skills (`auth-core-setup`, `auth-core-integrate`,
  `auth-core-permissions`, `auth-core-security-report`) in the Agent Skills format, the hook scripts, the hook
  configuration templates for the four tools, the installer of the hooks, the report's scanner and the example manifests.
- The server command `validate-manifest <path>`, the only server change, and the shared manifest file reader it needs.
- `docs/api/endpoints.txt`, the list of endpoints, read by `OpenApiTests` and by the new check script; the test helper
  `tests/Auth.IntegrationTests/Infrastructure/RepositoryFiles.cs`, which `OpenApiTests`, `ShippedConfigurationTests` and the
  new example test share (and the edits of those two classes that this needs).
- `scripts/check-skills.py` with its self-test (a standalone script: `scripts/check-docs.py` and `scripts/check-pins.py` stay
  as they are, Settled 13); the C# test of the example manifests; the Node tests of the hooks, the installer and the report's
  scanner, with the recorded hook payloads they read; `scripts/test-skills-linux.sh`, which runs them on Linux; the release
  eval `scripts/eval-skills.sh` with its fixtures; a step in `scripts/e2e-prod.sh` (and its header comment) that runs the
  command in the built image.
- The hook probe, which the owner runs by hand from a checklist before the plan's hook tasks start: it records what each
  tool sends to a hook and does with the answer ("The hook probe").
- `.gitattributes` (the line endings of what `skills/` and the tests under `tests/` hold).
- `docs/integration/angular.md`: its stale sentence at `:45` ("Copy the three files ... They depend on nothing else of the
  sample") and the count at `:8`. `auth.service.ts` imports `../account-api` (`auth/auth.service.ts:4`) and `account-api.ts`
  imports back (`account-api.ts:4`), so the files to copy are four, the fourth at `src/app/account-api.ts`.
- `docs/workflow.md` (the checks before a merge, the new rule, the release steps); the root `LICENSE`; `README.md` (the new
  part, and its sentence at `:106` that an Angular app "copies three files (a service, an interceptor and a guard)", which
  becomes four); `CHANGELOG.md`.

## Out of scope

- Wave 2, parked: skills for debugging, upgrading and deploying; React and Flutter samples; a project starter; store plugins
  (a Claude Code plugin, a Cursor or VS Code extension), later and only if clients ask; sign-in for AI agents (issue #4).
- Any change to an endpoint, to the manifest's rules or to a setting of the server.
- Running Auth-Core locally from the released image, without a clone of this repository: a separate future issue. Today the
  guides use the development stack of a clone (`deploy/docker-compose.yml`, which builds the image from source), and so does
  the kit.
- Guarding writes made by shell commands (`echo ... > file`, `sed -i`): the hooks see the agents' file-edit tools only.
  The security report and the repository's own secret scanning cover what slips through.
- Eval runs in Codex, Copilot and Cursor: those three get a manual check per release.
- Translating the skills: their folder names and texts are English; an agent triggers on a request in any language.

## Contract

### Repository layout and licence

```
skills/
  LICENSE                                    MIT, the text of docs/LICENSE
  auth-core-setup/
    SKILL.md
    references/quiz.md                       the questions, their plain wording, what each answer writes
    references/env-templates.md              the two environment templates, line by line
    references/agents-section.md             the AGENTS.md section, as written into a project
    scripts/install-hooks.mjs                copies the hooks into a project, merges the four configurations, writes the
                                             AGENTS.md section and the CLAUDE.md line
    scripts/hooks/secret-guard.mjs           the two entry points: no logic, no static import, they never fail the work
    scripts/hooks/manifest-check.mjs
    scripts/hooks/secret-guard-run.mjs       what each entry point loads
    scripts/hooks/manifest-check-run.mjs
    scripts/hooks/hook-io.mjs                the per-tool reading of a hook's input and writing of its answer
    scripts/hooks/project.mjs                the project root, kit.json, the version rule, git and the failure line
    scripts/hooks/secrets.mjs                the detectors of secret values; self-contained: it imports nothing
                                             (not `project.mjs`, not `hook-io.mjs`, no other file), so the report skill's copy runs alone
    assets/hook-configs/claude-settings.json
    assets/hook-configs/codex-hooks.json
    assets/hook-configs/copilot-hooks.json
    assets/hook-configs/cursor-hooks.json
    examples/invite-only.yaml                example manifests; each passes the server's parser
    examples/company-self-service.yaml
    examples/private-persons.yaml
    examples/companies-private-and-providers.yaml
  auth-core-integrate/
    SKILL.md
    references/fastapi.md
    references/angular.md
    references/other-stacks.md
    references/smoke-test.md
    assets/angular/auth/auth.service.ts      byte-identical copies of the four files of samples/notes-web (889 lines),
    assets/angular/auth/auth.interceptor.ts  laid out as in the sample: the three under src/app/auth/ ...
    assets/angular/auth/auth.guard.ts
    assets/angular/account-api.ts            ... and account-api.ts under src/app/
  auth-core-permissions/
    SKILL.md
    references/existing-companies.md
  auth-core-security-report/
    SKILL.md
    references/checks.md
    scripts/scan-tracked.mjs                 the detectors over the files git tracks; prints path:line:detector, never a value
    scripts/secrets.mjs                      a copy of auth-core-setup/scripts/hooks/secrets.mjs, byte for byte; it imports nothing, so it needs no other file
tests/skills-hooks/                          Node tests of the hooks, the installer and the scanner (not installed into projects)
tests/skills-hooks/fixtures/                 the payloads of the hook probe, one file per tool, surface and case
tests/skills-hooks/run.mjs                   the runner: node tests/skills-hooks/run.mjs
tests/skills-hooks/support/                  the probe's kit (recording hook, configurations, checklist, normaliser) and the docker stub
tests/skills-eval/                           fixtures of the release eval (not installed into projects)
docs/api/endpoints.txt
scripts/check-skills.py
scripts/eval-skills.sh
scripts/test-skills-linux.sh
```

- Everything under `skills/` is MIT. `skills/LICENSE` is the MIT text with the copyright line of `docs/LICENSE`.
- The root `LICENSE`, parameter **Licensed Work**: its sentence that excludes `clients/`, `samples/` and `docs/` from the
  Licensed Work (it wraps over three lines, `LICENSE:9-11`) names `skills/` as well: "clients/, samples/, docs/ and
  skills/". The line breaks follow the existing wrap. The closing words "(see the LICENSE file in each of those
  directories)" are not touched: they are already loose for `clients/`, whose file is `clients/python/LICENSE` (there is no
  `clients/LICENSE`), and `skills/LICENSE` is the fourth file like `docs/LICENSE` and `samples/LICENSE`. The rest of the
  parameter is unchanged.
- Tests and fixtures live outside `skills/`, because `npx skills` copies a skill's whole folder into the developer's
  project. Node tests and fixtures under `tests/` are not part of any install.
- **No `node:` token in Markdown outside `docs/superpowers/`.** The prose rule of `scripts/check-pins.py` (`:128`) reads
  `node` as an image name, because `samples/notes-web/Dockerfile:7` builds `FROM node:24-alpine@sha256:...`, so a Markdown
  file that writes `node:test` or `node:20` fails the gate. The skills, their `references/`, any `.md` under `tests/`,
  `docs/workflow.md`, `README.md` and `docs/reviews/*.md` (the probe's record and the manual check's, which
  `check-pins.py:65` reads) write "Node's test runner" and "Node 20" instead. (This spec is under
  `docs/superpowers/`, which the rule does not read.)
- **Line endings.** The scripts and Markdown of `skills/` are copied into projects and compared byte for byte (the two
  copies of `secrets.mjs`), so `.gitattributes` gains `skills/** text eol=lf`, `tests/skills-hooks/** text eol=lf` and
  `tests/skills-eval/** text eol=lf` (it has no rule for `*.mjs`, `*.json` or `*.md` today). `docs/api/endpoints.txt` is
  covered by the existing `*.txt` rule. A copy made into `skills/` of a file of the repository (the four Angular files of the
  sample) is written LF, though a Windows checkout of the original has CR LF (as it has for the sample's `texts.ts` and
  `deploy/.env.prod.example`); so the guard and the tests that read such files split lines on `\r?\n` ("Secret guard", step 2),
  and rule 8 of `check-skills.py` compares the copies after CR LF is read as LF.
- Every `SKILL.md` stays under 500 lines (the Agent Skills guidance); longer text goes to `references/`, one level deep.
- **Frontmatter** of every `SKILL.md` (the fields of the Agent Skills specification, https://agentskills.io/specification):

  ```yaml
  ---
  name: auth-core-setup                      # equals the folder name; 1-64 of a-z, 0-9 and single hyphens
  description: >-                            # 1-1024 characters: what it does and when to use it
    Sets up Auth-Core sign-in for this project: asks who gets an account, which sign-in buttons to show, what the
    app protects and who may do what, then writes auth.yaml, an environment template and the Auth-Core hooks.
    Use when the user wants to add login, accounts, companies, roles or Auth-Core, or to change auth.yaml.
  license: MIT
  compatibility: Auth-Core server 0.3.0. The hooks need Node 20 or newer; the manifest check needs Docker.
  metadata:
    auth-core-version: "0.3.0"               # the server release these instructions are written for
  ---
  ```

  The `#` comments are for this spec; a real file has none. No other field (`allowed-tools` is experimental in the
  specification, and the tool-specific fields of Cursor are not read by the other three tools). A one-line value is a
  double-quoted string, or a plain value with no `: ` and no ` #` in it (a plain value with `: ` is not valid YAML, and
  `npx skills` reads the frontmatter with a YAML parser); longer text is a folded block `>-`.

- **How a skill names things of this repository.** An installed skill sits in the developer's project, where this
  repository's files do not exist. A skill therefore points at a file of this repository by its URL at the tag of its
  version, `https://github.com/MckCieply/Auth-Core/blob/v0.3.0/<path>` (or `tree/` for a folder), and carries in its own
  text, `references/` and `assets/` what the agent needs without fetching anything: the steps, the code shapes, the rules,
  and the Angular files themselves (below). An endpoint
  is written as `METHOD /auth/...`, a setting as `Auth:Section:Key` or `Auth__Section__Key`, an environment variable by its
  name. A skill's Docker image lines (a compose snippet in `references/`), and its prose, name an image by digest, because
  `scripts/check-pins.py` reads every Markdown file outside `docs/superpowers/`, `skills/` included; the digest is the one the
  repository's own compose files carry for that image (`scripts/check-skills.py` checks that), and the release image of
  Auth-Core itself is named by `${AUTH_CORE_VERSION}`.

### Installing

- **Skills.** `npx skills add https://github.com/MckCieply/Auth-Core/tree/v<server version>/skills` (Settled 1). `-a`
  picks the agents (`claude-code`, `codex`, `github-copilot`, `cursor`) and `-y` skips the prompts. `npx skills` installs
  into `.claude/skills/` for Claude Code and into `.agents/skills/` for the other three, by a symlink (a junction on Windows)
  unless `--copy` is given, and records the source and the tag in `skills-lock.json`; `npx skills update` keeps the
  recorded tag (https://github.com/vercel-labs/skills, `src/source-parser.ts`, `src/agents.ts`, `src/local-lock.ts`,
  `src/update-source.ts`).
- **Before the tag exists** (the manual check of a release, "Release" in `docs/workflow.md`) the same command takes a local
  path, `npx skills add ./skills`, run in a clone of the pushed release branch. (The tree URL of a branch is ambiguous when
  the branch name holds a slash, so it is not used: to verify below.)
- **Hooks** are not installed by `npx skills`: `auth-core-setup` installs them (below).

### Version match (Settled 1)

- The install command names the tag of the developer's server version, so the skills match it. The short form
  `npx skills add MckCieply/Auth-Core#v0.3.0` is the same source to the CLI's parser; the guides show the URL form, which
  the CLI's README documents.
- Every skill starts with the same check, the section "Before you start" of its `SKILL.md`:
  1. The skill's version is its `metadata.auth-core-version`.
  2. The project's server version is, in this order: `server_version` in `.auth-core/kit.json`; else `AUTH_CORE_VERSION`
     in an `auth-core*.env.example` file of the project (never `.env` itself, which holds secrets); else the tag of an
     `image: ghcr.io/mckcieply/auth-core:<tag>` line in a compose file of the project, where a `<tag>` of the form
     `${AUTH_CORE_VERSION...}` (as in `deploy/docker-compose.prod.yml:50`) is read as the default after `:-` when it has
     one and otherwise as the value of `AUTH_CORE_VERSION` from the second source; else the version the developer stated in the request; else the skill asks the developer.
  3. Equal: go on. Different: the skill says, in one sentence, which two versions differ and gives the command that
     installs the skills of the server's version, then asks whether to go on anyway. A server older than `0.3.0` has no
     skills of its own version: the skill says so and that the manifest check cannot run against it.
- `auth-core-setup` with no answer to point 2 asks the version as one of its questions (question 7).
- **How two versions compare** (the skills' "Before you start" and `manifest-check.mjs` use the same rule). A version is
  `MAJOR.MINOR.PATCH`, three numbers of digits, with an optional leading `v` that is ignored and an optional pre-release
  suffix after a hyphen (`0.3.0-rc.1`). *Equal* means the same text after the `v` is dropped. *Older than 0.3.0* means
  that the three numbers, compared one by one, are lower; the suffix is ignored, so `0.3.0-rc.1` is not older than
  `0.3.0` and `0.2.9` is. Any other text (`latest`, `main`, a branch name, a digest, a locally built tag, an unresolved
  `${...}`) is *not a version*: it is neither equal nor older. A skill with a server that is not a version says that it
  cannot compare, and asks whether to go on; the manifest check with such a server makes the call anyway (the outcomes of
  "What the agent is told" cover an image without the command).

### `auth-core-setup`

**What it does.** A quiz, one question at a time, in plain words that describe what the product's users will see; then
it writes files and lists them. If the developer already gave answers (in the request, or in a file they name), the skill
does not ask those again. The questions, their wording and what each answer writes are in `references/quiz.md`:

| # | Asked | Choices | Written |
| --- | --- | --- | --- |
| 1 | "Who can get an account?" | "A company signs itself up on your site and then invites its people" / "Only people who are invited; you create each company" | `signup: self-service` / `signup: invite-only` |
| 1b | "Can a private person also sign up alone, just for themselves?" | yes / no | the block `private` with `signup: self-service` / no block |
| 2 | "Show 'Continue with Google' and 'Continue with Microsoft' on the sign-in screen?" | each yes or no; the order is the order of the buttons | `providers: [google, microsoft]` (or one, or the key left out) |
| 3 | "What does your app protect? Name the things people work with and what they do with them (for example: invoices, read, create, approve)." | free text; the skill proposes `resource:action` names and the developer confirms or edits them | `permissions` |
| 4 | "Every new company starts with these roles. Who does what?" | the skill proposes `admin: ["*"]` and one role with the everyday permissions; the developer changes names and contents | `default_roles` |
| 5 | (only with 1b yes) "What may a private person do in their own space?" | a subset of the permissions of 3 | `private.permissions` |
| 6 | "Where will this run first: on your computer, on a server, or both?" and then, for each: the address of the frontend, the product's name in the mails, `en` or `pl`, and for a server the mail relay (host, port, sender address) | | the environment templates |
| 7 | (only when "Before you start" found no version) "Which Auth-Core version will you run?" | the skill's own version is the default | `.auth-core/kit.json`, `AUTH_CORE_VERSION` |

Rules the skill applies before it writes, with the server's words when it refuses (they are the reasons of
`ManifestParser`, `src/Auth.Server/Tenancy/ManifestParser.cs`):

- A permission is 1-64 characters of lowercase letters, digits, `_`, `-` and `:`, starting with a letter
  (`ManifestParser.cs:87-90`, `PermissionCatalog.cs:81`); at most 500 (`ManifestParser.cs:24,82-85`); the skill names them
  `resource:action`. `members:manage`, `roles:manage`, `org:manage` and `org:delete` are built in and never listed
  (`PermissionCatalog.cs:12-20`).
- Between 1 and 100 default roles (`ManifestParser.cs:95-99`); a role name is valid and unique without regard to case
  (`ManifestParser.cs:105-113`; at most 100 characters, `src/Auth.Server/Requests/NameInput.cs:13`); a role holds permissions of the catalog or `"*"`
  (`ManifestParser.cs:115-119`); at least one role holds `members:manage` or `"*"` (`ManifestParser.cs:129-132`).
- `"*"` includes `org:delete`, the right to delete the whole company. When the developer gives `"*"` to a role other than
  the first admin's, the skill says so in one sentence and asks once.
- `private.permissions` never holds `"*"` or a built-in permission (`ManifestParser.cs:149-152`) and only permissions of the
  catalog (`ManifestParser.cs:154-157`).
- Self-service sign-up (company or private) needs the address of the completion screen, `AUTH_FRONTEND_SIGNUP_URL`
  (`Auth:App:FrontendUrls:CompleteSignup`); without it sign-up stays off and `/auth/health` says `Degraded` (`deploy/docker-compose.prod.yml:96-97`).
  A provider needs its keys and `AUTH_FRONTEND_PROVIDER_URL` (`Auth:App:FrontendUrls:ProviderReturn`). The skill says so
  when the answers turn these on, and writes the variables into the templates.

**Files it writes**, then lists, each with one line on what it is:

1. **The manifest.** Where one already exists (`manifests` in `.auth-core/kit.json`, else a file named `auth.yaml` that has
   `default_roles`), that file; otherwise `auth.yaml` at the project's root, or a path the developer names. A comment block
   at the top says in plain words what each key does, as `deploy/auth.yaml:1-20` does. The file is at most 64 KiB, the
   server's limit (`ManifestParser.MaxBytes`, `ManifestParser.cs:22`).
2. **Environment templates**, one per environment of question 6, never with a secret value:
   - `auth-core.local.env.example`: the variables of the development stack (`.env.example` of this repository:
     `POSTGRES_PASSWORD`, `AUTH_DEV_SEED_EMAIL`, `AUTH_DEV_SEED_PASSWORD`) and the settings a compose overlay passes to
     Auth-Core (`Auth__Tokens__Issuer`, `Auth__Tokens__Audience`, `Auth__App__FrontendUrls__ResetPassword`,
     `Auth__App__FrontendUrls__VerifyEmail`, `Auth__App__FrontendUrls__AcceptInvite`, and when the answers need them
     `Auth__App__FrontendUrls__CompleteSignup` and `Auth__App__FrontendUrls__ProviderReturn`), with `http://localhost`
     addresses (allowed in Development only).
   - `auth-core.env.example`: every variable of `deploy/.env.prod.example`, in its order, with the answers filled in where
     they are not secret (`AUTH_CORE_VERSION`, `AUTH_ISSUER`, `AUTH_AUDIENCE`, `AUTH_APP_NAME`, `AUTH_APP_LOCALE`, the
     `AUTH_FRONTEND_*` addresses, `AUTH_EMAIL_FROM`, `AUTH_SMTP_HOST`, `AUTH_SMTP_PORT`, `AUTH_SMTP_SECURITY`).
   - A secret is left **empty**, with the comment `# secret: set it in .env (git-ignored), never in this file`:
     `POSTGRES_PASSWORD`, `AUTH_DB_APP_PASSWORD`, `AUTH_DEV_SEED_PASSWORD`, `AUTH_SMTP_PASSWORD`,
     `AUTH_GOOGLE_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_SECRET`. Compose refuses to start while `POSTGRES_PASSWORD` or
     `AUTH_DB_APP_PASSWORD` is empty (`deploy/docker-compose.prod.yml:23,27,81`), which is what the developer should see
     until they fill `.env`. The other four do not stop compose: `AUTH_SMTP_PASSWORD` must be present in `.env` but may be
     blank for a relay without authentication (`:117`), and a provider's client secret defaults to blank (`:105,107`), which
     leaves that provider off and makes `/auth/health` say `Degraded` (`:99-102`). The provider lines carry a pointer to
     `https://github.com/MckCieply/Auth-Core/blob/v0.3.0/docs/deployment/provider-keys.md`.
   - The skill tells the developer to copy the template to `.env`, fill it, and keep `.env` out of git. When `.env` is not
     git-ignored (`git check-ignore -q .env` fails), it offers to add the line `.env` to `.gitignore`, and says why this comes
     first: the secret guard refuses a secret value in any file git does not ignore, `.env` included.
3. **`.auth-core/`, the hooks, their configurations, the `AGENTS.md` section and `CLAUDE.md`**, by running
   `node <this skill's folder>/scripts/install-hooks.mjs --project <root> --server-version <v> --manifest <path>`
   (below). The installer, not the agent, writes the last two, so that the rules for them are tested code:
   - **`AGENTS.md`**: the section below is added to the project's `AGENTS.md`, or the file is created with it; on a re-run
     the text between the markers is replaced and nothing else in the file changes.
   - **`CLAUDE.md`**: created with the single line `@AGENTS.md` when the project has none. When it exists and does not
     contain the line `@AGENTS.md`, the skill first asks whether to add it at the end, and says why: Claude Code reads
     `AGENTS.md` only when no `CLAUDE.md` exists, unless `CLAUDE.md` imports it (https://code.claude.com/docs/en/memory).
     On a yes it passes `--claude-md add`; without that flag the installer leaves an existing `CLAUDE.md` as it is.
4. **The image.** The installer ends by asking Docker whether the image of `kit.json` is on the machine
   (`docker image inspect <image>`, through the same `AUTH_CORE_DOCKER` override as the check). When it is not, and the
   image is a release of Auth-Core (its name starts with `ghcr.io/mckcieply/auth-core:`), it prints the line `image <image> is
   not on this machine; run: docker pull <image>`, and the skill offers to run that command (the first manifest check would
   otherwise report the image missing). It runs it only on a yes. For any other image (a local build, which `kit.json` may
   name, Settled 31) the line is `image <image> is not on this machine; build or pull <image> yourself` and the skill offers
   no command: the kit does not pull a name it did not choose. The installer reads the answer of `image inspect` by the rules of
   "Manifest check" step 3: exit 0 is there; exit 1 with `No such image` is missing (the two lines above); a program that cannot be
   started, or any non-zero exit with a daemon text on standard error (exit 1 on a real daemon that is down), is Docker not
   available; any other answer prints no image line. When Docker is not available the installer does not fail: it prints
   `Docker is not available; when it is, run: docker pull <image>` (for any other image: `... build or pull <image> yourself`)
   and exits as it would have.

When the installer has run, the skill itself runs `node .auth-core/hooks/manifest-check.mjs --check <manifest>` once and says
what it printed. On a first run the hooks are not active yet when the manifest is written (the installer comes after it, and
Codex runs a project's hooks only after the developer's `/hooks` trust step), so no hook has checked the file; later edits
are checked by the hooks.

It ends with the list of files it wrote or changed, what the installer printed about each tool (below), and the question
"Shall I connect your backend and frontend now?", which starts `auth-core-integrate` on a yes.

**Re-run.** With a manifest already there, the skill reads it, says in plain words what it holds now (who gets an account,
the buttons, the permissions, the roles), asks "What do you want to change?" and asks only the questions that change
touches. It edits the manifest in place, keeping its comments and the order of its keys and roles, and shows the
difference. It runs the installer again, which changes only what differs: it replaces the hooks, merges the configurations
and replaces the `AGENTS.md` section. If `CLAUDE.md` still lacks the import line, the skill asks about it again. If
`.auth-core/kit.json` names an image whose tag is not the server version (a local build), the skill asks whether to keep it
and passes `--keep-image` on a yes.

**It never:**

- writes a secret value (a password, a client secret, a private key, a connection string with a password) into any
  file, a template included: placeholders only, and a pointer to where the value comes from;
- generates the signing keys (the development stack of a clone needs `.secrets/`, which `scripts/dev-keys.sh` makes,
  `deploy/docker-compose.yml:7-8`: the skill tells the developer to run it, by its URL, and does not run it; on Windows, in
  Git Bash, the command is `MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh`, which keeps Git Bash from turning the `/CN=` of the
  script's `openssl -subj` into a path, `README.md:118`, `scripts/dev-keys.sh:28-29`), edits the
  developer's `.env`, or starts or stops containers without asking;
- turns on sign-up or a provider the developer did not choose;
- writes a manifest the rules above refuse (the manifest check confirms it after the write: the first time, the skill runs
  it itself, as above).

### `.auth-core/` and the hook installer

`install-hooks.mjs` is a Node script with no dependency (Node 20 or newer). It is what puts the hooks into a project, so
that copying files and merging JSON is code with tests and not an agent's improvisation. Its arguments: `--project <root>`,
`--server-version <v>`, `--manifest <path>` (repeatable; each path relative to the root, inside it), `--keep-image` and
`--claude-md add`. Exit 0 when everything it was asked to do is done, 1 when a file it must not overwrite blocked part of
it (a configuration that is not JSON, an `AGENTS.md` with a begin marker and no end marker), 2 for wrong arguments (a `--manifest` path that leaves the project is
one; the arguments are checked before anything is written).

- **It writes a file only when its bytes would change.** Every file below (the hook copies, `kit.json`, the four
  configurations, `AGENTS.md`, `CLAUDE.md`) is compared with what it would write, and is left alone, with no output line, when
  they are equal; that is what makes a second run change no file, in bytes and in modification times.
- It copies `scripts/hooks/*.mjs` of the skill into `<project>/.auth-core/hooks/`, replacing older copies.
- It writes `<project>/.auth-core/kit.json`, with `kit_version` read from the `metadata.auth-core-version` of the `SKILL.md`
  beside its `scripts/` folder (`../SKILL.md`, found with a pattern, not a YAML library) and `server_version` the value of
  `--server-version` with a leading `v` taken off (rule 2 of `check-skills.py` keeps the four skills at one version):

  ```json
  {
    "kit_version": "0.3.0",
    "server_version": "0.3.0",
    "image": "ghcr.io/mckcieply/auth-core:0.3.0",
    "manifests": ["auth.yaml"]
  }
  ```

  `image` is the image the manifest check runs; a developer may point it at a locally built image. `manifests` are paths
  relative to the project root: the paths already in the file plus the `--manifest` ones, each of which must lie inside the
  project (a path with `..`, an absolute path, or a symbolic link that leaves the project is refused as a wrong argument, exit 2, with nothing written). On a re-run
  it keeps `image` when its tag differs from `server_version` only if the developer passed `--keep-image` and the old
  `image` passes the image rule of "Manifest check"; otherwise it writes `ghcr.io/mckcieply/auth-core:<server_version>`.
- It merges the four configurations below into the project: an existing file keeps every key and every hook it had, an
  entry whose command or arguments name `.auth-core/hooks/` is replaced rather than added twice, and a file that is not valid JSON is
  left alone and reported (the hooks of that tool are then not installed, and the script exits 1). It keeps the file's
  own line endings and indentation, and it writes nothing, and prints no line, when the merged JSON equals what the file
  holds already (compared as parsed JSON). A new file has LF endings, two-space
  indentation and a final newline.
- It writes the `AGENTS.md` section: the text of `references/agents-section.md` with `<server_version>` and
  `<manifest path>` filled in, between the markers. No `AGENTS.md`: the file is created with the section. A file that has
  the markers: the text between them is replaced and every other byte stays. A file without them: the section is added at
  the end after a blank line. A begin marker without its end marker: the file is left alone and reported (exit 1).
- It handles `CLAUDE.md`: absent, it is created with the line `@AGENTS.md`; present with that line (as a line of its own),
  untouched; present without it, the line is appended only when `--claude-md add` was passed, and otherwise the file is
  untouched and the installer prints `skipped CLAUDE.md: it does not import AGENTS.md` (exit 0), which the skill turns into
  the question above.
- It prints one line per file it created or changed (`created .auth-core/hooks/secret-guard.mjs`,
  `changed .claude/settings.json`), then the one step each tool needs from the developer, one line per tool:
  `claude: nothing to do`; `codex: run /hooks in Codex and trust the project's hooks`; `copilot: VS Code runs workspace hooks
  only in a trusted workspace`; `cursor: nothing to do`. For a tool that the README lists as not running hooks (the probe
  settles which: "The hook probe"), the line is instead `<tool>: hooks do not run there; run the two checks by hand (AGENTS.md
  says how)`. Last, the image line (item 4 of "Files it writes") when the image is missing.
- `.auth-core/` is meant to be committed: the team shares the hooks. The configurations name `.auth-core/hooks/` by a path
  relative to the project, so they hold no path of the machine (unless the probe shows a tool that needs one, below).

### The hooks

One script set, run by `node`, called by a small configuration per tool. Each entry script takes `--tool claude|codex|copilot|cursor`,
reads the hook's JSON from standard input, and answers in that tool's shape (`hook-io.mjs`). With `--check` in place of
`--tool` it is run by hand ("Checks by hand").

**The project root** is where `.auth-core/` is. The scripts sit in `<root>/.auth-core/hooks/` (Settled 5), so the root is
found from the script file itself, `fileURLToPath(import.meta.url)`, with three `dirname` calls: the first gives
`hooks`, the second `.auth-core`, the third the root. It is not the root of the git repository and not the working directory of the
tool: a project may be one folder of a monorepo, and a tool may start a hook from another folder. `git` is always run with
the project root as its working directory. A tool reads its hook configuration from the folder it is started in, except
Copilot's CLI and cloud agent, which are expected to read `.github/hooks/` only at the repository root: in a project that
is a subfolder of a monorepo they run no hooks. The probe records where each tool reads it, and the installer's line for a
tool that will not read the project's folder says so.

**Paths.** A path a tool gives is made absolute against the input's `cwd` (the project root when there is none), and then
resolved with `fs.realpathSync.native` on its nearest ancestor that exists, with the rest of the path appended as it is, so
that a new file in a folder that does not exist yet resolves and nothing throws (a path that failed to resolve must not let a
write through). The project root is resolved the same way. "Inside the project" is `path.relative` from the root not
starting with `..` and not absolute; on win32 the comparison ignores case (the case of a drive letter), and the 8.3 short
names are already expanded by `realpath.native`.

**Input, per tool** (what the script reads; fields marked † are not documented by the tool: the hook probe records them
from the tools themselves, and the cells are corrected from its fixtures before the plan's hook tasks start, see "The hook
probe"):

| Tool | Edit tools it acts on | Path | Text |
| --- | --- | --- | --- |
| Claude Code | `tool_name` `Write`, `Edit`, `MultiEdit` | `tool_input.file_path` | `content` (Write); `old_string`, `new_string` and `replace_all` (Edit); every `edits[]` of those (MultiEdit) |
| Codex | `tool_name` `apply_patch` (the matcher `Edit\|Write` selects it) †; `Write`/`Edit` read as Claude Code's | the `*** Add File:`, `*** Update File:` and `*** Move to:` lines of `tool_input.command` † | the lines that start with `+` under each file † |
| Copilot | `tool_name` of the create and edit tools † (`create`, `edit`, and VS Code's †) | the first of `path`, `file_path`, `filePath` in `tool_input` (a JSON text is parsed first) † | the first of `file_text`, `content`, `new_str`, `new_string`, `newString` †; for an edit also the old text † |
| Cursor | `tool_name` `Write` | `tool_input.file_path` † | `tool_input.content`, or every `edits[].new_string` † |

Any other tool, or an input without a path, is let through with no output. A payload that carries `cursor_version` read by
the `claude` entry is Cursor running the Claude Code configuration (Cursor loads `.claude/settings.json` hooks by default,
https://cursor.com/docs/reference/third-party-hooks): that run does nothing, and the `cursor` entry acts (Settled 8).

**Answers, per tool:**

| Tool | Refuse a write (secret guard) | Tell the agent after an edit (manifest check) |
| --- | --- | --- |
| Claude Code | exit 0, `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"<m>"}}` | exit 0, `{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"<m>"}}` |
| Codex | the same as Claude Code | the same as Claude Code |
| Copilot | exit 2, `<m>` on standard error, and `{"permissionDecision":"deny","permissionDecisionReason":"<m>","hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"<m>"}}` | exit 0, `{"additionalContext":"<m>","hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"<m>"}}` |
| Cursor | exit 0, `{"permission":"deny","agent_message":"<m>","user_message":"<m>"}` | exit 0, `{"additional_context":"<m>"}` |

Sources: https://code.claude.com/docs/en/hooks; https://learn.chatgpt.com/docs/hooks (Codex);
https://docs.github.com/en/copilot/reference/hooks-configuration and https://code.visualstudio.com/docs/copilot/customization/hooks
(Copilot: the CLI and the cloud agent read the top-level fields and treat a non-zero exit as a refusal; VS Code reads
`hookSpecificOutput` and exit 2); https://cursor.com/docs/hooks. Letting a write through is exit 0 with no output, for all four.

**Failure of the hook itself.** A hook never fails the agent's work because the hook is broken; only a deliberate refusal
refuses. (A guard that blocks every edit when it breaks would be switched off; the security report and the repository's
secret scanning remain, Settled 10.)

- Each entry script has no static `import` and no logic of its own. It installs handlers for `uncaughtException` and
  `unhandledRejection` first, then loads its modules with `import()` inside a `try` and runs them. Whatever goes wrong
  (input that is not JSON, git missing, a module missing or broken, an exception, a rejected promise, a timeout of its own)
  ends in exit 0, nothing on standard output, and one line on standard error: `auth-core hook: ` and a fixed text that names
  the stage (`could not read its input`, `could not load its code`, `failed`), then the class name or code of the error
  (`SyntaxError`, `ERR_MODULE_NOT_FOUND`) and never its message, because Node's messages quote the input they were given. The
  write goes on. (With `--check`, run by hand, nothing waits to be let through: the same failures print the same line and exit 3.)
- A machine where `node` is not found, or is older than 20, fails before any script runs. For Claude Code, Codex and Cursor a
  failed hook command is expected to be a non-blocking error †; Copilot's CLI and cloud agent read a non-zero exit as a
  refusal. The same holds for a tool other than Claude Code that runs the hooks of `.claude/settings.json` (Cursor does by
  default, VS Code with `chat.useClaudeHooks` on) and leaves `${CLAUDE_PROJECT_DIR}` unset: the path in the exec form is then
  wrong and `node` fails, which is this case again. The probe records what each tool does, that one included; where one blocks, the README says plainly that the hooks need Node 20 and
  how to remove that tool's file, and the installer's line for that tool says it.

#### The configurations

`assets/hook-configs/`, merged by the installer. The commands are relative to the project, so the files hold no path of a
machine and the team shares them; the scripts find their own root (above), so only the start of the command depends on the
folder a tool starts it from. For Claude Code that is `${CLAUDE_PROJECT_DIR}`; Copilot's file says `"cwd": "."` (relative to
the repository root, †); where the probe shows that Codex or Cursor does not start a hook in the project root, the installer
writes the absolute path of the script for that tool, and says that file is per machine and each teammate runs the setup again.

`.claude/settings.json` (exec form, so that the path works under PowerShell as well as a POSIX shell):

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Write|Edit|MultiEdit",
        "hooks": [ { "type": "command", "command": "node",
                     "args": ["${CLAUDE_PROJECT_DIR}/.auth-core/hooks/secret-guard.mjs", "--tool", "claude"], "timeout": 15 } ] }
    ],
    "PostToolUse": [
      { "matcher": "Write|Edit|MultiEdit",
        "hooks": [ { "type": "command", "command": "node",
                     "args": ["${CLAUDE_PROJECT_DIR}/.auth-core/hooks/manifest-check.mjs", "--tool", "claude"], "timeout": 90 } ] }
    ]
  }
}
```

`.codex/hooks.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "Edit|Write",
        "hooks": [ { "type": "command", "command": "node .auth-core/hooks/secret-guard.mjs --tool codex", "timeout": 15 } ] }
    ],
    "PostToolUse": [
      { "matcher": "Edit|Write",
        "hooks": [ { "type": "command", "command": "node .auth-core/hooks/manifest-check.mjs --tool codex", "timeout": 90 } ] }
    ]
  }
}
```

`.github/hooks/auth-core.json` (event names in PascalCase, which makes the Copilot CLI and the cloud agent send the
same snake_case input VS Code sends; no matcher, because VS Code's edit tool names are not documented: the script picks
the tools itself):

```json
{
  "version": 1,
  "hooks": {
    "PreToolUse": [
      { "type": "command", "command": "node .auth-core/hooks/secret-guard.mjs --tool copilot", "cwd": ".", "timeout": 15, "timeoutSec": 15 }
    ],
    "PostToolUse": [
      { "type": "command", "command": "node .auth-core/hooks/manifest-check.mjs --tool copilot", "cwd": ".", "timeout": 90, "timeoutSec": 90 }
    ]
  }
}
```

`.cursor/hooks.json`:

```json
{
  "version": 1,
  "hooks": {
    "preToolUse": [ { "command": "node .auth-core/hooks/secret-guard.mjs --tool cursor", "matcher": "Write", "timeout": 15 } ],
    "postToolUse": [ { "command": "node .auth-core/hooks/manifest-check.mjs --tool cursor", "matcher": "Write", "timeout": 90 } ]
  }
}
```

`afterFileEdit` is not used: Cursor defines no answer for it, so it cannot speak to the agent.

#### Secret guard (`secret-guard.mjs`, before a write)

1. **Which files.** A path (resolved as in "Paths") outside the project root,
   or a project root that is not inside a git work tree (`git rev-parse --is-inside-work-tree` fails), is let through. A path
   that git ignores is let through: `git check-ignore -q -- <path>` exits 0 only for a file that is not tracked and matches
   an ignore rule, so `.env` passes when `.gitignore` lists it, and a tracked file never does. Everything else is checked,
   including a new file that no rule ignores.
2. **Which text.** What the file holds after the write, as far as the write changes it:
   - a whole-file write (`Write`, Cursor's `Write`, Copilot's create): its text, every line;
   - an edit (`Edit`, `MultiEdit`, Copilot's and Cursor's edit): the hook reads the file from disk (the write has not happened
     yet), applies each edit in memory as the tool will (the first occurrence of `old_string` is replaced, every one with
     `replace_all`), and checks the **whole lines** that contain the replaced text, so that a replacement that only adds a
     value after an existing name (`PASSWORD=` becomes `PASSWORD=<value>`) is seen with its name. When the file cannot be read
     or the old text is not found (the tool itself will fail), the new text alone is checked;
   - Codex's `apply_patch`: the added lines.

   There is no size limit: the whole text is checked, line by line (lines are split on `\r?\n`, so a file with CR LF endings gives
   the findings of the same file with LF and no value ends in a CR), so that padding cannot hide a value. NUL bytes are removed
   before the check, for the same reason. A hook that runs out of time is not a refusal (the tool carries on, as with any
   failure of a hook), so the detectors must run in time linear in the text: no pattern with nested unbounded repetition, no
   second pass per match. A Node test gives the guard 10 MiB (one line of 10 MiB; ten thousand lines of 1 KiB; and
   `password=` followed by a long run of one character) and requires an answer in 5 seconds, a third of the hook's 15. Line numbers are those of the text as it will be in the file (for a patch: of the
   added lines of that file, counted from 1).
3. **What is a secret value** (`secrets.mjs`); each detector has a name, used in the message:

   | Detector | Matches | Where |
   | --- | --- | --- |
   | `private key` | a line `-----BEGIN [A-Z ]*PRIVATE KEY-----` | every file |
   | `Google client secret` | `GOCSPX-` followed by 28 of `A-Z a-z 0-9 _ -` | every file |
   | `Microsoft client secret` | 3 of `A-Za-z0-9_~.-`, a digit, `Q~`, then 31-34 of `A-Za-z0-9_~.-` | every file |
   | `password in a connection string` | `Password=` or `Pwd=` (any case) followed by a value up to `;`, a quote or the end | every file but test files |
   | `password in a URL` | `<scheme>://<user>:<password>@<host>` | every file but test files |
   | `assigned secret` | a name that contains `password`, `passwd`, `pwd`, `secret`, `private_key`, `privatekey`, `api_key` or `apikey` (any case; `_`, `-`, `.` and case changes split words), then `=`, `:` or `=>`, then a value: a quoted string that has the shape of a secret (below) in any file, or an unquoted value in a file of settings | every file but test files |

   A **file of settings** is one whose name, in any case, is `.env`, ends in `.env`, starts with `.env.` or holds `.env.`
   (so `.env.local`, `prod.env` and the kit's own `auth-core.env.example` and `auth-core.local.env.example` are all files of
   settings), or ends in `.yml`, `.yaml`, `.ini`, `.cfg`, `.conf`, `.properties` or `.toml`; a **shell script or build file**, which ends in `.sh`, `.bash` or `.ps1` or is named
   `Makefile` (an unquoted `export X_PASSWORD=Xk9...` in a script is as much a secret as the same line in `.env`; an `export`
   before the name, a `$env:` prefix and `:=` or `?=` in place of `=` do not change the rule); and a Dockerfile's `ENV` line.
   After `:` an unquoted value must be set off by white space (`name: value`), so `secrets:reset_all`, a permission of a
   manifest, is not an assignment; a quoted value need not be (`"password":"..."` in compact JSON). A value that is an ISO
   date, `YYYY-MM-DD`, is no secret and passes (`AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES=2027-03-01`, `deploy/.env.prod.example:65`).
   An unquoted value ends at a space followed by `#` (an inline comment). A **quoted** value counts only when it has the shape of a secret: a single
   token with no white space, at least 8 characters, not shaped like a path or a URL (it does not start with `/`, `./`,
   `../`, `~/` or a drive letter and a colon, and does not hold `://`), and with characters of at least three of the four
   classes lower case, upper case, digit and other. So the labels of an interface, `password: 'Password'` and the
   sentences of `samples/notes-web/src/app/texts.ts:16-18,165,184`, pass, and so does a manifest permission such as
   `secrets:reset_all`. The cost is that a weak password with two classes (`hunter2hunter`) in quotes passes this rule; that is
   accepted (the owner, 2026-10-19), because a rule that flagged every quoted word near the word "password" would flag the
   interface text of every product and be switched off, and the security report and the repository's secret scanning remain.
   On a **comment line** (the first character that is
   not blank is `#`, or `;` in an `.ini` file) the `assigned secret` detector counts only a value that is a single token,
   with no whitespace in it. Prose is then not a value: the kit's own `# secret: set it in .env (git-ignored), never in this
   file`, and the comments of `deploy/.env.prod.example` (`:13,20`) from which the templates are written, pass, while a
   commented-out `# AUTH_SMTP_PASSWORD=<one real token>` is still caught, which skipping comment lines altogether would
   miss. A Node test holds the kit's own template lines, the `texts.ts` lines above and `secrets:reset_all` to this, and `check-skills.py` (rule 12) does it for every line of
   `references/env-templates.md`.

   For the last three detectors, a value is a **placeholder**, and passes, only when the **whole value** is one of these
   (after its quotes are taken off): empty; shorter than 8 characters; enclosed in `<` `>`; a reference (`${...}`, `$NAME`, `$(...)`,
   `%NAME%`, `{{...}}`); made only of `x`, `*` or `.`; or made only of placeholder words, joined by `-`, `_`, `.` or spaces,
   each word optionally followed by digits, and digit-only parts: the words are `change`, `changeme`, `me`, `placeholder`,
   `example`, `your`, `replace`, `set`, `in`, `todo`, `here`, `secret`, `password`, `value`, `env`, `dev`, `test`, `local`,
   `dummy`, `fake` and `sample`. So `change-me`, `your-password-here` and `replace_me_2` pass, and a value that merely
   contains one of those words (`example-k3Jd9sQw...`) does not.
   A **test file** is one whose path **relative to the project root** has a segment `test`, `tests`, `__tests__`, `e2e`, `spec`
   or `fixtures` (the folders above the project do not count: a project in `~/tests/myapp` is not a test folder), or whose name
   is `*.spec.*`, `*.test.*`, `test_*.py`, `*_test.py` or `conftest.py`: test passwords live there. The first three detectors
   apply to test files too.
4. **The message** (`<m>`), never quoting the value:

   ```
   Auth-Core secret guard: not written. <path> is tracked by git (or not ignored), and the new text holds a secret value:
   <detector> at line <n> (<name>, when the detector knows it). Put the value in a git-ignored file such as .env and read
   it from the environment (if .env is not ignored yet, add the line .env to .gitignore first); in this file write a
   placeholder or leave it empty. Provider keys:
   https://github.com/MckCieply/Auth-Core/blob/v<kit_version>/docs/deployment/provider-keys.md
   ```

   One line per finding, at most 10, then `and <k> more`. The link names the tag `v<kit_version>` of `.auth-core/kit.json`,
   and `main` when there is no usable `kit.json`: the guard guards the same without it.

#### Manifest check (`manifest-check.mjs`, after an edit)

1. **Which files.** A path, resolved as in "Paths", that lies inside the project root and is either listed in
   `manifests` of `.auth-core/kit.json` (compared the same way), or is named `auth.yaml` and has a line that starts with
   `default_roles:` (the key every manifest has, and the one `auth-core-setup` looks for). Any other edit, and a symbolic
   link that leaves the project: no output.
2. **Which server.** `.auth-core/kit.json`, which the check does not trust: it is a file of the project, and an agent or a
   dependency may have changed it.
   - **Usable.** A `kit.json` is usable when it is a JSON object whose `kit_version`, `server_version` and `image` are strings
     and whose `manifests` is an array of strings. Missing, unreadable, not JSON, or any other shape is unusable, which is a
     normal answer and not a failure of the hook: the message "could not be checked: no usable .auth-core/kit.json; run the
     auth-core-setup skill" in the tool's shape, exit 0, and no `docker` call. The hook keeps no state, so it says this at
     every edit of a file it would have checked (with no usable `kit.json` that is any file named `auth.yaml` that has
     `default_roles`). The secret guard does not read `kit.json` for anything it must do.
   - **Nothing of `kit.json` is copied raw.** A `kit_version` or `server_version` appears in a message, or in the link of the
     guard's message, only when it has the syntax of a version ("How two versions compare"); anything else is shown as `not a
     version` and the link names `main`. An `image` that fails its rule and a `manifests` entry are never copied into a
     message or a link.
   - `image` must match `^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,254}$` (a reference of letters, digits and `. _ / : @ -`, that
     cannot start with `-`, so Docker cannot read it as an option, and holds no space, quote or shell character); otherwise
     "could not be checked: .auth-core/kit.json holds an image name that is not valid", and no `docker` call.
   - Each `manifests` entry is a path relative to the root, with no `..` segment, whose resolved target lies inside the root;
     an entry that is not is treated as not listed.
   - A `server_version` older than `0.3.0` (by the rule of "How two versions compare"): "could not be checked: Auth-Core <v>
     has no validate-manifest command; it arrived in 0.3.0", and no `docker` call. A `server_version` that is not a version
     is not older: the call is made.
3. **The run** (Settled 4), with a 60-second limit (below the hook's own 90, so that the script cleans up before the tool
   could stop it; the environment variable `AUTH_CORE_CHECK_LIMIT_MS` changes it, for the tests only):

   ```
   docker run --rm --name auth-core-check-<8 random hex digits> --pull never --network none \
     --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges --pids-limit 256 \
     --mount type=bind,source=<resolved absolute path of the manifest>,target=/manifest/auth.yaml,readonly \
     [--user <uid>:<gid>    on Linux and macOS: the developer's own ids, so a file only they may read is readable] \
     <image> validate-manifest /manifest/auth.yaml
   ```

   - `docker` is started without a shell (an argument list), so no shell rewrites the Windows path. The program is `docker`,
     or, for the tests only, the command named by the environment variable `AUTH_CORE_DOCKER`: a JSON array such as
     `["node","<absolute path of tests/skills-hooks/support/docker-stub.mjs>"]` (the tests run with a temporary repository as the
     working directory, so the path is absolute, built from the test's own location; a stub that is a `.cmd` or a script cannot
     be started without a shell on Windows, and `node` can).
   - A `--mount` value is comma-separated text, so a path that holds `,`, `"` or a control character is not passed: "could
     not be checked: the path of the file holds a comma, a quote or a control character, which Docker's --mount cannot
     take; rename the folder or the file".
   - When the limit passes, the script runs `docker rm -f <the name>` (the container would otherwise outlive the hook) and
     reports the limit.
   - **What the script makes of the answer.** This is the contract with `docker`, which `support/docker-stub.mjs` reproduces
     with the same codes and texts. Measured on Docker 29.8.1: a daemon that cannot be reached gives exit **1** and
     `error during connect: ...` on standard error, for `image inspect` and for `docker run --pull never` alike; a missing image
     gives exit 1 and `Error response from daemon: No such image: <image>` for `image inspect`, and exit **125** with the same
     text for `docker run --pull never`. So the stub answers `image inspect` with exit 0 for an image that is there and exit 1
     with that text for one that is not, and answers a daemon that is down as exit 1 in one case and as exit 125 in a second
     (both are read alike). The rules are read in this order. A program that cannot be started (`ENOENT`) means that Docker is
     not available. So does any non-zero exit, whatever the code, with a line of standard error that holds (any case) `Cannot
     connect to the Docker daemon`, `Is the docker daemon running` or `error during connect`; this is read before every rule
     below, and not in a line that starts with `invalid: ` or `error: ` (a reason of the image can quote a name from the
     manifest, and must not pass for a daemon text). Exit 125 is Docker's own failure: with `No such image` or
     `Unable to find image`, the image is not on the machine; any other 125 is a refused run. Exit 0 with a first line of
     standard output that is `valid`: valid. Exit 1 with a line of standard error that starts `invalid: `: not valid, with
     that line's reason. Exit 3 with a line that starts `error: `: could not be checked, with that line's reason. Every
     other outcome, whatever the code (exit 1 or 3 without its line, exit 2, a crash), is "the image answered with exit
     code <c> and no result". No outcome claims that the image is older than `0.3.0`: only the version rule says that.
4. **What the agent is told** (nothing blocks; the edit is done):

   | Outcome | Message |
   | --- | --- |
   | exit 0 and a first line `valid` | nothing |
   | exit 1 and an `invalid: ` line | `Auth-Core manifest check: <path> is not valid for Auth-Core <server_version>: <reason>. A server given this file keeps its last valid manifest and /auth/health says Degraded. Fix the file.` where `<reason>` is that line after `invalid: ` |
   | exit 3 and an `error: ` line | `Auth-Core manifest check: <path> could not be checked: <that line after error: >.` |
   | `docker` cannot be started, or a non-zero exit (1 or 125) whose standard error says the daemon is not running | `Auth-Core manifest check: <path> could not be checked: Docker is not available. The file was not validated; start Docker, or check it later with the command in the auth-core-setup skill.` |
   | exit 125 and the image is not on the machine | `... could not be checked: the image <image> is not on this machine. Run: docker pull <image>` when `<image>` starts with `ghcr.io/mckcieply/auth-core:`; for any other image `... could not be checked: the image <image> is not on this machine. Build or pull <image> yourself.` |
   | any other exit 125 | `... could not be checked: Docker refused the run (exit 125).` |
   | the limit passed | `... could not be checked: the check took longer than 60 seconds.` |
   | any other outcome | `... could not be checked: the image answered with exit code <c> and no result.` |

   The image's `invalid:` and `error:` lines are shown after the same printable escaping the server applies
   (`ManifestParser.cs:175-202`: control and format characters, line and paragraph separators and lone surrogates are written as
   a backslash, `u` and four hex digits), and cut at 2,000 characters; the stub cases of `manifest-check.test.mjs` hold both. A reason can quote a name from the manifest (`ManifestParser.cs:89,107,118,151,156`), which
   is why step 1 never reaches outside the project.

#### Checks by hand

For a tool that does not run the hooks (the README lists them once the probe has shown which: Copilot's cloud agent and
Cursor are the two in doubt), the agent runs the same two scripts by hand; `AGENTS.md` tells every tool to.

- `node .auth-core/hooks/manifest-check.mjs --check <path>` (a relative path is taken from the working directory, and the
  scripts that call it pass an absolute one) runs steps 2 to 4 for that manifest and prints the message of the
  table on **standard output** (a failure of the script itself prints its `auth-core hook:` line on standard error, as
  everywhere), or `Auth-Core manifest check: <path> is valid for Auth-Core <server_version>.` for exit 0. Exit 0 valid, 1 not
  valid, 3 could not be checked (a path outside the project is one: step 1 applies).
- `node .auth-core/hooks/secret-guard.mjs --check <file>...` reads each file from disk and checks every line by the rules
  above (a file git ignores is skipped, and says so; with `--no-git` that test and the project-root and work-tree tests of
  step 1 are not made, which `check-skills.py` uses on templates in a temporary folder, away from any `.auth-core/`). It prints the message of step 4 for each file with a finding and exits 1
  when there is one, otherwise 0.

### The hook probe (run by the owner, before the plan's hook tasks)

What Codex, the Copilot CLI, VS Code Copilot, the Copilot cloud agent and Cursor send to a hook, and what they do with its answer, is not
documented well enough to write tests against: those are the cells marked † above. So the owner runs a probe by hand, from
a short checklist, and the recordings become the fixtures of the tests. The probe is run before the plan's hook tasks start;
the plan itself can be written meanwhile, and the hook tasks wait for the recordings. (Claude Code is documented, but the
owner has it at hand, so it takes the same short run and all fixtures come from one procedure.)

**What the plan's first task builds, small and reviewed before the owner starts:**

- `tests/skills-hooks/support/record-hook.mjs`: a hook that writes its standard input, its arguments, its working directory,
  the *names* (never the values) of its environment variables and `process.version` to a file named for the case, prints
  nothing and exits 0. (On the cloud agent, which has no folder the owner can read, it prints the same JSON to standard
  error, which the session log shows.)
- `tests/skills-hooks/support/probe-configs/<tool>.json`: for each surface, the configuration that runs the recording hook
  for every tool, before and after.
- `tests/skills-hooks/support/probe-checklist.md`: the steps below, one page per surface, with a line to tick per case.
- `tests/skills-hooks/support/normalise-fixtures.mjs`: the normaliser below.

**What the owner does**, per surface (Claude Code; Codex; the Copilot CLI; VS Code Copilot; the Copilot cloud agent through a throwaway issue
in a scratch repository; Cursor), about 15 minutes each:

1. Makes a scratch project (`git init`), copies in the surface's probe configuration and the recording hook.
2. Gives the agent these cases, each by the exact prompt written in the checklist: create a file whose text is `__TEXT__`;
   change `__TEXT__` in it to `__NEW__`; a change of two places in one file, and of two files, where the tool has such an
   edit; read a file; run a shell command. For Cursor, the same again with `.claude/settings.json` holding the Claude Code
   hooks too (Settled 8).
3. Notes these behaviours, each in one line with the tool's version: the working directory of the hook command; whether an
   answer of the tables above refuses a write (a probe hook that always refuses); what the tool does with a non-zero exit that
   is not a refusal, and with a `node` that is not found; where the tool reads its configuration from in a project that is a
   folder of a monorepo. And per tool: Claude Code, the exec form (`"command": "node"` with `args`) under PowerShell and Git
   Bash, and whether `MultiEdit` exists; Codex, the shape of `.codex/hooks.json`, the `apply_patch` input and the trust step
   (`/hooks`); Copilot (CLI, VS Code and cloud agent), that the surface reads `.github/hooks/auth-core.json` with PascalCase events, `command` and `cwd`,
   and whether Node is on the cloud agent's runner; Cursor, that `preToolUse` and `postToolUse` fire for `Write`, what their
   `tool_input` holds, that `cursor_version` is in every payload, and how commands run on Windows; VS Code with
   `chat.useClaudeHooks` on (off by default), whether the Claude Code entry runs and can be told from Claude Code's; and for
   Cursor and VS Code both, with the Claude Code configuration, whether `CLAUDE_PROJECT_DIR` is set in the hook's environment
   (the recording hook lists the names), and what the tool does when it is not and the path of the exec form comes out wrong
   (the checklist has a case with a script path that does not exist).
4. Runs `node tests/skills-hooks/support/normalise-fixtures.mjs <recordings folder>`, reads the diff, and commits.

**Normalised before it is committed.** A recording carries the owner's paths, names and session ids, and the repository is
public. The normaliser keeps every field name, the nesting and the type of every value, and replaces local data with fixed
text: the project folder with `/__PROJECT__` (every slash form and drive letter), the home folder with `/__HOME__`, a user
or host name with `__USER__`, an e-mail address with `__EMAIL__`, a time with `__TIME__`, `transcript_path` with
`/__TRANSCRIPT__`, and `session_id`, `conversation_id`, `generation_id`, `tool_use_id` and any UUID-shaped text with
`__ID__`. A Node test (`fixtures.test.mjs`) fails for a fixture that still holds a drive-letter path, `/Users/`, `/home/`, an
`@` address or a UUID.

**What it leaves**, committed:

- `tests/skills-hooks/fixtures/<tool>/<surface>/<case>.json`: each normalised payload, with `__TEXT__` and `__NEW__` where
  the prompt's text was. A fixture holds no secret-shaped text (so `scripts/secret-scan.sh` stays clean); the Node tests put
  one in at run time.
- `docs/reviews/<date>-skills-hook-probe.md`: per tool and surface, the prompts, the observations and the versions.
- One commit to this spec that corrects the tables of "The hooks" and the configurations from the fixtures and removes every
  † the fixtures settle, and the sentences of the README text that say which tools run hooks. A † that stays (a surface the
  owner could not reach) is named in the README as untested: its hooks may not run, and the agent runs the checks by hand.

Criterion 14 tests the recorded fixtures, not payloads written from the documentation, and criterion 11 fails for a row
of the input table that has none.

### `validate-manifest` (the server change)

**Invocation.** `dotnet Auth.Server.dll validate-manifest <path>`; in the image, whose entry point is
`["dotnet", "Auth.Server.dll"]` (`src/Auth.Server/Dockerfile:38`), `docker run --rm <image> validate-manifest <path>`.

**It needs nothing else.** `Program.cs` dispatches it before the host is built, next to `--health-probe` and `admin`
(`src/Auth.Server/Program.cs:26-36`), so the key files, the mail settings, the provider settings, the proxy list, the rate
limits and the database are never read (they are read from `Program.cs:43` on, and each stops the start when missing).
It is a top-level argument and not an `admin` command because `admin` builds the persistence services and activates the
manifest against the database before any command runs (`src/Auth.Server/Admin/AdminCli.cs:46-62`). No configuration,
no environment variable and no network is needed.

**One reader, so the reasons cannot drift.** `ManifestActivator.ReadFileAsync` (`ManifestActivator.cs:37-58`) moves to
`ManifestFile.ReadAsync(string path, CancellationToken)` in `src/Auth.Server/Tenancy/ManifestFile.cs`, used by both the
activator and the command. Its behaviour is the activator's: a file that does not exist is `the file does not exist`; a
file over `ManifestParser.MaxBytes` (65,536 bytes) is `the file is larger than 65536 bytes`; an `IOException` or
`UnauthorizedAccessException` is `the file cannot be read (<type name>)`; otherwise the text, read as UTF-8, goes to
`ManifestParser.Parse`. Its result also says which of the two kinds a refusal is: the file could not be had (it does not
exist, or cannot be read) or its content is not a manifest (too large, or refused by the parser); the activator treats both
alike, the command needs the difference for its exit code. The command is `src/Auth.Server/Admin/ValidateManifestCommand.cs`.

**Arguments.** Exactly one, the path (relative paths are taken from the working directory). None, more than one, or one
that starts with `-`: a usage error.

**Exit codes and output** (the meanings of `AdminCli`'s codes, `AdminCli.cs:18-27`):

| Exit | When | Standard output | Standard error |
| --- | --- | --- | --- |
| 0 | the manifest is valid | the summary below | nothing |
| 1 | the parser refuses it, or it is larger than 64 KiB | nothing | `invalid: <reason>` |
| 2 | the arguments are wrong | nothing | `error: usage: validate-manifest <path>` |
| 3 | the file does not exist, or cannot be read | nothing | `error: <reason>` |

`<reason>` is exactly the reason the server records when it does not use a file: the activator sets it
(`ManifestActivator.cs:30-33`, from the reader `:44,49,56` or the parser) and logs it (the template, `:75`). The command
prints it bare after its own prefix, for example `invalid: default_roles must hold between 1 and 100 roles` or
`invalid: at least one default role must hold 'members:manage' or '*', so that the first admin of a company can manage it`;
the parser's reasons carry no prefix of their own (`ManifestParser.cs:98,131,173`). The reasons are those of
`ManifestParser.cs:47,53,70,72,76,84,89,98,107,112,118,131,137,146,151,156,166`; a key the parser does not know is a YAML
error, reported as `the file is not valid YAML for a manifest: <the YAML library's message>` (`ManifestParser.cs:49-54,204`).
A directory is "a file that does not exist": `FileInfo.Exists` is false for one (`ManifestActivator.cs:41-45`), so it gives
exit 3 and `error: the file does not exist`.

The summary of a valid manifest, one item per line, names as the file has them after the parser's folding:

```
valid
permissions: documents:read, documents:write, documents:approve
default_roles: admin [*]; user [documents:read, documents:write]
signup: self-service
private_signup: self-service [documents:read, documents:write]
providers: none
note: sign-up needs Auth:App:FrontendUrls:CompleteSignup (AUTH_FRONTEND_SIGNUP_URL); without it sign-up stays off and /auth/health says Degraded
```

The format, line by line. Every line ends with LF on every platform (the command writes `\n` itself, so one golden file
serves Windows and Linux), and has no trailing space: a key with no value is the key and its colon alone.

- `permissions:` the permissions of the file in file order, duplicates folded to the first place (`ManifestParser.cs:92`);
  the four built-in ones appear only when the file lists them; with none, the line is `permissions:`.
- `default_roles:` the roles in file order, joined by `; `, each as `<name> [<permissions>]` with its permissions in file order,
  duplicates folded (`:126`); a role that lists `*` is `[*]` (`:124-125`); a role that holds nothing is `<name> []`. There is
  always one role at least.
- `signup:` `invite-only` or `self-service`; `invite-only` adds no note.
- `private_signup:` `off`, or `self-service [<permissions>]` with the permissions **sorted** by ordinal comparison (the
  parser sorts them, `:159`, the only list that is sorted), and `self-service []` when there are none.
- `providers:` `none`, or the names in file order joined by `, `, duplicates folded to the first place (`:169`).
- The `note:` lines come last, in this order, each only when it applies: the sign-up note when `signup` or `private_signup`
  is `self-service`, and `note: each provider needs its keys and Auth:App:FrontendUrls:ProviderReturn
  (AUTH_FRONTEND_PROVIDER_URL)` when a provider is listed.

Nothing else is printed: no log line, no stack trace. The tests compare the output with **golden files** under
`tests/Auth.IntegrationTests/Golden/` (`<name>.txt` next to `<name>.yaml` for a synthetic input): the summaries of
`deploy/auth.yaml` and `samples/notes-api/auth.yaml` (read from where they are), and of two synthetic manifests that
between them hold every rule above: `no-permissions` (no `permissions` key, a role with no permissions, `invite-only`, no
private block, no providers) and `unsorted-and-repeated` (private permissions written out of order, a permission listed
twice in a role, `providers: [microsoft, google, microsoft]`, both sign-up modes).

### The `AGENTS.md` section

Written by the installer of `auth-core-setup` between markers, under 2 KiB (Codex reads at most 32 KiB of `AGENTS.md` by default,
https://learn.chatgpt.com/docs/agent-configuration/agents-md):

```markdown
<!-- auth-core:begin (written by the auth-core-setup skill; run it again to change this section) -->
## Auth-Core

This project signs people in with Auth-Core <server_version>. Its manifest is `<manifest path>`.

- Permissions are named `resource:action`. Every endpoint that reads or changes a company's data checks a permission and
  filters by the token's `org_id` (in FastAPI, `auth.require_permission(...)` of the Auth-Core package; in another stack,
  the equivalent that the auth-core-integrate skill wrote).
- The default roles of the manifest are copied when a company is created. Existing companies keep their own roles; their
  admins change them through the company API (`/auth/org/roles`).
- Never write a secret value (a password, a client secret, a private key) into a file git tracks. Secrets go in `.env`,
  which git ignores; tracked files hold placeholders.
- Tokens stay in memory: never write an access token or a provider ticket to localStorage, sessionStorage, a cookie of the
  app or a URL.
- Skills: auth-core-setup (accounts, sign-in buttons, permissions, roles, environment), auth-core-integrate (connect a
  backend or a frontend), auth-core-permissions (add a role or a permission, guard an endpoint), auth-core-security-report
  (review the project).
- The hooks in `.auth-core/hooks/` refuse secret values in tracked files and check the manifest with Auth-Core's own
  validator after each edit. If your tool does not run them, run them by hand: after you edit the manifest,
  `node .auth-core/hooks/manifest-check.mjs --check <manifest path>`; before you finish, for each file you wrote,
  `node .auth-core/hooks/secret-guard.mjs --check <file>`.
<!-- auth-core:end -->
```

### `auth-core-integrate`

**What it does.** The first connection of a backend and a frontend to Auth-Core.

- It finds the stack: FastAPI from `fastapi` in `pyproject.toml` or a `requirements*.txt`; Angular from `angular.json`.
- **FastAPI and Angular are the official paths**, written from the guides and samples at the skill's tag, and tested by the
  release eval (task 5):
  - FastAPI (`references/fastapi.md`, from `docs/integration/python-fastapi.md`): install `auth-core-fastapi` from a tag
    with `#subdirectory=clients/python`, at the Python tag that `references/fastapi.md` names itself, literally, in the line
    `auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v<X.Y.Z>#subdirectory=clients/python` (the agent
    fetches nothing; the tag is copied from `python-fastapi.md:14-30` when the skills are written, and rule 13 of
    `check-skills.py` keeps it the guide's);
    configure `issuer`, `audience` and the optional `jwks_url` from the environment (`AUTH_ISSUER`, `AUTH_AUDIENCE`,
    `AUTH_JWKS_URL`, as `samples/notes-api/src/notes_api/settings.py:24-26`); create one `AuthCore` and call
    `auth.install(app)`; guard endpoints with `auth.current_user` and `auth.require_permission("<permission>")`; add
    `org_id` to every table a company owns and filter every query by it (`python-fastapi.md:159`); tests sign their own
    tokens with a `JwksCache` that serves a test key (`python-fastapi.md:144-157`, `samples/notes-api/tests/conftest.py`).
  - Angular (`references/angular.md`, from `docs/integration/angular.md`): one origin for the app, `/auth` and `/api`
    behind a proxy, no CORS (the refresh cookie `auth_rt` is `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/auth`,
    `src/Auth.Server/Sessions/RefreshCookie.cs:8-9,44-53`, ADR 0004); copy the four files that the skill ships in `assets/angular/` (the agent fetches nothing): the three of
    `samples/notes-web/src/app/auth/` (`auth.service.ts`, `auth.interceptor.ts`, `auth.guard.ts`) to the product's
    `src/app/auth/`, and `samples/notes-web/src/app/account-api.ts`, which `auth.service.ts` imports (`:4`) and which imports
    it back (`account-api.ts:4`), to `src/app/account-api.ts` so that the two relative imports hold. They are byte-identical
    copies of the sample's files, like the report's copy of `secrets.mjs`, and rule 8 of `check-skills.py` keeps them so
    (line endings aside: the sample's files follow the checkout's, the copies are LF); register the initializer
    and the interceptor in `app.config.ts` (the sample's `app.config.ts:11-20`) and put the guard on the product's routes in
    `app.routes.ts` with `canActivate: [authGuard]` (`angular.md:65`, the sample's `app.routes.ts:37-38`); set `TOKEN_PATH_PREFIXES` and `TOKEN_PATHS`
    to the product's paths; the access token in memory only; the screens of the mail links (`/reset`, `/verify`, `/invite`,
    and `/complete-signup` when sign-up is on) and, when providers are listed, the buttons and the return screen
    (`angular.md:236-262`; the sample keeps in `sessionStorage` only the provider's name and the time of a re-authentication in
    progress, `samples/notes-web/src/app/pages/provider-buttons.ts:16,40`, never a token or a ticket, and the product may do the
    same); the interceptor's table of answers; the response headers (`angular.md:168`).
  - The development stack of the clone needs the keys of `.secrets/`, made by `scripts/dev-keys.sh` of the clone
    (`deploy/docker-compose.yml:7-8`); the skill points at that script by its URL and tells the developer to run it once (on Windows, in Git Bash, as
    `MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh`, `README.md:118`); it makes no keys itself.
  - The compose overlay and the proxy for local development, modelled on `samples/notes-api/compose.yml`,
    `samples/notes-web/compose.yml` and `samples/notes-web/Caddyfile`, run on the development stack of an Auth-Core clone
    at the server's tag (`python-fastapi.md:197-232`): the proxy at a fixed address that Auth-Core trusts
    (`Auth__Proxy__KnownNetworks__0`), the issuer equal to the origin the browser uses plus `/auth`, the manifest mounted at
    `/etc/auth-core/auth.yaml`.
- **Any other stack is best effort.** The skill works from the API contract (the server's own `GET /auth/openapi/v1.json`,
  served in every environment, `src/Auth.Server/Api/OpenApiSetup.cs:14,22,42`, and `references/other-stacks.md`: the
  token claims, the `401`/`403`/`503` table, one origin, the token in memory) and the two official paths as the pattern.
  Before it changes a file it tells the developer, in these words (eval task 6): "This is an untested path: Auth-Core has no sample for
  <stack>, so I am working from the API description and the FastAPI and Angular samples. Review what I write and test it
  before you rely on it." A backend that verifies tokens itself is told the rules of `python-fastapi.md:128-135` (RS256
  only, `typ: at+jwt`, `kid`, `iss`, `aud`, expiry with 5 minutes of skew, the required claims).
- **The smoke test** (`references/smoke-test.md`): one test in the backend's own test suite, skipped unless
  `AUTH_CORE_SMOKE_URL` is set (the origin of the running local stack). It signs in with `POST /auth/login`
  `{"email","password"}`, taking the address and the password from `AUTH_DEV_SEED_EMAIL` and `AUTH_DEV_SEED_PASSWORD` of the
  environment (the development seed user, the admin of the development company with the manifest's default roles,
  `src/Auth.Server/Seeding/DevUserSeeder.cs:14-19`), calls one protected endpoint of the product with the access token and
  expects `200`, and calls it without the token and expects `401`. The test never holds a credential in its text.
- The skill grows with each new sample: a new sample adds a `references/<stack>.md` and moves that stack from best effort to
  official.

**It never:** puts the access token anywhere but memory; adds the token to requests for another origin; turns on CORS with
credentials for `/auth`; writes a secret value; changes Auth-Core's own files in a clone.

### `auth-core-permissions`

**What it does.** "Add a role", "add a permission", "guard this endpoint", or all three in one request.

1. Edits the manifest: a new permission joins `permissions` (with the rules of `auth-core-setup`); a new or changed role
   goes under `default_roles`; a permission a private person should have joins `private.permissions` when the developer
   says so. The manifest check runs after the edit (by hand, with `--check`, where the tool runs no hooks). In a project with
   no `.auth-core/` (the setup skill never ran) there is no script to run: the skill skips the check, says so, and offers
   `auth-core-setup`.
2. Guards the endpoint in the backend: FastAPI with `Depends(auth.require_permission("<permission>"))`; any other stack
   with the untested-path notice of `auth-core-integrate`.
3. Adds a test that the endpoint answers `403` with `{"error":"forbidden"}` to a valid token without the permission, and
   does not answer `403` to one with it (FastAPI: tokens signed with the test key, as `samples/notes-api/tests/test_notes.py:192-197`).
4. In the frontend, when it is Angular, shows the control only when `me().permissions` lists the permission
   (`angular.md:74`), and says the server still decides.
5. **Warns, every time it changes `default_roles`:** "Roles in the manifest are copied into a company only when the
   company is created. Companies that exist already keep their own roles." (`deploy/auth.yaml:5`,
   `python-fastapi.md:63-64`.) It then says how existing companies change (`references/existing-companies.md`):
   - an admin with `roles:manage` creates or changes a role through `POST /auth/org/roles` and `PUT /auth/org/roles/{id}`
     (`{"name","permissions"}`, `src/Auth.Server/Tenancy/Contracts.cs:71`, `src/Auth.Server/Api/OrgRoleEndpoints.cs:31-63`),
     and one with `members:manage` gives it to people through `PUT /auth/org/members/{user_id}/role` (`{"role_id"}`,
     `Contracts.cs:59`, `OrgMemberEndpoints.cs:33`); the product's own team screen can offer this;
   - a role that holds `"*"` gains a new permission by itself (`PermissionCatalog.cs:55-66`);
   - `private.permissions` reach every private account at its next refresh (`python-fastapi.md:55-60`);
   - the server reads the manifest at start, so the change takes effect after a restart (`ManifestHolder.cs:10-12`), and
     a token keeps its old permissions until it is refreshed, about 10 minutes (`python-fastapi.md:137-138`).

**It never:** gives `"*"` to a role the developer did not ask for; removes a permission a guard in the code still uses
without saying which guards use it; edits a company's roles in a running instance by itself.

### `auth-core-security-report`

**What it does.** Reads the project and writes `auth-core-security-report.md` at its root. The report lists the weak spots
of the product, so the skill adds the line `/auth-core-security-report.md` to the project's `.gitignore` (creating the file
when there is none, and adding nothing when the line is there) and tells the developer in one sentence that it did, and that
the line comes out when they decide to commit the report. In a project that is not in a git work tree it adds nothing and says
so. It changes no other file unless the developer then asks it to fix a finding. The checks are in `references/checks.md`:

| Severity | Check |
| --- | --- |
| Critical | a secret value in a file git tracks or would track: the detectors of the secret guard over `git ls-files -co --exclude-standard`, run by `node <this skill's folder>/scripts/scan-tracked.mjs` (the report names the file, the line and the detector, never the value) |
| Critical | signing or encryption key files (`*.key`, `*.pem`, a PEM private key) tracked by git |
| High | an endpoint of the backend with neither `auth.current_user` nor `auth.require_permission` (or the stack's equivalent), other than endpoints the developer names as public |
| High | a query on a table that has `org_id` without a filter on the caller's `org_id` |
| High | the access token or a provider ticket written to `localStorage`, `sessionStorage`, a cookie of the app or a URL |
| High | the token sent to another origin, or to a path of `/auth` that does not need it (the interceptor's lists) |
| High | CORS that allows credentials, or any origin, on `/auth` or on the backend |
| High | `jwks_url` over plain `http` to a host that is not on an internal network |
| Medium | a default role other than the first admin's that holds `"*"` (and so `org:delete`) |
| Medium | a production template or compose file with `http://` frontend addresses, `AUTH_RATE_LIMIT_ENABLED=false`, or a trusted proxy network wider than one address (`Auth__Proxy__KnownNetworks__0`) |
| Medium | access logs of the proxy kept for `/reset`, `/verify`, `/invite`, `/complete-signup`, `/auth/providers/*/start` or `/auth/providers/*/callback` (`angular.md:136-142`) |
| Medium | `.env` not ignored by git |
| Low | the app's responses without the headers of `angular.md:168` (`Referrer-Policy: no-referrer` first) |
| Low | the server version behind the skill's version |

**The report:** a header (date, commit, server version, skill version); counts per severity; the findings, worst first, each
with an id (`ACS-<n>`), a title, the severity, the location as `path:line`, why it matters in one or two sentences, and the
fix; then "Checked and found nothing" and "Not checked" (with the reason, for example a stack it cannot read). It never
contains a secret value. It lists weaknesses, which is why it is git-ignored: the developer commits it only after the
findings are fixed.

**The scanner.** `scripts/scan-tracked.mjs` lists the files with `git ls-files -co --exclude-standard`, runs the detectors of
`scripts/secrets.mjs` over each (the placeholder and test-file rules included, and the whole text of every file; a file with a
NUL byte in its first 8 KiB is skipped as binary), and prints one line per finding, `<path>:<line>: <detector>`, never the value. It sits in this skill and imports its own copy of the
detectors: the skill is installed into a different folder for each tool, `auth-core-setup` may never have run, and a link
from one skill into another is not allowed (rule 4 of `check-skills.py`). The copy is the file `scripts/hooks/secrets.mjs` of
`auth-core-setup`, byte for byte, and `check-skills.py` fails when the two differ. It needs Node 20.

**It never:** changes a file before the developer asks, apart from the report itself and the one `.gitignore` line; runs the
product or calls a server; opens a file git ignores (`.env` included: whether `.env` is ignored is asked of
`git check-ignore`, not read from the file); prints a secret value in the report or the chat.

### `docs/api/endpoints.txt`

Every endpoint of the service, one `METHOD /path` per line, in the order and spelling of the array `Endpoints` of
`tests/Auth.IntegrationTests/OpenApiTests.cs:15-33`, which it replaces: `OpenApiTests` reads the file into the same static
`Endpoints` (a `string[]`, so its other use, `Endpoints.Length` at `OpenApiTests.cs:451`, is unchanged), the existing
test `The_description_names_every_endpoint_of_the_service_and_nothing_else` (`OpenApiTests.cs:94-102`) keeps the file equal
to the served description, and `Every_route_of_the_service_is_in_the_description` keeps the description equal to the routes.
There is no checked-in description file today; the description is built at run time (`OpenApiSetup.cs:27-53`).

**The shared helper.** Reading a file of the repository needs the repository root, which only `ShippedConfigurationTests`
finds today (the private `RepositoryFile`, `ShippedConfigurationTests.cs:16-26`: the folder with `Auth-Core.slnx`). That
method moves to `tests/Auth.IntegrationTests/Infrastructure/RepositoryFiles.cs` as `RepositoryFiles.Resolve(string relative)` (`Path` would collide with `System.IO.Path`);
`ShippedConfigurationTests` calls it, and so do `OpenApiTests` and `SkillExampleManifestTests`.

### `scripts/check-skills.py`

A check in the style of `scripts/check-pins.py` and `scripts/check-docs.py`: standard library only (rule 12 also runs
`node`, which the gate has anyway), no Docker, no network, run from the repository root, `--self-test` runs every rule on a good and a bad sample with no repository, exit 0 when
everything holds and 1 otherwise, one line per problem as `<file>:<line>: <problem>`. Files are the ones git knows
(`git ls-files -co --exclude-standard`).

**It has its own rules (Settled 13).** It does not load `scripts/check-docs.py`, and that script and `scripts/check-pins.py`
are not changed. `check-docs.py` is one function over a document (`check_text`, `check-docs.py:84-133`) that cannot be
called rule by rule; its variable rule knows two files and would refuse `AUTH_DEV_SEED_EMAIL` and `AUTH_JWKS_URL`
(`:44,98-101`); its links resolve anywhere in the repository, where a skill's links must stay in its folder (`:88-93`). So
`check-skills.py` states the rules a skill needs, below, and copies from `check-docs.py` only the emoji ranges. Both scripts
keep running on their own files at every merge; `check-pins.py` already reads the Markdown of `skills/` (its rule 3; rule 10
below adds the comparison of digests).

It checks:

1. **The skills are the four.** `skills/LICENSE` exists; exactly the folders `auth-core-setup`, `auth-core-integrate`,
   `auth-core-permissions` and `auth-core-security-report` have a `SKILL.md`, and no other folder under `skills/` does.
2. **Frontmatter.** Between a first line `---` and the next `---`; `name` equals the folder, 1-64 characters,
   `^[a-z0-9]+(-[a-z0-9]+)*$`; `description` 1-1024 characters; `license: MIT`; `compatibility` at most 500 characters;
   `metadata.auth-core-version` a version `X.Y.Z`, the same in all four; no other field; no comment line. The values are read
   by a small reader, not a YAML library, which accepts only what `npx skills`' YAML parser reads the same way: a one-line
   value is a double-quoted string, or a plain value that holds neither `: ` nor ` #`; a longer value is a folded block `>-`
   of indented lines; `metadata` is one level of `key: "value"`. A plain value with `: ` is a problem (it is not valid YAML).
3. **Size.** Each `SKILL.md` under 500 lines, and the text between the markers of
   `skills/auth-core-setup/references/agents-section.md`, with a 100-character manifest path and a 20-character version
   filled in, under 2 KiB (the limit of "The `AGENTS.md` section").
4. **Paths and links**, in every `.md` under `skills/`: a relative link ends at a file of the skill's own folder (it never
   leaves it, so never into another skill); no emoji (the ranges of `EMOJI` in `check-docs.py:52`); and every
   `https://github.com/MckCieply/Auth-Core/blob/<ref>/<path>` or `tree/<ref>/<path>` URL names a path of this repository that
   exists, with `<ref>` equal to `v` + `metadata.auth-core-version`. Code spans are not read as paths of this repository: in
   a skill, `src/app/app.config.ts` is a file of the developer's project, and a file of this repository is always written
   as its URL.
5. **Endpoints.** Every `/auth/...` path in a skill's text, with or without a method before it, matches a line of
   `docs/api/endpoints.txt`: a `{...}` segment of the list matches any one segment (`/auth/providers/google/start`), a
   trailing `*` of the text matches any rest (`/auth/*`, `/auth/providers/*/start` matches per segment), and a method, when
   written, must be the line's. A path counts only when it is not preceded by a letter, a digit, `/`, `.`, `_` or `-`, so
   `/run/secrets/auth/signing.crt` is not read as `/auth/...`. The bare prefix `/auth` and the two paths that are not operations,
   `/auth/openapi/v1.json` and `/auth/scalar` (`OpenApiSetup.cs:22-23`), are accepted.
6. **Settings and variables.** `AUTH_...` and `POSTGRES_...` names appear in `deploy/.env.prod.example`,
   `deploy/docker-compose.prod.yml`, `deploy/docker-compose.yml`, `.env.example` or a `samples/*/compose.yml`, or are one
   of the variables the kit itself defines for the developer's project (today `AUTH_CORE_SMOKE_URL`, `AUTH_CORE_DOCKER` and `AUTH_CORE_CHECK_LIMIT_MS`; a
   list in the script); `Auth:Section:Key` ends in a name that appears in a `.cs` file under `src/`;
   `Auth__Section__Key` (a trailing `__<digits>` dropped) is read as `Auth:Section:Key` and follows the same rule.
7. **Examples.** `skills/auth-core-setup/examples/` holds at least the four files of the layout (their validity is the C#
   test's).
8. **Scripts, configurations and copies.** Each file of `assets/hook-configs/` is valid JSON, and every command in it names a script
   of `scripts/hooks/` by the path the installer gives it (`.auth-core/hooks/<name>.mjs`); the two entry scripts
   (`secret-guard.mjs`, `manifest-check.mjs`) hold no static `import` or `export` (a line whose first word is `import`
   followed by a space, a quote, `{` or `*`, or is `export`; a dynamic `import(` is the one that is allowed), so
   that a missing module cannot stop them before their own handlers (Settled 10); `skills/auth-core-setup/scripts/hooks/secrets.mjs`
   holds no `import` at all, static or dynamic (no line whose first word is `import`, no `import(`), so it is self-contained
   and the report skill's copy runs alone; `skills/auth-core-security-report/scripts/secrets.mjs` is byte for byte
   `skills/auth-core-setup/scripts/hooks/secrets.mjs`;
   and each of the four files of `skills/auth-core-integrate/assets/angular/` is the file of `samples/notes-web/src/app/` with
   the same relative path, identical after CR LF is read as LF (the sample's files are checked out with CR LF on Windows; the
   copies in `skills/` are LF, rule 11).
   A change to the sample that is not copied here fails the gate.
9. **The version check.** Each `SKILL.md` has the section `## Before you start` that names `metadata.auth-core-version`,
   `.auth-core/kit.json` and `AUTH_CORE_VERSION`.
10. **Images.** Every `image:` line and every `FROM` of a Dockerfile block in the Markdown of `skills/` that names an image
    by digest names a reference (`name:tag@sha256:...`) that a compose file or a Dockerfile of this repository carries too,
    so that a bump of a digest there is not left behind here. (An image without a digest is `check-pins.py`'s to refuse: its
    rule 3 reads every Markdown file outside `docs/superpowers/`; the release image of Auth-Core is its exception.)
11. **Line endings.** No file under `skills/` holds a carriage return (`.gitattributes` keeps it so on Windows).
12. **The templates pass the detectors.** `skills/auth-core-setup/references/env-templates.md` holds exactly two fenced
    blocks whose info string is `env auth-core.local.env.example` and `env auth-core.env.example`; each is written to a
    temporary folder under the name in its info string (another or a missing one is a problem), and
    `node skills/auth-core-setup/scripts/hooks/secret-guard.mjs --check --no-git <the files>` must exit 0: a template line
    the kit's own guard would refuse is a problem, so the guard never refuses what the setup skill writes. The rule takes the
    path of the guard script as a parameter, so that `--self-test`, which has no repository, runs it with a stub guard (a
    one-line script that exits 0 or 1) on a good and a bad template.
13. **The Python tag.** The `python-v<X.Y.Z>` tag that `skills/auth-core-integrate/references/fastapi.md` names is the one
    `docs/integration/python-fastapi.md` names.

### The C# test of the example manifests

`tests/Auth.IntegrationTests/SkillExampleManifestTests.cs`, a plain class with no fixture (no database, no keys):

- finds the files through `RepositoryFiles` (above);
- a theory over every `*.yaml` in `skills/auth-core-setup/examples/`, each read with `ManifestFile.ReadAsync` and expected
  to have no error;
- a fact that the folder holds at least four files, so that an empty or moved folder fails instead of passing with no case;
- a fact on the four named files: `invite-only.yaml` has `signup` invite-only and no private sign-up;
  `company-self-service.yaml` self-service; `private-persons.yaml` private sign-up on; `companies-private-and-providers.yaml`
  both and `providers` `google`, `microsoft`.

### The tests of `validate-manifest`

- `ValidateManifestCommandTests` (in process, files in a temporary folder): the exit codes and lines of the table above, one
  case per reason of `ManifestParser` listed above, the size limit at 65,536 and 65,537 bytes, a directory (exit 3,
  `error: the file does not exist`), an unreadable file (held open with `FileShare.None`, as `ManifestActivationTests` does at
  `:119`; a `chmod` would not work in the Linux run, which is root, `scripts/test-linux.sh`), the usage errors, and the
  golden files of "`validate-manifest`" above, line for line.
- `ValidateManifestEntryPointTests`: the assembly's entry point invoked with `["validate-manifest", <path>]` while no `Auth`
  setting and no connection string is configured, with standard output and error captured, returns 0 for a valid file and
  1 for an invalid one. Without the early dispatch the host would stop on the missing key settings. In a collection that
  does not run in parallel, as `HealthProbeEntryPointTests` (`tests/Auth.IntegrationTests/HealthProbeTests.cs:175-209`).
- `ManifestFileTests`, a unit test on `ManifestFile.ReadAsync` alone (the activator needs `AuthDbContext`,
  `ManifestActivator.cs:21`, so it is not a unit): for a missing file, a directory, a file of 65,537 bytes and a file for
  each reason of the parser, the exact reason text it returns (written out in the test, so it is not vacuous) equals the
  text after the prefix of the line the command prints for the same file. That the activator uses the reader is shown by
  `ManifestActivationTests`, an integration test with a host, whose existing assertions on `DegradedReason` stay unchanged.
- `scripts/e2e-prod.sh`: the image is built in step 1 (`:273-277`), so the check goes in step 2, the step of the image, after
  the labels. It runs the hook's own check, so that the gate uses the exact flag list of "Manifest check" (`--user` on
  Linux and macOS included) and not a copy of it. The script builds the image as `0.0.0-prodtest` (`:62-63`), which the
  version rule calls older than `0.3.0`, so it does not take `server_version` from the image's tag: in a `mktemp -d` project
  it copies `skills/auth-core-setup/scripts/hooks/*.mjs` to `.auth-core/hooks/`, copies `deploy/auth.yaml` to `auth.yaml`, and
  writes `.auth-core/kit.json` itself (with `python3`, which the script needs already) with `server_version` and
  `kit_version` set to the `metadata.auth-core-version` of the skills, `image` set to the script's `$IMAGE`, and `manifests`
  `["auth.yaml"]`. Then, with the project as the working directory and an absolute path (through `host_path`),
  `node .auth-core/hooks/manifest-check.mjs --check <absolute path of auth.yaml>` must exit 0 and print the `is valid` message.
  The same on a copy with `default_roles: {}` must exit 1 and its output must hold `default_roles must hold between 1 and
  100 roles`. `MSYS_NO_PATHCONV=1` and `host_path` are used as at `:80,143,281`. The script's tool loop (`:72`: `docker openssl
  curl python3`) gains `node`, and its header's "Needs" line, its step list (`:6-33`) and the step's `pass` line (`:352`)
  name the check.

### The Node tests of the hooks and the installer

`tests/skills-hooks/`, run with `node tests/skills-hooks/run.mjs` (Node 20 or newer, `node:test`, no dependency). The runner
lists the `*.test.mjs` files of its own folder and starts `node --test` with that list as absolute paths, then exits with its
code. A form that depends on Node's own handling of a directory or a pattern does not do: `node --test tests/skills-hooks/`
fails on Node 24 (MODULE_NOT_FOUND), and PowerShell and Node 20 expand no `*.test.mjs`. The runner works the same on Node 20
and 24, on Windows (PowerShell and Git Bash) and on Linux, and it runs no helper of `support/` as a test. The tests run the
scripts as child processes with JSON on standard input, in a temporary git repository. A test that needs a link out of the
project makes a junction on Windows (`fs.symlinkSync(target, path, 'junction')`, to a folder that holds the file: a symbolic
link needs a privilege there that a developer may not have) and a symbolic link elsewhere; a test that cannot make its link
fails, and is never skipped silently.

- `secret-guard.test.mjs`: each detector refuses in a tracked file, in a new file that no rule ignores, and passes in an
  ignored `.env`; the files of settings, the kit's own `auth-core.env.example` and `auth-core.local.env.example` among them, and the
  shell scripts and build files (an unquoted `export X_PASSWORD=<a secret-shaped value>`, assembled at run time, is refused in
  a `.sh`, a `.bash`, a `.ps1` and a `Makefile`, and `PASSWORD=$(cat /run/secrets/db)` in a `.sh` passes);
  each placeholder form passes and a value that only contains a placeholder word does not; the assigned-secret rule passes in
  each kind of test file and the private-key rule does not, and a project whose own folder lies under a `tests` folder
  (`<temporary folder>/tests/myapp`) is not exempt as a whole, only its files below `tests/` are; the message names the detector and the line, never contains the
  value, and says to add `.env` to `.gitignore` first; an `Edit` that only adds a value after an existing name
  (`PASSWORD=` to `PASSWORD=<value>`) and a `MultiEdit` that does the same in its second edit are refused, with the line
  numbers of the file after the edit; a text over 1 MiB, and a text with NUL bytes between its characters, are checked and
  refused; 10 MiB of input (the three shapes of "Secret guard" step 2) is answered in 5 seconds; a secret in a new file in a new
  nested folder that does not exist yet is refused (the resolving of "Paths" does not throw or let it through); on win32 the
  same path with the other case of the drive letter is refused too; the kit's own template
  lines (the `# secret: set it in .env ...` comment, `deploy/.env.prod.example`'s comments and empty `NAME=` lines) pass,
  and a commented-out `# AUTH_SMTP_PASSWORD=<one real token>` is refused; the lines 16-18, 165 and 184 of
  `samples/notes-web/src/app/texts.ts`, a manifest permission `secrets:reset_all` and `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES=2027-03-01`
  pass (each read from the checked-out file as it is, and again with every LF changed to CR LF, with the same result), a quoted single token of eight
  characters or more with three character classes is refused, and a quoted two-class one passes (the accepted cost); the
  guard guards without a `kit.json`; 12 findings give 10 lines and `and 2 more`; a path outside the project root, a folder without git and a path
  through a symbolic link that leaves the project pass with exit 0.
- `hook-io.test.mjs`: every fixture of `tests/skills-hooks/fixtures/` (the payloads of the probe), with `/__PROJECT__` in its paths and in
  `cwd` replaced by the path of the test's temporary project (its Windows form too), so that the path lies inside the
  project and the guard can refuse, and with `__TEXT__` replaced at
  run time by a secret-shaped text and by a harmless one: the guard reads the path and the text, and answers in the tool's
  shape and exit code; at least one refusal is asserted for each tool, so a test in which the paths lie outside the project
  and nothing is ever refused fails; a tool that is not an edit tool gets no output; the `claude` entry does nothing on
  Cursor's payloads; and every row of the input table has at least one fixture, so a tool without a recording fails the test.
- `hook-failure.test.mjs`: each entry script, on input that is not JSON, with git missing from `PATH`, with a module deleted
  from a copy of `.auth-core/hooks/`, with a module that throws when loaded, and with one that rejects a promise, exits 0 with nothing on standard output and one line on standard error that starts
  `auth-core hook: ` and does not hold the message of the error (the test puts a marker word in the module's message and
  looks for it); with `--check` the same failures exit 3.
- `manifest-check.test.mjs`, with `AUTH_CORE_DOCKER` naming `node` and `support/docker-stub.mjs`, which records its arguments
  and answers as told: the arguments of the run (the `--name`, `--mount`, `--network none`, `--pull never`, `--read-only`,
  `--tmpfs /tmp`, `--cap-drop ALL`, `--security-opt no-new-privileges`, `--pids-limit`, `--user` on POSIX only, the image of
  `kit.json`); each outcome of the table gives its message in the tool's shape (the missing-image message with the pull hint for a
  `ghcr.io/mckcieply/auth-core:` image and with `build or pull <image> yourself` for any other); an edit of a manifest in a folder that does not exist yet and, on win32, one whose path has the other case of the drive
  letter (both are checked as inside the project); an edit of another file, of a symbolic link
  that leaves the project, of an `auth.yaml` without `default_roles` that `kit.json` does not list, and a `server_version`
  older than `0.3.0` (`0.2.9`; but not `0.3.0-rc.1`, `latest` or `v0.3.1`) make the calls the rules say; a missing,
  unreadable (a directory named `kit.json`: portable, where `chmod` does nothing as root or on Windows), not-JSON or wrongly
  shaped `kit.json` (`manifests` a number, `image` an object) gives the "no usable
  .auth-core/kit.json" message at every edit, in the tool's shape, exit
  0, no call (the hook keeps no state); a `server_version` or `kit_version` that is not a version, and an `image` that fails its
  rule, never appear in a message or a link (`not a version`, `main`); the outcomes of the contract with `docker` are
  reproduced by the stub with the codes and texts of "The run" (a daemon that is down both as exit 1 and as exit 125, each
  "Docker is not available"; an `invalid: ` line whose reason quotes `error during connect` stays an invalid message); an `image` that
  starts with `-` or holds a space, a `manifests` entry with `..` or an absolute path, and a manifest path with a comma make
  no call; a stub that never answers is stopped at the limit (`AUTH_CORE_CHECK_LIMIT_MS`, set by the test to a second) and `docker
  rm -f` is run with the container's name; an `invalid: ` line of the stub that holds an escape character and a line separator
  reaches the message as escaped text, and one of 5,000 characters is cut at 2,000 characters.
- `manual-check.test.mjs`: `manifest-check.mjs --check` prints the same messages and exits 0, 1 and 3 for a valid, an
  invalid and an unchecked manifest; `secret-guard.mjs --check` prints the message for each file with a finding, skips an
  ignored file and says so, and exits 1 or 0.
- `install-hooks.test.mjs`: a fresh project gets every file and a `kit.json` whose `kit_version` is the `metadata.auth-core-version` of the skill and
  whose `server_version` has lost the leading `v` of `--server-version`; existing `.claude/settings.json` and
  `.cursor/hooks.json` with hooks of their own keep them, and their CRLF endings and four-space indentation; a second run
  prints nothing and changes no file (the files' bytes and times are the same); a configuration file that is not JSON is
  left as it was and the exit is 1; a `--manifest` path with `..`, an absolute one, and a symbolic link out of the project
  are refused with exit 2 and nothing written; `--keep-image` keeps a valid `image` and drops an invalid one, and without it the image follows
  `--server-version`; `AGENTS.md` is created, has the section added at its end, has the text between the markers replaced and
  nothing else, and is left alone with exit 1 when the end marker is missing; `CLAUDE.md` is created, left alone with the
  `skipped` line, appended to with `--claude-md add`, and not appended to twice; the printed list names every created or
  changed file, then the four tool lines (and the line of a tool listed as without hooks), then, when the stub says the
  image is missing, the `docker pull` line for a `ghcr.io/mckcieply/auth-core:` image and the `build or pull <image> yourself`
  line for another, none when it says it is there, and the "Docker is not available" line, with exit 0,
  when the stub cannot be started and when it answers a daemon text with exit 1 or with exit 125.
- `scan-tracked.test.mjs`: a temporary repository with a planted secret in a tracked file, one in an untracked file that no
  rule ignores, and one in an ignored file: the first two give `<path>:<line>: <detector>`, the third gives nothing, and the
  output holds no value.
- `hook-configs.test.mjs`: the four templates have the shapes of the "configurations" above.
- `fixtures.test.mjs`: no fixture of the probe holds a drive-letter path, `/Users/`, `/home/`, an `@` address or a UUID.

Inputs that hold secret-shaped text are assembled at run time (for example `'-----BEGIN ' + 'PRIVATE KEY-----'`), so no
file of the repository carries one and `scripts/secret-scan.sh` stays clean.

**On Linux.** The hooks run on both systems (path forms, `--user`, the output of `git`), so `scripts/test-skills-linux.sh`
runs `node tests/skills-hooks/run.mjs` in a Node 20 container, the way `scripts/test-linux.sh` runs the suite: it copies
the files git knows into a scratch folder, mounts that, and prints `PASS the skills tests on Linux` or `FAIL ...`. The image
is the official Node 20 image on Debian bookworm, `node:20-bookworm` (it has git, which the tests need), named by digest in the script like `scripts/secret-scan.sh` names
gitleaks; the digest is the one of the tag when the script is written.

### The release eval

`scripts/eval-skills.sh`, run before each release (not at each merge), in Claude Code only:

- It takes the version being released as its argument (`scripts/eval-skills.sh 0.3.0`), builds the image under the GHCR
  name of that version, as `scripts/e2e-prod.sh` does, so the hooks find it with `--pull never`, and builds one virtual
  environment with the dependencies of the fixtures and `clients/python` of the working tree (the tag does not exist yet),
  which every task that runs Python uses: the copy of `samples/notes-api` imports `fastapi` and `auth_core_fastapi` in its
  tests (`conftest.py:9-14`) as the bare apps do.
- For each task it makes a fresh copy of a fixture in a temporary folder, runs `git init` and a first commit there, copies
  `skills/*` into the copy's `.claude/skills/` (the eval tests the skills' text and hooks; the installer of `npx skills` is in
  the manual check), and runs:

  ```
  claude -p "<task prompt>" --permission-mode dontAsk --allowedTools "<list>" --output-format stream-json --verbose --max-turns 80
  ```

  The fixtures: tasks 1 to 3 and 7 a copy of `samples/notes-api` and `samples/notes-web`; task 4
  `tests/skills-eval/flawed-app/`; tasks 5 and 6 the apps without Auth-Core of `tests/skills-eval/bare-api/` (FastAPI, with
  its tests), `bare-web/` (the source of an Angular app, never built) and `bare-express/` (an Express backend).
  Every task prompt begins "The server runs Auth-Core <version>." with the version the eval was given: the copies hold no
  `kit.json`, environment file or compose image line, and without this "Before you start" would end in a question that
  `claude -p` cannot answer. `stream-json` lists the agent's messages and tool calls in order, which task 6 needs. A `-p` run loads the project's skills and hooks
  (https://code.claude.com/docs/en/headless).
- **The tools each task may use, and no others.** `Read`, `Write`, `Edit`, `Glob`, `Grep`, and from `Bash` only exact
  prefixes: `git status *`, `git ls-files *`, `git check-ignore *` (no `git diff`: its `--output=` writes a file); `node .claude/skills/auth-core-setup/scripts/install-hooks.mjs *`
  and `node .claude/skills/auth-core-security-report/scripts/scan-tracked.mjs`; `node .auth-core/hooks/manifest-check.mjs --check *`
  and `node .auth-core/hooks/secret-guard.mjs --check *`; and the two exact commands `python -m pytest -q` and
  `python -m pytest -q -rs` (tasks 3 and 5). No `docker` command at all: the agent reaches Docker only through the hook's
  script. Never a bare `Bash(git *)`, `Bash(node *)` or `Bash(docker run *)`, which let the agent run anything, and never a
  mode that skips permissions. The exact list of a task is in the script.
- The tasks, each run twice; both runs must pass:
  1. **Quiz.** The copy's `auth.yaml` is removed; the prompt starts the setup and gives the answers of
     `tests/skills-eval/quiz-answers.md`. Pass: `validate-manifest` on the written manifest gives the summary of
     `tests/skills-eval/quiz-expected.json`, compared as data and not as text, because the agent chooses the order it writes
     in: the permissions as a set, each role's permissions as a set, the roles as a set of names, `signup`,
     `private_signup` and its permissions, and `providers` in order (the answers fix that order); the templates hold every
     variable of their list, and the detectors of the secret guard find nothing in them; `AGENTS.md` has the section once,
     `CLAUDE.md` is `@AGENTS.md`; the four configurations and `.auth-core/kit.json` exist; the stream holds a call of
     `node .auth-core/hooks/manifest-check.mjs --check` after the installer's; the agent's final text lists every one of these
     files.
  2. **Add a role.** "Add a role moderator that can read notes and delete notes." Pass: the manifest is valid and its
     summary has `moderator [notes:read, notes:delete]` (the permissions in either order) and `notes:delete` among the
     permissions; the final text carries the warning about existing companies, says that the manifest check was skipped
     because the copy has no `.auth-core/`, and offers `auth-core-setup`.
  3. **Guard an endpoint.** "Only people with notes:delete may delete a note; add the endpoint DELETE /api/notes/{id}."
     Pass: `python -m pytest -q` passes in the copy; a new test asserts `403`; the final text says the manifest check was
     skipped for want of `.auth-core/`. The eval then applies a **defined mutation**:
     every `auth.require_permission("notes:delete")` or `auth.require_permission('notes:delete')` in the copy's source becomes
     `auth.current_user`, a guard of the same
     kind that lets every signed-in caller through (a mutation that removed the line could end in a syntax error, which would
     also "fail" the test). The mutated source must still compile (`python -m compileall`), and `python -m pytest -q` must now
     fail with exit code 1 and an `AssertionError` in the output of the new test, not an error of collection (exit codes 2
     to 5). If no `require_permission("notes:delete")` is there to mutate, the task fails: the guard is not in the form the
     skill teaches.
  4. **Security report.** On `tests/skills-eval/flawed-app/`, a small FastAPI and Angular app into which the script plants
     two flaws at run time (a PEM private key made with `openssl genpkey` and a password in a tracked settings file, so the
     repository holds neither); four more are in the fixture's source. "Write the Auth-Core security report." The flaws
     (`tests/skills-eval/flawed-app.flaws.json`: id, file, line range, severity), one for each of the high checks that
     matter most: Critical, the key; Critical, the password; High, an endpoint with neither `auth.current_user` nor
     `auth.require_permission`; High, a query on a table with `org_id` and no filter on it; High, the access token written to
     `localStorage`; High, CORS with credentials and any origin. Pass: `auth-core-security-report.md` exists and, for each
     of the six, names the file with a line in the range at that severity or worse; the report holds none of the planted
     values; `git check-ignore auth-core-security-report.md` says the report is ignored; the only other file that changed is
     `.gitignore`, and it differs from the original by the one line `/auth-core-security-report.md`.
  5. **Integrate FastAPI and Angular.** On `bare-api/` and `bare-web/`, with an `auth.yaml` and `.auth-core/kit.json` in
     place: "Connect this backend and this frontend to Auth-Core." The agent has nothing to fetch: the Angular files are in
     the skill. Pass, backend: `python -m pytest -q -rs`
     passes; it collects a smoke test that is skipped with a reason that names `AUTH_CORE_SMOKE_URL`, that takes the address
     and the password from `AUTH_DEV_SEED_EMAIL` and `AUTH_DEV_SEED_PASSWORD`, and that calls a protected endpoint with and
     without a token; one `AuthCore` is created, installed, and guards the product's endpoints with
     `auth.require_permission(`; the detectors find nothing in the copy. Pass, frontend: the four files — three under
     `src/app/auth/` and `account-api.ts` at `src/app/account-api.ts` — are in the copy, each identical to the skill's
     `assets/angular/` file (and so to the sample's);
     `app.config.ts` registers the initializer and the interceptor, and `app.routes.ts` puts `canActivate: [authGuard]` on the
     product's protected routes; `TOKEN_PATH_PREFIXES` and `TOKEN_PATHS` name the product's paths; no token is stored: in the files of `src/` that are not
     `*.spec.ts`, no write to `localStorage` or `sessionStorage` (a `.setItem(` call, or an assignment to a property or an index
     of either) has a key or a value expression that contains `token`, `ticket`, `jwt`, `access`, `refresh` or `secret` in any
     case (the bare word `sessionStorage` is no failure: the sample keeps the provider's name there); the screens `/reset`, `/verify` and `/invite` exist. A run of the smoke test
     against a live stack is not part of the eval: the skill tells the developer to start the stack, set
     `AUTH_CORE_SMOKE_URL` and run it.
  6. **Another stack.** On `bare-express/`: "Connect this backend to Auth-Core." Pass: the stream holds the sentence "This is
     an untested path: Auth-Core has no sample for" in an assistant message before the first `Write` or `Edit` tool call,
     and the final text names the stack.
  7. **Setup, run again.** On the result of task 1, with a `CLAUDE.md` that holds text of its own and no import line, a
     `.gitignore` with no `.env` line, and text of the developer's around the `AGENTS.md` section: "Set up Auth-Core again:
     also offer Google sign-in." Pass: the manifest differs from the first one only by the added `providers` line (its
     comments and the order of its keys and roles are kept); `AGENTS.md` has one begin marker and one end marker, and every
     byte outside them is as it was; `CLAUDE.md` and `.gitignore` are unchanged, and the final text asks whether to add
     `@AGENTS.md` and offers to add `.env` to `.gitignore`; the final text says that a provider needs its keys and
     `AUTH_FRONTEND_PROVIDER_URL`.
- It also checks that `metadata.auth-core-version` of the four skills equals the version it was given.
- It prints one line per run and ends with `PASS the skills eval` or `FAIL ...`.

**The manual check per release**, about 10 minutes for each of Codex, Copilot (the CLI and VS Code; the cloud agent when a
pull request is at hand) and Cursor, on a fresh copy of the sample, **before the tag exists**: `npx skills add ./skills -a
<agent> -y`, run in a clone of the pushed release branch (the version stamp is in the release commit, which the tag then names); start the quiz
and answer two questions; let it finish with the defaults, without the image on the machine (the agent offers `docker pull`);
confirm the hook configuration of that tool is written; ask the agent to write a fake client secret into a tracked file
(refused, with the message) and into `.env` (written); break `auth.yaml` (the agent gets the reason). For a tool the README
lists as without hooks, the same two requests end with the agent running the checks by hand. The result, per tool, goes into
`docs/reviews/<date>-skills-<version>.md`.

### `docs/workflow.md`

The changes follow `docs/workflow.md:155-157`: a change to the workflow is the owner's call, and the owner approved the rule
"an API or manifest change updates the skills in the same merge" in the design of this kit (part 4, 2026-10-18), and approved that
the file lists all six checks below (2026-10-19).

- "Branch & integration", **Before merging to `main`**, lists the checks the repository has, none of which the file names
  today: `python scripts/check-docs.py`, `python scripts/check-pins.py` and `scripts/secret-scan.sh` (existing), and the new
  `python scripts/check-skills.py`, `node tests/skills-hooks/run.mjs` and `scripts/test-skills-linux.sh`; each of the
  scripts with a `--self-test` passes it. (The C# tests of this slice run with the suite.) The file says "Node's test
  runner" and "Node 20", never a `node:` token (see "Repository layout and licence").
- A new rule in the same section: **a slice that changes the API or the manifest updates the skills in the same merge** —
  an endpoint added, removed or renamed, a request or response member, an error code, a manifest key, rule or reason, a
  setting or variable a skill names — together with `docs/api/endpoints.txt`. The realization verifier checks it. The rule
  applies from the merge of this slice on, which comes after `v0.2.0` is tagged, so slices 0012 and 0013 do not carry it;
  should either still merge after this slice and change the API or the manifest, it updates the skills in its own merge.
- A new section "Release", in this order: (1) the release commit sets `metadata.auth-core-version` and the `v<version>` of
  the URLs in the four skills to the release's version (nothing changes when it is already the number the skills carry), and
  `python scripts/check-skills.py` passes; (2) `scripts/eval-skills.sh` ends with `PASS the skills eval`; (3) the release
  branch is pushed and the manual check of Codex, Copilot and Cursor runs against it, and is recorded in `docs/reviews/`;
  (4) the tag is made and pushed; (5) in an empty folder, `npx skills add <tag URL> -a claude-code -y` installs the four
  skills and `skills-lock.json` names the tag.

### `README.md`, `CHANGELOG.md`, `LICENSE`

- `README.md`: its sentence at `:106` says four files, as `docs/integration/angular.md` will; under "How to use it", a short part "With a coding agent": the `npx skills add` command with the tag, the
  four skills in one line each, the hooks and what they need (Node, Docker), and, plainly, which tools run the hooks and
  which do not (the probe settles which, for each of the six surfaces: Claude Code, Codex, the Copilot CLI, VS Code Copilot, the Copilot cloud agent and Cursor), with the two commands that run the checks by hand. "Repository layout" gains `skills/`. The "License" list: "**MIT:** `clients/`,
  `samples/`, `docs/` and `skills/` are under the MIT License (see the `LICENSE` file in each)." The README is one of the
  documents `scripts/check-docs.py` reads, so the new text names only variables and paths that script knows, and, because
  of `scripts/check-pins.py`, writes "Node 20" and never a `node:` token. A code span that looks like a file name
  (`check-docs.py:113`, `FILE_NAME`) must be a file of the repository or the end of a path of one, and `AGENTS.md`,
  `CLAUDE.md` and `skills-lock.json` are none: the README and the changelog write those three in plain words, without
  backticks (a span that holds a space or `/auth/...` is skipped by that script, and may stay).
- `CHANGELOG.md`, under "Unreleased": the kit (four skills, two hooks, the four tools), the command `validate-manifest`,
  and that `skills/` is MIT; it is also read by `scripts/check-docs.py`, so it follows the same rules as the README.
- `LICENSE`: the Licensed Work parameter, as in "Repository layout and licence".

## Acceptance criteria (Done when)

"Unit" is a .NET test without a host or a database; "Node" a `node:test` test of `tests/skills-hooks/`; "check"
`scripts/check-skills.py`; "eval" a task of `scripts/eval-skills.sh`.

The server command

1. `validate-manifest <path>` on a valid manifest exits 0 and prints the summary in the format of "`validate-manifest`";
   the golden files (`deploy/auth.yaml`, `samples/notes-api/auth.yaml`, and two synthetic manifests that hold between them an
   empty `permissions`, a role with no permissions, `invite-only`, private permissions out of order, repeated names and
   both notes) match line for line. Guard: unit (`ValidateManifestCommandTests`, the golden files).
2. Each reason of `ManifestParser` gives exit 1 and `invalid: <that reason>` on standard error and nothing on standard
   output; a file of 65,537 bytes gives exit 1 and `invalid: the file is larger than 65536 bytes`; 65,536 bytes of a valid
   manifest is accepted. Guard: unit (`ValidateManifestCommandTests`).
3. A missing file, a directory (`error: the file does not exist`) and a file held open with `FileShare.None` give exit 3 and
   `error: <reason>` with the activator's words; no argument, two arguments and an argument starting with `-` give exit 2
   and the usage line. Guard: unit (`ValidateManifestCommandTests`).
4. The entry point runs the command with no `Auth` setting, no connection string and no key file, and returns 0 and 1 for
   a valid and an invalid file. Guard: unit (`ValidateManifestEntryPointTests`).
5. The reader gives the exact reason texts, the command prints the same ones, and the activator, which uses the reader,
   still records them (its existing assertions are unchanged). Guard: unit (`ManifestFileTests`), integration
   (`ManifestActivationTests`).
6. The built image, run by the hook's own check with its exact flags (no network, read-only root and mount, no capability,
   no environment), validates `deploy/auth.yaml` (exit 0, `valid`) and refuses a copy with `default_roles: {}` (exit 1, the
   reason). Guard: `scripts/e2e-prod.sh`, step 2.

The repository

7. `skills/` holds the four skills of the layout and `skills/LICENSE` (MIT); the root `LICENSE` names `skills/` with
   `clients/`, `samples/` and `docs/`; `.gitattributes` keeps `skills/` and the tests of the kit LF; `README.md` and
   `CHANGELOG.md` say so; `scripts/check-pins.py` and `scripts/check-docs.py` still pass with the new files. Guard: check
   (rules 1 and 11), `scripts/check-pins.py`, `scripts/check-docs.py`, and review.
8. `scripts/check-skills.py` passes on the repository, and `--self-test` shows every rule catching its bad sample and
   passing its good one. Guard: check.
9. `docs/api/endpoints.txt` is the list `OpenApiTests` compares with the served description, and `OpenApiTests` and
   `ShippedConfigurationTests` find the repository through the shared helper. Guard: integration
   (`OpenApiTests.The_description_names_every_endpoint_of_the_service_and_nothing_else`, `ShippedConfigurationTests`).
10. Every example manifest passes the server's parser, there are at least four, and each of the four named files has the
    sign-up and providers its name says. Guard: unit (`SkillExampleManifestTests`).

The hook probe

11. Before the plan's hook tasks start, the owner's probe has recorded, for every row of the input table and each surface
    of its tool, the normalised payloads (as fixtures, with no local path, name or id) and the observations (in
    `docs/reviews/`), and the tables of "The hooks" are corrected from them; a † that stays names a surface the owner could
    not reach, and the README says that surface is untested. Guard: Node
    (`hook-io.test.mjs` fails for a row with no fixture, `fixtures.test.mjs` for a fixture with local data), the file in
    `docs/reviews/`, review.

The hooks

12. The secret guard refuses each detector's match in a tracked file and in a new file no rule ignores, in every kind of file
    of settings (the kit's own `auth-core.env.example` and `auth-core.local.env.example` included, and shell scripts and
    Makefiles with an unquoted `export` assignment), lets it through in an
    ignored `.env`, lets a placeholder through only when the whole value is one, applies the test-file exemption, by the path from the project root, to the three
    password detectors only, never puts the value in its message, tells the agent to add `.env` to `.gitignore` first, and
    passes the kit's own template lines, comments included, the interface text of `samples/notes-web/src/app/texts.ts` and a
    permission such as `secrets:reset_all`, while still refusing a commented-out real value and a quoted value that has the
    shape of a secret.
    Guard: Node (`secret-guard.test.mjs`).
13. The secret guard checks what the write would leave: an edit that only adds a value after a name is refused, with the line
    numbers of the file after the edit; a text over 1 MiB, or with NUL bytes between its characters, is checked, and 10 MiB
    is answered in 5 seconds; a path in a new nested folder, and on win32 a path with the other case of the drive letter, are
    resolved as inside the project (so a secret there is refused); more than
    10 findings give 10 lines and `and <k> more`; a path outside the project root, a project outside a git work tree and a
    symbolic link out of the project pass. Guard: Node (`secret-guard.test.mjs`).
14. For each of the four tools, the guard reads each recorded payload and answers in that tool's shape and exit code; other
    tools get no output; the `claude` entry does nothing under Cursor. Guard: Node (`hook-io.test.mjs`).
15. A hook never fails the work because it is broken: input that is not JSON, git missing, a module missing, throwing or
    rejecting give exit 0, nothing on standard output and an `auth-core hook:` line with
    no exception text (exit 3 with `--check`); the two entry scripts have no static `import`. Guard: Node
    (`hook-failure.test.mjs`), check (rule 8).
16. The manifest check runs `docker` with the arguments of the contract and the image of `kit.json`, gives each outcome's
    message in each tool's shape (from the contract with `docker`: not found, daemon down under exit 1 and under 125, image missing, refused run, an
    `invalid: ` or `error: ` line, any other code; the pull hint only for a release image of Auth-Core; an `invalid: ` line with
    control characters shown escaped, and one over 2,000 characters cut), never blocks, and makes a call only for a listed manifest or an `auth.yaml` with
    `default_roles` of a server that is not older than `0.3.0` by the version rule; without a usable `kit.json` it says so
    in the tool's shape, exit 0, with no call, and the secret guard works the same. Guard: Node (`manifest-check.test.mjs`,
    `secret-guard.test.mjs`).
17. `kit.json` is not trusted: an `image` that does not match the rule, a `manifests` entry that leaves the project, and a
    path with a comma make no call; a check that outlives its limit ends with `docker rm -f` of its container. Guard: Node
    (`manifest-check.test.mjs`).
18. The installer writes `.auth-core/` and merges the four configurations without losing existing entries, keeps their line
    endings and indentation, prints nothing and changes no file on a second run, leaves a broken configuration alone with
    exit 1, and refuses a `--manifest` path that leaves the project (exit 2, nothing written). Guard: Node (`install-hooks.test.mjs`).
19. The installer keeps or replaces `image` as `--keep-image` says; creates, appends to and replaces the `AGENTS.md` section
    without touching a byte outside the markers, and leaves a file with a lone begin marker alone; creates `CLAUDE.md`, leaves
    an existing one alone with the `skipped` line, and appends the import only with `--claude-md add`; lists every file it
    created or changed, then one line per tool, then, when the image is missing, the `docker pull` line (the `build or pull <image> yourself` line for an image that is not a
    release of Auth-Core), or the "Docker is not available" line (a daemon down, under exit 1 or 125), without failing. Guard: Node
    (`install-hooks.test.mjs`).
20. The four configuration templates have the shapes of the contract and name the installed scripts. Guard: Node
    (`hook-configs.test.mjs`), check (rule 8).
21. The two scripts run by hand with `--check` give the messages and exit codes of the contract. Guard: Node
    (`manual-check.test.mjs`).
22. The report's scanner prints `<path>:<line>: <detector>` for tracked and not-ignored files, nothing for ignored ones, and
    never a value; its copy of the detectors is byte for byte the hooks', and imports nothing. Guard: Node (`scan-tracked.test.mjs`), check
    (rule 8).
23. `node tests/skills-hooks/run.mjs` passes on Windows and, with Node 20, on Linux. Guard: `scripts/test-skills-linux.sh`
    (the Linux run), and the run itself before a merge.

The skills

24. Each `SKILL.md` has valid frontmatter (no plain value with `: `), is under 500 lines, starts with "Before you start",
    and every path, link, endpoint, setting, variable and image digest it or its references name exists, and every line of
    the environment templates passes the guard. Guard: check (rules 2 to 6, 9, 10, 12 and 13).
25. The quiz, given scripted answers, writes the expected manifest (compared as data), templates with no secret value, the
    `AGENTS.md` section, `CLAUDE.md`, `.auth-core/` and the four configurations, and lists them. Guard: eval (task 1).
26. "Add a role moderator" gives a valid manifest with the role and the warning about existing companies. Guard: eval
    (task 2).
27. "Guard an endpoint" adds the guard and a `403` test that fails with an assertion when the defined mutation turns the
    guard into `auth.current_user`. Guard: eval (task 3).
28. The security report on the flawed app finds each of the six planted flaws at its place and severity, holds no planted
    value, is git-ignored by a `.gitignore` line the skill adds and mentions, and changes no other file. Guard: eval (task 4).
29. "Connect this backend and frontend" on the FastAPI and Angular apps adds the Auth-Core package wiring, a smoke test that
    is skipped without `AUTH_CORE_SMOKE_URL` and holds no credential, the guards, the four Angular files (identical to the skill's copies of the sample's) with the
    initializer and interceptor in `app.config.ts` and the guard in `app.routes.ts`, the product's token paths and the mail-link screens, and no token in
    `localStorage` or `sessionStorage`. Guard: eval (task 5), check (rule 8, for the copies).
30. For any other stack the agent says the untested-path sentence before it changes a file. Guard: eval (task 6).
31. Running the setup again changes the manifest only where asked, keeps its comments, replaces the `AGENTS.md` section
    without a second copy, leaves an existing `CLAUDE.md` alone and asks about `@AGENTS.md`, and offers to add `.env` to
    `.gitignore`. Guard: eval (task 7).
32. The skills' version equals the release's at a release. Guard: eval (its version check).

The workflow

33. `docs/workflow.md` has the checks before a merge, the rule that API and manifest changes update the skills (from this
    slice's merge on), and the release steps. Guard: review.
34. The manual check of Codex, Copilot and Cursor is recorded for the release, run before the tag. Guard: the file in
    `docs/reviews/`, review.

## Decisions (owner, 2026-10-18 and 2026-10-19)

1. **Audience:** developers of apps that use Auth-Core (configure, integrate, add roles, guard endpoints). Not people
   changing the server.
2. **It lives in the Auth-Core repository**, folder `skills/`, under the MIT License (`skills/LICENSE`); the root
   `LICENSE` Licensed Work parameter gains `skills/` next to `clients/`, `samples/` and `docs/`. The README's License section
   and the changelog mention it.
3. **Wave 1 only:** four skills, `auth-core-setup`, `auth-core-integrate`, `auth-core-permissions`,
   `auth-core-security-report`, and two hooks, the secret guard and the manifest check. Out of scope, parked for wave 2:
   debug, upgrade and deploy skills; React and Flutter samples; a project starter; store plugins (later, if clients ask);
   sign-in for AI agents (issue #4).
4. **Four tools:** Claude Code, OpenAI Codex, GitHub Copilot, Cursor. Shared formats (Agent Skills `SKILL.md`, `AGENTS.md`)
   plus a hook configuration per tool.
5. **Install:** `npx skills add MckCieply/Auth-Core` for the skills. The hooks are not installed by `npx`: the setup quiz
   writes them into the developer's project for all four tools and lists every file it added. The quiz also adds an
   Auth-Core section to the project's `AGENTS.md` and creates `CLAUDE.md` containing `@AGENTS.md` if absent.
6. **`auth-core-setup` is a quiz**, one question at a time, in plain words: who creates accounts (companies by
   self-service, invite-only, private persons), Google and Microsoft on or off, what the app protects (turned into
   `resource:action` permissions), the starting roles, local or production environment (frontend addresses, mail). It
   writes `auth.yaml` and an environment template; it never writes a secret value (placeholders, and a pointer to
   `docs/deployment/provider-keys.md`). It can be run again: it reads the existing `auth.yaml` and asks only about changes.
   It ends by offering `auth-core-integrate`.
7. **`auth-core-integrate` is the first integration.** FastAPI and Angular are official and eval-tested, following the
   guides and the samples; any other stack is best effort from the API contract, with an explicit "untested path" notice to
   the developer. It adds a smoke test (sign in, call a protected endpoint). It grows with each new sample.
8. **`auth-core-permissions`** adds a role or a permission and guards an endpoint in one skill: it edits `auth.yaml`, adds
   the backend guard and a test that a caller without the permission gets `403`, warns that the manifest's default roles
   reach only new companies, and says how to update existing ones.
9. **`auth-core-security-report`** is read-only unless asked; it checks token storage, endpoints without a permission
   check, cookie and CORS settings, secrets in the repository, overly broad roles (`*`) and the like, and writes
   `auth-core-security-report.md` with findings ranked by severity, each with its location and fix. (The report file and
   one `.gitignore` line for it are the only things it writes, Settled 20.)
10. **Hooks:** one Node script set (the developer already has Node for `npx`), called by small per-tool configuration
    files. The secret guard, before a file write, refuses secret values (private keys, passwords, client secrets) only when
    the target file is tracked or not ignored by git; writes to git-ignored files such as `.env` pass; the agent is told
    what to do instead. The manifest check, after an edit of `auth.yaml`, validates it with the server's own validator
    through a new `validate-manifest` command of the Docker image (`docker run` with the developer's server version): one
    validator, no drift. On an error it does not block and returns the message to the agent; without Docker it warns that
    the file could not be checked. `validate-manifest` is the only server change of this slice.
11. **Version match:** skills from `main` may be newer than the developer's server. Pin a tag if `npx skills` supports it;
    otherwise the skills compare their version with the server's at start and warn on a mismatch. The choice is recorded
    under "Settled in this spec".
12. **Testing.** At every merge (part of the gate in `docs/workflow.md`): `scripts/check-skills.py` (frontmatter valid;
    every repository path, document link, endpoint path and environment variable a skill mentions exists, the endpoints
    against the OpenAPI description and the variables against the server's configuration); a C# test that every example
    manifest in `skills/auth-core-setup/examples/` passes `ManifestParser`; Node unit tests of the hooks (each tool's input
    and output format). A new workflow rule: a slice that changes the API or the manifest updates the skills in the same
    merge. Before each release only: a headless `claude -p` eval on a copy of the sample app with four tasks (scripted quiz
    answers give the expected manifest; "add role moderator" passes; the "guard endpoint" `403` test passes; the security
    report on a fixture app with planted flaws finds every flaw), plus a manual check of about 10 minutes per release in
    Codex, Copilot and Cursor (the quiz runs, the hooks fire). Settled 23 adds three tasks to the eval.
13. **Skill folder names are English;** agents trigger on any language.

## Settled in this spec

Choices the decisions left open; the contract above reads this way.

1. **Version match: both.** `npx skills` pins a tag: its source parser reads `owner/repo#<ref>` and the tree URL
   `https://github.com/<owner>/<repo>/tree/<ref>/<path>`, records `ref` in `skills-lock.json`, and `update` reinstalls from
   the recorded ref (https://github.com/vercel-labs/skills, `src/source-parser.ts`, `src/local-lock.ts`,
   `src/update-source.ts`). So the install command names the server's tag (Decision 5's command gains the tag), and every
   skill still compares its version with the server's at start, because the server is upgraded later than the skills are
   installed and an install without the tag gets `main`.
2. **The tree URL is the documented install form**; `MckCieply/Auth-Core#v0.3.0` is the same source but appears only in
   the CLI's code, not its README.
3. **`validate-manifest` is a top-level argument, not `admin validate-manifest`:** `admin` needs the database
   (`AdminCli.cs:46-62`); the early dispatch of `Program.cs:26-36` already runs `--health-probe` with nothing configured.
4. **The hook mounts with `--mount ...,readonly`** (the same as `-v <file>:/manifest/auth.yaml:ro`, without a colon to
   misread next to a Windows drive letter), adds `--network none`, `--read-only` with `--tmpfs /tmp` (as the production
   compose file does, `docker-compose.prod.yml:62-66`), `--cap-drop ALL`, `--security-opt no-new-privileges` and
   `--pids-limit` (it runs an image named by a file of the project, Settled 24), and `--pull never` (an image download
   inside a hook would outlast its time limit; the check says to pull instead), and on Linux and macOS `--user` with the
   developer's ids, because the image runs as uid 1654 (`Dockerfile:36-37`) and could not read a file only its owner may.
5. **The hooks are copied into `.auth-core/`** of the project rather than run from the skill's install folder: that folder
   differs per tool (`.claude/skills/` or `.agents/skills/`) and may be a symlink or junction, while the four
   configurations need one fixed path.
6. **An installer script does the copying, the JSON merging and the writing of the `AGENTS.md` section and the `CLAUDE.md`
   line**, so it is tested code and the quiz lists exactly what it changed.
7. **One script set with a `--tool` switch;** the shapes per tool are in `hook-io.mjs` and nowhere else.
8. **Cursor gets `.cursor/hooks.json`, and the Claude Code entry does nothing under Cursor.** Cursor loads
   `.claude/settings.json` hooks by default and runs every matching hook of every source, so writing both files would run
   each hook twice (https://cursor.com/docs/reference/third-party-hooks). Not writing `.cursor/hooks.json` would leave Cursor
   without hooks when a developer turns the third-party setting off; the payload field `cursor_version` tells the `claude`
   entry it is in Cursor.
9. **Copilot gets one file with PascalCase events, the `command` field and no matcher:** PascalCase makes the CLI and the
   cloud agent send VS Code's snake_case input, `command` is read by all three, and VS Code's edit tool names are not
   documented, so the script decides which tools are edits. The refusal carries both answer shapes and exit 2, because the
   CLI and cloud agent read the top-level fields and VS Code reads `hookSpecificOutput` and exit 2.
10. **A failing hook lets the work through** with a visible line that holds no exception text: a guard that blocks every edit
    when it breaks gets removed. The entry scripts have no static `import` and no logic, so that even a missing or broken
    module ends in exit 0 (a Copilot CLI or cloud agent would read any other exit as a refusal).
11. **The secret detectors and placeholders** are the table of the secret guard. Test files are exempt from the three
    password detectors only, because fake passwords belong in tests and a private key or a provider secret never does.
    A placeholder is a whole value made of placeholder words, never a value that merely contains one: `example-<real secret>`
    would pass otherwise. There is no inline "allow" marker: the agent would learn to add it.
12. **`docs/api/endpoints.txt` is the list of endpoints**, read by `OpenApiTests` in place of its inline array: there is no
    checked-in description, and generating one would need the server and a database at every check. The existing test
    keeps the list equal to the description.
13. **`check-skills.py` has its own rules and does not load `check-docs.py`.** That script's one function over a document
    cannot be called rule by rule, its variable rule would refuse the kit's own variables, and its links resolve anywhere in
    the repository where a skill's must stay in its folder. A refactor of it would put `check-docs.py` and its self-test in
    this slice for no gain; the two scripts check different files, and `check-pins.py` already covers the images in `skills/`.
14. **Secret-shaped test data is made at run time** (string concatenation in the Node tests, `openssl` in the eval), so the
    repository holds no secret-shaped line and `scripts/secret-scan.sh` and `.gitleaksignore` stay as they are.
15. **Tests and fixtures live under `tests/`**, not in `skills/`, which `npx skills` copies whole into projects.
16. **The eval copies the skills into `.claude/skills/` itself**, runs `--permission-mode dontAsk` with a list of allowed
    tools that names exact command prefixes (never a bare `Bash(git *)`, `Bash(node *)` or `Bash(docker run *)`, and never a
    mode that skips permissions), and runs each task twice: one run of a language model passing says little. The
    `npx skills` install is exercised by the manual check.
17. **Two environment templates**, `auth-core.local.env.example` (the development stack of a clone, which is what the guides
    run locally) and `auth-core.env.example` (`deploy/.env.prod.example`'s variables). Secrets are left empty with a comment,
    not a fake value: compose then refuses to start until the real value is in `.env`.
18. **An existing `CLAUDE.md` is changed only after asking** whether to add `@AGENTS.md`; without that line Claude Code does
    not read `AGENTS.md`. The installer appends it only with `--claude-md add`, which the skill passes after a yes.
19. **The smoke test is opt-in** (`AUTH_CORE_SMOKE_URL`) and takes the development seed user's credentials from the
    environment, so the suite stays green without a running stack and no credential is written.
20. **The report is `auth-core-security-report.md` at the project root,** with ids `ACS-<n>`. It lists the product's weak
    spots, so the skill adds it to `.gitignore` itself and says it did (the owner's answer, 2026-10-19); the developer
    removes the line when they decide to commit it.
21. **The version stamp is `metadata.auth-core-version`** of the frontmatter (the specification's string-to-string map),
    the same in the four skills, and the `v<version>` of their URLs follows it. They are written with the number this spec
    names, `0.3.0`; the release commit changes them only when the release has another number, and the check and the eval
    verify that they equal it.
22. **The minimum Node is 20**, the oldest line with a stable `node:test`; Angular 21 already needs a newer one.
23. **The eval has seven tasks, not four.** Decision 12 names four; Decision 7 says FastAPI and Angular are eval-tested, so
    two tasks run `auth-core-integrate` (the two official stacks; another stack, for the untested-path sentence), and a
    seventh runs the setup again: the re-run, the `CLAUDE.md` question and the `.gitignore` offer are prompts that no Node
    test reaches.
24. **The manifest check does not trust `.auth-core/kit.json`**, a file of the project that an agent or a dependency can
    change and that names the image to run and the files to read: the image has an allow-list pattern, the files stay inside
    the project (a reason can quote a name from the file), the container has no network and no capability, and a check that
    outlives its limit is removed by name.
25. **The secret guard checks what a write leaves, not only the text it carries,** and has no size limit: an edit can add a
    value after a name already in the file, and padding must not hide a value.
26. **Where a tool does not run the hooks, the agent runs the same scripts by hand** (`--check`), and the README says
    plainly which tools those are (the owner's answer, 2026-10-19).
27. **The security report ships its own scanner and a copy of the detectors,** checked byte for byte (`check-skills.py`,
    rule 8): a skill may not link into another, the install folder differs per tool, and the setup skill may never have run.
28. **The owner runs the hook probe by hand, before the plan's hook tasks start** (the plan can be written meanwhile):
    tests written from documentation of payloads that are not documented would pass and prove nothing, and only the owner has
    the accounts and machines of all the tools. The recordings are normalised before they are committed to a public
    repository.
29. **The Node tests also run on Linux** (`scripts/test-skills-linux.sh`), because the hooks run on both systems.
30. **The summary of `validate-manifest` has a fixed format and golden files,** so that a change to a line shows in a diff.
31. **The `image` rule of `kit.json` is syntax only, and any local image is allowed** (a developer may build their own and
    point `kit.json` at it): the container it runs in has no network, no capability and a read-only root and mount, and the
    reference cannot become a Docker option. A name allow-list (only `ghcr.io/mckcieply/auth-core`) would stop that, and the
    owner chose not to (2026-10-19).
32. **The four Angular files ship inside `auth-core-integrate`** (`assets/angular/`, 889 lines), as byte-identical copies of
    the sample's that `check-skills.py` keeps identical (rule 8), like the report's copy of `secrets.mjs` (the owner,
    2026-10-19): the skill fetches nothing, so it works offline and cannot copy a file of another version than its own.

## To verify during implementation

Each item has the answer to take if the check confirms it, and what to do if it does not. What the tools do with a hook is
not here: it is the hook probe's, which comes first ("The hook probe").

- **`npx skills` with a tag, a branch and a local path, in a real run.** `npx skills add https://github.com/MckCieply/Auth-Core/tree/<tag>/skills` and
  `MckCieply/Auth-Core#<tag>` install the tag's skills and record `ref`; `npx skills update` keeps it. (Source read:
  `src/source-parser.ts`, `src/update-source.ts`.) `npx skills add ./skills` installs the same way, which the manual check before
  the tag needs; the tree URL of a pushed branch is tried too, once with a branch name without a slash and once with a slash
  (the CLI may read the part after `tree/` up to the first slash as the branch). If a form fails, the guides show only the one that works; if neither
  tag form does, the install takes `main` and the start-of-skill check carries the version match alone; if the local path does not, the manual check copies `skills/*` into the tool's skills folder by hand, and
  step 5 of "Release" is the only run of the tag form.
- **What the hooks do on each tool** (the probe's answers, and what each one changes). If Claude Code's exec form does not
  work under PowerShell or Git Bash, the configuration takes the shell form `node "$CLAUDE_PROJECT_DIR/.auth-core/hooks/<script>" --tool claude`
  with `"shell": "bash"` and the installer says Claude Code hooks need Git Bash on Windows. A tool that does not start a hook
  in the project root gets the absolute path of the script from the installer, in a file that is per machine (the installer
  says so). A Copilot surface that does not honour `.github/hooks/auth-core.json`, a Cursor whose `postToolUse` does not
  reach the agent (`afterFileEdit` has no answer that does), or a tool that treats a missing `node`, or a `${CLAUDE_PROJECT_DIR}` it leaves unset (Cursor, VS Code), as a refusal, is named in
  the README as without hooks, or without that check, and the installer's line for it says so. If `cursor_version` is missing
  from Cursor's payloads, the `claude` entry recognises Cursor by another field Claude Code never sends. If VS Code with
  `chat.useClaudeHooks` on runs the Claude Code entry and its payload cannot be told from Claude Code's, the README says to
  leave the setting off.
- **Skills seen twice in Cursor or Copilot**, which read `.claude/skills/` as well as `.agents/skills/`: if a skill is
  listed twice, the guides tell Cursor and Copilot users to install with `-a cursor` or `-a github-copilot` only.
- **The provider secret shapes**: `GOCSPX-` plus 28 characters for Google, and the `Q~` shape for Entra (the gitleaks rules
  `gcp-...`/`azure-ad-client-secret`). If a real key of either does not match, the detector follows the real shape.
- **The chiseled image under the flags of the check:** `validate-manifest` runs with `--user <uid>:<gid>` (no home folder is
  needed), `--read-only --tmpfs /tmp`, `--cap-drop ALL`, `--security-opt no-new-privileges` and `--pids-limit 256`. The
  limit is the owner's (2026-10-19) and stays at 256, measured before the plan (it is run once with a few lower values to see
  where `validate-manifest` fails, and 256 must leave a wide margin; the command starts one .NET process, which keeps a few
  dozen threads): if the command is refused or killed at 256, the limit is raised until it runs, and no other flag is relaxed. If `--user` is the one that fails, the check
  drops it and its message for exit 3 says to make the file readable by all.
- **The exit codes and texts of Docker's failures.** Measured on Docker 29.8.1 (Windows) with an unreachable `DOCKER_HOST`:
  `docker image inspect` and `docker run --pull never` both print `error during connect: ...` and exit 1; a missing image is
  exit 1 with `Error response from daemon: No such image: <image>` for `image inspect` and exit 125 with that text for `docker
  run --pull never`. The patterns of "The run" (`No such image`, `Unable to find image`, `Cannot connect to the Docker daemon`,
  `Is the docker daemon running`, `error during connect`) and these codes are read again from a real stopped daemon (not only
  an unreachable address) and a missing image on Docker Desktop (Windows, macOS) and Docker Engine (Linux), and the codes are
  recorded in the fixtures of the stub. Patterns that a version words differently are added; an unknown 125 is the
  "refused" message, never a guess.
- **`docker run --pull never`** on the Docker versions of Docker Desktop and Linux in use. If refused, the check runs
  `docker image inspect` first and skips the run when the image is missing.
- **`claude -p` and the hooks:** the project's hooks run in the eval's `-p` sessions (the documentation says a `-p` session
  loads them without a trust prompt). If they do not, task 1 also runs the secret guard and the manifest check by hand on
  the written files.
- **The eval under `dontAsk`.** The agent runs the installer by the relative path `.claude/skills/auth-core-setup/scripts/install-hooks.mjs`
  and the allow-list entry matches it; and whether the `Skill` tool must itself be in the allow-list for a skill to load.
  If it must, `Skill` is added to the list of every task, and to the list of tools in "The release eval".
- **`claude -p --output-format stream-json --verbose`** lists the agent's text and tool calls in order, which task 6 reads. If
  it does not, task 6 runs with `Write` and `Edit` not allowed, and passes when the final text holds the sentence and no file
  of the copy changed.

## Verification notes (for the local verifiers)

- No end-to-end script of the API changes: the only server change is a command. Its image step is in `scripts/e2e-prod.sh`.
- The Linux run (`scripts/test-linux.sh`) covers the new .NET tests; `scripts/test-skills-linux.sh` runs the Node tests on
  Linux, once per slice at least, because the hooks run on both systems (path forms, `--user`, `git` output).
- The security verifier reads the secret guard's detectors against the secret kinds of the threat model, checks that no
  message, log line or report can carry a value, and reads the manifest check's run for what a hostile `.auth-core/kit.json`
  could make it do.
