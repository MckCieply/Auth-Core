# External C# feedback — 12.10.2026

Feedback from a .NET developer (8 years of server work) on the state of the repository after slice 0009. Each point is
checked against the code. Only the points marked **valid** or **partly valid** become work; the rest keep the reasoning
so the question does not come back.

Snapshot at the time of review: 2 projects (`Auth.Server`, `Auth.Infrastructure`), 144 source files outside migrations
(median 54 lines, largest 311, 45 files under 40 lines), 572 lines with `var`, 6 migrations, Linux chiseled image.

## Summary

| # | Point | Verdict | Action | Status |
|---|-------|---------|--------|--------|
| 1 | Explicit types instead of `var` | Partly valid (readability, not the compiler) | Owner decides; if yes, enforce with an analyzer rule | **Open**: owner decision |
| 2 | LINQ only in single queries, as small as possible | Partly valid | Review in-memory LINQ on request paths; EF queries stay | **Open**: not part of the gap work |
| 3 | Start migrations from a base instead of snapshots/designers | Unclear | Ask the reviewer what "base" means | **Open**: question to the reviewer |
| 4 | Seed with seeders, not migrations | Already done | None | Nothing to do |
| 5 | Over-built helpers; merge small files into ~1k-line files | Partly valid | Simplify named helpers; group by feature, not by line count | **Open**: question to the reviewer |
| 6 | Keep to APIs that work on Linux | Valid as a rule; code is clean today | Run the test suite on Linux, not only on Windows | **Done**: `scripts/test-linux.sh`, part of the pre-merge checklist |
| 7 | Profiler/guard service next to the server, `GC.Collect` on high allocation | Not adopted as described | Memory limit + runtime metrics + container health instead | Memory limit and health check **done**; runtime metrics **open** (later) |
| 8 | Separate DLL per sector | Not now (YAGNI; the reviewer agrees) | None | Nothing to do |

## 1. Explicit types instead of `var` — partly valid

- The compiler claim does not hold: `var` is resolved at compile time and produces the same IL as the explicit type.
  There is no runtime or compiler difference.
- The readability point holds: on GitHub, in a diff, or in a review without an IDE, `var x = await svc.GetAsync(...)`
  hides the type. Many .NET teams require explicit types except where the type is on the right side (`new`, cast).
- **Proposed action (needs owner decision):** add an `.editorconfig` with
  `csharp_style_var_for_built_in_types = false`, `csharp_style_var_elsewhere = false`,
  `csharp_style_var_when_type_is_apparent = true`, and `dotnet_diagnostic.IDE0008.severity = warning`, plus
  `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`. With `TreatWarningsAsErrors` the build then fails until
  the ~572 sites are fixed. The fix is mechanical (IDE "use explicit type" over the solution), roughly 1–2 hours
  including the test run.

## 2. LINQ only in single queries — partly valid

- Most LINQ in `src/` is EF Core (`Where`/`AnyAsync`/`FirstOrDefaultAsync`/`ExecuteDeleteAsync`). EF translates it to
  SQL; it is not LINQ-to-objects and has no per-element cost on the server. This stays.
- In-memory LINQ exists and costs allocations (iterators, closures). It is fine on cold paths (admin, seeding,
  manifest parsing at startup) and worth removing on hot paths.
- Hot-path sites found:
  - `src/Auth.Server/Sessions/AccessTokenClaimFilter.cs:34,44` — `.Where(...).ToList()` on every token issuance.
  - `src/Auth.Server/Sessions/RefreshCookie.cs:34` — `value.Any(char.IsControl)` on every refresh.
- Mixed sites: `src/Auth.Server/Tenancy/RoleService.cs:23-33` loads roles, then groups and sorts in memory. Sets are
  small (roles of one company), so this is readability rather than performance.
- **Proposed action:** replace LINQ-to-objects with plain loops on the token/refresh/login paths; leave EF queries and
  cold-path LINQ alone. Rule for new code: no LINQ-to-objects on a per-request path.

## 3. Migrations "from a base" — unclear

- Praise noted: every migration has a working `Down`.
- "Start from a base instead of snapshots/designers" can mean two different things:
  1. Squash the 6 migrations into one baseline migration before the first production deploy (prod is not live yet:
     Toviq M14–M18). Cheap now, painful after prod.
  2. Drop EF-generated designers/snapshot and keep the schema as hand-written SQL scripts (a base script plus deltas).
     This leaves `dotnet ef migrations add` behind and is a larger change.
- **Open question for the reviewer:** which of the two was meant?

## 4. Seeders instead of seeding in migrations — already done

- No migration contains `InsertData`, `HasData` or `migrationBuilder.Sql` with data. The only seeding is
  `src/Auth.Server/Seeding/DevUserSeeder.cs`, which runs at startup and only in `Development`.
- No action.

## 5. Over-built helpers and many small files — partly valid

- The reviewer named no specific helper. Without names, this cannot be checked line by line.
  **Open question for the reviewer:** which 2–3 helpers looked over-built?
