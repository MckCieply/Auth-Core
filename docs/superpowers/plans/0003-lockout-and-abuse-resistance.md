# Lockout and Abuse Resistance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0003
- **Date:** 2026-01-20
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0003-lockout-and-abuse-resistance.md`](../specs/0003-lockout-and-abuse-resistance.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** `POST /auth/login` counts attempts per submitted email, refuses them
with `429` during an escalating cooldown, locks a fast burst at once, and takes
the same time for an unknown email as for a wrong password.

**Architecture:** A new table holds one row per login identifier (the SHA-256 of
the normalised email): the streak of attempts since the last successful login.
The rules are a pure function (`LockoutPolicy.Register`: state + now → new state +
decision). A small store applies that function inside one short transaction with
the row locked, **before** the password is looked at, so every request gets its
own number in the streak and no database lock is held while a password is
hashed. The login endpoint asks the store first; a refused attempt returns its
own `429` result and never reaches the account lookup. A successful login deletes
the row. On the unknown-email path the endpoint verifies the submitted password
against a decoy hash made at startup. An hourly background job removes rows
without an attempt for 24 hours.

**Tech Stack:** as plans 0001 and 0002 (.NET 10, ASP.NET Core minimal APIs,
ASP.NET Core Identity, OpenIddict 7.7.1, EF Core 10.0.12 + Npgsql 10.0.3,
PostgreSQL 16, xUnit v3, Testcontainers). **No new package.**

## Global Constraints

- Everything in plans 0001 and 0002 → Global Constraints still holds (central
  package versions, `TreatWarningsAsErrors`, no secrets in repo or logs,
  Conventional Commits, local-only workflow).
- Numbers are **constants in code**, not configuration (spec Decision 15):
  threshold **10** attempts, base cooldown **1 minute**, **+1 minute** per further
  attempt, cap **30 minutes**, burst **5 failed attempts in a 10-second fixed
  window**, streak lifetime **24 hours**.
- Lockout response: `429`, body exactly
  `{"error":"too_many_attempts","retry_after_seconds":<n>}`, header
  `Retry-After: <n>`, `Cache-Control: no-store`, `Pragma: no-cache`, no
  `Set-Cookie`. `<n>` = remaining cooldown in whole seconds, rounded up, at least 1.
- The `401 {"error":"invalid_credentials"}` and the `200` of login stay exactly as
  built. The existing `LoginTests`, `LoginRequestTests`, `LoginScopeTests` and
  `LoginCookieTests` must pass **unedited**.
- During a cooldown the endpoint neither looks the account up nor evaluates the
  password (spec Decision 7).
- The key stored is the SHA-256 of the email as `UserManager.NormalizeEmail`
  returns it. The submitted email is never stored and never logged; log lines
  carry counts only.
- Lockout applies to `/auth/login` only. No limit on `/auth/refresh` or
  `/auth/logout`, no per-IP limiting, no `ForwardedHeaders` (spec Decision 6).
- Exactly one new migration. Its id is given by the orchestrator (Task 1).
- Implementers leave their changes **uncommitted** in the working tree. The
  orchestrator reads the diff, runs the gate and commits with the subject named
  in the task's last step.
- Gate for every task: `dotnet build -warnaserror && dotnet test`. Before the
  last commit also `dotnet format --verify-no-changes` and the hermetic run:
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`.

## Verified before this plan was written

A throwaway console app (EF Core 10.0.12, Npgsql 10.0.3, PostgreSQL 16; not in
the repository) settled the spec's "To verify" items that could sink the design:

- **Atomic counting.** One transaction per attempt: a single
  `INSERT … ON CONFLICT DO UPDATE … RETURNING *` through `FromSql` creates the
  row or locks the existing one and returns it; `SaveChanges` then writes the new
  state. Run by 60 parallel callers on a **new** key it gave 60 distinct attempt
  numbers, a stored count of 60 and exactly 5 allowed attempts under a 5-attempt
  rule: no lost update, no duplicate-key failure. 400 calls racing a loop that
  kept deleting the row (what the pruner does) gave no error.
- **Two traps found on the way.** "`INSERT … DO NOTHING`, then `SELECT … FOR
  UPDATE`" loses that race: a delete between the two statements leaves nothing
  to select. And `FromSql` must be materialised with `ToListAsync()`: with
  `SingleAsync()` EF wraps the text in a subquery, which PostgreSQL rejects for a
  data-modifying statement.
- **Decoy cost.** `PasswordHasher.VerifyHashedPassword` against a decoy produced
  by `HashPassword` took the same time as against a real hash (median 47 ms
  each on the development machine).
- **Normalisation.** `UpperInvariantLookupNormalizer.NormalizeEmail` upper-cases
  and returns `null` for `null`.

The code below repeats the pattern of that app. It is still **to be compiled
against the installed packages**: check every EF Core and Identity name before
use and record any that differs in the task report.

## Review Focus

The spec does not name these, but a client or an attacker would hit them. Each
line has a pinning test in the task named in brackets.

1. **Parallel requests:** 12 failed logins sent at the same instant get exactly
   5 password evaluations; the rest are refused. A lost count or a duplicate-key
   error would show as a wrong tally or a `500`. [Task 1, Task 3]
2. **Spelling variants of one email** (`USER@EXAMPLE.COM`, `User@Example.Com`)
   share one streak, exactly as they share one account. [Task 2]
3. **A malformed request is not an attempt:** a `400` neither counts toward the
   threshold nor extends a cooldown. [Task 2]
4. **Hostile emails:** a 7,000-character address, non-ASCII letters, surrounding
   spaces → `401`, then `429` like any other identifier; never a `500`, and the
   stored key stays 32 bytes. [Task 2]
5. **Restart:** a cooldown that is running when the process restarts is still
   running afterwards. [Task 2]

## File Structure

```
src/Auth.Infrastructure/Persistence/
  LoginStreak.cs                                 the row: one login identifier's streak
  AuthDbContext.cs                               + DbSet<LoginStreak>, key and index
  Migrations/<id>_AddLoginStreaks.cs (+ .Designer.cs), AuthDbContextModelSnapshot.cs
src/Auth.Server/
  Program.cs                                     + store, decoy, pruner registrations
  Login/LoginEndpoint.cs                         + gate, decoy verification, clear on success
  Lockout/LockoutPolicy.cs                       constants + the pure transition (StreakState, AttemptDecision)
  Lockout/LoginStreakStore.cs                    count one attempt in one transaction; clear a streak
  Lockout/LoginIdentifier.cs                     normalised email → 32-byte key
  Lockout/TooManyAttemptsResult.cs               the 429
  Lockout/DecoyPasswordHash.cs                   the hash unknown emails are verified against
  Lockout/LockoutPruner.cs                       one pruning pass
  Lockout/LockoutPruningService.cs               hourly BackgroundService around LockoutPruner
tests/Auth.IntegrationTests/
  Infrastructure/AuthAppFactory.cs               + WithServices(Action<IServiceCollection>)
  Infrastructure/LockoutApi.cs                   failed logins at human pace; the 429 contract assertion
  Infrastructure/CountingPasswordHasher.cs       records every hash a password is verified against
  LockoutPolicyTests.cs  LoginStreakStoreTests.cs  LockoutTests.cs
  LockoutVelocityTests.cs  LoginTimingTests.cs  LockoutPruningTests.cs
