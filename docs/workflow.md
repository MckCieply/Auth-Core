# Development workflow

How work gets done in Auth-Core: a spec-driven, agent-orchestrated flow with
verification built in, and hard rules about branches and pull requests.

## Model policy

- **Orchestrator: always Opus.** The session that plans, decomposes work, dispatches
  agents and makes judgement calls runs on Opus.
- **Agents: Sonnets.** Every dispatched subagent — implementer and verifiers — runs
  on Sonnet. Opus decides; Sonnets do.

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

Every stage of work runs the same Superpowers-aligned pipeline. The orchestrator
(Opus) drives; each step is a Sonnet agent unless noted.

1. **Brainstorm** — for each stage, brainstorm the problem and options first
   (Superpowers `brainstorming`). No code, no spec yet — explore the shape of the
   work and the trade-offs.
2. **Spec** — turn the brainstorm into a written spec: what is being built,
   acceptance criteria, contracts, out-of-scope. The spec is the source of truth.
3. **Plan** — from the spec, write an implementation plan the Superpowers way
   (`writing-plans`): ordered, verifiable steps with checkpoints.
4. **Implement** — a Sonnet agent executes the plan (`executing-plans` /
   `subagent-driven-development`). Implementation only; it does not grade itself.
5. **Verify** — verification agents run against the spec. Prefer running all
   verifiers **in parallel** (Superpowers `dispatching-parallel-agents`):
   - **Realization vs spec** — does the implementation actually satisfy the spec,
     point by point? Gaps and deviations are reported, not waved through.
   - **API verifier** — hits the running API (real requests against endpoints) and
     checks behaviour, status codes and the token contract end to end.
   - **Security verifier** — reviews the change for security issues (authn/z,
     token handling, input validation, secrets, the threat model).

   A stage is done only when every verifier passes against the spec
   (Superpowers `verification-before-completion`).

```
brainstorm ──> spec ──> plan ──> implement (Sonnet)
                                      │
                                      ▼
                        ┌── verify: realization vs spec ──┐
                        ├── verify: API (hits endpoints) ──┤  (parallel)
                        └── verify: security ─────────────┘
                                      │
                                      ▼
                                 PR (never to main directly)
```

## Branch and PR rules

- **Never commit directly to `main`.** All work happens on a branch and lands
  through a pull request. `main` is protected with required checks.
- **PR descriptions are brief/concise.** Use the `output-format: brief` /
  `concise` style — what changed and why, in a few lines. No filler, no restated
  diff. (Consistent with the Caveman and i-have-adhd output rules above.)
- A PR merges only when CI is green and the stage's verifiers have passed against
  the spec.