- Candidates worth a second look (small types that may be one concept split in several files): `Email/StorableTime.cs`,
  `Requests/IdInput.cs`, `Signup/SignupCompletion.cs`, `Signup/SignupGate.cs`, the two `*HealthCheck.cs` files,
  `Lockout/LoginIdentifier.cs`. Some small types are deliberate for security (for example
  `Lockout/DecoyPasswordHash.cs` exists for timing resistance) and stay.
- The "~1k lines per file, C# dislikes small files" claim does not hold for the compiler: Roslyn compiles the whole
  project at once, and the number of files has no measurable effect on build or runtime. The common .NET convention is
  one public type per file; entities in `Auth.Infrastructure/Persistence/` follow it and stay.
- **Proposed action:** group closely related small types per feature where they form one concept (for example the
  request/response records of one feature in one `*Contracts.cs`, as `Api/AccountContracts.cs` already does), and
  simplify any helper the reviewer points to. No target file size.

## 6. Linux-compatible APIs — valid as a rule, clean today

- Search for Windows-only APIs (`Registry`, `EventLog`, `ProtectedData`, `System.Drawing`, `WindowsIdentity`,
  `PerformanceCounter`, hard-coded `\\` paths) found nothing in `src/`.
- The production image is Linux (chiseled), and `scripts/e2e-login.sh` runs the real container.
- Gap: the unit and integration tests run on Windows only (no CI; Testcontainers only puts Postgres on Linux). A
  Linux-only failure in a code path that the e2e script does not touch would not be seen.
- **Proposed action:** add a script that runs `dotnet test` inside the .NET SDK Linux container (with the Docker socket
  for Testcontainers), and run it before each merge to `main`.
- **Done:** `scripts/test-linux.sh` runs `dotnet test --solution Auth-Core.slnx` in the SDK image of the Dockerfile (same
  digest) with the Docker socket mounted and `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`, on a copy of the files git
  knows, so the Windows `bin/` and `obj/` stay untouched. `docs/workflow.md` lists it in the checklist before a merge to `main`.
  There is still no CI, by the owner's choice.

## 7. Profiler + guard service, `GC.Collect` on high allocation — not adopted as described

- Forcing `GC.Collect` is an anti-pattern in modern .NET: it blocks all threads, promotes live objects to older
  generations, and usually makes the next collections more expensive. The .NET 10 GC already adapts to load, and it reads
  the container memory limit (the default hard limit is 75% of the cgroup limit).
- Process supervision is the job of the container runtime and the host: `restart: unless-stopped` is already set in
  `deploy/docker-compose.prod.yml`, and production will run under Toviq's deploy (M14–M18). A watchdog process inside
  the same deploy adds a second failure point without adding a guarantee that Docker does not already give.
- Real gaps the point exposes:
  - The `auth` service has no memory limit in `deploy/docker-compose.prod.yml`. Without it the GC sizes itself to the
    whole host. **Action:** set `mem_limit` (and optionally `DOTNET_GCHeapHardLimitPercent`).
    **Done:** `mem_limit: ${AUTH_MEM_LIMIT:-512m}`, with `memswap_limit` at the same value so that going over ends in a kill, not in
    swapping. `DOTNET_GCHeapHardLimitPercent` is not set: the default of 75% of the
    cgroup limit is what we want, and `docs/deployment/vps.md` names it as an optional knob.
  - The `auth` container has no healthcheck (the chiseled image has no shell or curl), so Docker restarts it only when
    the process exits, not when it hangs. **Action:** a health probe that works without a shell (a tiny probe binary in
    the image, or an external check from Toviq), decided together with M14–M18.
    **Done, differently from the options listed:** `Auth.Server.dll --health-probe` (checked at the top of `Program.cs`,
    before the host is built) makes one GET of `/auth/health` on `127.0.0.1` and exits 0 for a 2xx, 1 otherwise; the
    compose file runs it as the healthcheck. `/auth/health` is the liveness answer already (it does not reach the database,
    and `Degraded` is a 200), so no endpoint was added. A separate probe binary, a `HEALTHCHECK` with curl, a watchdog
    service and a forced `GC.Collect` were rejected. Docker only marks the container `unhealthy`; it does not restart it for
    that, so a down database never restarts `auth`.
  - No runtime metrics. **Open.** **Action (later):** expose .NET runtime metrics (GC heap size, allocation rate, thread pool)
    through OpenTelemetry or `dotnet-counters`, so memory problems are seen before they matter.

## 8. Separate DLL per sector — not now

- The code is already split by feature folder (`Login/`, `Tokens/`, `Tenancy/`, `Signup/`, …) in 2 projects. More
  assemblies would add build and reference overhead without a consumer who needs them separately. The reviewer also
  called it too much for this project.
- No action. Revisit only if a part needs to ship or deploy on its own.

## Done and open

- **Done:** point 6 (Linux test script) and the memory limit and health check of point 7.
- **Open, owner decision:** point 1 (explicit types).
- **Open, question to the reviewer:** points 3 and 5.
- **Open, not in this work:** point 2 (hot-path LINQ) and the runtime metrics of point 7.

## Open questions for the reviewer

1. Point 3: "base" means a squashed baseline migration, or hand-written SQL instead of EF designers?
2. Point 5: which helpers looked over-built?