scripts/e2e-lockout.sh                           real-network lockout sequence + timing medians
docs/superpowers/plans/0003-acceptance-map.md    criteria → tests
README.md                                        quickstart + status
```

Lockout code lives in a new `Lockout/` folder; `Login/` keeps the password grant
and only its endpoint changes. Nothing in `Sessions/` is modified.

Test conventions used below (all exist): `SessionTestBase` gives `Postgres`,
`Keys`, `Clock` (a `FakeTimeProvider`, frozen until a test advances it), `Factory`
and `Client`; `LoginApi.Login` / `LoginApi.LoginOk` post the JSON body;
`LoginApi.HeaderNames` lists header names without `Date`. Every test host has a
database of its own.

---

### Task 1: The streak table and the lockout rules

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/LoginStreak.cs`,
  `src/Auth.Infrastructure/Persistence/Migrations/<id>_AddLoginStreaks.cs` (+ `.Designer.cs`),
  `src/Auth.Server/Lockout/LockoutPolicy.cs`, `src/Auth.Server/Lockout/LoginStreakStore.cs`
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`,
  `src/Auth.Infrastructure/Persistence/Migrations/AuthDbContextModelSnapshot.cs` (generated),
  `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/LockoutPolicyTests.cs`,
  `tests/Auth.IntegrationTests/LoginStreakStoreTests.cs`

**Interfaces:**
- Produces: `LoginStreak` (entity) with `byte[] IdentifierHash` (key), `int AttemptCount`,
  `DateTimeOffset LastAttemptAt`, `DateTimeOffset? LockedUntil`, `DateTimeOffset BurstStartedAt`,
  `int BurstCount`; `AuthDbContext.LoginStreaks`. Table `LoginStreaks`.
- Produces: `readonly record struct StreakState(int AttemptCount, DateTimeOffset LastAttemptAt,
  DateTimeOffset? LockedUntil, DateTimeOffset BurstStartedAt, int BurstCount)` with
  `static StreakState Fresh(DateTimeOffset now)`.
- Produces: `readonly record struct AttemptDecision(bool Allowed, TimeSpan RetryAfter)`.
- Produces: `LockoutPolicy` with `const int Threshold = 10`, `static readonly TimeSpan BaseCooldown`,
  `CooldownStep`, `MaxCooldown`, `StreakLifetime`,
  `static (StreakState Next, AttemptDecision Decision) Register(StreakState current, DateTimeOffset now)`,
  `static TimeSpan CooldownFor(int attemptCount)`.
  **The burst rule is not in this task** (Task 3 adds it); the two burst fields are
  stored and carried unchanged.
- Produces: `LoginStreakStore` (singleton) with
  `Task<AttemptDecision> RegisterAttemptAsync(byte[] identifierHash, CancellationToken cancellationToken)` and
  `Task ClearAsync(byte[] identifierHash, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing rule tests** (`LockoutPolicyTests`, no database).
  Attempts are spaced 3 s apart on purpose: Task 3 adds a rule that locks five
  attempts inside 10 s, and these tests must keep passing then.

```csharp
using Auth.Server.Lockout;

namespace Auth.IntegrationTests;

public sealed class LockoutPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HumanPace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Registers <paramref name="attempts"/> attempts, <paramref name="spacing"/> apart, the first one that long after <paramref name="from"/>.</summary>
    private static (StreakState State, AttemptDecision Last, DateTimeOffset Now) Attempt(
        StreakState state, DateTimeOffset from, int attempts, TimeSpan spacing)
    {
        var now = from;
        AttemptDecision last = default;
        for (var i = 0; i < attempts; i++)
        {
            now += spacing;
            (state, last) = LockoutPolicy.Register(state, now);
        }

        return (state, last, now);
    }

    [Fact]
    public void First_nine_attempts_are_allowed_and_start_no_cooldown()
    {
        var (state, last, _) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        Assert.True(last.Allowed);
        Assert.Equal(9, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Tenth_attempt_is_still_allowed_and_starts_a_one_minute_cooldown()   // criterion 1
    {
        var (state, last, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        Assert.True(last.Allowed);
        Assert.Equal(now + Minute, state.LockedUntil);
    }

    [Fact]
    public void Attempt_during_a_cooldown_is_refused_and_adds_one_minute()   // criterion 3
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        (state, var first) = LockoutPolicy.Register(state, now);
        (state, var second) = LockoutPolicy.Register(state, now + TimeSpan.FromSeconds(30));

        Assert.False(first.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(2), first.RetryAfter);
        Assert.False(second.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(150), second.RetryAfter);   // 3 min from the lock, 30 s already gone
        Assert.Equal(12, state.AttemptCount);
    }

    [Fact]
    public void Remaining_cooldown_never_exceeds_thirty_minutes()   // criterion 3
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);

        AttemptDecision last = default;
        for (var i = 0; i < 50; i++)
        {
            (state, last) = LockoutPolicy.Register(state, now);
            Assert.True(last.RetryAfter <= LockoutPolicy.MaxCooldown, $"Attempt {i}: {last.RetryAfter}");
        }

        Assert.Equal(LockoutPolicy.MaxCooldown, last.RetryAfter);

        // Ten minutes later 20 are left; the attempt adds one.
        (_, var later) = LockoutPolicy.Register(state, now + TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(21), later.RetryAfter);
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(12, 3)]
    [InlineData(39, 30)]
    [InlineData(40, 30)]
    [InlineData(int.MaxValue, 30)]
    public void Cooldown_is_one_minute_at_the_threshold_plus_one_per_attempt_up_to_the_cap(int attemptCount, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LockoutPolicy.CooldownFor(attemptCount));

    [Fact]
    public void Failure_after_an_expired_cooldown_starts_a_longer_one()   // criterion 9
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 10, HumanPace);
        (state, _) = LockoutPolicy.Register(state, now);
        (state, _) = LockoutPolicy.Register(state, now);   // attempts 11 and 12, both refused: locked until now + 3 min

        var expiry = now + TimeSpan.FromMinutes(3);
        (state, var decision) = LockoutPolicy.Register(state, expiry);

        Assert.True(decision.Allowed);                      // a lock ending exactly now has expired
        Assert.Equal(13, state.AttemptCount);
        Assert.Equal(expiry + TimeSpan.FromMinutes(4), state.LockedUntil);
    }

    [Fact]
    public void Streak_is_forgotten_after_24_hours_without_an_attempt()   // criterion 10
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        (state, var decision) = LockoutPolicy.Register(state, now + LockoutPolicy.StreakLifetime);

        Assert.True(decision.Allowed);
        Assert.Equal(1, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Streak_is_kept_just_under_24_hours()   // criterion 10
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 9, HumanPace);

        (state, _) = LockoutPolicy.Register(state, now + LockoutPolicy.StreakLifetime - TimeSpan.FromSeconds(1));

        Assert.Equal(10, state.AttemptCount);
        Assert.NotNull(state.LockedUntil);
    }

    [Fact]
    public void Attempt_count_does_not_overflow()
    {
        var saturated = new StreakState(int.MaxValue, T0, T0 - Minute, T0, 0);

        var (state, decision) = LockoutPolicy.Register(saturated, T0 + HumanPace);

        Assert.True(decision.Allowed);
        Assert.Equal(int.MaxValue, state.AttemptCount);
        Assert.Equal(T0 + HumanPace + LockoutPolicy.MaxCooldown, state.LockedUntil);
    }
}
```

- [ ] **Step 2: Write the failing store tests** (`LoginStreakStoreTests`)

```csharp
using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class LoginStreakStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private LoginStreakStore Store => Factory.Services.GetRequiredService<LoginStreakStore>();

    private static byte[] Key(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private async Task<LoginStreak?> RowAsync(byte[] key)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.LoginStreaks.AsNoTracking().SingleOrDefaultAsync(s => s.IdentifierHash == key, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task First_attempt_creates_the_row_and_is_allowed()
    {
        var key = Key("first");

        var decision = await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
        var row = await RowAsync(key);
        Assert.NotNull(row);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(32, row.IdentifierHash.Length);
    }

    [Fact]
    public async Task Parallel_attempts_on_a_new_identifier_are_each_counted_once()   // Review Focus 1, Decision 14
    {
        var key = Key("race");

        // Twelve, not more: every waiting caller holds a server connection, and the PostgreSQL container's 100 are
        // shared with every test class running in parallel.
        const int Callers = 12;
        var decisions = await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ =>
            Task.Run(() => Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken))));

        // The clock is frozen, so no cooldown expires meanwhile: once the streak locks, nothing else gets through.
        var allowed = decisions.Count(d => d.Allowed);
        Assert.InRange(allowed, 1, LockoutPolicy.Threshold);
        // No attempt is lost. Written so that it also holds once a rule raises the streak to the threshold early
        // (Task 3): reaching the lock after `allowed` attempts skips `Threshold - allowed` numbers.
        Assert.Equal(Callers + LockoutPolicy.Threshold - allowed, (await RowAsync(key))!.AttemptCount);
    }

    [Fact]
    public async Task Stored_instants_read_back_equal()
    {
        var key = Key("precision");
        for (var i = 0; i < LockoutPolicy.Threshold; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(3));
            await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        }

        var refused = await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        // Exactly two minutes: PostgreSQL keeps microseconds, and the store must compute on what the row can hold.
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(2), refused.RetryAfter);
    }

    [Fact]
    public async Task Identifiers_do_not_share_a_streak()
    {
        await Store.RegisterAttemptAsync(Key("one"), TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(Key("one"), TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(Key("two"), TestContext.Current.CancellationToken);

        Assert.Equal(2, (await RowAsync(Key("one")))!.AttemptCount);
        Assert.Equal(1, (await RowAsync(Key("two")))!.AttemptCount);
    }

    [Fact]
    public async Task Clear_removes_the_streak_and_the_next_attempt_is_the_first()
    {
        var key = Key("clear");
        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);

        await Store.ClearAsync(key, TestContext.Current.CancellationToken);
        Assert.Null(await RowAsync(key));

        await Store.RegisterAttemptAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await RowAsync(key))!.AttemptCount);
    }

    [Fact]
    public async Task Clear_of_an_unknown_identifier_does_nothing() =>
        await Store.ClearAsync(Key("never-seen"), TestContext.Current.CancellationToken);
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`Auth.Server.Lockout`, `LoginStreak` and
  `AuthDbContext.LoginStreaks` do not exist).

- [ ] **Step 4: Add the entity and map it.**

`src/Auth.Infrastructure/Persistence/LoginStreak.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The lockout state of one login identifier (spec 0003): its attempts since the last successful login. The key is
/// a hash, never the submitted email, and a row exists for emails without an account too.
/// </summary>
public sealed class LoginStreak
{
    /// <summary>SHA-256 of the normalised email: 32 bytes whatever was submitted.</summary>
    public required byte[] IdentifierHash { get; init; }

    public int AttemptCount { get; set; }

    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>End of the last cooldown started in this streak; <see langword="null"/> when there was none.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset BurstStartedAt { get; set; }

    public int BurstCount { get; set; }
}
```

`AuthDbContext.cs` (the whole file):

```csharp
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<LoginStreak> LoginStreaks => Set<LoginStreak>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.Entity<LoginStreak>(streak =>
        {
            streak.HasKey(s => s.IdentifierHash);
            // Pruning selects by age.
            streak.HasIndex(s => s.LastAttemptAt);
        });
    }
}
```

- [ ] **Step 5: Generate the migration and give it its id.**

```bash
dotnet tool restore
dotnet ef migrations add AddLoginStreaks -p src/Auth.Infrastructure -s src/Auth.Server
```

Expected: two new files in `src/Auth.Infrastructure/Persistence/Migrations/`
(`<stamp>_AddLoginStreaks.cs`, `<stamp>_AddLoginStreaks.Designer.cs`) and a changed
`AuthDbContextModelSnapshot.cs`. `Up` creates the table `LoginStreaks`
(`IdentifierHash bytea` primary key, `AttemptCount integer`, `LastAttemptAt` and
`BurstStartedAt` `timestamp with time zone`, `LockedUntil` nullable,
`BurstCount integer`) and the index `IX_LoginStreaks_LastAttemptAt`; `Down` drops
the table. It must touch **no other table**; if it does, stop and report.

`dotnet ef` stamps the id from the machine's clock. The id kept in the
repository is the one **the orchestrator gives in the task brief**: 14 digits,
`yyyyMMddHHmmss` in UTC, greater than `20260108204217` (the initial migration).
Apply it before anything else is built:

1. Rename both generated files to `<id>_AddLoginStreaks.cs` and
   `<id>_AddLoginStreaks.Designer.cs` (plain `mv`; they are not tracked yet).
2. In the `.Designer.cs` file change the attribute to
   `[Migration("<id>_AddLoginStreaks")]`.
3. Leave `AuthDbContextModelSnapshot.cs` as generated; it holds no id.
4. Check: `git grep --untracked -n "_AddLoginStreaks" -- src` (the files are
   untracked, so plain `git grep` would not see them) prints exactly one line,
   the `[Migration("<id>_AddLoginStreaks")]` attribute with the given id;
   `ls src/Auth.Infrastructure/Persistence/Migrations` shows the two files under
   that id; and
   `dotnet ef migrations has-pending-model-changes -p src/Auth.Infrastructure -s src/Auth.Server`
   reports no pending change.

- [ ] **Step 6: Implement the rules.** `src/Auth.Server/Lockout/LockoutPolicy.cs`:

```csharp
namespace Auth.Server.Lockout;

