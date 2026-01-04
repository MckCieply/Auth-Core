# Development workflow

How work gets done in Auth-Core: a spec-driven, agent-orchestrated flow with
verification built in. It is grounded in conventions proven in three sibling
projects — `speech-to-mail`, `Investing-Partner`, `stereo-splitter` (see
[Sources](#sources)) — adapted to one deliberate constraint:

> **Local-only.** No GitHub Issues, no CI, no GitHub pull requests. Work happens
> on feature branches that are **merged into `main` locally** (the "MR" step),
> after local verification passes. The rigorous review that the sibling projects
> run as CI gates, we run **locally as agents** before the merge.

## Model policy

- **Orchestrator: always Opus.** The session that plans, decomposes work,
  dispatches agents and makes judgement calls runs on Opus.
- **Agents: Sonnet by default.** Every dispatched subagent — implementer and
  verifiers — runs on Sonnet. Opus decides; Sonnets do.
- **Per-role tuning (from `Investing-Partner`'s `_models.md`).** Not everything
  needs the same model: a pure-execution step (no reasoning) may drop to **Haiku**,
  and a max-quality pass may lift a decision-heavy verifier to **Opus**. Running
  everything on Opus is ~5x the cost without 5x the result; thinking effort earns
  its keep on the hardest judgement steps, not on mechanical ones. This tunes the
  default above — it does not replace it.

## Plugins

The flow is built on three Claude Code plugins, installed from their GitHub
marketplaces (see [`.claude/settings.json`](../.claude/settings.json)):

| Plugin | Marketplace source | Role in this repo |
| ------ | ------------------ | ----------------- |
| **Superpowers** | [`obra/superpowers`](https://github.com/obra/superpowers) | Core methodology: brainstorming, writing/executing plans, subagent-driven development, dispatching parallel agents, requesting code review, verification-before-completion. The flow below follows its skills. |
| **Caveman** | [`JuliusBrussee/caveman`](https://github.com/JuliusBrussee/caveman) | Terse, token-efficient output — drops filler, keeps technical substance. |
| **i-have-adhd** | [`ayghri/i-have-adhd`](https://github.com/ayghri/i-have-adhd) | ADHD-friendly output: lead with the next action, number multi-step work, end with one concrete next step, cap lists, drop preamble and recaps. |

Install (interactive equivalent of the committed config):

```
claude plugin marketplace add obra/superpowers
claude plugin install superpowers@superpowers-dev

claude plugin marketplace add JuliusBrussee/caveman
claude plugin install caveman@caveman

claude plugin marketplace add ayghri/i-have-adhd
claude plugin install i-have-adhd@i-have-adhd
```

## The flow (per stage)

Every stage runs the same Superpowers-aligned pipeline. The orchestrator (Opus)
drives; each step is a Sonnet agent unless noted.

1. **Brainstorm — in conversation, not committed.** For each stage, explore the
   problem, the options and the trade-offs first (Superpowers `brainstorming`). The
   brainstorm is **not** a committed artifact; only its outcome is — as the spec's
   `Decisions` section. No code, no spec yet.
2. **Spec** — write the spec as the **final, resolved** version: what is built,
   acceptance criteria, contracts, out-of-scope, and a **`## Decisions (owner,
   date)`** section recording the choices the brainstorm settled. Residual unknowns
   are either numbered open questions carried explicitly or pushed to the plan's
   "Open questions for owner" — never left scattered. **The spec is the source of
   truth.**
3. **Plan** — from the spec, write an implementation plan the Superpowers way
   (`writing-plans`): ordered, verifiable steps with checkpoints.
4. **Implement** — a Sonnet agent executes the plan (`executing-plans` /
   `subagent-driven-development`). Implementation only; it does not grade itself.
5. **Verify** — verification agents run against the spec, locally, before the
   merge. See [Verification](#verification). A stage is done only when every
   verifier passes.

```
brainstorm ──> spec ──> plan ──> implement (Sonnet)
                                      │
                                      ▼
                        ┌── verify: realization vs spec ──┐
                        ├── verify: API / e2e ────────────┤  (parallel, local)
                        └── verify: security ─────────────┘
                                      │
                                      ▼
                        merge branch → main (local, no PR/CI)
```

### Dispatch discipline (from `Investing-Partner`'s orchestrator contract)

- **Dispatch is synchronous and blocking.** The agent's full output comes back in
  the same call. **Never** run a subagent in the background, **never** "wait" for
  one (no `ScheduleWakeup`, `Monitor`, sleep, or `echo waiting`), and **never** end
  the turn before the stage's deliverable is written to disk.
- A subagent that asks a question instead of doing the work → **re-dispatch once**
  with "you have all the data, execute now, no questions." If it refuses a second
  time, stop the pipeline and report.
- **Parallel agents get disjoint files** (they share one working tree); never let
  two agents install dependencies at once.
- **Every subagent prompt is self-contained** and tells the agent to read the spec
  (and `CLAUDE.md`) first. The orchestrator reviews every result — reads the diff,
  runs build + tests locally — before moving on.

## Verification

The sibling projects run this as CI gates; **we run it locally** as Sonnet agents
before merging a branch into `main`. Three verifiers, run in parallel:

- **Realization vs spec** — does the implementation satisfy the spec, point by
  point? (The 8-layer checklist below.)
- **API / e2e** — brings the stack up from a clean state and drives the real
  endpoints.
- **Security** — authn/authz, token handling, input validation, secrets, threat model.

Principles (adapted from `speech-to-mail`'s `spec-review.md` / `e2e-review.md`):

1. **Verify against the SPEC, not the plan.** A Superpowers plan embeds the target
   code verbatim, so a faithful transcription always matches the plan — even when
   the plan is wrong. Check the diff against the **spec**; where plan and spec
   disagree, **the spec wins and the disagreement is itself a finding**.
   *(Origin: an autonomous run once merged 7 PRs in 20–90 s each, shipping a spec
   violation baked into a plan that plan-conformance checking could never catch.)*
2. **The 8 layers of spec-review:**
   1. Spec/architecture conformance (not plan conformance).
   2. Each acceptance criterion mapped to the **specific test** (by name) that
      guards it — "some test probably covers it" is a finding.
   3. Runtime behaviour the diff doesn't show — degraded/failure paths,
      idempotency across restarts, error-response shapes.
   4. Test hermeticity — no reliance on warm state, ambient env vars, or test order.
   5. Cross-task interface contracts a later step will consume.
   6. **Plan-vs-spec contradiction → escalate, never auto-fix** (owner decides
      whether the spec or the plan changes).
   7. Diff scope vs the change's declared file list.
   8. Security posture (secrets never in the diff) — every stage.
3. **Every finding carries a `scope`**, decided by one mechanical test: *does the
   same failure reproduce on `main`?* → `in-diff` (back to the implementer) /
   `pre-existing-blocking` (escalate to owner) / `pre-existing-nonblocking` (note
   it, don't grow this change). Stops scope-creep.
4. **Verifiers are read-only and never fix** — they reject and describe; fixing is
   the implementer's job, in a separate step.
5. **Fail-closed** — a missing or malformed verdict is a failure, never a pass.
6. **Fresh context per verifier** — it does not trust the implementer's own claims,
   commit messages, or branch description; it checks from the spec as if unseen.
7. **e2e specifics** — bring the stack up from a genuinely clean state; hit the
   **real** endpoints over the real network (not an in-process test client, which
   bypasses startup/lifespan); re-run a **regression pass** of known-good paths
   every time; attach evidence (transcripts, screenshots); write the verdict
   **before** teardown.
8. **Bounded iteration** — at most **2 fix rounds per verifier**; a third
   consecutive block stops the loop and escalates to the owner (you). No silent
   third attempt.
9. **Sensitive changes need owner sign-off before merge** — changes to this
   workflow, to a spec, or to the merge step itself are the owner's call, never an
   agent's. (Local equivalent of the sibling projects' `needs-human` gate.)

## Branch & integration (local, no PR/CI)

- **Never develop directly on `main`.** Work on a feature branch.
- **Integration is a local merge** of the branch into `main` (the "MR") — no GitHub
  pull request, no CI checks, no protected-branch required checks.
- **Before merging to `main`:** build + tests pass locally **and** all verifiers
  return PASS. A merge happens only then; a red build is work, not "done".
- **Commit and merge summaries are brief/concise** (`output-format` brief) — what
  changed and why, in a few lines; no filler, no restated diff.
- **Conventional Commits** — `type(scope): description` (e.g. `feat:`, `fix:`,
  `docs:`, `chore:`). Documentation is written in **English**.

## Artifacts & where they live

- **Brainstorm** — in the conversation only. **Not committed.** Its result lands in
  the spec's `Decisions` section.
- **Spec** — committed under `docs/superpowers/specs/NNNN-<slug>.md`, as the final,
  resolved version (`## Decisions (owner, date)`, plus a `## To verify during
  implementation` section for genuine implementation-time unknowns).
- **Plan** — committed under `docs/superpowers/plans/NNNN-<slug>.md` (Superpowers
  `writing-plans`); residual decisions for the owner live in the plan's
  "Open questions for owner".

## Backlog & tracking (local, no Issues)

- **No GitHub Issues.** The backlog is local: the Superpowers implementation plans
  (`writing-plans`, kept under `docs/superpowers/plans/`) are the task list — work
  them in order.
- **Bugs/follow-ups found during verification** are added to the local plan, not
  left as silent scope creep, and not merged into the current change unless the
  spec calls for it.
- **Verify the effect, not the artifact's existence.** A step that claims to
  "produce X" counts only if it actually changed X versus `HEAD`
  (`git status --porcelain`) — checking that a file merely exists once passed while
  a re-run did no work at all (a real `Investing-Partner` bug).

## Conventions & hygiene

- **Repeatable work → a reusable local script or skill**, not ad-hoc manual steps.
  *(The sibling projects push these to CI; we keep them local by design.)*
- **`CLAUDE.md` stays short** and always-loaded; detailed material lives in child
  documents per topic, opened on demand.
- Keep a **"Pitfalls we already hit"** running log so the same gotcha isn't
  repeated.

## Sources

Conventions above are drawn from and adapted from:

- **`speech-to-mail`** — verifier methodology (`docs/superpowers/spec-review.md`,
  `e2e-review.md`), orchestrator-delegates-implementation pattern, spec-not-plan
  principle. *(We run its gates locally instead of in CI.)*
- **`Investing-Partner`** — dispatch synchronicity contract, per-role model policy
  (`_models.md`), verify-the-effect-not-the-artifact.
- **`stereo-splitter`** — orchestrator + `model: "sonnet"` delegation, disjoint
  files for parallel agents, build/test-before-integration.

**Deliberately not adopted** (they assume infrastructure we don't use):
GitHub-Actions CI gates, GitHub Issues as the backlog, and GitHub pull requests —
replaced by local verification, local plans, and a local merge to `main`.