/// <summary>The lockout state of one identifier, as the rules see it.</summary>
public readonly record struct StreakState(
    int AttemptCount, DateTimeOffset LastAttemptAt, DateTimeOffset? LockedUntil, DateTimeOffset BurstStartedAt, int BurstCount)
{
    /// <summary>An identifier without a streak.</summary>
    public static StreakState Fresh(DateTimeOffset now) => new(0, now, null, now, 0);
}

/// <summary>What to do with one attempt: evaluate its password, or refuse it and say for how long.</summary>
public readonly record struct AttemptDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The lockout rules of spec 0003 as one pure transition. An attempt is counted when it arrives, before its password
/// is looked at (Decision 14); a successful login then removes the state altogether, so a streak is the attempts
/// since the last success.
/// </summary>
public static class LockoutPolicy
{
    /// <summary>The attempt that starts the first cooldown.</summary>
    public const int Threshold = 10;

    public static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CooldownStep = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(30);

    /// <summary>A streak without an attempt for this long is forgotten.</summary>
    public static readonly TimeSpan StreakLifetime = TimeSpan.FromHours(24);

    public static (StreakState Next, AttemptDecision Decision) Register(StreakState current, DateTimeOffset now)
    {
        var state = now - current.LastAttemptAt >= StreakLifetime ? StreakState.Fresh(now) : current;
        var count = state.AttemptCount == int.MaxValue ? int.MaxValue : state.AttemptCount + 1;

        if (state.LockedUntil is { } until && until > now)
        {
            // Refused whatever the password is, and the cooldown grows; the time left never passes the cap.
            var cap = now + MaxCooldown;
            var extended = until + CooldownStep < cap ? until + CooldownStep : cap;
            return (state with { AttemptCount = count, LastAttemptAt = now, LockedUntil = extended },
                new AttemptDecision(false, extended - now));
        }

        // This attempt is evaluated. From the threshold on it also starts a cooldown, which only a correct password
        // (the caller then clears the streak) keeps from applying to the next attempt.
        var lockedUntil = count >= Threshold ? now + CooldownFor(count) : (DateTimeOffset?)null;
        return (state with { AttemptCount = count, LastAttemptAt = now, LockedUntil = lockedUntil },
            new AttemptDecision(true, TimeSpan.Zero));
    }

    /// <summary>The cooldown started by attempt number <paramref name="attemptCount"/> of a streak: min(30, n − 9) minutes.</summary>
    public static TimeSpan CooldownFor(int attemptCount)
    {
        var maxSteps = (int)((MaxCooldown - BaseCooldown) / CooldownStep);
        var steps = Math.Clamp(attemptCount - Threshold, 0, maxSteps);
        return BaseCooldown + (CooldownStep * steps);
    }
}
```

- [ ] **Step 7: Implement the store.** `src/Auth.Server/Lockout/LoginStreakStore.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Lockout;

/// <summary>
/// Persists login streaks. Counting an attempt is one short transaction with the identifier's row locked, so
/// parallel attempts each get their own number in the streak. It uses a scope of its own: the transaction is over
/// before the caller hashes a password, and the request's own DbContext never tracks a streak row.
/// </summary>
public sealed class LoginStreakStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<AttemptDecision> RegisterAttemptAsync(byte[] identifierHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifierHash);

        var now = StorablePrecision(clock.GetUtcNow());
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One statement creates the row or locks the existing one, and returns it either way. The no-op update is
        // what takes the row lock; a parallel attempt, or the pruner's delete, waits on it until this transaction ends.
        // ToListAsync, not SingleAsync: EF must send the statement as it is, not wrapped in a subquery.
        var rows = await db.LoginStreaks
            .FromSql($"""
                INSERT INTO "LoginStreaks" ("IdentifierHash", "AttemptCount", "LastAttemptAt", "LockedUntil", "BurstStartedAt", "BurstCount")
                VALUES ({identifierHash}, 0, {now}, NULL, {now}, 0)
                ON CONFLICT ("IdentifierHash") DO UPDATE SET "AttemptCount" = "LoginStreaks"."AttemptCount"
                RETURNING *
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        var (next, decision) = LockoutPolicy.Register(
            new StreakState(row.AttemptCount, row.LastAttemptAt, row.LockedUntil, row.BurstStartedAt, row.BurstCount), now);
        row.AttemptCount = next.AttemptCount;
        row.LastAttemptAt = next.LastAttemptAt;
        row.LockedUntil = next.LockedUntil;
        row.BurstStartedAt = next.BurstStartedAt;
        row.BurstCount = next.BurstCount;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    /// <summary>Ends the streak of an identifier (a successful login). No row is not an error.</summary>
    public async Task ClearAsync(byte[] identifierHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifierHash);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifierHash).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>PostgreSQL keeps microseconds. Compute on what the row can hold, so a stored instant reads back equal.</summary>
    private static DateTimeOffset StorablePrecision(DateTimeOffset instant) =>
        instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
}
```

`Program.cs`: add `builder.Services.AddSingleton<LoginStreakStore>();` next to the
`TokenPruner` registration, with `using Auth.Server.Lockout;`.

If the statement does not behave as described under "Verified before this plan
was written" (wrong table or column name, a provider error on `RETURNING`), stop
and report instead of switching to read-then-write without a row lock: that
would reintroduce the lost update the store exists to prevent.

- [ ] **Step 8: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS, the 140 existing tests included (every test host applies the
  new migration to a fresh database).
- [ ] **Step 9: Commit** — `feat(lockout): add the login streak table and the lockout rules`

### Task 2: Login refuses attempts during a cooldown

**Files:**
- Create: `src/Auth.Server/Lockout/LoginIdentifier.cs`, `src/Auth.Server/Lockout/TooManyAttemptsResult.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/LockoutApi.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs`
- Test: `tests/Auth.IntegrationTests/LockoutTests.cs`

**Interfaces:**
- Consumes: `LoginStreakStore.RegisterAttemptAsync`, `LoginStreakStore.ClearAsync`, `AttemptDecision` (Task 1).
- Produces: `LoginIdentifier.HashOf(string? normalizedEmail)` → `byte[]` (32 bytes).
- Produces: `TooManyAttemptsResult(TimeSpan retryAfter) : IResult`, `const string Error = "too_many_attempts"`.
- Produces: `LoginEndpoint.HandleAsync(HttpContext http, UserManager<ApplicationUser> users,
  IOptions<TokenOptions> tokens, TimeProvider clock, LoginStreakStore streaks)`.
- Produces (tests): `LockoutApi` with `const string WrongPassword`, `static readonly TimeSpan HumanPace` (3 s),
  `Task FailAsync(HttpClient client, FakeTimeProvider clock, string email, int times)`,
  `Task<int> AssertLockedAsync(HttpResponseMessage response)` (returns `retry_after_seconds`).

- [ ] **Step 1: Write the test helper.** `Infrastructure/LockoutApi.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Helpers for the lockout tests: failed logins at a human pace, and the 429 contract.</summary>
public static class LockoutApi
{
    public const string WrongPassword = "definitely-wrong";

    /// <summary>Wide enough that the burst rule (five failed attempts in 10 s) never fires.</summary>
    public static readonly TimeSpan HumanPace = TimeSpan.FromSeconds(3);

    /// <summary>Sends <paramref name="times"/> failed logins, each 3 s of host clock after the last, and asserts every one gets the ordinary 401.</summary>
    public static async Task FailAsync(HttpClient client, FakeTimeProvider clock, string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            clock.Advance(HumanPace);
            using var response = await LoginApi.Login(client, email, WrongPassword);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Attempt {i + 1}: expected 401, got {(int)response.StatusCode}: {raw}");
        }
    }

    /// <summary>Asserts the whole lockout contract of spec 0003 and returns <c>retry_after_seconds</c>.</summary>
    public static async Task<int> AssertLockedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.TooManyRequests, $"Expected 429, got {(int)response.StatusCode}: {raw}");

        using var body = JsonDocument.Parse(raw);
        var seconds = body.RootElement.GetProperty("retry_after_seconds").GetInt32();
        Assert.Equal($$"""{"error":"too_many_attempts","retry_after_seconds":{{seconds}}}""", raw);
        Assert.True(seconds >= 1, $"retry_after_seconds must be at least 1, got {seconds}.");
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "A refused attempt must not write the cookie.");
        return seconds;
    }
}
```

- [ ] **Step 2: Write the failing tests** (`LockoutTests`). The clock is frozen
  unless a test moves it, so the expected seconds are exact.

```csharp
using System.Net;
using System.Net.Http.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class LockoutTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private const string UnknownEmail = "nobody@example.com";

    private Task<HttpResponseMessage> Wrong(string email) => LoginApi.Login(Client, email, LockoutApi.WrongPassword);

    private Task<HttpResponseMessage> Correct() => LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

    [Fact]
    public async Task Tenth_failure_locks_and_the_correct_password_is_then_refused()   // criterion 1
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        using var refused = await Correct();

        // The 1-minute cooldown, plus the minute this attempt adds.
        Assert.Equal(120, await LockoutApi.AssertLockedAsync(refused));
    }

    [Fact]
    public async Task Lockout_response_carries_the_error_and_the_remaining_cooldown()   // criterion 2
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        Clock.Advance(TimeSpan.FromSeconds(20.5));

        using var refused = await Wrong(Factory.SeedEmail);

        // 39.5 s were left; this attempt adds 60 s; rounded up.
        Assert.Equal(100, await LockoutApi.AssertLockedAsync(refused));
    }

    [Fact]
    public async Task Every_attempt_while_locked_adds_one_minute_whatever_the_password()   // criterion 3, Decision 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        using var wrong = await Wrong(Factory.SeedEmail);
        using var correct = await Correct();
        using var wrongAgain = await Wrong(Factory.SeedEmail);

        int[] retries =
        [
            await LockoutApi.AssertLockedAsync(wrong),
            await LockoutApi.AssertLockedAsync(correct),
            await LockoutApi.AssertLockedAsync(wrongAgain),
        ];
        Assert.Equal([120, 180, 240], retries);
    }

    [Fact]
    public async Task Cooldown_never_exceeds_thirty_minutes()   // criterion 3
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);

        var last = 0;
        for (var i = 0; i < 40; i++)
        {
            using var refused = await (i % 2 == 0 ? Wrong(Factory.SeedEmail) : Correct());
            var seconds = await LockoutApi.AssertLockedAsync(refused);
            Assert.InRange(seconds, last, 1800);
            last = seconds;
        }

        Assert.Equal(1800, last);
    }

    [Fact]
    public async Task Unknown_and_existing_email_get_the_same_lockout_response()   // criteria 4, 8
    {
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var existingFailure = await Wrong(Factory.SeedEmail);
            using var unknownFailure = await Wrong(UnknownEmail);
            Assert.Equal(HttpStatusCode.Unauthorized, existingFailure.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, unknownFailure.StatusCode);
        }

        using var existing = await Wrong(Factory.SeedEmail);
        using var unknown = await Wrong(UnknownEmail);

        await LockoutApi.AssertLockedAsync(existing);
        await LockoutApi.AssertLockedAsync(unknown);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(existing.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
        Assert.Equal(existing.Headers.RetryAfter, unknown.Headers.RetryAfter);
        Assert.Equal(LoginApi.HeaderNames(existing), LoginApi.HeaderNames(unknown));
    }

    [Fact]
    public async Task After_the_cooldown_a_correct_login_succeeds_and_resets_the_streak()   // criterion 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        Clock.Advance(TimeSpan.FromSeconds(61));

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        // Had the streak survived, the first of these would start a cooldown and the second would be refused.
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Successful_login_resets_the_streak_before_any_lock()   // criterion 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);   // the tenth attempt, and it is correct

        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Failure_after_an_expired_cooldown_starts_a_longer_one()   // criterion 9
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);   // cooldown: 1 min
        Clock.Advance(TimeSpan.FromSeconds(61));

        using var eleventh = await Wrong(Factory.SeedEmail);                // evaluated; starts 2 min
        using var twelfth = await Wrong(Factory.SeedEmail);                 // refused; 2 min + 1 min
        Assert.Equal(HttpStatusCode.Unauthorized, eleventh.StatusCode);
        Assert.Equal(180, await LockoutApi.AssertLockedAsync(twelfth));

        Clock.Advance(TimeSpan.FromSeconds(181));
        using var thirteenth = await Wrong(Factory.SeedEmail);              // evaluated; starts 4 min
        using var fourteenth = await Wrong(Factory.SeedEmail);              // refused; 4 min + 1 min
        Assert.Equal(HttpStatusCode.Unauthorized, thirteenth.StatusCode);
        Assert.Equal(300, await LockoutApi.AssertLockedAsync(fourteenth));
    }

    [Fact]
    public async Task Streak_is_forgotten_after_24_hours_without_an_attempt()   // criterion 10
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(TimeSpan.FromHours(24));

        // Nine more: with the old streak still counted, the first would lock and the second would get a 429.
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Spelling_variants_of_one_email_share_a_streak()   // Review Focus 2
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail.ToLowerInvariant(), 5);
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail.ToUpperInvariant(), 5);

        using var refused = await LoginApi.Login(Client, "User@Example.Com", Factory.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
    }

    [Fact]
    public async Task Malformed_request_is_not_an_attempt()   // Review Focus 3
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);

        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var malformed = await Client.PostAsJsonAsync(LoginApi.Path, new { email = Factory.SeedEmail });
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        // Had the 400s counted, the streak would be at 12 and locked.
        Clock.Advance(LockoutApi.HumanPace);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    public static TheoryData<string> HostileEmails() => new()
    {
        new string('a', 7000) + "@example.com",
        "ÜSER@exämple.com",
        "  spaced@example.com  ",
        "emoji-\U0001F600@example.com",
    };

    [Theory]
    [MemberData(nameof(HostileEmails))]
    public async Task Hostile_email_is_counted_and_locked_like_any_other(string email)   // Review Focus 4
    {
        await LockoutApi.FailAsync(Client, Clock, email, 10);

        using var refused = await Wrong(email);

        Assert.Equal(120, await LockoutApi.AssertLockedAsync(refused));

        // Whatever was submitted, what is stored is one row with a 32-byte key.
        using var scope = Factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(32, row.IdentifierHash.Length);
    }

    [Fact]
    public async Task Cooldown_survives_a_host_restart()   // Review Focus 5
    {
        await using (var before = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock))
        {
            using var client = SessionApi.CreateClient(before);
            await LockoutApi.FailAsync(client, Clock, before.SeedEmail, 10);
        }

        await using var after = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock);
        using var restarted = SessionApi.CreateClient(after);
        using var refused = await LoginApi.Login(restarted, after.SeedEmail, after.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet test -- --filter-class "*LockoutTests"`.
  Expected: FAIL — the attempts after the tenth get `401` or `200`, not `429`.
  The two "resets" tests, `Streak_is_forgotten_after_24_hours_without_an_attempt`
  and `Malformed_request_is_not_an_attempt` pass already: they describe behaviour
  that must survive the change.

- [ ] **Step 4: Implement the key and the `429`.**

`src/Auth.Server/Lockout/LoginIdentifier.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Auth.Server.Lockout;

public static class LoginIdentifier
{
    /// <summary>
    /// The lockout key of a submitted email: SHA-256 of the email as <c>UserManager.NormalizeEmail</c> returns it, so
    /// the key follows the account lookup exactly. Always 32 bytes; the email itself is never stored (Decision 10).
    /// </summary>
    public static byte[] HashOf(string? normalizedEmail) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail ?? string.Empty));
}
```

`src/Auth.Server/Lockout/TooManyAttemptsResult.cs`:

```csharp
using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Lockout;

/// <summary>
/// The lockout response: <c>429</c> with the remaining cooldown in the body and in <c>Retry-After</c>. The same for
/// every identifier, with or without an account, and with the cache headers of the login <c>401</c>.
/// </summary>
public sealed class TooManyAttemptsResult(TimeSpan retryAfter) : IResult
{
    public const string Error = "too_many_attempts";

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
        httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
        httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new { error = Error, retry_after_seconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(httpContext);
    }
}
```

- [ ] **Step 5: Put the gate into the endpoint.** In `LoginEndpoint.HandleAsync`
  add the parameter `LoginStreakStore streaks` (and its null check, and
  `using Auth.Server.Lockout;`), then replace the block from the
  `// Deliberately no lockout…` comment through the `if` that returns
  `InvalidCredentialsResult` (its closing brace included) with:

```csharp
        // Count the attempt before anything about it is known (spec 0003, Decision 14). While a cooldown runs
        // there is no lookup and no password check: the response must not depend on either (Decision 7).
        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(request.Username));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        // Timing equalisation for unknown emails is the next step of spec 0003 (decoy hash).
        var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
        if (user is null || !await users.CheckPasswordAsync(user, request.Password ?? string.Empty))
        {
            return new InvalidCredentialsResult();
        }

        // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client
        // that goes away right after its correct password was verified must not be left with it.
        await streaks.ClearAsync(identifier, CancellationToken.None);
```

  Update the class summary: it now also says that an attempt is counted first and
  refused with `429` during a cooldown. `Program.cs` needs no change: minimal APIs
  resolve the store from the services.

- [ ] **Step 6: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS. If a pre-existing test now gets a `429`, do not edit it:
  report which one, with the number of failed logins it sends per identifier.
- [ ] **Step 7: Probe one input and report it (do not fix it here).** In a scratch
  test that is not kept, post a login whose email contains U+FFFE
  (`"a￾b@example.com"`). The email normaliser may throw on it, which would
  be a `500` that exists before this slice as well. Put the status you saw in the
  task report; the owner decides where it gets fixed.
- [ ] **Step 8: Commit** — `feat(lockout): refuse login attempts during a cooldown`

### Task 3: Velocity rule

**Files:**
- Modify: `src/Auth.Server/Lockout/LockoutPolicy.cs`
- Test: `tests/Auth.IntegrationTests/LockoutPolicyTests.cs` (add), `tests/Auth.IntegrationTests/LockoutVelocityTests.cs`

**Interfaces:**
- Consumes: `LockoutPolicy.Register`, `StreakState` (Task 1); `LockoutApi` (Task 2).
- Produces: `LockoutPolicy.BurstLimit` (`const int`, 5), `LockoutPolicy.BurstWindow` (`TimeSpan`, 10 s);
  `Register` now applies the burst rule. No signature changes.
- The rule (spec Decision 9): a window opens at an evaluated attempt that falls
  outside the open window and lasts 10 s; the fifth evaluated attempt inside one
  window raises the streak to the threshold, which starts the base cooldown. A
  refused attempt does not touch the window.

- [ ] **Step 1: Add the failing rule tests** to `LockoutPolicyTests`:

```csharp
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);

    [Fact]
    public void Fifth_attempt_inside_ten_seconds_starts_the_cooldown_at_once()   // criterion 5
    {
        var (state, last, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);

        Assert.True(last.Allowed);                                  // the fifth is still evaluated
        Assert.Equal(LockoutPolicy.Threshold, state.AttemptCount);  // raised to the threshold
        Assert.Equal(now + Minute, state.LockedUntil);

        (_, var sixth) = LockoutPolicy.Register(state, now + Fast);
        Assert.False(sixth.Allowed);
    }

    [Fact]
    public void Four_fast_attempts_do_not_lock()
    {
        var (state, _, _) = Attempt(StreakState.Fresh(T0), T0, 4, Fast);

        Assert.Equal(4, state.AttemptCount);
        Assert.Null(state.LockedUntil);
    }

    [Fact]
    public void Window_runs_ten_seconds_from_its_first_attempt()   // criterion 5
    {
        var (four, _, first) = Attempt(StreakState.Fresh(T0), T0 - Fast, 1, Fast);   // one attempt, at T0
        (four, _, _) = Attempt(four, first, 3, TimeSpan.Zero);                       // three more at T0

        var (inside, _) = LockoutPolicy.Register(four, T0 + TimeSpan.FromSeconds(9.999));
        var (outside, _) = LockoutPolicy.Register(four, T0 + LockoutPolicy.BurstWindow);

        Assert.NotNull(inside.LockedUntil);
        Assert.Null(outside.LockedUntil);
        Assert.Equal(5, outside.AttemptCount);
        Assert.Equal(1, outside.BurstCount);                        // a new window, opened by this attempt
    }

    [Fact]
    public void After_a_burst_lock_the_escalation_continues_from_the_threshold()   // Decision 9
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);

        var expiry = now + Minute;
        (state, var decision) = LockoutPolicy.Register(state, expiry);

        Assert.True(decision.Allowed);
        Assert.Equal(11, state.AttemptCount);
        Assert.Equal(expiry + TimeSpan.FromMinutes(2), state.LockedUntil);
    }

    [Fact]
    public void Refused_attempts_do_not_touch_the_window()
    {
        var (state, _, now) = Attempt(StreakState.Fresh(T0), T0, 5, Fast);
        var burst = (state.BurstStartedAt, state.BurstCount);

        (state, _) = LockoutPolicy.Register(state, now + Fast);

        Assert.Equal(burst, (state.BurstStartedAt, state.BurstCount));
    }
```

- [ ] **Step 2: Write the failing HTTP tests** (`LockoutVelocityTests`). No test
  here moves the clock unless it says so: attempts sent back to back are a burst.

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class LockoutVelocityTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task FailFastAsync(string email, int times)
    {
        for (var i = 0; i < times; i++)
        {
            using var response = await LoginApi.Login(Client, email, LockoutApi.WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Five_failures_within_ten_seconds_lock_the_sixth_attempt()   // criterion 5
    {
        await FailFastAsync(Factory.SeedEmail, 5);

        using var sixth = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(120, await LockoutApi.AssertLockedAsync(sixth));   // base 1 min + this attempt's 1 min
    }

    [Fact]
    public async Task Five_failures_at_a_human_pace_do_not_lock()   // criterion 5
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 5);
        Clock.Advance(LockoutApi.HumanPace);

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Fifth_attempt_of_a_burst_with_the_correct_password_logs_in()
    {
        await FailFastAsync(Factory.SeedEmail, 4);

        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Successful_login_closes_the_window()   // Decision 9
    {
        await FailFastAsync(Factory.SeedEmail, 4);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        // Had the window survived the success, the first of these would be its sixth attempt.
        await FailFastAsync(Factory.SeedEmail, 4);
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Closed_window_starts_again()
    {
        await FailFastAsync(Factory.SeedEmail, 4);
        Clock.Advance(TimeSpan.FromSeconds(10));
        await FailFastAsync(Factory.SeedEmail, 4);   // eight failures, never five in one window

        Clock.Advance(TimeSpan.FromSeconds(10));
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
    }

    [Fact]
    public async Task Repeated_successful_logins_never_trip_the_rule()   // Decision 9
    {
        for (var i = 0; i < 8; i++)
        {
            await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);
        }
    }

    [Fact]
    public async Task Burst_on_an_unknown_email_locks_the_same_way()   // criteria 4, 8
    {
        await FailFastAsync(Factory.SeedEmail, 5);
        await FailFastAsync("nobody@example.com", 5);

        using var existing = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);
        using var unknown = await LoginApi.Login(Client, "nobody@example.com", LockoutApi.WrongPassword);

        await LockoutApi.AssertLockedAsync(existing);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(existing), LoginApi.HeaderNames(unknown));
    }

    [Fact]
    public async Task Parallel_failures_buy_at_most_five_password_evaluations()   // Review Focus 1, Decision 14
    {
        // Twelve, not more: see LoginStreakStoreTests on the connections a parallel test holds.
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword))));

        try
        {
            // A 401 is the only outcome of an evaluated wrong password; everything else must be the lockout response.
            Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run
  `dotnet test -- --filter-class "*LockoutPolicyTests"` and
  `dotnet test -- --filter-class "*LockoutVelocityTests"`.
  Expected: `LockoutPolicy.BurstLimit` / `BurstWindow` do not exist (compile
  error). After adding the two constants only, these fail: the two "fifth attempt"
  tests, the window-edge test, the escalation-after-a-burst test, the
  unknown-email burst test and the parallel test (10 evaluated instead of 5). The
  rest pass already — four fast attempts, refused attempts leaving the window
  alone, human pace, the correct fifth attempt, the success that closes the
  window, the closed window and repeated successes: behaviour that must survive
  the change.

- [ ] **Step 4: Implement.** In `LockoutPolicy` add the constants

```csharp
    /// <summary>This many evaluated attempts inside one <see cref="BurstWindow"/> start the cooldown at once.</summary>
    public const int BurstLimit = 5;

    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
```

  and replace the tail of `Register` (from the `// This attempt is evaluated.`
  comment to the end of the method) with:

```csharp
        // This attempt is evaluated. A fixed 10-second window opens at an attempt that falls outside the open one;
        // the fifth attempt inside a window is not a person typing, and raises the streak to the threshold.
        var burstOpen = state.BurstCount > 0 && now - state.BurstStartedAt < BurstWindow;
        var burstStartedAt = burstOpen ? state.BurstStartedAt : now;
        var burstCount = burstOpen ? state.BurstCount + 1 : 1;
        if (burstCount >= BurstLimit)
        {
            count = Math.Max(count, Threshold);
        }

        // From the threshold on the attempt also starts a cooldown, which only a correct password (the caller then
        // clears the streak) keeps from applying to the next attempt.
        var lockedUntil = count >= Threshold ? now + CooldownFor(count) : (DateTimeOffset?)null;
        return (new StreakState(count, now, lockedUntil, burstStartedAt, burstCount), new AttemptDecision(true, TimeSpan.Zero));
```

  Update the class summary to name the burst rule.

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS, Task 1's and Task 2's tests unedited: their attempts are 3 s
  apart, and the one parallel store test is written to hold under both rule sets
  (12 callers now give 5 allowed and a stored count of 17). If one of them now
  locks early, the spacing is wrong, not the rule: report it.
- [ ] **Step 6: Commit** — `feat(lockout): lock a burst of five failed attempts at once`

### Task 4: Timing equalisation

**Files:**
- Create: `src/Auth.Server/Lockout/DecoyPasswordHash.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/CountingPasswordHasher.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs`, `src/Auth.Server/Program.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/LoginTimingTests.cs`

**Interfaces:**
- Consumes: the gate of Task 2; `LockoutApi`.
- Produces: `DecoyPasswordHash` (singleton) with `string Value` — a hash of a random
  password made once by the configured `IPasswordHasher<ApplicationUser>`.
- Produces: `LoginEndpoint.HandleAsync(…, LoginStreakStore streaks, DecoyPasswordHash decoy)`.
- Produces (tests): `AuthAppFactory WithServices(Action<IServiceCollection> configure)`;
  `CountingPasswordHasher : PasswordHasher<ApplicationUser>` with
  `IReadOnlyCollection<string> VerifiedHashes`.
- Why a test of calls and not of milliseconds (spec Decision 12): the medians are
  measured over the real network by `scripts/e2e-lockout.sh` (Task 6).

- [ ] **Step 1: Write the test infrastructure.**

`AuthAppFactory` (add; keep everything else):

```csharp
private readonly List<Action<IServiceCollection>> _serviceOverrides = [];

/// <summary>Changes the host's service registrations after the app has made its own.</summary>
public AuthAppFactory WithServices(Action<IServiceCollection> configure)
{
    _serviceOverrides.Add(configure);
    return this;
}

// at the end of ConfigureWebHost:
foreach (var configure in _serviceOverrides)
{
    builder.ConfigureTestServices(configure);
}
```

`Infrastructure/CountingPasswordHasher.cs`:

```csharp
using System.Collections.Concurrent;
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>The real password hasher, recording the stored hash of every verification it runs.</summary>
public sealed class CountingPasswordHasher : PasswordHasher<ApplicationUser>
{
    private readonly ConcurrentQueue<string> _verifiedHashes = new();

    public IReadOnlyCollection<string> VerifiedHashes => _verifiedHashes;

    public override PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        _verifiedHashes.Enqueue(hashedPassword);
        return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
```

- [ ] **Step 2: Write the failing tests** (`LoginTimingTests`)

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class LoginTimingTests : SessionTestBase
{
    private readonly CountingPasswordHasher _hasher = new();

    public LoginTimingTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<ApplicationUser>>(_hasher)));
    }

    private string Decoy => Factory.Services.GetRequiredService<DecoyPasswordHash>().Value;

    private async Task<string> StoredHashAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByEmailAsync(email))!.PasswordHash!;
    }

    [Fact]
    public async Task Unknown_email_runs_exactly_one_verification_against_the_decoy()   // criterion 6
    {
        using var response = await LoginApi.Login(Client, "nobody@example.com", LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Wrong_password_runs_exactly_one_verification_against_the_stored_hash()   // criterion 6
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([await StoredHashAsync(Factory.SeedEmail)], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Account_without_a_password_runs_the_decoy_too()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(new ApplicationUser { UserName = "invited@example.com", Email = "invited@example.com" });
            Assert.True(created.Succeeded);
        }

        using var response = await LoginApi.Login(Client, "invited@example.com", LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal([Decoy], _hasher.VerifiedHashes.ToArray());
    }

    [Fact]
    public async Task Locked_attempt_runs_no_verification_at_all()   // Decision 7
    {
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 10);
        var before = _hasher.VerifiedHashes.Count;

        using var refused = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        await LockoutApi.AssertLockedAsync(refused);
        Assert.Equal(10, before);
        Assert.Equal(before, _hasher.VerifiedHashes.Count);
    }

    [Fact]
    public async Task Decoy_has_the_cost_parameters_of_a_real_hash()   // Decision 11
    {
        var stored = Convert.FromBase64String(await StoredHashAsync(Factory.SeedEmail));
        var decoy = Convert.FromBase64String(Decoy);

        // Identity's hash starts with a 13-byte header: format marker, PRF, iteration count, salt size.
        Assert.Equal(stored[..13], decoy[..13]);
        Assert.Equal(stored.Length, decoy.Length);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`DecoyPasswordHash` does not exist). With an empty
  `DecoyPasswordHash` registered: the unknown-email and no-password tests fail
  with **zero** verifications recorded — the timing oracle of slice 1, as a test.

- [ ] **Step 4: Implement.** `src/Auth.Server/Lockout/DecoyPasswordHash.cs`:

```csharp
using System.Security.Cryptography;
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Lockout;

/// <summary>
/// The hash a submitted password is verified against when there is no account, or no password, to verify it
/// against, so that such a login costs what a wrong password costs (spec 0003, Decision 5). It is made once, by
/// the configured hasher, so its cost parameters are the ones real hashes get (Decision 11). The password behind
/// it is random and discarded: no input verifies.
/// </summary>
public sealed class DecoyPasswordHash
{
    public DecoyPasswordHash(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // The hasher is registered as a scoped service; this singleton must not hold on to it.
        using var scope = scopes.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();
        Value = hasher.HashPassword(new ApplicationUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Value { get; }
}
```

`Program.cs`: register `builder.Services.AddSingleton<DecoyPasswordHash>();` and,
right after `var app = builder.Build();`, create it so the first login does not
pay for it:

```csharp
// Made now, not by the first login with an unknown email, which would then take twice as long as the next one.
_ = app.Services.GetRequiredService<DecoyPasswordHash>();
```

`LoginEndpoint.HandleAsync`: add the parameter `DecoyPasswordHash decoy` (and its
null check) and replace the lookup block of Task 2 (from the
`// Timing equalisation…` comment through the `if` that returns
`InvalidCredentialsResult`; the `ClearAsync` call after it stays) with:

```csharp
        var password = request.Password ?? string.Empty;
        var user = await users.FindByEmailAsync(request.Username ?? string.Empty);
        if (user?.PasswordHash is null)
        {
            // No account, or one without a password: still pay for one verification, so this answer takes as long
            // as a wrong password does.
            _ = users.PasswordHasher.VerifyHashedPassword(user ?? new ApplicationUser(), decoy.Value, password);
            return new InvalidCredentialsResult();
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            return new InvalidCredentialsResult();
        }
```

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS.
- [ ] **Step 6: Commit** — `feat(lockout): verify unknown emails against a decoy hash`

### Task 5: Pruning

**Files:**
- Create: `src/Auth.Server/Lockout/LockoutPruner.cs`, `src/Auth.Server/Lockout/LockoutPruningService.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/LockoutPruningTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext.LoginStreaks`, `LockoutPolicy.StreakLifetime`, `TimeProvider`.
- Produces: `LockoutPruner` (singleton) with `Task<int> PruneOnceAsync(CancellationToken cancellationToken)` —
  deletes the rows whose `LastAttemptAt` is at or before `now − StreakLifetime`
  (the same boundary `LockoutPolicy.Register` uses to forget a streak).
- Produces: `LockoutPruningService : BackgroundService` with
  `static readonly TimeSpan Interval = TimeSpan.FromHours(1)` — one pass at host
  start, then one per interval; a failed pass is logged and the loop continues;
  the loop ends normally when the host stops. Same shape as
  `Sessions/TokenPruningService.cs` **as built**.
- A row that old can hold no running cooldown: a cooldown ends at most 30 minutes
  after the last attempt.
- A pass may delete a row at the moment an attempt arrives for it. The store's
  single upsert statement (Task 1) makes that safe; one test here pins it.

- [ ] **Step 1: Write the failing tests** (`LockoutPruningTests`)

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auth.IntegrationTests;

public sealed class LockoutPruningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    // The host's own pruning loop waits on the fake clock, and Clock.Advance fires its timer: a background pass
    // would race the explicit pass of a test. Stop the loop before the clock moves.
    private async Task StopBackgroundPruningAsync()
    {
        var service = Factory.Services.GetServices<IHostedService>().OfType<LockoutPruningService>().Single();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> PruneAsync() =>
        await Factory.Services.GetRequiredService<LockoutPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    private async Task<int> RowCountAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pruning_removes_streaks_without_an_attempt_for_24_hours()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "ghost-1@example.com", 2);
        await LockoutApi.FailAsync(Client, Clock, "ghost-2@example.com", 10);   // locked, then long expired

        Clock.Advance(LockoutPolicy.StreakLifetime);

        Assert.Equal(2, await PruneAsync());
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task Pruning_keeps_a_younger_streak_and_it_still_counts()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 9);

        Clock.Advance(TimeSpan.FromHours(23));

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(1, await RowCountAsync());

        await LockoutApi.FailAsync(Client, Clock, Factory.SeedEmail, 1);   // the tenth: starts the cooldown
        using var refused = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
        await LockoutApi.AssertLockedAsync(refused);
    }

    [Fact]
    public async Task Pruning_removes_only_the_old_rows()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "old@example.com", 1);
        Clock.Advance(LockoutPolicy.StreakLifetime - TimeSpan.FromMinutes(1));
        await LockoutApi.FailAsync(Client, Clock, "young@example.com", 1);
        Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await PruneAsync());
        Assert.Equal(1, await RowCountAsync());
    }

    [Fact]
    public async Task Attempt_racing_a_pruning_pass_is_still_answered()
    {
        await StopBackgroundPruningAsync();
        await LockoutApi.FailAsync(Client, Clock, "ghost@example.com", 1);
        Clock.Advance(LockoutPolicy.StreakLifetime);

        // The pass deletes the row these attempts are about to count on. None of them may fail for it.
        var pass = Task.Run(PruneAsync);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => LoginApi.Login(Client, "ghost@example.com", LockoutApi.WrongPassword))));
        await pass;

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Successful_logins_leave_nothing_to_prune()
    {
        await StopBackgroundPruningAsync();
        await LoginApi.LoginOk(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(0, await RowCountAsync());
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`LockoutPruner`, `LockoutPruningService` do not exist).
- [ ] **Step 3: Implement.**

`src/Auth.Server/Lockout/LockoutPruner.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Lockout;

/// <summary>
/// One pruning pass over the login streaks: rows without an attempt for <see cref="LockoutPolicy.StreakLifetime"/>,
/// which the rules already treat as absent. Anyone can create a row by submitting an email, so rows must expire.
/// </summary>
public sealed partial class LockoutPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LockoutPruner> logger)
{
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - LockoutPolicy.StreakLifetime;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var removed = await db.LoginStreaks.Where(s => s.LastAttemptAt <= threshold).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, removed);
        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Streaks} login streak entries.")]
    private static partial void LogPruned(ILogger logger, int streaks);
}
```

`src/Auth.Server/Lockout/LockoutPruningService.cs`:

```csharp
namespace Auth.Server.Lockout;

/// <summary>Runs <see cref="LockoutPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class LockoutPruningService(LockoutPruner pruner, TimeProvider clock, ILogger<LockoutPruningService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                await PruneAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: end the loop normally rather than as a cancelled task, which the host would count as a failure.
        }
    }

    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            await pruner.PruneOnceAsync(stoppingToken);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped in the middle of a pass. Whatever the store made of the cancellation, it is not a failure.
        }
        catch (Exception exception)
        {
            // Pruning is housekeeping: a failed pass must not take the auth service down. Try again next time.
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Login streak pruning failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
```

`Program.cs`: `builder.Services.AddSingleton<LockoutPruner>();` and
`builder.Services.AddHostedService<LockoutPruningService>();`, next to the token
pruning registrations.

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS. `TokenPruningTests` must stay green unedited: it stops only
  the token pruning loop, and the lockout loop deletes no token rows.
- [ ] **Step 5: Commit** — `feat(lockout): prune login streaks idle for 24 hours`

### Task 6: Real-network e2e, docs and acceptance map

**Files:**
- Create: `scripts/e2e-lockout.sh`, `docs/superpowers/plans/0003-acceptance-map.md`
- Modify: `README.md` (quickstart: add `scripts/e2e-lockout.sh`; status line)

**Interfaces:**
- Consumes: the compose stack and `.env` of slice 1 (unchanged).
- Produces: `scripts/e2e-lockout.sh` — same conventions as `scripts/e2e-refresh.sh`
  (`set -euo pipefail`, `BASE_URL`, `env_get`, `wait_healthy`, `fail` / `pass`,
  credentials never on a command line, a `mktemp -d` directory removed by an
  `EXIT` trap, exit non-zero on the first failure, `ALL PASS` at the end, does not
  bring the stack up or down). It **never prints a password, a token or a cookie**;
  it does print the two medians. Header comment: what it checks, the full
  sequence, that it takes about 3 minutes because it waits out a real cooldown,
  that the threshold at human pace, the escalation and the 24-hour reset are
  covered by integration tests with a controlled clock, and that a run which
  dies between steps 4 and 6 leaves the seed email locked for up to 30 minutes
  (the other e2e scripts then fail at their first login; `down -v` clears it). It
  must be re-runnable on the same stack: every unknown address is made new per
  run. LF line endings; set the
  executable bit in the index: `git update-index --chmod=+x scripts/e2e-lockout.sh`
  (after `git add`; on Windows the working tree does not show it).

  Helpers beyond the ones copied from `e2e-refresh.sh`:

```bash
# make_body <email-env> <password-env> <file>: JSON login body from the given env var names, written to a file
# under $tmp, so that no secret is on a command line and no interpreter starts between the requests of a burst.
make_body() {
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]], "password": os.environ[sys.argv[2]]}))' "$1" "$2" > "$3"
}

# login <body-file>  ->  sets HTTP_CODE, BODY, TIME_S (curl's time_total, seconds); response headers in $tmp/hdr
login() {
  local out
  out="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code} %{time_total}' \
    -X POST "$BASE_URL/auth/login" -H 'Content-Type: application/json' --data-binary "@$1")"
  HTTP_CODE="${out%% *}"
  TIME_S="${out##* }"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

header_names() { # sorted header names of the last response, Date excluded
  tail -n +2 "$tmp/hdr" | tr -d '\r' | grep ':' | cut -d: -f1 | tr '[:upper:]' '[:lower:]' | grep -vx 'date' | sort | tr '\n' ' '
}

# expect_locked <what>  ->  asserts the lockout contract on the last response; sets RETRY
expect_locked() {
  [[ "$HTTP_CODE" == "429" ]] || fail "$1: HTTP $HTTP_CODE, expected 429"
  [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$1: unexpected 429 body"
  RETRY="${BASH_REMATCH[1]}"
  [[ "$(header retry-after)" == "$RETRY" ]] || fail "$1: Retry-After differs from retry_after_seconds"
  [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"
  [[ -z "$(header set-cookie)" ]] || fail "$1: a refused attempt set a cookie"
}

median_ms() { python3 -c 'import statistics,sys; print(round(statistics.median(map(float, sys.argv[1:])) * 1000))' "$@"; }
```

  Every body a burst needs is built with `make_body` **before** the burst starts:
  the five requests must reach the server within 10 seconds, and `python3` may be
  slow to start.

  Steps:
  1. health; login with the seed credentials → `200` (also proves the seed
     identifier starts without a streak).
  2. **Timing (criterion 6), before any lock.** 15 samples per path, alternating:
     an unknown address that is new for every sample
     (`nobody-$RANDOM$RANDOM-$i@example.invalid`) with a wrong password → `401`,
     keep `TIME_S`; the seed email with a wrong password → `401`, keep `TIME_S`;
     then the seed email with the right password → `200` (it ends the streak, so
     the failed samples never add up to a burst or to the threshold). Print both
     medians with `median_ms`; fail unless `0.5 <= unknown / wrong <= 2.0` and both
     are above 0.
  3. **Burst on an unknown email.** One unknown address made for this run
     (`nobody-burst-$RANDOM$RANDOM@example.invalid`, reused for the six requests;
     a constant would still be locked on the next run), wrong password, five
     times back to back → `401` each with the body `{"error":"invalid_credentials"}`;
     the sixth → `expect_locked`; `RETRY` is between 61 and 120. Keep
     `header_names` → `UNKNOWN_HEADERS`.
  4. **Burst on the seed email.** Wrong password five times → `401` each; then the
     **right** password → `expect_locked` (criterion 1: a correct password is
     refused); `header_names` equals `UNKNOWN_HEADERS`, and the body differs from
     step 3's only in the number (criteria 4 and 8).
  5. **Expiry.** `sleep $((RETRY + 1))` with the `RETRY` of step 4; the right
     password → `200` with an `auth_rt` `Set-Cookie` (criterion 7).
  6. **Reset.** Wrong password four times back to back → `401` each; right
     password → `200` (the streak of step 4 is gone, and the account is left
     without a streak for whoever runs next).
- Produces: `0003-acceptance-map.md`, same layout as `0002-acceptance-map.md`:

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Ten failures lock; a correct password is then refused | `LockoutTests.Tenth_failure_locks_and_the_correct_password_is_then_refused`, `LockoutPolicyTests.Tenth_attempt_is_still_allowed_and_starts_a_one_minute_cooldown`; e2e step 4 |
| 2 | `429` with the error and the remaining cooldown | `LockoutTests.Lockout_response_carries_the_error_and_the_remaining_cooldown` (and `LockoutApi.AssertLockedAsync` wherever a lock is asserted); e2e steps 3–4 |
| 3 | Every attempt while locked adds one minute, cap 30 | `LockoutTests.Every_attempt_while_locked_adds_one_minute_whatever_the_password`, `LockoutTests.Cooldown_never_exceeds_thirty_minutes`, `LockoutPolicyTests.Attempt_during_a_cooldown_is_refused_and_adds_one_minute`, `LockoutPolicyTests.Remaining_cooldown_never_exceeds_thirty_minutes` |
| 4 | Unknown and existing email: same lockout response, same timing | `LockoutTests.Unknown_and_existing_email_get_the_same_lockout_response`, `LockoutVelocityTests.Burst_on_an_unknown_email_locks_the_same_way`, `LoginTimingTests.Locked_attempt_runs_no_verification_at_all`; e2e steps 3–4 |
| 5 | Five failures in 10 s lock the sixth | `LockoutVelocityTests.Five_failures_within_ten_seconds_lock_the_sixth_attempt`, `LockoutVelocityTests.Five_failures_at_a_human_pace_do_not_lock`, `LockoutPolicyTests.Window_runs_ten_seconds_from_its_first_attempt`; e2e steps 3–4 |
| 6 | Timing: one decoy verification; medians within 0.5–2× | `LoginTimingTests.Unknown_email_runs_exactly_one_verification_against_the_decoy`, `LoginTimingTests.Wrong_password_runs_exactly_one_verification_against_the_stored_hash`, `LoginTimingTests.Decoy_has_the_cost_parameters_of_a_real_hash`; e2e step 2 |
| 7 | Correct login after the cooldown succeeds and resets; success resets mid-streak | `LockoutTests.After_the_cooldown_a_correct_login_succeeds_and_resets_the_streak`, `LockoutTests.Successful_login_resets_the_streak_before_any_lock`, `LockoutVelocityTests.Successful_login_closes_the_window`; e2e steps 5–6 |
| 8 | No response reveals whether the email has an account | the tests of criterion 4, `LoginTimingTests.Account_without_a_password_runs_the_decoy_too`, and spec 0001's `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable` (unedited) |
| 9 | Escalation carries over from one cooldown to the next | `LockoutTests.Failure_after_an_expired_cooldown_starts_a_longer_one`, `LockoutPolicyTests.Failure_after_an_expired_cooldown_starts_a_longer_one`, `LockoutPolicyTests.Cooldown_is_one_minute_at_the_threshold_plus_one_per_attempt_up_to_the_cap` (integration only) |
| 10 | Streak forgotten after 24 hours | `LockoutTests.Streak_is_forgotten_after_24_hours_without_an_attempt`, `LockoutPolicyTests.Streak_is_forgotten_after_24_hours_without_an_attempt`, `LockoutPolicyTests.Streak_is_kept_just_under_24_hours`, `LockoutPruningTests.Pruning_removes_streaks_without_an_attempt_for_24_hours` (integration only) |

  plus a Review Focus table (the five lines above → their tests:
  1 `LoginStreakStoreTests.Parallel_attempts_on_a_new_identifier_are_each_counted_once`,
  `LockoutVelocityTests.Parallel_failures_buy_at_most_five_password_evaluations`;
  2 `LockoutTests.Spelling_variants_of_one_email_share_a_streak`;
  3 `LockoutTests.Malformed_request_is_not_an_attempt`;
  4 `LockoutTests.Hostile_email_is_counted_and_locked_like_any_other`;
  5 `LockoutTests.Cooldown_survives_a_host_restart`), a "Plan-vs-implementation
  notes" section (every name or behaviour that differed from this plan, per task)
  and an empty "Local verification log" the orchestrator fills after verification.
- README: in the quickstart block add
  `scripts/e2e-lockout.sh          # lockout → cooldown → timing medians (~3 min)`
  after the `e2e-refresh.sh` line; extend the status note with one sentence:
  login is protected by a per-identifier lockout
  ([spec 0003](docs/superpowers/specs/0003-lockout-and-abuse-resistance.md)).

- [ ] **Step 1: Write** the script, the acceptance map and the README change. Keep every
  test name in the map in sync with the code (`grep` each one).
- [ ] **Step 2: Run the e2e on a clean stack.** `cp .env.example .env` (set values) →
  `scripts/dev-keys.sh` → `docker compose -f deploy/docker-compose.yml --env-file .env down -v` →
  `… up -d --build` → `scripts/e2e-login.sh` → `scripts/e2e-refresh.sh` →
  `scripts/e2e-lockout.sh` → `… down -v`. Expected: all three end with `ALL PASS`
  (the first two unedited: neither sends five failed logins for one identifier).
  Then remove `.env` and `.secrets/`. Record the two medians in the acceptance
  map's notes.
- [ ] **Step 3: Run the final local gate.**
  `dotnet format --verify-no-changes && dotnet build -warnaserror && dotnet test`, then
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`, then
  `git grep -nE "PRIVATE KEY|Password=" -- ':!*.md' ':!.env.example'` (only the two
  known hits: the `${POSTGRES_PASSWORD…}` placeholder in `deploy/docker-compose.yml`
  and an assertion string in `KeyMaterialTests.cs`) and
  `git status --porcelain` (no `.env`, no `.secrets/`).
- [ ] **Step 4: Commit** — `test(e2e): lockout over the real network; acceptance map for spec 0003`

---

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context,
   read-only), as defined in [`docs/workflow.md`](../../workflow.md#verification):
   realization vs **spec** (8 layers, using the acceptance map), API/e2e (clean
   stack, the three e2e scripts + independent probes), security (no enumeration
   through body, headers or timing; Decision 7 and Decision 14 hold under
   parallel requests; nothing submitted is stored or logged; diff). Only one of
   them builds and tests in the tree; only one uses compose and port 8080.
2. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the
   issue goes to the owner.
3. Record the outcome: the verification log in the acceptance map, and an
   `## As built (owner, date)` section in spec 0003.
4. When every verifier passes, merge the feature branch into `main` locally.

## Open questions for owner

1. **Six or more simultaneous logins with the right password on one identifier.**
   An attempt is counted before its password is evaluated (Decision 14), and five
   attempts inside 10 s start a cooldown (Decision 9). Five parallel correct
   logins all succeed; a sixth that arrives before the first of them has finished
   is refused with `429`. Sequential logins, however fast, are unaffected (each
   success ends the streak). Default proposal: accept; a single user does not
   open six logins within one hash verification.
2. **Pruning also runs once at host start**, not only hourly — the same choice as
   the token pruning of slice 2 (its escalation E3). Acceptable here too?
3. **Every login now writes to the database** (count the attempt; delete the row
   on success), and a refused attempt writes too. This is what makes the count
   exact under parallel requests; without per-IP limiting (deferred) it also means
   an anonymous client can cause one small write per request. Acceptable until
   the per-IP work lands?

## As built

Implemented and verified locally (three verifiers; realization vs spec passed in
round 2, after one fix round). What the implementation settled is recorded in spec
0003 → "As built" and in the [acceptance map](0003-acceptance-map.md). Tasks 1–5
were built as written here: the code compiled and passed the analyzers unchanged.
Task 6's script adds an `expect_401` helper. Where the result departs from this
plan: the pruning tests of Task 5, and those of slice 2, turned out to stop their
own host now and then, so they build it without the pruning services instead of
stopping them (which edits an existing test file, against the Global Constraints);
two tests were added after verification
(`Decoy_follows_the_configured_hasher_settings`, `Window_is_fixed_not_sliding`).
Open questions 1–3 above were accepted by the owner as proposed; the escalations
E1–E4 are in the acceptance map.
