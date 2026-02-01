# Email Flows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0004
- **Date:** 2026-01-26
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0004-email-flows.md`](../specs/0004-email-flows.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** A user resets a forgotten password and confirms an email address through
single-use links sent by mail; an unconfirmed account cannot log in; no response
or timing says which addresses have an account.

**Architecture:** The two endpoints that send mail never look an account up: they
validate, apply a per-address limit and write one row to a queue table, all in one
transaction, and answer `202`. A background dispatcher first removes, in one
statement, the queue rows that will never become a mail (no account, nothing to
confirm), then takes the remaining rows one at a time: it issues a link token
(only its SHA-256 is stored, one row per user and kind, so a new token replaces
the old one), composes the mail and sends it through SMTP (MailKit); a failed
send rolls the token back to a savepoint and is retried on a schedule. Consuming a token is one
`DELETE … RETURNING` inside the transaction that also applies its effects, so a
reset is all-or-nothing and a token works once. The limit is a pure function over
one row per address and kind, applied with the row locked — the pattern of the
lockout of spec 0003. A session carries the account's security stamp, and a
refresh is refused once the stamp has changed, so a session cannot outlive a
password change even if its login overlapped it.

**Tech Stack:** as plans 0001–0003 (.NET 10, ASP.NET Core minimal APIs, ASP.NET
Core Identity, OpenIddict 7.7.1, EF Core 10.0.12 + Npgsql 10.0.3, PostgreSQL 16,
xUnit v3, Testcontainers 4.15). **One new package: `MailKit` 4.18.1** (MIT; brings
`MimeKit` 4.18.1 and `BouncyCastle.Cryptography` 2.7.0, both MIT). Mail catcher
for tests and e2e: `axllent/mailpit:v1.31.3` (MIT).

## Global Constraints

- Everything in plans 0001–0003 → Global Constraints still holds (central package
  versions, `TreatWarningsAsErrors`, no secrets in repo or logs, Conventional
  Commits, local-only workflow).
- Numbers are **constants in code**, not configuration (spec Decision 15): reset
  token **1 hour**, verification token **24 hours**; mail limit **one accepted
  request per 60 seconds** and **five per fixed 60-minute window**, per address
  and per kind; retries after **5 s, 30 s, 2 min, 10 min, then every 10 min**;
  a request is dropped once it is **1 hour** old.
- Contract (spec → Contract). Every response of the four new endpoints carries
  `Cache-Control: no-store` and `Pragma: no-cache` and never a `Set-Cookie`.
  Bodies, exactly: `202` and `204` empty; `{"error":"invalid_request"}`;
  `{"error":"invalid_token"}`; `{"error":"weak_password","rules":[…]}` with rules
  from `too_short`, `requires_upper`, `requires_lower`, `requires_digit`, in that
  order; `{"error":"too_many_attempts","retry_after_seconds":<n>}` with
  `Retry-After: <n>` (the existing `TooManyAttemptsResult`);
  `403 {"error":"email_not_verified"}` on login.
- **The request path of `forgot` and `verify/request` never looks an account up**
  (spec Decision 12): no `UserManager`, no query on `AspNetUsers`.
- **A link token in clear exists only in the mail.** The database holds its
  SHA-256. No token, link or mail body is logged; log lines carry ids, kinds,
  counts and exception **type names** only. No submitted address is logged.
- The mail's recipient is the account's stored `Email`; nothing a requester
  submitted is placed in a mail or a mail header.
- Password policy: at least 8 characters, an uppercase letter, a lowercase letter,
  a digit; no non-alphanumeric character required. Letters and digits of any
  script count (`Ż` is an uppercase letter).
- The `401`, `429` and `200` of login stay exactly as built. Existing test files
  must pass **unedited**, except `Infrastructure/AuthAppFactory.cs` (Task 2 adds
  default settings to it).
- Exactly one new migration (Task 3). Its id is given by the orchestrator.
- Implementers leave their changes **uncommitted** in the working tree. The
  orchestrator reads the diff, runs the gate and commits with the subject named
  in the task's last step.
- Gate for every task: `dotnet build -warnaserror && dotnet test`. Before the
  last commit also `dotnet format --verify-no-changes` and the hermetic run:
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`.
- **Unicode escapes stay escapes.** Where the code below writes `\u` followed by
  four hex digits (C# string and character literals, JSON bodies in raw strings),
  the file must hold those six characters, not the character they stand for: an
  invisible character in a source file is a defect, and a raw NUL is not valid
  JSON. Some editing tools decode such escapes on the way in: after writing a
  file that should hold one, check with `grep -n 'uFFFE' <file>` (or the escape
  in question) that it is there as text.
- Test hosts: a test that asks for mail builds its host **without**
  `MailDispatchService` and drives `MailDispatcher.DispatchDueAsync` by hand
  (`MailTestBase` does both). Never call `StopAsync` on a hosted service of a
  running host. At most about 12 parallel calls per test.

## Verified before this plan was written

Two throwaway probes (not in the repository) settled the spec's "To verify" items
that could sink the design.

**Against the real host** (a temporary test class on `AuthAppFactory`, PostgreSQL
16, OpenIddict 7.7.1, Identity 10.0.12):

- **Ending every session.** `IOpenIddictTokenManager.RevokeBySubjectAsync(subject)`
  and `IOpenIddictAuthorizationManager.RevokeBySubjectAsync(subject)` exist. After
  two logins they revoked 4 tokens and 2 authorizations, and both refresh cookies
  got `401 {"error":"invalid_grant"}` — the answer `SessionApi.AssertInvalidGrantAsync`
  checks. A login afterwards starts a working session.
- **One transaction.** `db.Database.BeginTransactionAsync()` on the scope's
  `AuthDbContext` covers `UserManager.RemovePasswordAsync` + `AddPasswordAsync`,
  both `RevokeBySubjectAsync` calls and an `ExecuteDeleteAsync`. Rolled back:
  refresh `200`, old password `200`, new password `401`. Committed: `401`, `401`,
  `200`.
- **Password rules.** With `RequiredLength = 8` and `RequireNonAlphanumeric = false`
  the validators report one error per broken rule: `PasswordTooShort`,
  `PasswordRequiresUpper`, `PasswordRequiresLower`, `PasswordRequiresDigit`.
- **Normalisation depends on the host.** With ICU (a developer machine)
  `NormalizeEmail` throws `ArgumentException` for U+FFFE and for an unpaired
  surrogate, and accepts U+FFFF and U+FDD0. In invariant-globalization mode (the
  chiseled container image) it throws for none of them. So "cannot be normalised"
  must be decided by a rule of our own, with the `try`/`catch` only as a net
  (Task 1).
- **Cultures.** In invariant-globalization mode `new CultureInfo("pl")` throws
  `CultureNotFoundException`, so `.resx` satellite assemblies cannot be selected
  there. The texts are C# string tables chosen by the configured locale (Task 4).
- Only `DevSeedTests` reads `EmailConfirmed`; no existing test logs in as an
  unconfirmed user. The seed passwords of the tests and of `.env.example` satisfy
  the new policy.

**A console app with MailKit 4.18.1 against Mailpit v1.31.3:**

- `new BodyBuilder { TextBody, HtmlBody }.ToMessageBody()` is
  `multipart/alternative` with `text/plain; charset=utf-8` and
  `text/html; charset=utf-8` parts; Polish diacritics arrive intact in subject and
  both parts. `ConnectAsync(host, port, SecureSocketOptions.None)` + `SendAsync`
  works without credentials.
- Mailpit API: `GET /api/v1/search?query=to:<address>` → `messages_count` and
  `messages[].ID`; `GET /api/v1/message/{id}` → `Subject`, `Text`, `HTML`,
  `From.Address`, `From.Name`; `GET /api/v1/message/{id}/raw`; `GET /readyz` → `200`.
- The **machine's host name** leaks twice unless told otherwise: MimeKit builds
  `Message-Id` from it, and MailKit sends it in the SMTP greeting, which the
  server copies into a `Received` header. Both are set from the sender's domain
  (Task 5: `MessageId` and `SmtpClient.LocalDomain`).
- Nothing listening → `SocketException`; a silent host → `TimeoutException` after
  `SmtpClient.Timeout`. The dispatcher treats any exception from the transport as
  a failed attempt.
- `dotnet list package --vulnerable --include-transitive`: none.
- Testcontainers 4.15: `new ContainerBuilder(string image)` and
  `Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(…)` exist.

**The plan's code as a whole.** Before implementation an independent reviewer
extracted every code block of this plan into a scratch copy of the repository
(not this one), applied the described edits to the existing files, generated the
migration and ran the suite. A second reviewer then read the plan against the
spec for what running cannot show (the order of the tasks, races, enumeration,
what the tests really prove); its findings were built into the same scratch copy
and run again. The code below is that copy: it builds with warnings as errors,
`dotnet format --verify-no-changes` is clean, and all 387 tests pass — the 201
existing ones unedited — also when the mail classes are run repeatedly. The e2e
script of Task 12 was **not** part of those runs.

The code is still to be compiled task by task: the scratch run proves the final
state, not that every task builds on its own. Record in the task report any name
that differs.

## Review Focus

The spec does not name these, but a client or an attacker would hit them. Each
line has a pinning test in the task named in brackets.

1. **The same token sent twice at once** (a double-clicked button): exactly one
   request succeeds, the other is an `invalid_token`; never two password changes,
   never a `500`. [Task 8, Task 9]
2. **Spelling variants of one address** (`USER@EXAMPLE.COM`) share one mail limit
   and reach the one account, exactly as at login. [Task 7]
3. **An application name with markup** (`Tom & <Jerry>`) and a frontend URL that
   already has a query string: the HTML part is encoded and the link is still a
   valid URL with one `token` parameter. [Task 4]
4. **The mail server is down while requests keep coming:** requests still answer
   `202` at once, the earlier link keeps working until a new mail has really
   left, and nothing is sent twice when the server comes back. [Task 6]
5. **A reset for an account that is locked out and unconfirmed at the same
   time:** one reset lifts the lock, confirms the email and lets the new password
   in on the first try. [Task 8]

## File Structure

```
Directory.Packages.props                         + MailKit
src/Auth.Infrastructure/
  DependencyInjection.cs                         + password policy
  Persistence/MailKind.cs                        the two kinds of mail
  Persistence/EmailToken.cs                      a link token (hash, user, kind, expiry)
  Persistence/MailRequest.cs                     a queue row
  Persistence/MailRequestLimit.cs                the limit of one address and kind
  Persistence/AuthDbContext.cs                   + three sets and their mapping
  Persistence/Migrations/<id>_AddEmailFlows.cs (+ .Designer.cs), AuthDbContextModelSnapshot.cs
src/Auth.Server/
  Auth.Server.csproj                             + MailKit
  appsettings.Development.json                   + development mail settings
  Program.cs                                     + registrations, four routes
  Requests/JsonObjectBody.cs                     bounded JSON object body → named strings
  Requests/EmailInput.cs                         is this email acceptable; normalise it
  Login/JsonLoginRequestHandler.cs               uses the two above (E1)
  Login/LoginEndpoint.cs                         + security stamp in the session; refusal of an unconfirmed account
  Sessions/SessionPolicy.cs                      + the stamp claim
  Sessions/RefreshEndpoint.cs                    + a refresh is refused once the stamp has changed
  Seeding/DevUserSeeder.cs                       + optional unconfirmed second user
  Email/MailSettings.cs                          settings + fail-fast loader
  Email/StorableTime.cs                          the clock at the precision PostgreSQL keeps
  Email/MailLimitPolicy.cs                       the limit as a pure transition
  Email/MailRequestStore.cs                      limit + enqueue in one transaction
  Email/EmailTokens.cs                           lifetimes, hashing, issue, consume
  Email/MailTexts.cs                             the texts, Polish and English
  Email/MailComposer.cs                          kind + recipient + token → subject, text, HTML
  Email/IMailTransport.cs  Email/SmtpMailTransport.cs
  Email/MailDelivery.cs                          retry schedule and time limits
  Email/MailDispatchSignal.cs                    wakes the dispatcher
  Email/MailDispatcher.cs                        one pass over the due queue rows
  Email/MailDispatchService.cs                   the loop around it
  Email/EmailPruner.cs  Email/EmailPruningService.cs
  Account/AccountResults.cs                      the responses of the new endpoints
  Account/MailRequestEndpoint.cs                 forgot and verify/request
  Account/PasswordRules.cs                       validator errors → rule names
  Account/ResetPasswordEndpoint.cs
  Account/VerifyEmailEndpoint.cs
tests/Auth.IntegrationTests/
  Infrastructure/AuthAppFactory.cs               + default mail settings
  Infrastructure/CapturingMailTransport.cs       records mails; can be told to fail
  Infrastructure/CapturingLoggerProvider.cs      records log lines
  Infrastructure/MailTestBase.cs                 host without the dispatcher + capturing transport
  Infrastructure/AccountApi.cs                   the four endpoints, token extraction, user creation
  Infrastructure/MailpitFixture.cs               one Mailpit container for the SMTP tests
  EmailInputTests.cs  PasswordPolicyTests.cs  MailSettingsTests.cs
  MailLimitPolicyTests.cs  MailRequestStoreTests.cs  EmailTokensTests.cs
  MailComposerTests.cs  SmtpMailTransportTests.cs  MailDispatcherTests.cs
  MailDispatchServiceTests.cs  MailRequestEndpointTests.cs  ResetPasswordTests.cs
  VerifyEmailTests.cs  UnverifiedLoginTests.cs  EmailPruningTests.cs
deploy/docker-compose.yml                        + mailpit, mail settings, second seed user
.env.example                                     + second seed user
scripts/e2e-email.sh                             both flows over the real network
docs/superpowers/plans/0004-acceptance-map.md    criteria → tests
README.md                                        quickstart + status
```

Mail infrastructure lives in a new `Email/` folder, the four endpoints in a new
`Account/` folder, request parsing shared with login in `Requests/`. In `Sessions/`
only the stamp claim and its check in the refresh endpoint are added (Task 8);
nothing in `Keys/` or `Tokens/` is modified, and nothing in `Lockout/` either
(`TooManyAttemptsResult` and `LoginIdentifier` are reused as they are).

Test conventions used below (all exist): `SessionTestBase` gives `Postgres`,
`Keys`, `Clock` (a `FakeTimeProvider`, frozen until a test advances it), `Factory`
and `Client`; `LoginApi.Login` / `LoginOk` / `HeaderNames`; `SessionApi.LoginAsync`,
`Refresh`, `AssertInvalidGrantAsync`; `LockoutApi.FailAsync`, `AssertLockedAsync`,
`WrongPassword`; `AuthAppFactory.WithSetting`, `WithServices`,
`WithoutHostedService<T>()`, `WithEnvironment`, `SeedEmail`, `SeedPassword`. Every
test host has a database of its own.

---

### Task 1: Request plumbing, email validation and the password policy

**Files:**
- Create: `src/Auth.Server/Requests/JsonObjectBody.cs`, `src/Auth.Server/Requests/EmailInput.cs`,
  `src/Auth.Infrastructure/Identity/UnicodePasswordValidator.cs`
- Modify: `src/Auth.Server/Login/JsonLoginRequestHandler.cs`,
  `src/Auth.Infrastructure/DependencyInjection.cs`
- Test: `tests/Auth.IntegrationTests/EmailInputTests.cs`,
  `tests/Auth.IntegrationTests/PasswordPolicyTests.cs`

**Interfaces:**
- Produces: `JsonObjectBody` (static) with `const int MaxBytes = 8 * 1024`,
  `static bool IsJson(string? contentType)`,
  `static Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)`,
  `static bool TryGetRequiredString(JsonElement obj, string name, out string value)`,
  `static Task<string[]?> ReadStringsAsync(HttpRequest request, string[] names, CancellationToken cancellationToken)`
  (the values of the named properties in order; `null` when the request is not a
  JSON object of at most `MaxBytes` holding each of them as a non-blank string).
- Produces: `EmailInput` (static) with `const int MaxLength = 254`,
  `static bool IsWellFormed(string email)`,
  `static bool TryNormalize(string email, ILookupNormalizer normalizer, [NotNullWhen(true)] out string? normalized)`.
- Produces: `UnicodePasswordValidator` — ASP.NET Identity's password validator
  with letters and digits of any script counted; it **replaces** the built-in
  `IPasswordValidator<ApplicationUser>` registration.
- Changes: the login extraction handler rejects an email that `EmailInput` does
  not accept with its existing `400`. The password options of ASP.NET Identity.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/EmailInputTests.cs`:

```csharp
using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailInputTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("zażółć@example.com", true)]
    [InlineData("a\uD83D\uDE00b@example.com", true)]     // a well-formed surrogate pair
    [InlineData("a\uFFFEb@example.com", false)]          // the one that made the ICU normaliser throw (E1)
    [InlineData("a\uFFFFb@example.com", false)]
    [InlineData("a\uFDD0b@example.com", false)]
    [InlineData("a\uD83F\uDFFEb@example.com", false)]    // U+1FFFE, a noncharacter outside the BMP
    [InlineData("a\u0007b@example.com", false)]
    [InlineData("a\u007Fb@example.com", false)]          // DEL
    [InlineData("a\u0085b@example.com", false)]          // a C1 control character
    [InlineData("a@example.com\n", false)]
    public void Well_formedness_does_not_depend_on_the_host(string email, bool expected) =>
        Assert.Equal(expected, EmailInput.IsWellFormed(email));

    [Fact]
    public void Unpaired_surrogate_is_not_well_formed()
    {
        // Built at run time: an attribute argument cannot hold an unpaired surrogate.
        var email = "a" + (char)0xD800 + "b@example.com";

        Assert.False(EmailInput.IsWellFormed(email));
    }

    [Fact]
    public void Normalised_form_is_the_one_the_account_lookup_uses()
    {
        using var scope = Factory.Services.CreateScope();
        var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();

        Assert.True(EmailInput.TryNormalize("User@Example.com", normalizer, out var normalized));
        Assert.Equal(normalizer.NormalizeEmail("User@Example.com"), normalized);
        Assert.False(EmailInput.TryNormalize("a\uFFFEb@example.com", normalizer, out _));
    }

    [Fact]
    public async Task Login_does_not_apply_the_length_limit_of_the_mail_endpoints()
    {
        using var response = await LoginApi.Login(Client, new string('a', 300) + "@example.com", "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_an_email_that_cannot_be_normalised_is_a_400_and_not_an_attempt()   // criterion 16
    {
        const string Body = """{"email":"a\uFFFEb@example.com","password":"x"}""";

        // Twelve: a counted identifier would be locked long before the last one.
        for (var i = 0; i < 12; i++)
        {
            using var response = await Client.PostAsync(LoginApi.Path, new StringContent(Body, Encoding.UTF8, "application/json"));
            var raw = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Request {i + 1}: expected 400, got {(int)response.StatusCode}: {raw}");
            Assert.Contains("invalid_request", raw);
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(0, await db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));
    }
}
```

`tests/Auth.IntegrationTests/PasswordPolicyTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PasswordPolicyTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Theory]
    [InlineData("Abcdefg1", true)]                  // 8 characters, no special character needed
    [InlineData("Correct-Horse-Battery-1", true)]
    [InlineData("Abcdef1", false)]                  // 7 characters
    [InlineData("abcdefg1", false)]                 // no uppercase
    [InlineData("ABCDEFG1", false)]                 // no lowercase
    [InlineData("Abcdefgh", false)]                 // no digit
    [InlineData("zażółć12Ż", true)]                 // the only uppercase letter is not A-Z
    [InlineData("ZAŻÓŁĆ12ż", true)]                 // the only lowercase letter is not a-z
    [InlineData("zażółć123", false)]                // letters of any script, but none uppercase
    public async Task Policy_is_eight_characters_upper_lower_and_digit(string password, bool acceptable)   // criterion 8
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var succeeded = true;
        foreach (var validator in users.PasswordValidators)
        {
            succeeded &= (await validator.ValidateAsync(users, new ApplicationUser(), password)).Succeeded;
        }

        Assert.Equal(acceptable, succeeded);
    }

    [Fact]
    public async Task Policy_does_not_block_a_login_with_a_password_set_before_it()
    {
        const string Weak = "abc";
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Factory.SeedEmail))!;
            user.PasswordHash = users.PasswordHasher.HashPassword(user, Weak);
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Weak);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`Auth.Server.Requests` does not exist).

- [ ] **Step 3: Implement the body reader.** `src/Auth.Server/Requests/JsonObjectBody.cs`.
  `IsJson`, `ReadBoundedAsync` and `TryGetRequiredString` are **moved** here from
  `JsonLoginRequestHandler` unchanged in behaviour.

```csharp
using System.Buffers;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Requests;

/// <summary>
/// Reads the request bodies of the API: a JSON object of at most <see cref="MaxBytes"/> whose named properties are
/// non-blank strings. Shared by login and the account endpoints, so that "malformed" means the same everywhere.
/// </summary>
public static class JsonObjectBody
{
    /// <summary>Largest accepted request body, in bytes.</summary>
    public const int MaxBytes = 8 * 1024;

    private const string JsonMediaType = "application/json";

    public static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, JsonMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the whole body, or returns <see langword="null"/> as soon as it exceeds <paramref name="limit"/> bytes.</summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var total = 0;
            while (total <= limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, limit + 1 - total), cancellationToken);
                if (read == 0)
                {
                    return buffer.AsSpan(0, total).ToArray();
                }

                total += read;
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static bool TryGetRequiredString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>
    /// The values of the properties <paramref name="names"/>, in that order, or <see langword="null"/> when the
    /// request is not acceptable: wrong content type, too large, not a JSON object, or a named property that is
    /// missing, not a string, or blank.
    /// </summary>
    public static async Task<string[]?> ReadStringsAsync(HttpRequest request, string[] names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(names);

        if (!IsJson(request.ContentType) || request.ContentLength > MaxBytes)
        {
            return null;
        }

        var body = await ReadBoundedAsync(request.Body, MaxBytes, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new string[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                if (!TryGetRequiredString(root, names[i], out values[i]))
                {
                    return null;
                }
            }

            return values;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape (e.g. "\ud800").
            return null;
        }
    }
}
```

- [ ] **Step 4: Implement the email check.** `src/Auth.Server/Requests/EmailInput.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Requests;

/// <summary>
/// Decides whether a submitted email can be used at all, before anything is counted or looked up. The rule is our
/// own, so that it is the same on every host: the framework's normaliser rejects U+FFFE where ICU is present and
/// accepts it where it is not (spec 0004, criterion 16).
/// </summary>
public static class EmailInput
{
    /// <summary>The longest address the mail endpoints accept (RFC 5321). Login does not apply it.</summary>
    public const int MaxLength = 254;

    /// <summary>
    /// <see langword="false"/> for an email holding a control character, a Unicode noncharacter or an unpaired
    /// surrogate.
    /// </summary>
    public static bool IsWellFormed(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        for (var i = 0; i < email.Length; i++)
        {
            var c = email[i];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c) && i + 1 < email.Length && char.IsLowSurrogate(email[i + 1]))
            {
                // U+xFFFE and U+xFFFF are noncharacters in every plane.
                if ((char.ConvertToUtf32(c, email[i + 1]) & 0xFFFE) == 0xFFFE)
                {
                    return false;
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(c) || c is (>= '\uFDD0' and <= '\uFDEF') or '\uFFFE' or '\uFFFF')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The email as the account lookup normalises it, or <see langword="false"/> when it is not well formed or the
    /// host's normaliser rejects it.
    /// </summary>
    public static bool TryNormalize(string email, ILookupNormalizer normalizer, [NotNullWhen(true)] out string? normalized)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(normalizer);

        normalized = null;
        if (!IsWellFormed(email))
        {
            return false;
        }

        try
        {
            normalized = normalizer.NormalizeEmail(email);
        }
        catch (ArgumentException)
        {
            // Whatever else this host's normaliser refuses: a malformed request, not a server error.
            return false;
        }

        return normalized is not null;
    }
}
```

- [ ] **Step 5: Use both in the login handler.** In
  `src/Auth.Server/Login/JsonLoginRequestHandler.cs`:
  - delete the private methods `IsJson`, `ReadBoundedAsync` and
    `TryGetRequiredString` and the constant `JsonMediaType`; call
    `JsonObjectBody.IsJson`, `JsonObjectBody.ReadBoundedAsync` and
    `JsonObjectBody.TryGetRequiredString` instead (in `HandleAsync` and in
    `TryParseCredentials`); the error description that named the media type keeps
    its text: `"The request body must be 'application/json'."`;
  - change `MaxBodyBytes` to `public const int MaxBodyBytes = JsonObjectBody.MaxBytes;`;
  - replace the control-character check by:

```csharp
        // NUL is not storable in a Postgres text column, control characters have no place in an email, and an
        // email the normaliser cannot take would throw in the endpoint. Same fixed 400 as any other malformed body.
        var normalizer = request.HttpContext.RequestServices.GetRequiredService<ILookupNormalizer>();
        if (!EmailInput.TryNormalize(email, normalizer, out _) || password.Contains('\0'))
        {
            Reject(context, InvalidCredentialsShapeDescription);
            return;
        }
```

  - add `using Auth.Server.Requests;` and `using Microsoft.AspNetCore.Identity;`;
    remove usings that became unused (`System.Buffers`, `Microsoft.Net.Http.Headers`
    if nothing else needs them — the build says).

- [ ] **Step 6: Set the password policy.** In
  `src/Auth.Infrastructure/DependencyInjection.cs` replace the `AddIdentityCore`
  call's options lambda:

```csharp
        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Spec 0004, Decision 9. Applies when a password is set; a login never checks it.
                options.Password.RequiredLength = 8;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AuthDbContext>();

        // Replaces Identity's validator rather than adding to it: the built-in one would still refuse a password
        // whose only uppercase letter is not A-Z.
        services.Replace(ServiceDescriptor.Scoped<IPasswordValidator<ApplicationUser>, UnicodePasswordValidator>());
```

  with `using Microsoft.Extensions.DependencyInjection.Extensions;`. And the
  validator, `src/Auth.Infrastructure/Identity/UnicodePasswordValidator.cs`
  (uppercase, lowercase and digit mean any Unicode letter or digit — the owner's
  decision, spec Decision 20):

```csharp
using Microsoft.AspNetCore.Identity;

namespace Auth.Infrastructure.Identity;

/// <summary>
/// ASP.NET Identity's password validator with one difference: a letter or a digit of any script counts, not only
/// A–Z, a–z and 0–9. With the built-in rule a password such as "zażółć12Ż" has no uppercase letter (spec 0004,
/// Decision 20). The policy itself (length, which kinds of character are required) stays in
/// <see cref="PasswordOptions"/>.
/// </summary>
public sealed class UnicodePasswordValidator(IdentityErrorDescriber? errors = null) : PasswordValidator<ApplicationUser>(errors)
{
    public override bool IsUpper(char c) => char.IsUpper(c);

    public override bool IsLower(char c) => char.IsLower(c);

    public override bool IsDigit(char c) => char.IsDigit(c);

    public override bool IsLetterOrDigit(char c) => char.IsLetterOrDigit(c);
}
```

- [ ] **Step 7: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS, including the unedited `LoginRequestTests` (every malformed
  body is still a `400`) and `LockoutTests.Hostile_email_is_counted_and_locked_like_any_other`.
  If an existing test submits an email that `IsWellFormed` now refuses, stop and
  report it — do not edit the test.

- [ ] **Step 8: Commit** — `feat(requests): validate emails on every host; set the password policy`

### Task 2: Mail settings

**Files:**
- Create: `src/Auth.Server/Email/MailSettings.cs`
- Modify: `Directory.Packages.props`, `src/Auth.Server/Auth.Server.csproj`,
  `src/Auth.Server/Program.cs`, `src/Auth.Server/appsettings.Development.json`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/MailSettingsTests.cs`

**Interfaces:**
- Produces: `enum SmtpSecurity { None, StartTls, Tls }`.
- Produces: `sealed class SmtpSettings` with `string Host`, `int Port`,
  `SmtpSecurity Security`, `string? Username`, `string? Password` (all `required init`
  except the two nullable ones, which are `init`).
- Produces: `sealed class MailSettings` with `string AppName`, `string Locale`
  (`"pl"` or `"en"`), `Uri ResetPasswordUrl`, `Uri VerifyEmailUrl`,
  `string FromAddress`, `SmtpSettings Smtp` (all `required init`).
  Classes, not records: a record's generated `ToString` would print the password.
- Produces: `static class MailSettingsLoader` with the key constants
  `AppNameKey = "Auth:App:Name"`, `LocaleKey = "Auth:App:Locale"`,
  `ResetPasswordUrlKey = "Auth:App:FrontendUrls:ResetPassword"`,
  `VerifyEmailUrlKey = "Auth:App:FrontendUrls:VerifyEmail"`,
  `FromKey = "Auth:Email:From"`, `SmtpHostKey = "Auth:Email:Smtp:Host"`,
  `SmtpPortKey = "Auth:Email:Smtp:Port"`, `SmtpSecurityKey = "Auth:Email:Smtp:Security"`,
  `SmtpUsernameKey = "Auth:Email:Smtp:Username"`, `SmtpPasswordKey = "Auth:Email:Smtp:Password"`,
  and `static MailSettings Load(IConfiguration configuration, bool isDevelopment)`,
  which throws `InvalidOperationException` naming the key and never a value.
- Produces: `MailSettings` registered as a singleton in `Program.cs`.
- Produces: `AuthAppFactory` defaults — `DefaultAppName = "Auth-Core Test"`,
  `DefaultResetUrl = "https://app.example.com/reset"`,
  `DefaultVerifyUrl = "https://app.example.com/verify"`,
  `DefaultFrom = "no-reply@example.com"`, locale `en`, SMTP `smtp.invalid:587`
  with `starttls` (valid in every environment; nothing is ever sent there).

- [ ] **Step 1: Add the package.** `Directory.Packages.props`, a new group after
  the token-server packages:

```xml
    <!-- Mail (plan 0004, Task 2). MIT; brings MimeKit and BouncyCastle.Cryptography, both MIT. -->
    <PackageVersion Include="MailKit" Version="4.18.1" />
```

  `src/Auth.Server/Auth.Server.csproj`, in the item group that holds
  `OpenIddict.AspNetCore`: `<PackageReference Include="MailKit" />`.

- [ ] **Step 2: Write the failing tests.** `tests/Auth.IntegrationTests/MailSettingsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class MailSettingsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string SmtpPassword = "smtp-secret-value";

    private static Dictionary<string, string?> Valid() => new()
    {
        [MailSettingsLoader.AppNameKey] = "speech-to-mail",
        [MailSettingsLoader.LocaleKey] = "pl",
        [MailSettingsLoader.ResetPasswordUrlKey] = "https://app.example.com/reset",
        [MailSettingsLoader.VerifyEmailUrlKey] = "https://app.example.com/verify",
        [MailSettingsLoader.FromKey] = "no-reply@example.com",
        [MailSettingsLoader.SmtpHostKey] = "smtp.example.com",
        [MailSettingsLoader.SmtpPortKey] = "587",
        [MailSettingsLoader.SmtpSecurityKey] = "starttls",
        [MailSettingsLoader.SmtpUsernameKey] = "mailer",
        [MailSettingsLoader.SmtpPasswordKey] = SmtpPassword,
    };

    private static MailSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        MailSettingsLoader.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    [Fact]
    public void Valid_settings_load()
    {
        var settings = Load(Valid());

        Assert.Equal("speech-to-mail", settings.AppName);
        Assert.Equal("pl", settings.Locale);
        Assert.Equal(new Uri("https://app.example.com/reset"), settings.ResetPasswordUrl);
        Assert.Equal(new Uri("https://app.example.com/verify"), settings.VerifyEmailUrl);
        Assert.Equal("no-reply@example.com", settings.FromAddress);
        Assert.Equal("smtp.example.com", settings.Smtp.Host);
        Assert.Equal(587, settings.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, settings.Smtp.Security);
        Assert.Equal("mailer", settings.Smtp.Username);
        Assert.Equal(SmtpPassword, settings.Smtp.Password);
    }

    [Theory]
    [InlineData(MailSettingsLoader.AppNameKey)]
    [InlineData(MailSettingsLoader.LocaleKey)]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey)]
    [InlineData(MailSettingsLoader.VerifyEmailUrlKey)]
    [InlineData(MailSettingsLoader.FromKey)]
    [InlineData(MailSettingsLoader.SmtpHostKey)]
    [InlineData(MailSettingsLoader.SmtpPortKey)]
    [InlineData(MailSettingsLoader.SmtpSecurityKey)]
    public void Missing_or_blank_value_is_refused_naming_the_key(string key)   // criterion 17
    {
        foreach (var value in new[] { null, "", "   " })
        {
            var values = Valid();
            values[key] = value;

            var ex = Assert.Throws<InvalidOperationException>(() => Load(values));
            Assert.Contains(key, ex.Message);
        }
    }

    [Theory]
    [InlineData(MailSettingsLoader.LocaleKey, "de")]
    [InlineData(MailSettingsLoader.LocaleKey, "PL-pl")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "ftp://app.example.com/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "http://app.example.com/reset")]     // not https outside Development
    [InlineData(MailSettingsLoader.VerifyEmailUrlKey, "https://app.example.com/verify#top")] // a fragment would swallow the token
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://user:secret@app.example.com/reset")]
    [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://app.example.com/reset?token=x")]         // the link adds its own
    [InlineData(MailSettingsLoader.FromKey, "not an address")]
    [InlineData(MailSettingsLoader.FromKey, "postmaster")]
    [InlineData(MailSettingsLoader.FromKey, "a@example.com, b@example.com")]
    [InlineData(MailSettingsLoader.AppNameKey, "two\nlines")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "0")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "65536")]
    [InlineData(MailSettingsLoader.SmtpPortKey, "smtp")]
    [InlineData(MailSettingsLoader.SmtpSecurityKey, "ssl3")]
    [InlineData(MailSettingsLoader.SmtpSecurityKey, "none")]                                 // no TLS outside Development
    public void Invalid_value_is_refused_naming_the_key_and_not_the_value(string key, string value)   // criterion 17
    {
        var values = Valid();
        values[key] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));
        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void Development_accepts_plain_http_links_and_an_unencrypted_mail_server()
    {
        var values = Valid();
        values[MailSettingsLoader.ResetPasswordUrlKey] = "http://localhost:4200/reset";
        values[MailSettingsLoader.SmtpSecurityKey] = "none";

        var settings = Load(values, isDevelopment: true);

        Assert.Equal(SmtpSecurity.None, settings.Smtp.Security);
    }

    [Fact]
    public void Credentials_are_optional_but_come_as_a_pair()
    {
        var none = Valid();
        none.Remove(MailSettingsLoader.SmtpUsernameKey);
        none.Remove(MailSettingsLoader.SmtpPasswordKey);
        Assert.Null(Load(none).Smtp.Username);

        var half = Valid();
        half.Remove(MailSettingsLoader.SmtpUsernameKey);
        var ex = Assert.Throws<InvalidOperationException>(() => Load(half));
        Assert.Contains(MailSettingsLoader.SmtpUsernameKey, ex.Message);
        Assert.DoesNotContain(SmtpPassword, ex.Message);
    }

    [Fact]
    public void Settings_do_not_print_the_password()
    {
        var settings = Load(Valid());

        Assert.DoesNotContain(SmtpPassword, settings.ToString());
        Assert.DoesNotContain(SmtpPassword, settings.Smtp.ToString());
    }

    [Fact]
    public async Task Host_does_not_start_without_mail_settings()   // criterion 17
    {
        // Production: in Development appsettings.Development.json supplies a sender of its own.
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithoutSetting(MailSettingsLoader.FromKey);

        // A machine-wide Auth__Email__From would fill the gap: hide it while the host is built.
        var variable = MailSettingsLoader.FromKey.Replace(":", "__", StringComparison.Ordinal);
        var saved = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains(MailSettingsLoader.FromKey, ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, saved);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Host_starts_on_the_test_defaults_in_every_environment(string environment)
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment(environment);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`Auth.Server.Email` does not exist).

- [ ] **Step 4: Implement.** `src/Auth.Server/Email/MailSettings.cs`:

```csharp
using Microsoft.AspNetCore.WebUtilities;
using MimeKit;

namespace Auth.Server.Email;

public enum SmtpSecurity
{
    /// <summary>Plain SMTP. Development only: a local mail catcher.</summary>
    None,

    /// <summary>Plain connection upgraded with STARTTLS (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    Tls,
}

/// <summary>Where mail is handed over. A class, not a record: a record would print the password.</summary>
public sealed class SmtpSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required SmtpSecurity Security { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}

/// <summary>
/// What a mail needs to know about the consumer: its name and language, where its reset and verification screens
/// are, who the mail is from and how to send it. The <c>Auth:App</c> keys mirror <c>app.*</c> of the manifest in
/// design.md, so the manifest loader will fill the same settings.
/// </summary>
public sealed class MailSettings
{
    public required string AppName { get; init; }

    /// <summary><c>pl</c> or <c>en</c>.</summary>
    public required string Locale { get; init; }

    public required Uri ResetPasswordUrl { get; init; }

    public required Uri VerifyEmailUrl { get; init; }

    public required string FromAddress { get; init; }

    public required SmtpSettings Smtp { get; init; }
}

public static class MailSettingsLoader
{
    public const string AppNameKey = "Auth:App:Name";
    public const string LocaleKey = "Auth:App:Locale";
    public const string ResetPasswordUrlKey = "Auth:App:FrontendUrls:ResetPassword";
    public const string VerifyEmailUrlKey = "Auth:App:FrontendUrls:VerifyEmail";
    public const string FromKey = "Auth:Email:From";
    public const string SmtpHostKey = "Auth:Email:Smtp:Host";
    public const string SmtpPortKey = "Auth:Email:Smtp:Port";
    public const string SmtpSecurityKey = "Auth:Email:Smtp:Security";
    public const string SmtpUsernameKey = "Auth:Email:Smtp:Username";
    public const string SmtpPasswordKey = "Auth:Email:Smtp:Password";

    private const int MaxAppNameLength = 100;

    /// <summary>Reads and checks the mail settings. Errors name the key and never echo a value.</summary>
    /// <param name="isDevelopment">Development accepts <c>http</c> links and a mail server without TLS.</param>
    /// <exception cref="InvalidOperationException">A value is missing or invalid.</exception>
    public static MailSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var appName = Required(configuration, AppNameKey);
        if (appName.Length > MaxAppNameLength || appName.Any(char.IsControl))
        {
            throw Invalid(AppNameKey, $"must be at most {MaxAppNameLength} characters without control characters");
        }

        var locale = Required(configuration, LocaleKey);
        if (locale is not ("pl" or "en"))
        {
            throw Invalid(LocaleKey, "must be 'pl' or 'en'");
        }

        var from = Required(configuration, FromKey);
        var at = from.LastIndexOf('@');
        if (!MailboxAddress.TryParse(from, out var mailbox)
            || !string.Equals(mailbox.Address, from, StringComparison.Ordinal)
            || at <= 0 || at == from.Length - 1)
        {
            throw Invalid(FromKey, "must be a single email address");
        }

        var host = Required(configuration, SmtpHostKey);
        if (!int.TryParse(Required(configuration, SmtpPortKey), out var port) || port is < 1 or > 65535)
        {
            throw Invalid(SmtpPortKey, "must be a port number between 1 and 65535");
        }

        SmtpSecurity security = Required(configuration, SmtpSecurityKey) switch
        {
            "none" => SmtpSecurity.None,
            "starttls" => SmtpSecurity.StartTls,
            "tls" => SmtpSecurity.Tls,
            _ => throw Invalid(SmtpSecurityKey, "must be 'none', 'starttls' or 'tls'"),
        };
        if (security == SmtpSecurity.None && !isDevelopment)
        {
            throw Invalid(SmtpSecurityKey, "must use TLS outside the Development environment");
        }

        var username = configuration[SmtpUsernameKey];
        var password = configuration[SmtpPasswordKey];
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
        {
            var missing = string.IsNullOrEmpty(username) ? SmtpUsernameKey : SmtpPasswordKey;
            throw Invalid(missing, "must be set when the other SMTP credential is");
        }

        return new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = FrontendUrl(configuration, ResetPasswordUrlKey, isDevelopment),
            VerifyEmailUrl = FrontendUrl(configuration, VerifyEmailUrlKey, isDevelopment),
            FromAddress = from,
            Smtp = new SmtpSettings
            {
                Host = host,
                Port = port,
                Security = security,
                Username = string.IsNullOrEmpty(username) ? null : username,
                Password = string.IsNullOrEmpty(password) ? null : password,
            },
        };
    }

    private static Uri FrontendUrl(IConfiguration configuration, string key, bool isDevelopment)
    {
        if (!Uri.TryCreate(Required(configuration, key), UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (isDevelopment && url.Scheme == Uri.UriSchemeHttp))
            || url.Fragment.Length > 0
            || url.UserInfo.Length > 0
            || QueryHelpers.ParseQuery(url.Query).ContainsKey("token"))
        {
            // A fragment would swallow the token; the link adds a 'token' parameter of its own.
            throw Invalid(key, isDevelopment
                ? "must be an absolute http or https URL without credentials, a fragment or a 'token' parameter"
                : "must be an absolute https URL without credentials, a fragment or a 'token' parameter");
        }

        return url;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            // Name the key only; never echo a value.
            throw new InvalidOperationException($"Configuration value '{key}' is missing or blank.");
        }

        return value;
    }

    private static InvalidOperationException Invalid(string key, string rule) =>
        new($"Configuration value '{key}' {rule}.");
}
```

  In `src/Auth.Server/Program.cs`, right after the key material is loaded:

```csharp
// Fail fast on missing or invalid mail settings too.
builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
```

  with `using Auth.Server.Email;`.

- [ ] **Step 5: Give every existing host its settings.**

  `src/Auth.Server/appsettings.Development.json` — add to the `Auth` object
  (a local mail catcher on its default port; the compose stack overrides the host
  in Task 12):

```json
    "App": {
      "Name": "auth-core-dev",
      "Locale": "en",
      "FrontendUrls": {
        "ResetPassword": "http://localhost:4200/reset",
        "VerifyEmail": "http://localhost:4200/verify"
      }
    },
    "Email": {
      "From": "no-reply@auth-core.localhost",
      "Smtp": {
        "Host": "localhost",
        "Port": 1025,
        "Security": "none"
      }
    }
```

  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` — add
  `using Auth.Server.Email;`, the constants, and the settings at the end of the
  constructor:

```csharp
    public const string DefaultAppName = "Auth-Core Test";
    public const string DefaultResetUrl = "https://app.example.com/reset";
    public const string DefaultVerifyUrl = "https://app.example.com/verify";
    public const string DefaultFrom = "no-reply@example.com";
```

```csharp
        // Mail: pinned like the token identifiers. The server name does not resolve: a host that is not given a
        // transport of its own (MailTestBase) or a real catcher (MailpitFixture) can never send anything.
        _settings[MailSettingsLoader.AppNameKey] = DefaultAppName;
        _settings[MailSettingsLoader.LocaleKey] = "en";
        _settings[MailSettingsLoader.ResetPasswordUrlKey] = DefaultResetUrl;
        _settings[MailSettingsLoader.VerifyEmailUrlKey] = DefaultVerifyUrl;
        _settings[MailSettingsLoader.FromKey] = DefaultFrom;
        _settings[MailSettingsLoader.SmtpHostKey] = "smtp.invalid";
        _settings[MailSettingsLoader.SmtpPortKey] = "587";
        _settings[MailSettingsLoader.SmtpSecurityKey] = "starttls";
```

- [ ] **Step 6: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS; every existing test host starts (the `Production` hosts of
  `DevSeedTests` included).

- [ ] **Step 7: Commit** — `feat(email): load and check the mail settings at startup`

### Task 3: The tables, the mail limit and the queue

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/MailKind.cs`, `EmailToken.cs`,
  `MailRequest.cs`, `MailRequestLimit.cs` (same folder),
  `src/Auth.Infrastructure/Persistence/Migrations/<id>_AddEmailFlows.cs` (+ `.Designer.cs`),
  `src/Auth.Server/Email/StorableTime.cs`, `src/Auth.Server/Email/MailLimitPolicy.cs`,
  `src/Auth.Server/Email/MailRequestStore.cs`
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`,
  `src/Auth.Infrastructure/Persistence/Migrations/AuthDbContextModelSnapshot.cs` (generated),
  `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/MailLimitPolicyTests.cs`,
  `tests/Auth.IntegrationTests/MailRequestStoreTests.cs`

**Interfaces:**
- Produces: `enum MailKind : short { PasswordReset = 1, EmailVerification = 2 }`.
- Produces: `EmailToken` with `Guid UserId` + `MailKind Kind` (composite key: at
  most one token per user and kind), `byte[] TokenHash` (unique),
  `DateTimeOffset ExpiresAt` — all `required init`. Table `EmailTokens`.
- Produces: `MailRequest` with `long Id` (identity key, `init`), `MailKind Kind`,
  `string NormalizedEmail`, `DateTimeOffset RequestedAt` (`required init`),
  `int Attempts`, `DateTimeOffset NextAttemptAt` (`set`). Table `MailRequests`.
- Produces: `MailRequestLimit` with `byte[] IdentifierHash` + `MailKind Kind`
  (composite key, `required init`), `DateTimeOffset WindowStartedAt`,
  `int WindowCount`, `DateTimeOffset LastAcceptedAt` (`set`). Table `MailRequestLimits`.
- Produces: `AuthDbContext.EmailTokens`, `.MailRequests`, `.MailRequestLimits`.
- Produces: `static class StorableTime` with `static DateTimeOffset Now(TimeProvider clock)`
  (the current instant cut to microseconds).
- Produces: `readonly record struct MailLimitState(DateTimeOffset WindowStartedAt, int WindowCount, DateTimeOffset LastAcceptedAt)`
  with `static MailLimitState Fresh(DateTimeOffset now)`;
  `readonly record struct MailLimitDecision(bool Allowed, TimeSpan RetryAfter)`;
  `MailLimitPolicy` with `MinimumGap` (60 s), `const int WindowLimit = 5`,
  `Window` (1 h) and
  `static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now)`.
- Produces: `MailRequestStore` (singleton) with
  `Task<MailLimitDecision> SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken cancellationToken)`:
  applies the limit and, when the request is accepted, adds the queue row — one
  transaction.

- [ ] **Step 1: Write the failing rule tests** (`MailLimitPolicyTests`, no database):

```csharp
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class MailLimitPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Registers requests at the given offsets from <see cref="T0"/> and returns the state and the last decision.</summary>
    private static (MailLimitState State, MailLimitDecision Last) After(params TimeSpan[] offsets)
    {
        var state = MailLimitState.Fresh(T0);
        MailLimitDecision last = default;
        foreach (var offset in offsets)
        {
            (state, last) = MailLimitPolicy.Register(state, T0 + offset);
        }

        return (state, last);
    }

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_request_is_accepted_and_opens_a_window()
    {
        var (state, last) = After(TimeSpan.Zero);

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0, 1, T0), state);
    }

    [Fact]
    public void Second_request_within_sixty_seconds_is_refused_with_the_time_left()   // criterion 9
    {
        var (state, last) = After(TimeSpan.Zero, Seconds(20));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(40), last.RetryAfter);
        Assert.Equal(new MailLimitState(T0, 1, T0), state);   // a refused request changes nothing
    }

    [Fact]
    public void Refused_requests_do_not_push_the_wait_up()   // criterion 9
    {
        var (_, last) = After(TimeSpan.Zero, Seconds(20), Seconds(30), Seconds(59));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(1), last.RetryAfter);
    }

    [Fact]
    public void Request_after_sixty_seconds_is_accepted_in_the_same_window()
    {
        var (state, last) = After(TimeSpan.Zero, Seconds(60));

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0, 2, T0 + Seconds(60)), state);
    }

    [Fact]
    public void Sixth_request_in_the_window_is_refused_until_the_window_ends()   // criterion 9
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), Seconds(305));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(3600 - 305), last.RetryAfter);
        Assert.Equal(5, state.WindowCount);
    }

    [Fact]
    public void Window_is_fixed_and_reopens_an_hour_after_it_opened()
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), Seconds(3600));

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0 + Seconds(3600), 1, T0 + Seconds(3600)), state);
    }

    [Fact]
    public void Longer_of_the_two_waits_is_reported()
    {
        // The fifth request lands 10 s before the window ends: the window frees in 5 s, the gap in 55 s.
        var (_, last) = After(Seconds(0), Seconds(900), Seconds(1800), Seconds(2700), Seconds(3590), Seconds(3595));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(55), last.RetryAfter);
    }

    [Fact]
    public void Window_that_closed_long_ago_counts_for_nothing()
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), TimeSpan.FromDays(3));

        Assert.True(last.Allowed);
        Assert.Equal(1, state.WindowCount);
    }
}
```

- [ ] **Step 2: Write the failing store tests** (`MailRequestStoreTests`):

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class MailRequestStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private const string Address = "SOMEONE@EXAMPLE.COM";

    private MailRequestStore Store => Factory.Services.GetRequiredService<MailRequestStore>();

    private Task<MailLimitDecision> SubmitAsync(MailKind kind, string address = Address) =>
        Store.SubmitAsync(kind, address, TestContext.Current.CancellationToken);

    private async Task<List<MailRequest>> QueueAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<MailRequestLimit>> LimitsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequestLimits.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Accepted_request_writes_the_limit_and_one_queue_row_due_at_once()
    {
        var decision = await SubmitAsync(MailKind.PasswordReset);

        Assert.True(decision.Allowed);
        var row = Assert.Single(await QueueAsync());
        Assert.Equal(MailKind.PasswordReset, row.Kind);
        Assert.Equal(Address, row.NormalizedEmail);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(row.RequestedAt, row.NextAttemptAt);
        var limit = Assert.Single(await LimitsAsync());
        Assert.Equal(32, limit.IdentifierHash.Length);
        Assert.Equal(1, limit.WindowCount);
    }

    [Fact]
    public async Task Refused_request_writes_nothing()
    {
        await SubmitAsync(MailKind.PasswordReset);
        Clock.Advance(TimeSpan.FromSeconds(20));

        var refused = await SubmitAsync(MailKind.PasswordReset);

        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(40), refused.RetryAfter);
        Assert.Single(await QueueAsync());
        Assert.Equal(1, Assert.Single(await LimitsAsync()).WindowCount);
    }

    [Fact]
    public async Task Kinds_and_addresses_have_limits_of_their_own()   // criterion 9
    {
        Assert.True((await SubmitAsync(MailKind.PasswordReset)).Allowed);
        Assert.True((await SubmitAsync(MailKind.EmailVerification)).Allowed);
        Assert.True((await SubmitAsync(MailKind.PasswordReset, "OTHER@EXAMPLE.COM")).Allowed);

        Assert.Equal(3, (await QueueAsync()).Count);
        Assert.Equal(3, (await LimitsAsync()).Count);
    }

    [Fact]
    public async Task Parallel_requests_for_one_address_let_exactly_one_through()
    {
        // Twelve, not more: every waiting caller holds a server connection (see LoginStreakStoreTests).
        var decisions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(() => SubmitAsync(MailKind.PasswordReset))));

        Assert.Equal(1, decisions.Count(d => d.Allowed));
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task Stored_instants_read_back_equal()
    {
        // PostgreSQL keeps microseconds (10 ticks): make sure the clock sits between two of them, or this proves nothing.
        Clock.Advance(TimeSpan.FromTicks(17 - (Clock.GetUtcNow().Ticks % 10)));
        await SubmitAsync(MailKind.PasswordReset);

        Clock.Advance(TimeSpan.FromSeconds(20));
        var refused = await SubmitAsync(MailKind.PasswordReset);

        // Exactly forty seconds: the store must compute on what the row can hold.
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(40), refused.RetryAfter);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`MailKind`, `MailLimitPolicy`, `MailRequestStore`,
  `AuthDbContext.MailRequests` do not exist).

- [ ] **Step 4: Add the entities and map them.**

`src/Auth.Infrastructure/Persistence/MailKind.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>The kinds of mail the service sends. The values are stored: never renumber them.</summary>
public enum MailKind : short
{
    PasswordReset = 1,
    EmailVerification = 2,
}
```

`src/Auth.Infrastructure/Persistence/EmailToken.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A link token sent in a mail (spec 0004). Only its hash is stored; the token in clear exists in the mail alone.
/// There is one row per user and kind: a new token overwrites it, and it is deleted when the token is used or expired.
/// </summary>
public sealed class EmailToken
{
    /// <summary>SHA-256 of the token as it appears in the link: 32 bytes.</summary>
    public required byte[] TokenHash { get; init; }

    public required Guid UserId { get; init; }

    public required MailKind Kind { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
```

`src/Auth.Infrastructure/Persistence/MailRequest.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// One request for a mail, waiting to be handled (spec 0004, Decisions 5 and 12). It names the address as it was
/// submitted, normalised, and nothing about an account: whether there is one is found out when the row is handled.
/// </summary>
public sealed class MailRequest
{
    public long Id { get; init; }

    public required MailKind Kind { get; init; }

    public required string NormalizedEmail { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Failed sends so far.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }
}
```

`src/Auth.Infrastructure/Persistence/MailRequestLimit.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The mail limit of one address and one kind of mail (spec 0004): the accepted requests of the current window.
/// The key is a hash, never the address, and a row exists for addresses without an account too.
/// </summary>
public sealed class MailRequestLimit
{
    /// <summary>SHA-256 of the normalised email: 32 bytes whatever was submitted.</summary>
    public required byte[] IdentifierHash { get; init; }

    public required MailKind Kind { get; init; }

    public DateTimeOffset WindowStartedAt { get; set; }

    /// <summary>Accepted requests in the window; 0 only on a row that has just been created.</summary>
    public int WindowCount { get; set; }

    public DateTimeOffset LastAcceptedAt { get; set; }
}
```

`AuthDbContext.cs` — add the three sets after `LoginStreaks`, and the mapping
after the `LoginStreak` block:

```csharp
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();

    public DbSet<MailRequest> MailRequests => Set<MailRequest>();

    public DbSet<MailRequestLimit> MailRequestLimits => Set<MailRequestLimit>();
```

```csharp
        builder.Entity<EmailToken>(token =>
        {
            // At most one token per user and kind: issuing a new one replaces the row.
            token.HasKey(t => new { t.UserId, t.Kind });
            token.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            // A token is looked up by its hash when it is used.
            token.HasIndex(t => t.TokenHash).IsUnique();
            // Pruning selects by expiry.
            token.HasIndex(t => t.ExpiresAt);
        });

        builder.Entity<MailRequest>(request =>
        {
            request.HasKey(r => r.Id);
            // The dispatcher takes the rows that are due, oldest first.
            request.HasIndex(r => r.NextAttemptAt);
        });

        builder.Entity<MailRequestLimit>(limit =>
        {
            limit.HasKey(l => new { l.IdentifierHash, l.Kind });
            // Pruning selects by age.
            limit.HasIndex(l => l.LastAcceptedAt);
        });
```

- [ ] **Step 5: Generate the migration and give it its id.**

```bash
dotnet tool restore
dotnet ef migrations add AddEmailFlows -p src/Auth.Infrastructure -s src/Auth.Server
```

Expected: two new files in `src/Auth.Infrastructure/Persistence/Migrations/`
(`<stamp>_AddEmailFlows.cs`, `<stamp>_AddEmailFlows.Designer.cs`) and a changed
`AuthDbContextModelSnapshot.cs`. `Up` creates three tables and nothing else:
`EmailTokens` (primary key `UserId uuid` + `Kind smallint`, `UserId` with a
cascading foreign key to `AspNetUsers`, `TokenHash bytea`, `ExpiresAt timestamp
with time zone`; the unique index `IX_EmailTokens_TokenHash` and
`IX_EmailTokens_ExpiresAt`),
`MailRequests` (`Id bigint` identity primary key, `Kind smallint`,
`NormalizedEmail text`, `RequestedAt`, `Attempts integer`, `NextAttemptAt`; index
`IX_MailRequests_NextAttemptAt`) and `MailRequestLimits` (primary key
`IdentifierHash bytea` + `Kind smallint`, `WindowStartedAt`, `WindowCount integer`,
`LastAcceptedAt`; index `IX_MailRequestLimits_LastAcceptedAt`). `Down` drops the
three tables. It must touch **no existing table**; if it does, stop and report.

`dotnet ef` stamps the id from the machine's clock. The id kept in the repository
is the one **the orchestrator gives in the task brief**: 14 digits,
`yyyyMMddHHmmss` in UTC, greater than `20260121180512` (the latest migration).
Apply it before anything else is built:

1. Rename both generated files to `<id>_AddEmailFlows.cs` and
   `<id>_AddEmailFlows.Designer.cs` (plain `mv`; they are not tracked yet).
2. In the `.Designer.cs` file change the attribute to
   `[Migration("<id>_AddEmailFlows")]`.
3. Leave `AuthDbContextModelSnapshot.cs` as generated; it holds no id.
4. Check: `git grep --untracked -n "_AddEmailFlows" -- src` (the files are
   untracked, so plain `git grep` would not see them) prints exactly one line, the
   `[Migration("<id>_AddEmailFlows")]` attribute with the given id;
   `ls src/Auth.Infrastructure/Persistence/Migrations` shows the two files under
   that id; and
   `dotnet ef migrations has-pending-model-changes -p src/Auth.Infrastructure -s src/Auth.Server`
   reports no pending change.

- [ ] **Step 6: Implement the clock helper and the rules.**

`src/Auth.Server/Email/StorableTime.cs`:

```csharp
namespace Auth.Server.Email;

public static class StorableTime
{
    /// <summary>
    /// The current instant at the precision PostgreSQL keeps (microseconds), so that an instant computed here and
    /// one read back from a row compare equal.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var instant = clock.GetUtcNow();
        return instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
```

`src/Auth.Server/Email/MailLimitPolicy.cs`:

```csharp
namespace Auth.Server.Email;

/// <summary>The mail limit of one address and kind, as the rules see it. A count of 0 means "no request yet".</summary>
public readonly record struct MailLimitState(DateTimeOffset WindowStartedAt, int WindowCount, DateTimeOffset LastAcceptedAt)
{
    public static MailLimitState Fresh(DateTimeOffset now) => new(now, 0, now);
}

/// <summary>Accept the request, or refuse it and say when one would be accepted.</summary>
public readonly record struct MailLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The mail limit of spec 0004 as one pure transition: at most one accepted request per <see cref="MinimumGap"/>,
/// and at most <see cref="WindowLimit"/> per fixed <see cref="Window"/>, which opens at an accepted request that
/// falls outside any open window. A refused request changes nothing, so retrying never makes the wait longer.
/// </summary>
public static class MailLimitPolicy
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    public const int WindowLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now)
    {
        var accepted = new MailLimitDecision(true, TimeSpan.Zero);
        if (current.WindowCount == 0)
        {
            return (new MailLimitState(now, 1, now), accepted);
        }

        var windowOpen = now - current.WindowStartedAt < Window;
        var gapWait = current.LastAcceptedAt + MinimumGap - now;
        var windowWait = windowOpen && current.WindowCount >= WindowLimit
            ? current.WindowStartedAt + Window - now
            : TimeSpan.Zero;
        var wait = gapWait > windowWait ? gapWait : windowWait;
        if (wait > TimeSpan.Zero)
        {
            return (current, new MailLimitDecision(false, wait));
        }

        return windowOpen
            ? (current with { WindowCount = current.WindowCount + 1, LastAcceptedAt = now }, accepted)
            : (new MailLimitState(now, 1, now), accepted);
    }
}
```

- [ ] **Step 7: Implement the store.** `src/Auth.Server/Email/MailRequestStore.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Takes a request for a mail: applies the limit of its address and kind and, if the request is within it, puts it
/// on the queue — one short transaction with the limit row locked, so parallel requests are each judged in turn. It
/// does the same work for every address and never looks an account up (spec 0004, Decision 12). It uses a scope of
/// its own, so the request's DbContext never tracks these rows.
/// </summary>
public sealed class MailRequestStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<MailLimitDecision> SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = StorableTime.Now(clock);
        var identifierHash = LoginIdentifier.HashOf(normalizedEmail);
        var kindValue = (short)kind;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One statement creates the row or locks the existing one, and returns it either way (the pattern of
        // LoginStreakStore). ToListAsync, not SingleAsync: EF must send the statement as it is.
        var rows = await db.MailRequestLimits
            .FromSql($"""
                INSERT INTO "MailRequestLimits" ("IdentifierHash", "Kind", "WindowStartedAt", "WindowCount", "LastAcceptedAt")
                VALUES ({identifierHash}, {kindValue}, {now}, 0, {now})
                ON CONFLICT ("IdentifierHash", "Kind") DO UPDATE SET "WindowCount" = "MailRequestLimits"."WindowCount"
                RETURNING *
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        var (next, decision) = MailLimitPolicy.Register(
            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now);
        if (decision.Allowed)
        {
            row.WindowStartedAt = next.WindowStartedAt;
            row.WindowCount = next.WindowCount;
            row.LastAcceptedAt = next.LastAcceptedAt;
            db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}
```

  A refused request can only meet an existing row (a new one always has room), so
  committing without a save leaves nothing behind.

  In `Program.cs`, with the other singletons: `builder.Services.AddSingleton<MailRequestStore>();`.

- [ ] **Step 8: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS. `PersistenceTests` (which checks the migrations against the
  model) must pass unedited.

- [ ] **Step 9: Commit** — `feat(email): add the mail tables, the mail limit and the queue`

### Task 4: Link tokens and the mail texts

**Files:**
- Create: `src/Auth.Server/Email/EmailTokens.cs`, `src/Auth.Server/Email/MailTexts.cs`,
  `src/Auth.Server/Email/MailComposer.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/EmailTokensTests.cs`,
  `tests/Auth.IntegrationTests/MailComposerTests.cs`

**Interfaces:**
- Consumes: `EmailToken`, `MailKind`, `AuthDbContext.EmailTokens` (Task 3),
  `MailSettings` (Task 2).
- Produces: `static class EmailTokens` with `ResetLifetime` (1 h),
  `VerificationLifetime` (24 h), `static TimeSpan LifetimeOf(MailKind kind)`,
  `static byte[] HashOf(string token)`,
  `static Task<string> IssueAsync(AuthDbContext db, Guid userId, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)`
  (one upsert on the row of that user and kind: the new hash and expiry replace
  the old ones; returns the token in clear: 43 base64url characters) and
  `static Task<Guid?> ConsumeAsync(AuthDbContext db, string token, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)`
  (one `DELETE … RETURNING`; the user id when a usable row was removed, else
  `null`). Both run in the **caller's** context and transaction.
- Produces: `sealed class ComposedMail` with `string To`, `string Subject`,
  `string TextBody`, `string HtmlBody` (`required init`; a class, so no generated
  `ToString` prints a body).
- Produces: `sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore)`
  and `static class MailTexts` with `static MailText For(string locale, MailKind kind)`.
  `Subject` and `Intro` hold `{0}` for the application name.
- Produces: `MailComposer` (singleton, constructor `(MailSettings settings)`) with
  `ComposedMail Compose(MailKind kind, string recipient, string token)`.

- [ ] **Step 1: Write the failing token tests** (`EmailTokensTests`):

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailTokensTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private DateTimeOffset Now => StorableTime.Now(Clock);

    private async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private Task<string> IssueAsync(Guid user, MailKind kind) =>
        InDbAsync(db => EmailTokens.IssueAsync(db, user, kind, Now, TestContext.Current.CancellationToken));

    private Task<Guid?> ConsumeAsync(string token, MailKind kind) =>
        InDbAsync(db => EmailTokens.ConsumeAsync(db, token, kind, Now, TestContext.Current.CancellationToken));

    private Task<List<EmailToken>> RowsAsync() =>
        InDbAsync(db => db.EmailTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Issued_token_is_43_url_safe_characters_and_only_its_hash_is_stored()   // criterion 15
    {
        var user = await Factory.SeedUserIdAsync();

        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(32, row.TokenHash.Length);
        Assert.Equal(user, row.UserId);
        Assert.Equal(Now + TimeSpan.FromHours(1), row.ExpiresAt);
    }

    [Fact]
    public async Task Token_works_once()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Equal(user, await ConsumeAsync(token, MailKind.PasswordReset));
        Assert.Null(await ConsumeAsync(token, MailKind.PasswordReset));
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Unknown_token_is_not_usable() =>
        Assert.Null(await ConsumeAsync("never-issued", MailKind.PasswordReset));

    [Fact]
    public async Task Token_of_the_other_kind_is_not_usable_and_is_left_alone()
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Null(await ConsumeAsync(token, MailKind.EmailVerification));
        Assert.Equal(user, await ConsumeAsync(token, MailKind.PasswordReset));
    }

    [Theory]
    [InlineData(MailKind.PasswordReset, 1)]
    [InlineData(MailKind.EmailVerification, 24)]
    public async Task Token_expires_after_its_lifetime(MailKind kind, int hours)   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var early = await IssueAsync(user, kind);
        Clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        Assert.Equal(user, await ConsumeAsync(early, kind));

        var late = await IssueAsync(user, kind);
        Clock.Advance(TimeSpan.FromHours(hours));
        Assert.Null(await ConsumeAsync(late, kind));
    }

    [Fact]
    public async Task Issuing_again_replaces_the_earlier_token_of_that_kind_only()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var firstReset = await IssueAsync(user, MailKind.PasswordReset);
        var verification = await IssueAsync(user, MailKind.EmailVerification);

        var secondReset = await IssueAsync(user, MailKind.PasswordReset);

        Assert.Equal(2, (await RowsAsync()).Count);
        Assert.Null(await ConsumeAsync(firstReset, MailKind.PasswordReset));
        Assert.Equal(user, await ConsumeAsync(secondReset, MailKind.PasswordReset));
        Assert.Equal(user, await ConsumeAsync(verification, MailKind.EmailVerification));
    }

    [Fact]
    public async Task Parallel_consumption_succeeds_exactly_once()   // criterion 6
    {
        var user = await Factory.SeedUserIdAsync();
        var token = await IssueAsync(user, MailKind.PasswordReset);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => ConsumeAsync(token, MailKind.PasswordReset))));

        Assert.Equal(1, results.Count(r => r == user));
        Assert.Equal(7, results.Count(r => r is null));
    }

    [Fact]
    public async Task Tokens_are_removed_with_their_user()
    {
        Guid id;
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "gone@example.com", Email = "gone@example.com" };
            Assert.True((await users.CreateAsync(user, "Short-Lived-1")).Succeeded);
            id = user.Id;
            await IssueAsync(id, MailKind.EmailVerification);
            Assert.True((await users.DeleteAsync(user)).Succeeded);
        }

        Assert.Empty(await RowsAsync());
    }
}
```

- [ ] **Step 2: Write the failing composer tests** (`MailComposerTests`, no host):

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class MailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string reset = "https://app.example.com/reset") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri(reset),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void Reset_mail_in_english()   // criterion 15
    {
        var mail = Composer().Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Equal("user@example.com", mail.To);
        Assert.Equal("Reset your speech-to-mail password", mail.Subject);
        Assert.Contains($"https://app.example.com/reset?token={Token}", mail.TextBody);
        Assert.Contains("speech-to-mail", mail.TextBody);
        Assert.Contains("valid for 1 hour", mail.TextBody);
        Assert.Contains("ignore this message", mail.TextBody);
        Assert.Contains($"href=\"https://app.example.com/reset?token={Token}\"", mail.HtmlBody);
        Assert.Contains("valid for 1 hour", mail.HtmlBody);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody);
    }

    [Fact]
    public void Verification_mail_in_polish()   // criterion 15
    {
        var mail = Composer("pl").Compose(MailKind.EmailVerification, "user@example.com", Token);

        Assert.Equal("Potwierdź adres e-mail w speech-to-mail", mail.Subject);
        Assert.Contains($"https://app.example.com/verify?token={Token}", mail.TextBody);
        Assert.Contains("ważny przez 24 godziny", mail.TextBody);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody);
        Assert.Contains("Potwierdź adres e-mail", mail.HtmlBody);      // letters stay letters, not entities
        Assert.Contains("ważny przez 24 godziny", mail.HtmlBody);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody);
    }

    [Theory]
    [InlineData("en", MailKind.PasswordReset)]
    [InlineData("en", MailKind.EmailVerification)]
    [InlineData("pl", MailKind.PasswordReset)]
    [InlineData("pl", MailKind.EmailVerification)]
    public void Every_language_and_kind_has_every_text(string locale, MailKind kind)
    {
        var text = MailTexts.For(locale, kind);

        Assert.All(
            new[] { text.Subject, text.Greeting, text.Intro, text.Button, text.Validity, text.LinkHint, text.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject);
        Assert.Contains("{0}", text.Intro);

        var mail = Composer(locale).Compose(kind, "user@example.com", Token);
        Assert.DoesNotContain("{0}", mail.Subject + mail.TextBody + mail.HtmlBody);
    }

    [Fact]
    public void Application_name_is_encoded_in_the_html_part_only()   // Review Focus 3
    {
        var mail = Composer(appName: "Tom & <Jerry>").Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Contains("Tom & <Jerry>", mail.TextBody);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody);
        Assert.Equal("Reset your Tom & <Jerry> password", mail.Subject);
    }

    [Fact]
    public void Link_keeps_a_query_string_the_frontend_url_already_has()   // Review Focus 3
    {
        var mail = Composer(reset: "https://app.example.com/account?view=reset")
            .Compose(MailKind.PasswordReset, "user@example.com", Token);

        Assert.Contains($"https://app.example.com/account?view=reset&token={Token}", mail.TextBody);
        Assert.Contains($"https://app.example.com/account?view=reset&amp;token={Token}", mail.HtmlBody);
    }

    [Fact]
    public void Composed_mail_does_not_print_its_content() =>
        Assert.DoesNotContain(Token, Composer().Compose(MailKind.PasswordReset, "user@example.com", Token).ToString());
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`EmailTokens`, `MailComposer`, `MailTexts` do not exist).

- [ ] **Step 4: Implement the tokens.** `src/Auth.Server/Email/EmailTokens.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// The link tokens of spec 0004: random values that carry nothing, stored as hashes, single-use and expiring
/// (Decision 4). Both operations run in the caller's context, so the caller's transaction decides whether they
/// happened: a reset that fails after consuming its token rolls the consumption back with everything else.
/// </summary>
public static class EmailTokens
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);

    public static TimeSpan LifetimeOf(MailKind kind) => kind switch
    {
        MailKind.PasswordReset => ResetLifetime,
        MailKind.EmailVerification => VerificationLifetime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of mail."),
    };

    public static byte[] HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Replaces the user's token of this kind by a new one and returns it in clear: 32 random bytes as 43
    /// base64url characters. The caller puts it into a mail and nowhere else. One statement on the row the table
    /// keeps per user and kind, so two callers at once still leave exactly one token, the later one's.
    /// </summary>
    public static async Task<string> IssueAsync(
        AuthDbContext db, Guid userId, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = HashOf(token);
        var kindValue = (short)kind;
        var expiresAt = now + LifetimeOf(kind);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "EmailTokens" ("UserId", "Kind", "TokenHash", "ExpiresAt")
            VALUES ({userId}, {kindValue}, {hash}, {expiresAt})
            ON CONFLICT ("UserId", "Kind") DO UPDATE SET "TokenHash" = EXCLUDED."TokenHash", "ExpiresAt" = EXCLUDED."ExpiresAt"
            """,
            cancellationToken);
        return token;
    }

    /// <summary>
    /// Uses a token up: removes its row if it is of this kind and not expired, and returns the user it belongs to;
    /// <see langword="null"/> for anything else. One statement, so of parallel calls with one token exactly one
    /// gets the user.
    /// </summary>
    public static async Task<Guid?> ConsumeAsync(
        AuthDbContext db, string token, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(token);

        var hash = HashOf(token);
        var kindValue = (short)kind;

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var removed = await db.EmailTokens
            .FromSql($"""
                DELETE FROM "EmailTokens"
                WHERE "TokenHash" = {hash} AND "Kind" = {kindValue} AND "ExpiresAt" > {now}
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0].UserId : null;
    }
}
```

- [ ] **Step 5: Implement the texts.** `src/Auth.Server/Email/MailTexts.cs`:

```csharp
using Auth.Infrastructure.Persistence;

namespace Auth.Server.Email;

/// <summary>The texts of one mail. <see cref="Subject"/> and <see cref="Intro"/> hold <c>{0}</c> for the application name.</summary>
public sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore);

/// <summary>
/// The mail texts, Polish and English. Plain tables rather than <c>.resx</c> satellites: the container image runs
/// without ICU, where a culture such as <c>pl</c> cannot be created, so resources could not be selected by culture.
/// </summary>
public static class MailTexts
{
    public static MailText For(string locale, MailKind kind) => (locale, kind) switch
    {
        ("en", MailKind.PasswordReset) => new MailText(
            Subject: "Reset your {0} password",
            Greeting: "Hello,",
            Intro: "We received a request to reset the password of your {0} account. Use the link below to choose a new password.",
            Button: "Choose a new password",
            Validity: "The link is valid for 1 hour and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("en", MailKind.EmailVerification) => new MailText(
            Subject: "Confirm your email address for {0}",
            Greeting: "Hello,",
            Intro: "Confirm that this email address belongs to your {0} account by using the link below.",
            Button: "Confirm email address",
            Validity: "The link is valid for 24 hours and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("pl", MailKind.PasswordReset) => new MailText(
            Subject: "Ustaw nowe hasło w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Otrzymaliśmy prośbę o zmianę hasła do Twojego konta w {0}. Użyj poniższego linku, aby ustawić nowe hasło.",
            Button: "Ustaw nowe hasło",
            Validity: "Link jest ważny przez 1 godzinę i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        ("pl", MailKind.EmailVerification) => new MailText(
            Subject: "Potwierdź adres e-mail w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Potwierdź, że ten adres e-mail należy do Twojego konta w {0}, używając poniższego linku.",
            Button: "Potwierdź adres e-mail",
            Validity: "Link jest ważny przez 24 godziny i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
    };
}
```

- [ ] **Step 6: Implement the composer.** `src/Auth.Server/Email/MailComposer.cs`:

```csharp
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Server.Email;

/// <summary>A mail ready to send. A class, not a record: nothing should print a body, which holds the link token.</summary>
public sealed class ComposedMail
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string TextBody { get; init; }

    public required string HtmlBody { get; init; }
}

/// <summary>
/// Builds the two mails of spec 0004 in the configured language, each as plain text and as HTML with the same
/// content. Only configuration and the token go into a mail; nothing a requester submitted does.
/// </summary>
public sealed class MailComposer(MailSettings settings)
{
    // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    public ComposedMail Compose(MailKind kind, string recipient, string token)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(token);

        var text = MailTexts.For(settings.Locale, kind);
        var target = kind == MailKind.PasswordReset ? settings.ResetPasswordUrl : settings.VerifyEmailUrl;
        var link = QueryHelpers.AddQueryString(target.AbsoluteUri, "token", token);
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join(
                "\r\n",
                text.Greeting, "", intro, "", link, "", text.Validity, text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="font-family: Arial, Helvetica, sans-serif; font-size: 16px; line-height: 1.5; color: #1f2933;">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p><a href="{Html.Encode(link)}" style="display: inline-block; padding: 12px 20px; background-color: #1f6feb; color: #ffffff; text-decoration: none; border-radius: 6px;">{Html.Encode(text.Button)}</a></p>
                <p>{Html.Encode(text.LinkHint)}<br><a href="{Html.Encode(link)}">{Html.Encode(link)}</a></p>
                <p>{Html.Encode(text.Validity)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }
}
```

  In `Program.cs`: `builder.Services.AddSingleton<MailComposer>();`.

- [ ] **Step 7: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  If the HTML encoder escapes a character the tests expect in clear (an
  apostrophe, a non-ASCII letter), report what it produced rather than changing
  the expectation.

- [ ] **Step 8: Commit** — `feat(email): add link tokens and the mail texts`

### Task 5: Sending through SMTP

**Files:**
- Create: `src/Auth.Server/Email/IMailTransport.cs`, `src/Auth.Server/Email/SmtpMailTransport.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/MailpitFixture.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/SmtpMailTransportTests.cs`

**Interfaces:**
- Consumes: `ComposedMail`, `MailComposer` (Task 4), `MailSettings`, `SmtpSettings`,
  `SmtpSecurity` (Task 2).
- Produces: `interface IMailTransport { Task SendAsync(ComposedMail mail, CancellationToken cancellationToken); }`
  — throws when the mail was not accepted by the server.
- Produces: `SmtpMailTransport` (singleton, constructor
  `(MailSettings settings, TimeProvider clock)`), registered as `IMailTransport`;
  `static readonly TimeSpan Timeout` = 10 seconds (one network operation) and
  `Deadline` = 20 seconds (a whole send). A goodbye that fails after the server
  accepted the mail does not make the send fail.
- Produces (tests): `MailpitFixture` (class fixture; one `axllent/mailpit:v1.31.3`
  container) with `string Host`, `int SmtpPort`,
  `Task<MailpitMessage> WaitForMailAsync(string to)`, `Task<int> CountAsync(string to)`;
  `sealed record MailpitMessage(string Subject, string Text, string Html, string FromName, string FromAddress, string Raw)`.

- [ ] **Step 1: Write the fixture.** `tests/Auth.IntegrationTests/Infrastructure/MailpitFixture.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Auth.IntegrationTests.Infrastructure;

public sealed record MailpitMessage(string Subject, string Text, string Html, string FromName, string FromAddress, string Raw);

/// <summary>
/// One Mailpit container (a mail catcher: SMTP in, HTTP API out) for a test class that needs a real SMTP server.
/// Tests keep apart by sending to an address of their own and searching for it.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private const int Smtp = 1025;
    private const int Api = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.31.3")
        .WithPortBinding(Smtp, assignRandomHostPort: true)
        .WithPortBinding(Api, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Api).ForPath("/readyz")))
        .Build();

    private HttpClient _api = null!;

    public string Host => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(Smtp);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _api = new HttpClient { BaseAddress = new Uri($"http://{Host}:{_container.GetMappedPublicPort(Api)}") };
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        await _container.DisposeAsync();
    }

    public async Task<int> CountAsync(string to)
    {
        var found = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
        return found.GetProperty("messages_count").GetInt32();
    }

    /// <summary>The one mail sent to <paramref name="to"/>, waiting up to 15 seconds for it to arrive.</summary>
    public async Task<MailpitMessage> WaitForMailAsync(string to)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var found = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
            if (found.GetProperty("messages_count").GetInt32() > 0)
            {
                var id = found.GetProperty("messages")[0].GetProperty("ID").GetString();
                var message = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}");
                var raw = await _api.GetStringAsync($"/api/v1/message/{id}/raw");
                var from = message.GetProperty("From");
                return new MailpitMessage(
                    message.GetProperty("Subject").GetString()!,
                    message.GetProperty("Text").GetString()!,
                    message.GetProperty("HTML").GetString()!,
                    from.GetProperty("Name").GetString()!,
                    from.GetProperty("Address").GetString()!,
                    raw);
            }

            Assert.True(DateTime.UtcNow < deadline, $"No mail for {to} arrived within 15 seconds.");
            await Task.Delay(100);
        }
    }
}
```

- [ ] **Step 2: Write the failing tests** (`SmtpMailTransportTests`). No auth host:
  the transport is built by hand against the container.

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SmtpMailTransportTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private MailSettings Settings(int? port = null) => new()
    {
        AppName = "speech-to-mail",
        Locale = "pl",
        ResetPasswordUrl = new Uri("https://app.example.com/reset"),
        VerifyEmailUrl = new Uri("https://app.example.com/verify"),
        FromAddress = "no-reply@mail.example.com",
        Smtp = new SmtpSettings { Host = mailpit.Host, Port = port ?? mailpit.SmtpPort, Security = SmtpSecurity.None },
    };

    private static string Recipient() => $"anna-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Mail_arrives_with_both_parts_and_diacritics_intact()   // criterion 15
    {
        var settings = Settings();
        var to = Recipient();
        var mail = new MailComposer(settings).Compose(MailKind.EmailVerification, to, Token);

        await new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken);

        var received = await mailpit.WaitForMailAsync(to);
        Assert.Equal("Potwierdź adres e-mail w speech-to-mail", received.Subject);
        Assert.Contains("Dzień dobry,", received.Text);
        Assert.Contains($"https://app.example.com/verify?token={Token}", received.Text);
        Assert.Contains("Jeśli to nie Ty, zignoruj tę wiadomość.", received.Text);
        Assert.Contains("Potwierdź adres e-mail", received.Html);
        Assert.Contains($"https://app.example.com/verify?token={Token}", received.Html);
        Assert.Equal("no-reply@mail.example.com", received.FromAddress);
        Assert.Equal("speech-to-mail", received.FromName);
        Assert.Contains("multipart/alternative", received.Raw);
    }

    [Fact]
    public async Task Message_id_is_made_from_the_sender_domain_not_the_machine_name()
    {
        var settings = Settings();
        var to = Recipient();
        var mail = new MailComposer(settings).Compose(MailKind.PasswordReset, to, Token);

        await new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken);

        var received = await mailpit.WaitForMailAsync(to);
        var messageId = received.Raw.Split("\r\n").Single(line => line.StartsWith("Message-Id:", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("@mail.example.com>", messageId);
        Assert.DoesNotContain(Environment.MachineName, received.Raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Server_that_does_not_answer_is_an_exception()
    {
        // Port 1 on the container host: nothing listens there.
        var settings = Settings(port: 1);
        var mail = new MailComposer(settings).Compose(MailKind.PasswordReset, Recipient(), Token);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SmtpMailTransport(settings, TimeProvider.System).SendAsync(mail, TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`SmtpMailTransport` does not exist).

- [ ] **Step 4: Implement.**

`src/Auth.Server/Email/IMailTransport.cs`:

```csharp
namespace Auth.Server.Email;

/// <summary>Hands a mail over to a mail server. Throws when the server did not accept it.</summary>
public interface IMailTransport
{
    Task SendAsync(ComposedMail mail, CancellationToken cancellationToken);
}
```

`src/Auth.Server/Email/SmtpMailTransport.cs`:

```csharp
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace Auth.Server.Email;

/// <summary>
/// Sends through the configured SMTP server with MailKit: one connection per mail, which is plenty for the two
/// mails this service sends. Any failure surfaces as an exception; the dispatcher decides about retries.
/// </summary>
public sealed class SmtpMailTransport(MailSettings settings, TimeProvider clock) : IMailTransport
{
    /// <summary>How long one network operation may take before the attempt counts as failed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a whole send may take. <see cref="Timeout"/> is per operation, and a send is about ten of them: a
    /// server that answers each one slowly must not hold the dispatcher, and the queue row it has locked, for minutes.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task SendAsync(ComposedMail mail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mail);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.AppName, settings.FromAddress));
        message.To.Add(new MailboxAddress(string.Empty, mail.To));
        message.Subject = mail.Subject;
        message.Date = clock.GetUtcNow();
        // MimeKit would otherwise build the id from this machine's host name, and MailKit would greet the server
        // with it (the server then writes it into a Received header). The machine's name is nobody's business.
        var senderDomain = settings.FromAddress[(settings.FromAddress.LastIndexOf('@') + 1)..];
        message.MessageId = MimeUtils.GenerateMessageId(senderDomain);
        message.Body = new BodyBuilder { TextBody = mail.TextBody, HtmlBody = mail.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds, LocalDomain = senderDomain };
        var security = settings.Smtp.Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.SslOnConnect,
        };
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, security, deadline.Token);
        if (settings.Smtp is { Username: { } username, Password: { } password })
        {
            await client.AuthenticateAsync(username, password, deadline.Token);
        }

        await client.SendAsync(message, deadline.Token);
        try
        {
            await client.DisconnectAsync(quit: true, deadline.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The server has accepted the mail. A goodbye that fails must not make the send count as failed,
            // or the mail would go out a second time.
        }
    }
}
```

  In `Program.cs`: `builder.Services.AddSingleton<IMailTransport, SmtpMailTransport>();`.

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  The first run pulls the Mailpit image if it is missing.

- [ ] **Step 6: Commit** — `feat(email): send mail through SMTP with MailKit`

### Task 6: The dispatcher

**Files:**
- Create: `src/Auth.Server/Email/MailDelivery.cs`, `src/Auth.Server/Email/MailDispatchSignal.cs`,
  `src/Auth.Server/Email/MailDispatcher.cs`, `src/Auth.Server/Email/MailDispatchService.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/CapturingMailTransport.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/CapturingLoggerProvider.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/MailTestBase.cs`
- Modify: `src/Auth.Server/Program.cs`,
  `tests/Auth.IntegrationTests/MailRequestStoreTests.cs` (base class → `MailTestBase`)
- Test: `tests/Auth.IntegrationTests/MailDispatcherTests.cs`,
  `tests/Auth.IntegrationTests/MailDispatchServiceTests.cs`

**Interfaces:**
- Consumes: `MailRequestStore.SubmitAsync`, `MailRequest`, `StorableTime` (Task 3);
  `EmailTokens.IssueAsync`, `MailComposer.Compose`, `ComposedMail` (Task 4);
  `IMailTransport` (Task 5).
- Produces: `static class MailDelivery` with `GiveUpAfter` (1 h), `PollInterval`
  (1 min), `MinimumWait` (1 s) and `static TimeSpan RetryDelay(int attempts)`
  (1 → 5 s, 2 → 30 s, 3 → 2 min, 4 and more → 10 min).
- Produces: `MailDispatchSignal` (singleton) with `void Notify()` and
  `Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)`.
- Produces: `MailDispatcher` (singleton) with
  `Task<int> DispatchDueAsync(CancellationToken cancellationToken)` (first removes
  in one statement the due rows that need no mail, then handles every other due
  row, and every row that is an hour old; returns how many rows in all) and
  `Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)`.
- Produces: `MailDispatchService` (`BackgroundService`).
- Produces (tests): `CapturingMailTransport` with `IReadOnlyList<ComposedMail> Sent`,
  `IReadOnlyList<ComposedMail> Attempted`, `int Attempts`, `bool Failing { get; set; }`,
  `TimeSpan Delay { get; set; }`; `CapturingLoggerProvider` with
  `string Text` and `IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries`;
  `MailTestBase : SessionTestBase` with `Mail`, `Logs`, `Task<int> DispatchAsync()`,
  `Task EnqueueAsync(MailKind kind, string email)`,
  `Task<ApplicationUser> CreateUserAsync(string email, bool confirmed, string password = MailTestBase.UserPassword)`,
  `Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)`,
  `static string TokenIn(ComposedMail mail)`; and `static class Poll` with
  `Task UntilAsync(Func<bool> condition, string what)`.

- [ ] **Step 1: Write the test infrastructure.**

`Infrastructure/CapturingMailTransport.cs`:

```csharp
using System.Collections.Concurrent;
using Auth.Server.Email;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the mail server: keeps what was sent and what was tried, fails every send while
/// <see cref="Failing"/> is set, and takes <see cref="Delay"/> of real time over each one.
/// </summary>
public sealed class CapturingMailTransport : IMailTransport
{
    private readonly ConcurrentQueue<ComposedMail> _sent = new();
    private readonly ConcurrentQueue<ComposedMail> _attempted = new();
    private int _attempts;
    private volatile bool _failing;

    public IReadOnlyList<ComposedMail> Sent => [.. _sent];

    /// <summary>Every mail a send was tried for, failed ones included, in order.</summary>
    public IReadOnlyList<ComposedMail> Attempted => [.. _attempted];

    /// <summary>Sends tried, failed ones included.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>How long a send takes, so that two dispatchers can be made to overlap.</summary>
    public TimeSpan Delay { get; set; }

    public bool Failing
    {
        get => _failing;
        set => _failing = value;
    }

    public async Task SendAsync(ComposedMail mail, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        _attempted.Enqueue(mail);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (_failing)
        {
            throw new IOException("The mail server is down (test).");
        }

        _sent.Enqueue(mail);
    }
}
```

`Infrastructure/CapturingLoggerProvider.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Keeps every log line the host writes, so a test can assert what is and is not logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => [.. _entries];

    public string Text => string.Join('\n', _entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
```

`Infrastructure/MailTestBase.cs`:

```csharp
using System.Text.RegularExpressions;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the mail tests: a host whose mails are captured instead of sent, whose log is kept, and whose dispatcher
/// does not run by itself — a test calls <see cref="DispatchAsync"/> when the queue should be handled, so nothing
/// races the assertions.
/// </summary>
public abstract partial class MailTestBase : SessionTestBase
{
    public const string UserPassword = "Another-Passw0rd";

    protected MailTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithoutHostedService<MailDispatchService>()
            .WithServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMailTransport>(Mail));
                services.AddSingleton<ILoggerProvider>(Logs);
            });
    }

    protected CapturingMailTransport Mail { get; } = new();

    protected CapturingLoggerProvider Logs { get; } = new();

    /// <summary>One pass of the dispatcher over the rows that are due now; returns how many it handled.</summary>
    protected Task<int> DispatchAsync() =>
        Factory.Services.GetRequiredService<MailDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken);

    protected async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>Puts a request on the queue the way the endpoints do, and asserts the limit let it through.</summary>
    protected async Task EnqueueAsync(MailKind kind, string email)
    {
        using var scope = Factory.Services.CreateScope();
        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        var decision = await Factory.Services.GetRequiredService<MailRequestStore>()
            .SubmitAsync(kind, normalized, TestContext.Current.CancellationToken);
        Assert.True(decision.Allowed, $"The mail limit refused the request; wait {decision.RetryAfter} on the test clock.");
    }

    protected async Task<ApplicationUser> CreateUserAsync(string email, bool confirmed, string password = UserPassword)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = confirmed };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
        return user;
    }

    /// <summary>The link token of a mail, read from its plain-text part.</summary>
    public static string TokenIn(ComposedMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var match = TokenInLink().Match(mail.TextBody);
        Assert.True(match.Success, "The mail holds no link with a token.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("[?&]token=([A-Za-z0-9_-]{43})(?![A-Za-z0-9_-])")]
    private static partial Regex TokenInLink();
}

public static class Poll
{
    /// <summary>Waits in real time, up to 10 seconds, for something a background service does.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}.");
            await Task.Delay(50);
        }
    }
}
```

  Then change the base class of `MailRequestStoreTests` (Task 3) from
  `SessionTestBase` to `MailTestBase`: from this task on a host runs the
  dispatcher, which would take the queue rows those tests count.

- [ ] **Step 2: Write the failing dispatcher tests** (`MailDispatcherTests`). The
  clock is frozen; a test advances it. Two requests for one address and kind need
  more than 60 seconds between them (the mail limit).

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class MailDispatcherTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private Task<List<EmailToken>> TokensAsync() =>
        InDbAsync(db => db.EmailTokens.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Request_for_an_account_sends_one_mail_and_stores_only_the_hash_of_its_token()   // criteria 1, 15
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Factory.SeedEmail, mail.To);
        Assert.Contains(AuthAppFactory.DefaultResetUrl + "?token=", mail.TextBody);
        var row = Assert.Single(await TokensAsync());
        Assert.Equal(EmailTokens.HashOf(TokenIn(mail)), row.TokenHash);
        Assert.Equal(MailKind.PasswordReset, row.Kind);
        Assert.Equal(StorableTime.Now(Clock) + EmailTokens.ResetLifetime, row.ExpiresAt);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_for_an_address_without_an_account_is_dropped_without_a_mail()   // criterion 2
    {
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await TokensAsync());
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Verification_mail_goes_to_an_unconfirmed_account_only()   // criterion 11
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.EmailVerification, Factory.SeedEmail);   // confirmed
        await EnqueueAsync(MailKind.EmailVerification, "new@example.com");
        await EnqueueAsync(MailKind.EmailVerification, "nobody@example.com");

        Assert.Equal(3, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("new@example.com", mail.To);
        Assert.Contains(AuthAppFactory.DefaultVerifyUrl + "?token=", mail.TextBody);
        Assert.Equal(MailKind.EmailVerification, Assert.Single(await TokensAsync()).Kind);
    }

    [Fact]
    public async Task Reset_mail_goes_to_an_unconfirmed_account_too()
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.PasswordReset, "new@example.com");

        await DispatchAsync();

        Assert.Equal("new@example.com", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Recipient_is_the_stored_address_not_the_submitted_spelling()
    {
        await CreateUserAsync("Anna.Nowak@Example.com", confirmed: true);
        await EnqueueAsync(MailKind.PasswordReset, "ANNA.NOWAK@example.COM");

        await DispatchAsync();

        Assert.Equal("Anna.Nowak@Example.com", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Newer_mail_replaces_the_earlier_token()   // criterion 6
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        Clock.Advance(PastTheLimit);
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Assert.Equal(2, Mail.Sent.Count);
        Assert.NotEqual(TokenIn(Mail.Sent[0]), TokenIn(Mail.Sent[1]));
        Assert.Equal(EmailTokens.HashOf(TokenIn(Mail.Sent[1])), Assert.Single(await TokensAsync()).TokenHash);
    }

    [Fact]
    public async Task Failed_send_is_retried_on_the_schedule_and_sent_once()   // criterion 14, Review Focus 4
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        TimeSpan[] delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)];
        for (var i = 0; i < delays.Length; i++)
        {
            Assert.Equal(1, await DispatchAsync());
            var row = Assert.Single(await QueueAsync());
            Assert.Equal(i + 1, row.Attempts);
            Assert.Equal(StorableTime.Now(Clock) + delays[i], row.NextAttemptAt);

            Assert.Equal(0, await DispatchAsync());                    // not due yet
            Clock.Advance(delays[i] - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await DispatchAsync());                    // still not due
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Empty(Mail.Sent);
        Assert.Empty(await TokensAsync());                             // a failed attempt leaves no token behind
        // Every attempt composed a mail of its own, with a token of its own.
        Assert.Equal(delays.Length, Mail.Attempted.Select(TokenIn).Distinct().Count());

        Mail.Failing = false;
        Assert.Equal(1, await DispatchAsync());
        Assert.Equal(0, await DispatchAsync());

        Assert.Single(Mail.Sent);
        Assert.Equal(delays.Length + 1, Mail.Attempts);
        Assert.Empty(await QueueAsync());
        Assert.Single(await TokensAsync());
    }

    [Fact]
    public async Task Requests_for_addresses_without_an_account_do_not_hold_up_a_real_mail()
    {
        // Anyone can queue these. One statement clears them, however many there are, before a mail is composed.
        for (var i = 0; i < 40; i++)
        {
            await EnqueueAsync(MailKind.PasswordReset, $"nobody-{i}@example.com");
        }

        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Assert.Equal(41, await DispatchAsync());

        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        Assert.Empty(await QueueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_dispatchers_at_once_try_every_request_exactly_once(bool failing)
    {
        string[] addresses = ["a@example.com", "b@example.com", "c@example.com", "d@example.com"];
        foreach (var address in addresses)
        {
            await CreateUserAsync(address, confirmed: true);
            await EnqueueAsync(MailKind.PasswordReset, address);
        }

        // Slow enough that the two passes overlap on every row.
        Mail.Delay = TimeSpan.FromMilliseconds(100);
        Mail.Failing = failing;

        var handled = await Task.WhenAll(Task.Run(DispatchAsync), Task.Run(DispatchAsync));

        Assert.Equal(addresses.Length, handled.Sum());
        Assert.Equal(addresses.Order(), Mail.Attempted.Select(m => m.To).Order());
        Assert.Equal(failing ? 0 : addresses.Length, Mail.Sent.Count);
        var left = await QueueAsync();
        Assert.Equal(failing ? addresses.Length : 0, left.Count);
        Assert.All(left, row => Assert.Equal(1, row.Attempts));
    }

    [Fact]
    public async Task Failed_send_leaves_the_earlier_link_in_force()   // Review Focus 4
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        var earlier = EmailTokens.HashOf(TokenIn(Assert.Single(Mail.Sent)));

        Clock.Advance(PastTheLimit);
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Assert.Equal(earlier, Assert.Single(await TokensAsync()).TokenHash);
        Assert.Equal(1, Assert.Single(await QueueAsync()).Attempts);
    }

    [Fact]
    public async Task Request_that_could_not_be_delivered_within_an_hour_is_dropped_and_logged()   // criterion 14
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Clock.Advance(MailDelivery.GiveUpAfter);
        Mail.Failing = false;
        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Error && e.Category.EndsWith(nameof(MailDispatcher), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_just_under_an_hour_old_is_still_sent()
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);

        Clock.Advance(MailDelivery.GiveUpAfter - TimeSpan.FromSeconds(1));
        await DispatchAsync();

        Assert.Single(Mail.Sent);
    }

    [Fact]
    public async Task Request_recorded_before_a_restart_is_sent_after_it()   // criterion 14
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var before = new AuthAppFactory(Postgres, Keys, database).WithClock(Clock).WithoutHostedService<MailDispatchService>())
        {
            // Reading Services starts the host, which migrates the database and seeds the user.
            var decision = await before.Services.GetRequiredService<MailRequestStore>()
                .SubmitAsync(MailKind.PasswordReset, before.SeedEmail.ToUpperInvariant(), TestContext.Current.CancellationToken);
            Assert.True(decision.Allowed);
        }

        var mail = new CapturingMailTransport();
        await using var after = new AuthAppFactory(Postgres, Keys, database).WithClock(Clock)
            .WithoutHostedService<MailDispatchService>()
            .WithServices(services => services.Replace(ServiceDescriptor.Singleton<IMailTransport>(mail)));

        Assert.Equal(1, await after.Services.GetRequiredService<MailDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(after.SeedEmail, Assert.Single(mail.Sent).To);
    }

    [Fact]
    public async Task No_token_and_no_mail_body_reach_the_log()   // criterion 15
    {
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();
        var token = TokenIn(Assert.Single(Mail.Sent));

        Clock.Advance(PastTheLimit);
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();                                         // a failed send is logged

        Assert.Contains(Logs.Entries, e => e.Category.EndsWith(nameof(MailDispatcher), StringComparison.Ordinal));
        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain("token=", Logs.Text);
        Assert.DoesNotContain(Factory.SeedEmail, Logs.Text.Replace("Seeded development user " + Factory.SeedEmail, "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
}
```

  The seed email is ASCII, so `ToUpperInvariant` is its normalised form.

- [ ] **Step 3: Write the failing service tests** (`MailDispatchServiceTests`): a
  host **with** the dispatch service, so they prove the loop. They wait in real
  time for a background pass.

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class MailDispatchServiceTests : SessionTestBase
{
    private readonly CapturingMailTransport _mail = new();

    public MailDispatchServiceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.Replace(ServiceDescriptor.Singleton<IMailTransport>(_mail)));
    }

    private async Task RequestResetAsync()
    {
        var decision = await Factory.Services.GetRequiredService<MailRequestStore>()
            .SubmitAsync(MailKind.PasswordReset, Factory.SeedEmail.ToUpperInvariant(), TestContext.Current.CancellationToken);
        Assert.True(decision.Allowed);
        Factory.Services.GetRequiredService<MailDispatchSignal>().Notify();
    }

    [Fact]
    public async Task Queued_request_is_sent_without_anyone_driving_the_dispatcher()
    {
        await RequestResetAsync();

        await Poll.UntilAsync(() => _mail.Sent.Count == 1, "the mail to be sent by the background service");
    }

    [Fact]
    public async Task Failed_send_is_retried_once_its_delay_has_passed_on_the_clock()   // criterion 14
    {
        _mail.Failing = true;
        await RequestResetAsync();
        await Poll.UntilAsync(() => _mail.Attempts == 1, "the first, failing attempt");

        _mail.Failing = false;

        // The loop may not have started its wait yet: keep moving the clock until the retry has happened. At most
        // 200 steps of 5 s, well under the hour after which the request would be dropped.
        await Poll.UntilAsync(
            () =>
            {
                Clock.Advance(TimeSpan.FromSeconds(5));
                return _mail.Sent.Count == 1;
            },
            "the retry after the delay");
    }

    [Fact]
    public async Task Request_that_arrives_while_the_loop_waits_wakes_it()
    {
        await RequestResetAsync();
        await Poll.UntilAsync(() => _mail.Sent.Count == 1, "the first mail");

        // The queue is empty now and the loop waits a minute of a clock that does not move: only the signal can wake it.
        Clock.Advance(TimeSpan.FromSeconds(61));   // past the mail limit; fires the wait once, which finds nothing
        await RequestResetAsync();

        await Poll.UntilAsync(() => _mail.Sent.Count == 2, "the second mail");
    }
}
```

- [ ] **Step 4: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`MailDispatcher`, `MailDispatchService`,
  `MailDispatchSignal`, `MailDelivery` do not exist).

- [ ] **Step 5: Implement the schedule and the signal.**

`src/Auth.Server/Email/MailDelivery.cs`:

```csharp
namespace Auth.Server.Email;

/// <summary>When mail is retried and when it is given up. Constants on purpose (spec 0004, Decision 15).</summary>
public static class MailDelivery
{
    /// <summary>A request that could not be delivered this long after it was made is dropped.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);

    /// <summary>The dispatcher looks at the queue at least this often, even when nothing wakes it.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The dispatcher never waits less than this between two passes, so a row it cannot take does not spin it.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <summary>How long after failed attempt number <paramref name="attempts"/> the next one is due.</summary>
    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10),
    };
}
```

`src/Auth.Server/Email/MailDispatchSignal.cs`:

```csharp
using System.Threading.Channels;

namespace Auth.Server.Email;

/// <summary>
/// Lets a request that has just queued a mail wake the dispatcher, so the mail leaves within seconds rather than
/// at the next poll. Notifications that arrive while the dispatcher is busy collapse into one.
/// </summary>
public sealed class MailDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Returns when notified, or after <paramref name="timeout"/> on <paramref name="clock"/>, whichever is first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, clock, linked.Token);
        var notified = _channel.Reader.WaitToReadAsync(linked.Token).AsTask();
        await Task.WhenAny(delay, notified);
        await linked.CancelAsync();

        while (_channel.Reader.TryRead(out _))
        {
            // Drain: whatever was signalled is about to be handled by the pass that follows.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
```

- [ ] **Step 6: Implement the dispatcher.** `src/Auth.Server/Email/MailDispatcher.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Handles the mail queue (spec 0004 → Delivery). A pass first removes, in one statement, the due rows that will
/// never become a mail — no account, or nothing to confirm — so that requests for addresses without an account,
/// which anyone can make, cannot hold up the mails that matter. Then, for each remaining due row, in one transaction
/// with the row locked: issue a token, compose the mail and send it. The token is committed only once the server
/// has accepted the mail, so a failed attempt leaves the earlier link in force and no clear token is ever stored
/// (Decision 13).
/// </summary>
public sealed partial class MailDispatcher(
    IServiceScopeFactory scopes, TimeProvider clock, MailComposer composer, IMailTransport transport, ILogger<MailDispatcher> logger)
{
    /// <summary>Handles every row that is due now. Returns how many rows it handled.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var handled = await DropUnmailableAsync(cancellationToken);
        while (await DispatchOneAsync(cancellationToken))
        {
            handled++;
        }

        return handled;
    }

    /// <summary>When the next row is due; <see langword="null"/> for an empty queue.</summary>
    public async Task<DateTimeOffset?> NextDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.MailRequests.MinAsync(r => (DateTimeOffset?)r.NextAttemptAt, cancellationToken);
    }

    /// <summary>
    /// Removes the due rows whose address has no account, or (for a verification) whose account is confirmed
    /// already. Those requests were answered with 202 like any other, and end here.
    /// </summary>
    private async Task<int> DropUnmailableAsync(CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var passwordReset = (short)MailKind.PasswordReset;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "MailRequests" WHERE "Id" IN (
                SELECT r."Id" FROM "MailRequests" r
                WHERE r."NextAttemptAt" <= {now}
                  AND NOT EXISTS (
                      SELECT 1 FROM "AspNetUsers" u
                      WHERE u."NormalizedEmail" = r."NormalizedEmail"
                        AND u."Email" IS NOT NULL
                        AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))
                FOR UPDATE OF r SKIP LOCKED)
            """,
            cancellationToken);
    }

    private async Task<bool> DispatchOneAsync(CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var givenUp = now - MailDelivery.GiveUpAfter;

        // A scope per row: nothing tracked for one row can leak into the next.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // A row is taken when it is due, or as soon as it is an hour old (to be dropped). SKIP LOCKED: a second
        // instance takes another row instead of waiting for this one.
        var due = await db.MailRequests
            .FromSql($"""
                SELECT * FROM "MailRequests"
                WHERE "NextAttemptAt" <= {now} OR "RequestedAt" <= {givenUp}
                ORDER BY "NextAttemptAt", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return false;
        }

        var request = due[0];
        if (request.RequestedAt <= givenUp)
        {
            LogGaveUp(logger, request.Id, request.Kind, request.Attempts);
            await RemoveAsync(db, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var user = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == request.NormalizedEmail, cancellationToken);
        if (user?.Email is null || (request.Kind == MailKind.EmailVerification && user.EmailConfirmed))
        {
            // The account went away, or was confirmed, after the pass began.
            await RemoveAsync(db, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        // A failed send goes back to here: the new token is undone and the earlier link stays in force, while the
        // row stays locked until its next attempt has been written. Without the lock a second instance could take
        // the row in between and send it again.
        const string BeforeToken = "before_token";
        await transaction.CreateSavepointAsync(BeforeToken, cancellationToken);

        var token = await EmailTokens.IssueAsync(db, user.Id, request.Kind, now, cancellationToken);
        var mail = composer.Compose(request.Kind, user.Email, token);
        try
        {
            await transport.SendAsync(mail, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackToSavepointAsync(BeforeToken, CancellationToken.None);
            var attempts = request.Attempts + 1;
            var next = now + MailDelivery.RetryDelay(attempts);
            await db.MailRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.Attempts, attempts).SetProperty(r => r.NextAttemptAt, next),
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            LogSendFailed(logger, request.Id, request.Kind, attempts, exception.GetType().Name);
            return true;
        }

        await RemoveAsync(db, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task RemoveAsync(AuthDbContext db, MailRequest request, CancellationToken cancellationToken)
    {
        db.MailRequests.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The type name of the failure only: an SMTP error text can quote the recipient.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending mail request {RequestId} ({Kind}) failed on attempt {Attempts}: {Failure}. It will be retried.")]
    private static partial void LogSendFailed(ILogger logger, long requestId, MailKind kind, int attempts, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mail request {RequestId} ({Kind}) could not be delivered within the time limit after {Attempts} failed attempts and was dropped.")]
    private static partial void LogGaveUp(ILogger logger, long requestId, MailKind kind, int attempts);
}
```

- [ ] **Step 7: Implement the service.** `src/Auth.Server/Email/MailDispatchService.cs`:

```csharp
namespace Auth.Server.Email;

/// <summary>
/// Runs <see cref="MailDispatcher"/>: a pass at host start, then one whenever a request signals, a retry falls due,
/// or <see cref="MailDelivery.PollInterval"/> has passed.
/// </summary>
public sealed partial class MailDispatchService(
    MailDispatcher dispatcher, MailDispatchSignal signal, TimeProvider clock, ILogger<MailDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var wait = await PassAsync(stoppingToken);
                await signal.WaitAsync(wait, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: end the loop normally rather than as a cancelled task, which the host would count as a failure.
        }
    }

    /// <summary>One pass; returns how long to wait before the next one unless a signal comes first.</summary>
    private async Task<TimeSpan> PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await dispatcher.DispatchDueAsync(stoppingToken);
            if (await dispatcher.NextDueAsync(stoppingToken) is { } due)
            {
                var until = due - clock.GetUtcNow();
                return until < MailDelivery.MinimumWait ? MailDelivery.MinimumWait
                    : until < MailDelivery.PollInterval ? until
                    : MailDelivery.PollInterval;
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped in the middle of a pass. Whatever the store made of the cancellation, it is not a failure.
        }
        catch (Exception exception)
        {
            // A failed pass must not take the auth service down. Try again at the next poll.
            LogFailed(logger, exception.GetType().Name);
        }

        return MailDelivery.PollInterval;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A mail dispatch pass failed ({Failure}); it will run again at the next poll.")]
    private static partial void LogFailed(ILogger logger, string failure);
}
```

  In `Program.cs`, with the other registrations:

```csharp
builder.Services.AddSingleton<MailDispatchSignal>();
builder.Services.AddSingleton<MailDispatcher>();
builder.Services.AddHostedService<MailDispatchService>();
```

- [ ] **Step 8: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS for the whole suite: every existing test host now runs the
  dispatch loop on an empty queue. If an existing test that counts rows or
  statements starts failing because of a background pass, report it; the remedy
  is `Factory.WithoutHostedService<MailDispatchService>()` in **that** test class,
  which is an edit to an existing file and needs the orchestrator's ruling.
  Things to check and report: that after `RollbackToSavepointAsync` the
  `ExecuteUpdateAsync` and the commit run in the same transaction (the row stays
  locked until its next attempt is written); that the two raw statements (the
  set-based `DELETE` and `SELECT … FOR UPDATE SKIP LOCKED`) are sent as written.

- [ ] **Step 9: Commit** — `feat(email): dispatch the mail queue in the background`

### Task 7: `POST /auth/password/forgot` and `POST /auth/email/verify/request`

**Files:**
- Create: `src/Auth.Server/Account/AccountResults.cs`, `src/Auth.Server/Account/MailRequestEndpoint.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/AccountApi.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/MailRequestEndpointTests.cs`,
  `tests/Auth.IntegrationTests/MailThroughSmtpTests.cs`

**Interfaces:**
- Consumes: `JsonObjectBody.ReadStringsAsync`, `EmailInput.TryNormalize`,
  `EmailInput.MaxLength` (Task 1); `MailRequestStore.SubmitAsync` (Task 3);
  `MailDispatchSignal.Notify` (Task 6); `TooManyAttemptsResult` (existing);
  `MailTestBase`, `MailpitFixture` (Tasks 5–6).
- Produces: `static class AccountResults` with `IResult Accepted()` (202, empty),
  `IResult Done()` (204, empty), `IResult InvalidRequest()`, `IResult InvalidToken()`,
  `IResult WeakPassword(IReadOnlyList<string> rules)`, `IResult EmailNotVerified()`
  (403) — each with `Cache-Control: no-store` and `Pragma: no-cache`.
- Produces: `static class MailRequestEndpoint` with
  `const string ForgotPasswordPath = "/auth/password/forgot"`,
  `const string VerifyEmailRequestPath = "/auth/email/verify/request"` and
  `static Task<IResult> HandleAsync(MailKind kind, HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)`.
- Produces (tests): `static class AccountApi` with the four paths
  (`ForgotPath`, `VerifyRequestPath`, `ResetPath`, `VerifyPath`), the bodies
  `InvalidRequest` and `InvalidToken`, `Forgot`, `RequestVerification`, `Reset`,
  `Verify`, `PostRaw`,
  `Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status)`,
  `Task AssertErrorAsync(HttpResponseMessage response, string expectedBody)` and
  `void AssertNeverStoredAndNoCookie(HttpResponseMessage response)`.

- [ ] **Step 1: Write the test helper.** `Infrastructure/AccountApi.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the four account endpoints of spec 0004 over HTTP and asserts their response contracts.</summary>
public static class AccountApi
{
    public const string ForgotPath = "/auth/password/forgot";
    public const string VerifyRequestPath = "/auth/email/verify/request";
    public const string ResetPath = "/auth/password/reset";
    public const string VerifyPath = "/auth/email/verify";

    public const string InvalidRequest = """{"error":"invalid_request"}""";
    public const string InvalidToken = """{"error":"invalid_token"}""";

    public static Task<HttpResponseMessage> Forgot(HttpClient client, string email) =>
        client.PostAsJsonAsync(ForgotPath, new { email });

    public static Task<HttpResponseMessage> RequestVerification(HttpClient client, string email) =>
        client.PostAsJsonAsync(VerifyRequestPath, new { email });

    public static Task<HttpResponseMessage> Reset(HttpClient client, string token, string newPassword) =>
        client.PostAsJsonAsync(ResetPath, new { token, new_password = newPassword });

    public static Task<HttpResponseMessage> Verify(HttpClient client, string token) =>
        client.PostAsJsonAsync(VerifyPath, new { token });

    public static Task<HttpResponseMessage> PostRaw(HttpClient client, string path, string body, string contentType = "application/json") =>
        client.PostAsync(path, new StringContent(body, Encoding.UTF8, contentType));

    /// <summary>A 202 or 204 of the contract: no body, never cached, no cookie.</summary>
    public static async Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("", raw);
        AssertNeverStoredAndNoCookie(response);
    }

    /// <summary>A 400 of the contract with exactly this body.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, string expectedBody)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(expectedBody, raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertNeverStoredAndNoCookie(response);
    }

    public static void AssertNeverStoredAndNoCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "An account endpoint must not write a cookie.");
    }
}
```

- [ ] **Step 2: Write the failing tests** (`MailRequestEndpointTests`). The clock is
  frozen: two requests for one address and kind are 0 seconds apart unless the
  test advances it.

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class MailRequestEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Unknown = "nobody@example.com";

    private static readonly string[] MalformedBodies =
    [
        "{}",
        """{"email":""}""",
        """{"email":"   "}""",
        """{"email":123}""",
        """{"email":null}""",
        """["a@example.com"]""",
        "not json",
        "",
        """{"email":"a@example.com",""",
        """{"email":"a\u0000b@example.com"}""",              // NUL
        """{"email":"a@example.com\n"}""",                   // control character
        """{"email":"a\uFFFEb@example.com"}""",              // the noncharacter of escalation E1
        """{"email":"a\uD800b@example.com"}""",              // unpaired surrogate escape
        $$"""{"email":"{{new string('a', 243)}}@example.com"}""",   // 255 characters
    ];

    private Task<HttpResponseMessage> Request(MailKind kind, string email) =>
        kind == MailKind.PasswordReset ? AccountApi.Forgot(Client, email) : AccountApi.RequestVerification(Client, email);

    private Task<int> QueueCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Fact]
    public async Task Forgot_for_an_account_is_accepted_and_a_reset_mail_follows()   // criterion 1
    {
        using var response = await AccountApi.Forgot(Client, Factory.SeedEmail);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Empty(Mail.Sent);                               // nothing is sent inside the request
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Factory.SeedEmail, mail.To);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultResetUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.TextBody);
    }

    [Fact]
    public async Task Address_without_an_account_gets_the_same_answer_the_same_rows_and_no_mail()   // criterion 2
    {
        using var known = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var unknown = await AccountApi.Forgot(Client, Unknown);

        await AccountApi.AssertEmptyAsync(unknown, HttpStatusCode.Accepted);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));

        // The request did the same work for both: one limit row and one queue row each, alike but for the address.
        var rows = await InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            (rows[0].Kind, rows[0].Attempts, rows[0].RequestedAt, rows[0].NextAttemptAt),
            (rows[1].Kind, rows[1].Attempts, rows[1].RequestedAt, rows[1].NextAttemptAt));
        Assert.Equal(2, await LimitCountAsync());

        await DispatchAsync();
        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData(MailKind.PasswordReset)]
    [InlineData(MailKind.EmailVerification)]
    public async Task Request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts(MailKind kind)   // criterion 2
    {
        var mark = Logs.Entries.Count;
        using var known = await Request(kind, Factory.SeedEmail);
        var forKnown = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await Request(kind, Unknown);
        var forUnknown = StatementsSince(mark);

        // A pruning pass of the host may log a DELETE of its own in between; what the request runs is not one.
        static List<string> OfTheRequest(List<string> statements) =>
            [.. statements.Where(sql => sql.Contains("MailRequest", StringComparison.Ordinal) && !sql.StartsWith("DELETE", StringComparison.Ordinal))];

        Assert.NotEmpty(OfTheRequest(forKnown));
        Assert.Equal(OfTheRequest(forKnown), OfTheRequest(forUnknown));
        Assert.DoesNotContain(forKnown.Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MailKind.PasswordReset)]
    [InlineData(MailKind.EmailVerification)]
    public async Task Second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait(MailKind kind)   // criterion 9
    {
        using var first = await Request(kind, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await Request(kind, Factory.SeedEmail);
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var third = await Request(kind, Factory.SeedEmail);
        Assert.Equal(40, await LockoutApi.AssertLockedAsync(third));

        Clock.Advance(TimeSpan.FromSeconds(40));
        using var fourth = await Request(kind, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(fourth, HttpStatusCode.Accepted);
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task Sixth_request_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 9
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await AccountApi.Forgot(Client, Factory.SeedEmail);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(TimeSpan.FromSeconds(61));
        }

        using var sixth = await AccountApi.Forgot(Client, Factory.SeedEmail);
        Assert.Equal(3600 - 305, await LockoutApi.AssertLockedAsync(sixth));

        Clock.Advance(TimeSpan.FromSeconds(3600 - 305));
        using var later = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(later, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Limit_answers_the_same_for_an_address_without_an_account()   // criterion 10
    {
        using var knownFirst = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var known = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var unknownFirst = await AccountApi.Forgot(Client, Unknown);
        using var unknown = await AccountApi.Forgot(Client, Unknown);

        Assert.Equal(await LockoutApi.AssertLockedAsync(known), await LockoutApi.AssertLockedAsync(unknown));
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Kinds_do_not_share_a_limit()   // criterion 9
    {
        using var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail);
        using var verification = await AccountApi.RequestVerification(Client, Factory.SeedEmail);
        using var forgotAgain = await AccountApi.Forgot(Client, Factory.SeedEmail);

        await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(verification, HttpStatusCode.Accepted);
        await LockoutApi.AssertLockedAsync(forgotAgain);
    }

    [Fact]
    public async Task Spelling_variants_of_one_address_share_the_limit_and_reach_the_account()   // Review Focus 2
    {
        using var upper = await AccountApi.Forgot(Client, Factory.SeedEmail.ToUpperInvariant());
        using var mixed = await AccountApi.Forgot(Client, "User@Example.Com");

        await AccountApi.AssertEmptyAsync(upper, HttpStatusCode.Accepted);
        await LockoutApi.AssertLockedAsync(mixed);
        await DispatchAsync();
        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Verification_mail_is_sent_only_to_an_unconfirmed_account()   // criterion 11
    {
        await CreateUserAsync("new@example.com", confirmed: false);

        using var unconfirmed = await AccountApi.RequestVerification(Client, "new@example.com");
        using var confirmed = await AccountApi.RequestVerification(Client, Factory.SeedEmail);
        using var unknown = await AccountApi.RequestVerification(Client, Unknown);

        foreach (var response in new[] { unconfirmed, confirmed, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(unconfirmed), LoginApi.HeaderNames(response));
        }

        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("new@example.com", mail.To);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultVerifyUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.TextBody);
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Malformed_request_is_a_400_and_is_not_counted(string path)   // criterion 16
    {
        // One host for all of them: a host per body would start a database per body.
        foreach (var body in MalformedBodies)
        {
            using var response = await AccountApi.PostRaw(Client, path, body);

            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await LimitCountAsync());
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Body_that_is_not_json_or_too_large_is_a_400(string path)
    {
        using var text = await AccountApi.PostRaw(Client, path, """{"email":"a@example.com"}""", "text/plain");
        using var large = await AccountApi.PostRaw(Client, path, $$"""{"email":"a@example.com","pad":"{{new string('x', 9000)}}"}""");

        await AccountApi.AssertErrorAsync(text, AccountApi.InvalidRequest);
        await AccountApi.AssertErrorAsync(large, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task Address_of_254_characters_is_accepted()
    {
        using var response = await AccountApi.Forgot(Client, new string('a', 242) + "@example.com");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData(AccountApi.ForgotPath)]
    [InlineData(AccountApi.VerifyRequestPath)]
    public async Task Only_post_is_allowed(string path)
    {
        using var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
```

  In `MalformedBodies` the three `\u…` sequences are **JSON escapes inside raw
  string literals**: the file must hold a backslash, `u` and four hex digits, not
  the character itself.

`tests/Auth.IntegrationTests/MailThroughSmtpTests.cs` — the whole path in one
host, nothing replaced: request → queue → background dispatcher → SMTP → mail.

```csharp
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Seeding;

namespace Auth.IntegrationTests;

public sealed class MailThroughSmtpTests(PostgresFixture postgres, KeyMaterialFixture keys, MailpitFixture mailpit)
    : IClassFixture<MailpitFixture>
{
    private AuthAppFactory Host(string seed) =>
        new AuthAppFactory(postgres, keys)
            .WithSetting(DevUserSeeder.EmailKey, seed)
            .WithSetting(MailSettingsLoader.LocaleKey, "pl")
            .WithSetting(MailSettingsLoader.SmtpHostKey, mailpit.Host)
            .WithSetting(MailSettingsLoader.SmtpPortKey, mailpit.SmtpPort.ToString(CultureInfo.InvariantCulture))
            .WithSetting(MailSettingsLoader.SmtpSecurityKey, "none");

    [Fact]
    public async Task Forgot_request_ends_as_a_mail_on_the_mail_server()   // criteria 1, 15
    {
        // An address of its own: the catcher is shared with the other SMTP tests.
        var seed = $"anna-{Guid.NewGuid():N}@example.com";
        await using var factory = Host(seed);
        using var client = factory.CreateClient();

        using var response = await AccountApi.Forgot(client, seed);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var mail = await mailpit.WaitForMailAsync(seed);
        Assert.Equal("Ustaw nowe hasło w " + AuthAppFactory.DefaultAppName, mail.Subject);
        Assert.Matches(Regex.Escape(AuthAppFactory.DefaultResetUrl) + @"\?token=[A-Za-z0-9_-]{43}\s", mail.Text);
        Assert.Contains("Ustaw nowe hasło", mail.Html);
        Assert.Equal(AuthAppFactory.DefaultFrom, mail.FromAddress);
    }

    [Fact]
    public async Task Forgot_for_an_unknown_address_sends_nothing()   // criterion 2
    {
        var seed = $"anna-{Guid.NewGuid():N}@example.com";
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";
        await using var factory = Host(seed);
        using var client = factory.CreateClient();

        using var forUnknown = await AccountApi.Forgot(client, unknown);
        using var forSeed = await AccountApi.Forgot(client, seed);

        // The queue is handled in order: once the later mail is there, the earlier request has been handled too.
        await mailpit.WaitForMailAsync(seed);
        Assert.Equal(0, await mailpit.CountAsync(unknown));
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`,
  then `dotnet test -- --filter-class "*MailRequestEndpointTests"`. Expected: the
  build passes (the helper only uses HTTP) and the tests FAIL with `404`.

- [ ] **Step 4: Implement the results.** `src/Auth.Server/Account/AccountResults.cs`:

```csharp
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Account;

/// <summary>
/// The responses of the account endpoints (spec 0004 → Contract). Every one of them is marked as never to be
/// stored, like the token responses, and none touches the refresh cookie.
/// </summary>
public static class AccountResults
{
    public const string InvalidRequestError = "invalid_request";
    public const string InvalidTokenError = "invalid_token";
    public const string WeakPasswordError = "weak_password";
    public const string EmailNotVerifiedError = "email_not_verified";

    /// <summary>202: the request was taken. Says nothing about whether a mail will follow.</summary>
    public static IResult Accepted() => new NoStoreResult(StatusCodes.Status202Accepted, null);

    /// <summary>204: the token was used and its effects applied.</summary>
    public static IResult Done() => new NoStoreResult(StatusCodes.Status204NoContent, null);

    public static IResult InvalidRequest() =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = InvalidRequestError });

    /// <summary>One answer for a token that is unknown, used, expired or replaced.</summary>
    public static IResult InvalidToken() =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = InvalidTokenError });

    public static IResult WeakPassword(IReadOnlyList<string> rules) =>
        new NoStoreResult(StatusCodes.Status400BadRequest, new { error = WeakPasswordError, rules });

    public static IResult EmailNotVerified() =>
        new NoStoreResult(StatusCodes.Status403Forbidden, new { error = EmailNotVerifiedError });

    private sealed class NoStoreResult(int statusCode, object? body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
            httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
            if (body is null)
            {
                httpContext.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            }

            return Results.Json(body, statusCode: statusCode).ExecuteAsync(httpContext);
        }
    }
}
```

- [ ] **Step 5: Implement the endpoint.** `src/Auth.Server/Account/MailRequestEndpoint.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/password/forgot</c> and <c>POST /auth/email/verify/request</c>: take a request for a mail. The
/// two differ only in the kind of mail. The handler validates, applies the limit of the address, queues the request
/// and answers; it never looks an account up, so its work and its answer are the same for every address
/// (spec 0004, Decision 12). Whether a mail follows is decided later, by the dispatcher.
/// </summary>
public static class MailRequestEndpoint
{
    public const string ForgotPasswordPath = "/auth/password/forgot";
    public const string VerifyEmailRequestPath = "/auth/email/verify/request";

    private static readonly string[] Fields = ["email"];

    public static async Task<IResult> HandleAsync(
        MailKind kind, HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized))
        {
            return AccountResults.InvalidRequest();
        }

        var decision = await requests.SubmitAsync(kind, normalized, http.RequestAborted);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
```

  In `Program.cs`, after the existing `MapPost` calls (with
  `using Auth.Server.Account;`, `using Auth.Infrastructure.Persistence;` and
  `using Microsoft.AspNetCore.Identity;`):

```csharp
app.MapPost(MailRequestEndpoint.ForgotPasswordPath,
    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
        MailRequestEndpoint.HandleAsync(MailKind.PasswordReset, http, normalizer, requests, signal));
app.MapPost(MailRequestEndpoint.VerifyEmailRequestPath,
    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
        MailRequestEndpoint.HandleAsync(MailKind.EmailVerification, http, normalizer, requests, signal));
```

- [ ] **Step 6: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Report the status a `GET` gets if it is not `405`.

- [ ] **Step 7: Commit** — `feat(account): take requests for reset and verification mails`

### Task 8: `POST /auth/password/reset`

**Files:**
- Create: `src/Auth.Server/Account/PasswordRules.cs`, `src/Auth.Server/Account/ResetPasswordEndpoint.cs`
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/Sessions/SessionPolicy.cs`,
  `src/Auth.Server/Sessions/RefreshEndpoint.cs`, `src/Auth.Server/Login/LoginEndpoint.cs`
- Test: `tests/Auth.IntegrationTests/ResetPasswordTests.cs`

**Interfaces:**
- Consumes: `EmailTokens.ConsumeAsync` (Task 4), `AccountResults` (Task 7),
  `JsonObjectBody.ReadStringsAsync` (Task 1), `StorableTime` (Task 3),
  `LoginIdentifier.HashOf` (existing), `IOpenIddictTokenManager.RevokeBySubjectAsync`,
  `IOpenIddictAuthorizationManager.RevokeBySubjectAsync`.
- Produces: `static class PasswordRules` with
  `static Task<IReadOnlyList<string>> BrokenAsync(UserManager<ApplicationUser> users, ApplicationUser user, string password)`
  — the rule names the password breaks, in the order `too_short`,
  `requires_upper`, `requires_lower`, `requires_digit`; empty when it is acceptable.
- Produces: `SessionPolicy.StampClaim = "session_stamp"`: the account's security
  stamp as the login read it, kept in the refresh token only (no destination,
  like `StartClaim`). `RefreshEndpoint` refuses a refresh whose stamp differs
  from the account's current one with its ordinary `invalid_grant`.
- Produces: `static class ResetPasswordEndpoint` with
  `const string Path = "/auth/password/reset"` and
  `static Task<IResult> HandleAsync(HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock)`.

- [ ] **Step 1: Write the failing tests** (`ResetPasswordTests`):

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ResetPasswordTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    /// <summary>Asks for a reset mail for <paramref name="email"/>, lets the dispatcher send it and returns its token.</summary>
    private async Task<string> ResetTokenAsync(string email)
    {
        using var response = await AccountApi.Forgot(Client, email);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent[^1]);
    }

    private async Task<HttpStatusCode> LoginStatusAsync(string email, string password)
    {
        // Past the burst window of the lockout, so a test's own failed logins never add up to a lock.
        Clock.Advance(LockoutApi.HumanPace);
        using var response = await LoginApi.Login(Client, email, password);
        return response.StatusCode;
    }

    [Fact]
    public async Task Reset_replaces_the_password()   // criterion 3
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, NewPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(Factory.SeedEmail, Factory.SeedPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, NewPassword));
    }

    [Fact]
    public async Task Reset_ends_every_session_of_the_account()   // criterion 4
    {
        var first = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromSeconds(30));
        var rotated = await SessionApi.RefreshOk(Client, first.RefreshToken);
        var second = await SessionApi.LoginAsync(Client, Factory);
        await CreateUserAsync("other@example.com", confirmed: true);
        using var otherLogin = await LoginApi.Login(Client, "other@example.com", UserPassword);
        var other = SessionApi.RefreshCookieOf(otherLogin).Value.Value!;
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        foreach (var refreshToken in new[] { rotated.RefreshToken, second.RefreshToken })
        {
            using var refresh = await SessionApi.Refresh(Client, refreshToken);
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        // Someone else's session is untouched.
        _ = await SessionApi.RefreshOk(Client, other);

        // A login with the new password starts a session that works.
        Clock.Advance(LockoutApi.HumanPace);
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, NewPassword);
        var cookie = SessionApi.RefreshCookieOf(login);
        _ = await SessionApi.RefreshOk(Client, cookie.Value.Value!);
    }

    [Fact]
    public async Task Session_whose_login_overlapped_a_password_change_cannot_refresh()   // criterion 4
    {
        // What such a login leaves behind: a session that no revocation saw, begun on the old security stamp.
        var session = await SessionApi.LoginAsync(Client, Factory);
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Factory.SeedEmail))!;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task Reset_lifts_a_lockout_and_confirms_the_email_in_one_go()   // criterion 5, Review Focus 5
    {
        const string Email = "locked@example.com";
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 10);
        using (var locked = await LoginApi.Login(Client, Email, UserPassword))
        {
            await LockoutApi.AssertLockedAsync(locked);
        }

        var token = await ResetTokenAsync(Email);
        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        // No waiting: the cooldown is gone, and (from Task 10 on) the unconfirmed-email refusal with it.
        using var login = await LoginApi.Login(Client, Email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(await InDbAsync(db => db.Users.Where(u => u.Email == Email).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reset_makes_the_other_links_of_the_account_unusable()   // criterion 5
    {
        const string Email = "new@example.com";
        await CreateUserAsync(Email, confirmed: false);
        using (var verification = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(verification, HttpStatusCode.Accepted);
        }

        // One pass sends both mails, the verification first; the token returned is the reset's.
        var token = await ResetTokenAsync(Email);
        Assert.Equal(2, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Token_works_once()   // criterion 6
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        using var first = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.NoContent);

        using var second = await AccountApi.Reset(Client, token, "Yet-Another-Passw0rd");

        await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, NewPassword));
    }

    [Fact]
    public async Task Unknown_used_expired_and_replaced_tokens_get_one_answer()   // criterion 6
    {
        var used = await ResetTokenAsync(Factory.SeedEmail);
        using (var ok = await AccountApi.Reset(Client, used, NewPassword))
        {
            await AccountApi.AssertEmptyAsync(ok, HttpStatusCode.NoContent);
        }

        Clock.Advance(PastTheLimit);
        var replaced = await ResetTokenAsync(Factory.SeedEmail);
        Clock.Advance(PastTheLimit);
        var expired = await ResetTokenAsync(Factory.SeedEmail);
        // Asked now, while `replaced` is a minute old: it is refused for being replaced, not for its age.
        using var replacedResponse = await AccountApi.Reset(Client, replaced, NewPassword);
        Clock.Advance(EmailTokens.ResetLifetime);

        using var unknownResponse = await AccountApi.Reset(Client, "never-issued", NewPassword);
        using var usedResponse = await AccountApi.Reset(Client, used, NewPassword);
        using var expiredResponse = await AccountApi.Reset(Client, expired, NewPassword);

        foreach (var response in new[] { unknownResponse, usedResponse, replacedResponse, expiredResponse })
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            Assert.Equal(LoginApi.HeaderNames(unknownResponse), LoginApi.HeaderNames(response));
        }
    }

    [Fact]
    public async Task Unusable_token_is_reported_before_a_weak_password()
    {
        using var response = await AccountApi.Reset(Client, "never-issued", "abc");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task Reset_does_not_clear_the_mail_limit()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        using (var reset = await AccountApi.Reset(Client, token, NewPassword))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        using var again = await AccountApi.Forgot(Client, Factory.SeedEmail);

        Assert.Equal(60, await LockoutApi.AssertLockedAsync(again));
    }

    [Fact]
    public async Task Token_is_good_until_just_under_an_hour()   // criterion 6
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        Clock.Advance(EmailTokens.ResetLifetime - TimeSpan.FromSeconds(1));

        using var response = await AccountApi.Reset(Client, token, NewPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Verification_token_does_not_reset_a_password_and_stays_usable()
    {
        const string Email = "new@example.com";
        await CreateUserAsync(Email, confirmed: false);
        using (var requested = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(requested, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var verification = TokenIn(Assert.Single(Mail.Sent));

        using var response = await AccountApi.Reset(Client, verification, NewPassword);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("abc", """["too_short","requires_upper","requires_digit"]""")]
    [InlineData("abcdefgh", """["requires_upper","requires_digit"]""")]
    [InlineData("ABCDEFG1", """["requires_lower"]""")]
    [InlineData("Abcdefgh", """["requires_digit"]""")]
    [InlineData("Ab1", """["too_short"]""")]
    [InlineData("1234567", """["too_short","requires_upper","requires_lower"]""")]
    public async Task Weak_password_names_every_broken_rule_and_leaves_everything_as_it_was(string password, string rules)   // criterion 7
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var weak = await AccountApi.Reset(Client, token, password);

        await AccountApi.AssertErrorAsync(weak, $$"""{"error":"weak_password","rules":{{rules}}}""");
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, Factory.SeedPassword));
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);

        // The same token still works with an acceptable password.
        using var strong = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(strong, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Password_without_a_special_character_is_accepted()   // criterion 8
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, "Abcdefg1");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, "Abcdefg1"));
    }

    [Fact]
    public async Task Letters_of_any_script_satisfy_the_policy()   // criterion 8
    {
        const string Polish = "zażółć12Ż";
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, Polish);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, Polish));
    }

    [Fact]
    public async Task New_password_may_equal_the_old_one()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var response = await AccountApi.Reset(Client, token, Factory.SeedPassword);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Parallel_resets_with_one_token_change_the_password_once()   // Review Focus 1
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);
        var passwords = Enumerable.Range(0, 6).Select(i => $"Parallel-Passw0rd-{i}").ToArray();

        var responses = await Task.WhenAll(passwords.Select(p => Task.Run(() => AccountApi.Reset(Client, token, p))));

        try
        {
            var winner = Assert.Single(Enumerable.Range(0, responses.Length), i => responses[i].StatusCode == HttpStatusCode.NoContent);
            for (var i = 0; i < responses.Length; i++)
            {
                if (i != winner)
                {
                    await AccountApi.AssertErrorAsync(responses[i], AccountApi.InvalidToken);
                }
            }

            Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(Factory.SeedEmail, passwords[winner]));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":"x"}""")]
    [InlineData("""{"new_password":"Brand-New-Passw0rd"}""")]
    [InlineData("""{"token":"","new_password":"Brand-New-Passw0rd"}""")]
    [InlineData("""{"token":"x","new_password":"   "}""")]
    [InlineData("""{"token":"x","new_password":123}""")]
    [InlineData("""{"token":"x","new_password":"Brand-New\u0000Passw0rd"}""")]   // NUL, as a JSON escape
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400(string body)
    {
        using var response = await AccountApi.PostRaw(Client, AccountApi.ResetPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task Malformed_request_with_a_good_token_does_not_use_it_up()
    {
        var token = await ResetTokenAsync(Factory.SeedEmail);

        using var malformed = await AccountApi.PostRaw(Client, AccountApi.ResetPath, $$"""{"token":"{{token}}","new_password":123}""");
        await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidRequest);

        using var response = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }
}
```

  `Reset_lifts_a_lockout_and_confirms_the_email_in_one_go` passes in this task
  with or without the login refusal of Task 10 (the reset confirms the email
  first); it is here so that Task 10 cannot break it unnoticed.

- [ ] **Step 2: Run them and confirm they fail.** Run
  `dotnet test -- --filter-class "*ResetPasswordTests"`. Expected: FAIL with `404`.

- [ ] **Step 3: Implement the rule names.** `src/Auth.Server/Account/PasswordRules.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// Turns what ASP.NET Identity's validators say about a password into the rule names of the contract
/// (spec 0004 → Password policy). The names are ours and stable; Identity's codes and texts are not sent.
/// </summary>
public static class PasswordRules
{
    private static readonly string[] Order = ["too_short", "requires_upper", "requires_lower", "requires_digit"];

    /// <summary>The rules <paramref name="password"/> breaks, in contract order; empty when it is acceptable.</summary>
    public static async Task<IReadOnlyList<string>> BrokenAsync(UserManager<ApplicationUser> users, ApplicationUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(user);

        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            foreach (var error in result.Errors)
            {
                broken.Add(error.Code switch
                {
                    nameof(IdentityErrorDescriber.PasswordTooShort) => "too_short",
                    nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "requires_upper",
                    nameof(IdentityErrorDescriber.PasswordRequiresLower) => "requires_lower",
                    nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "requires_digit",
                    // A rule the contract has no name for means the policy and this mapping have drifted apart.
                    _ => throw new InvalidOperationException($"Password rule '{error.Code}' has no name in the contract."),
                });
            }
        }

        return [.. Order.Where(broken.Contains)];
    }
}
```

- [ ] **Step 4: Implement the endpoint.** `src/Auth.Server/Account/ResetPasswordEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/password/reset</c>: exchanges a reset token for a new password. Everything happens in one
/// transaction that begins by using the token up: a second request with the same token waits for this one and then
/// finds no token, and a request that fails on the way — a weak password, an error — rolls back, token included.
/// On success every session of the account ends, its login streak is cleared and its email is confirmed
/// (spec 0004 → Effects of a reset). The user is not signed in.
/// </summary>
public static class ResetPasswordEndpoint
{
    public const string Path = "/auth/password/reset";

    private static readonly string[] Fields = ["token", "new_password"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users,
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(clock);

        var cancellationToken = http.RequestAborted;
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, cancellationToken);
        if (fields is null || fields[1].Contains('\0'))
        {
            return AccountResults.InvalidRequest();
        }

        var (token, password) = (fields[0], fields[1]);
        var now = StorableTime.Now(clock);

        // Leaving this method without a commit rolls everything back, the use of the token included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await EmailTokens.ConsumeAsync(db, token, MailKind.PasswordReset, now, cancellationToken) is not { } userId
            || await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return AccountResults.InvalidToken();
        }

        var broken = await PasswordRules.BrokenAsync(users, user, password);
        if (broken.Count > 0)
        {
            return AccountResults.WeakPassword(broken);
        }

        // The link came through the account's mailbox: that proves the address as well.
        user.EmailConfirmed = true;
        if (await users.HasPasswordAsync(user))
        {
            Ensure(await users.RemovePasswordAsync(user));
        }

        Ensure(await users.AddPasswordAsync(user, password));

        // Every session ends: the refresh tokens, and the authorizations they hang on (spec 0002, Decision 11).
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);

        // Every other link of the account, of either kind.
        await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        // Someone failing logins on purpose must not keep the owner out after a reset.
        var identifier = LoginIdentifier.HashOf(user.NormalizedEmail);
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifier).ExecuteDeleteAsync(cancellationToken);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return AccountResults.Done();
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and a password must never be logged.
            throw new InvalidOperationException(
                "Could not set the password: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
```

  In `Program.cs`: `app.MapPost(ResetPasswordEndpoint.Path, ResetPasswordEndpoint.HandleAsync);`.

- [ ] **Step 5: Tie a session to the security stamp.** Revoking by subject ends the
  sessions that exist when the reset runs. A login that verified the **old**
  password a moment earlier and issues its tokens a moment later is not among
  them — and someone who knows the old password can make that happen on purpose
  by logging in over and over while the owner resets. ASP.NET Identity changes
  the account's security stamp whenever the password changes, so a session that
  remembers the stamp it began with gives itself away at its first refresh.

  `src/Auth.Server/Sessions/SessionPolicy.cs`, after `StartClaim`:

```csharp
    /// <summary>
    /// Claim holding the account's security stamp as it was when the session began. ASP.NET Identity changes the stamp
    /// whenever the password changes, and a refresh is refused once the two differ (spec 0004 → Effects of a reset).
    /// Like <see cref="StartClaim"/> it has no destination: it lives in the refresh token only.
    /// </summary>
    public const string StampClaim = "session_stamp";
```

  `src/Auth.Server/Login/LoginEndpoint.cs`, right after the line that sets
  `SessionPolicy.StartClaim`:

```csharp
        // The stamp of the account as this login read it. A session that began before a password change, even one
        // whose login was still in flight when the change was committed, carries the old stamp and cannot refresh.
        identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);
```

  `src/Auth.Server/Sessions/RefreshEndpoint.cs` — replace the comment above the
  user lookup ("Only existence is checked … the forgot/reset spec's job.") and
  add the check after the `user is null` block:

```csharp
        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null)
        {
            return InvalidGrant();
        }

        // A password change ends every session (spec 0004): it revokes the tokens it can see, and it changes the
        // account's security stamp, which also stops a session whose login overlapped the change.
        if (!string.Equals(result.Principal!.GetClaim(SessionPolicy.StampClaim), user.SecurityStamp, StringComparison.Ordinal))
        {
            return InvalidGrant();
        }
```

  The refresh endpoint copies the claims of the old refresh token into the new
  one, so the stamp travels with the session unchanged. The access token is not
  affected: the claim has no destination (the existing tests that pin the access
  token's claim set must pass unedited).

- [ ] **Step 6: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  If the parallel test shows anything but one `204` and five `invalid_token`
  (a `500`, two `204`), stop and report the response bodies: the design rests on
  the `DELETE … RETURNING` being the first statement of the transaction.

- [ ] **Step 7: Commit** — `feat(account): reset a password with a mailed token`

### Task 9: `POST /auth/email/verify`

**Files:**
- Create: `src/Auth.Server/Account/VerifyEmailEndpoint.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/VerifyEmailTests.cs`

**Interfaces:**
- Consumes: `EmailTokens.ConsumeAsync` (Task 4), `AccountResults` (Task 7),
  `JsonObjectBody.ReadStringsAsync` (Task 1), `StorableTime` (Task 3).
- Produces: `static class VerifyEmailEndpoint` with
  `const string Path = "/auth/email/verify"` and
  `static Task<IResult> HandleAsync(HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, TimeProvider clock)`.

- [ ] **Step 1: Write the failing tests** (`VerifyEmailTests`):

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class VerifyEmailTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Email = "new@example.com";
    private static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    /// <summary>Asks for a verification mail for <see cref="Email"/>, lets the dispatcher send it and returns its token.</summary>
    private async Task<string> VerificationTokenAsync()
    {
        using var response = await AccountApi.RequestVerification(Client, Email);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent[^1]);
    }

    private Task<bool> ConfirmedAsync() =>
        InDbAsync(db => db.Users.Where(u => u.Email == Email).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Verification_confirms_the_email()   // criterion 12
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.True(await ConfirmedAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Verification_leaves_the_password_and_a_reset_link_alone()
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        using (var forgot = await AccountApi.Forgot(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var reset = TokenIn(Mail.Sent[^1]);

        using var response = await AccountApi.Verify(Client, token);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);

        // The reset link of the same account still works.
        using var resetResponse = await AccountApi.Reset(Client, reset, "Brand-New-Passw0rd");
        await AccountApi.AssertEmptyAsync(resetResponse, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_used_expired_and_replaced_tokens_get_one_answer()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var replaced = await VerificationTokenAsync();
        Clock.Advance(PastTheLimit);
        var expired = await VerificationTokenAsync();
        // Asked now, while `replaced` is a minute old: it is refused for being replaced, not for its age.
        using var replacedResponse = await AccountApi.Verify(Client, replaced);
        Clock.Advance(EmailTokens.VerificationLifetime);
        var used = await VerificationTokenAsync();
        using (var ok = await AccountApi.Verify(Client, used))
        {
            await AccountApi.AssertEmptyAsync(ok, HttpStatusCode.NoContent);
        }

        using var unknownResponse = await AccountApi.Verify(Client, "never-issued");
        using var usedResponse = await AccountApi.Verify(Client, used);
        using var expiredResponse = await AccountApi.Verify(Client, expired);

        foreach (var response in new[] { unknownResponse, usedResponse, replacedResponse, expiredResponse })
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            Assert.Equal(LoginApi.HeaderNames(unknownResponse), LoginApi.HeaderNames(response));
        }
    }

    [Fact]
    public async Task Expired_token_confirms_nothing()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        Clock.Advance(EmailTokens.VerificationLifetime);

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.False(await ConfirmedAsync());
    }

    [Fact]
    public async Task Token_is_good_until_just_under_24_hours()   // criterion 6
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        Clock.Advance(EmailTokens.VerificationLifetime - TimeSpan.FromSeconds(1));

        using var response = await AccountApi.Verify(Client, token);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_token_does_not_verify_and_stays_usable()
    {
        await CreateUserAsync(Email, confirmed: false);
        using (var forgot = await AccountApi.Forgot(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var reset = TokenIn(Assert.Single(Mail.Sent));

        using var response = await AccountApi.Verify(Client, reset);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
        Assert.False(await ConfirmedAsync());
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Parallel_verifications_with_one_token_succeed_once()   // Review Focus 1
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => AccountApi.Verify(Client, token))));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.NoContent))
            {
                await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            }

            Assert.True(await ConfirmedAsync());
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
    public async Task Confirmed_account_gets_no_further_verification_mail()   // criterion 11
    {
        await CreateUserAsync(Email, confirmed: false);
        var token = await VerificationTokenAsync();
        using (var verified = await AccountApi.Verify(Client, token))
        {
            await AccountApi.AssertEmptyAsync(verified, HttpStatusCode.NoContent);
        }

        Clock.Advance(PastTheLimit);
        using var again = await AccountApi.RequestVerification(Client, Email);
        await AccountApi.AssertEmptyAsync(again, HttpStatusCode.Accepted);
        await DispatchAsync();

        Assert.Single(Mail.Sent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":""}""")]
    [InlineData("""{"token":123}""")]
    [InlineData("""["x"]""")]
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400(string body)
    {
        using var response = await AccountApi.PostRaw(Client, AccountApi.VerifyPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run
  `dotnet test -- --filter-class "*VerifyEmailTests"`. Expected: FAIL with `404`.

- [ ] **Step 3: Implement.** `src/Auth.Server/Account/VerifyEmailEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/email/verify</c>: exchanges a verification token for a confirmed email. One transaction that begins
/// by using the token up, like the reset. It changes nothing else about the account: not the password, not its
/// sessions, not a reset link.
/// </summary>
public static class VerifyEmailEndpoint
{
    public const string Path = "/auth/email/verify";

    private static readonly string[] Fields = ["token"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(clock);

        var cancellationToken = http.RequestAborted;
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, cancellationToken);
        if (fields is null)
        {
            return AccountResults.InvalidRequest();
        }

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await EmailTokens.ConsumeAsync(db, fields[0], MailKind.EmailVerification, now, cancellationToken) is not { } userId
            || await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return AccountResults.InvalidToken();
        }

        user.EmailConfirmed = true;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not confirm the email: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return AccountResults.Done();
    }
}
```

  In `Program.cs`: `app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync);`.

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.

- [ ] **Step 5: Commit** — `feat(account): confirm an email with a mailed token`

### Task 10: Login refuses an unconfirmed account; the second seed user

**Files:**
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs`, `src/Auth.Server/Seeding/DevUserSeeder.cs`
- Test: `tests/Auth.IntegrationTests/UnverifiedLoginTests.cs`

**Interfaces:**
- Consumes: `AccountResults.EmailNotVerified()` (Task 7); the endpoints of Tasks 7–9.
- Changes: `LoginEndpoint.HandleAsync` — after the password is verified and the
  streak cleared, an account with `EmailConfirmed == false` gets the `403`.
- Produces: `DevUserSeeder.UnverifiedEmailKey = "Auth:DevSeed:UnverifiedEmail"`,
  `DevUserSeeder.UnverifiedPasswordKey = "Auth:DevSeed:UnverifiedPassword"`: an
  optional second user, `Development` only, created with `EmailConfirmed = false`.

- [ ] **Step 1: Write the failing tests** (`UnverifiedLoginTests`):

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class UnverifiedLoginTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private const string Email = "new@example.com";
    private const string NotVerified = """{"error":"email_not_verified"}""";

    private static async Task AssertNotVerifiedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(NotVerified, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    private Task<int> StreakCountAsync() =>
        InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Correct_password_for_an_unconfirmed_account_is_refused_with_its_own_answer()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);

        using var response = await LoginApi.Login(Client, Email, UserPassword);

        await AssertNotVerifiedAsync(response);
    }

    [Fact]
    public async Task Wrong_password_for_an_unconfirmed_account_is_the_ordinary_401()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);

        using var unconfirmed = await LoginApi.Login(Client, Email, LockoutApi.WrongPassword);
        using var confirmed = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, unconfirmed.StatusCode);
        Assert.Equal(await confirmed.Content.ReadAsStringAsync(), await unconfirmed.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(confirmed), LoginApi.HeaderNames(unconfirmed));
    }

    [Fact]
    public async Task Refusal_ends_the_streak_as_a_success_would()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 9);

        Clock.Advance(LockoutApi.HumanPace);
        using (var refused = await LoginApi.Login(Client, Email, UserPassword))   // the tenth attempt of the streak
        {
            await AssertNotVerifiedAsync(refused);
        }

        Assert.Equal(0, await StreakCountAsync());
        await LockoutApi.FailAsync(Client, Clock, Email, 9);   // nine more ordinary 401s: no lock
    }

    [Fact]
    public async Task During_a_cooldown_the_answer_is_the_lockout_not_the_refusal()   // criterion 13
    {
        await CreateUserAsync(Email, confirmed: false);
        await LockoutApi.FailAsync(Client, Clock, Email, 10);

        using var response = await LoginApi.Login(Client, Email, UserPassword);

        await LockoutApi.AssertLockedAsync(response);
    }

    [Fact]
    public async Task After_verification_the_account_logs_in()   // criterion 12
    {
        await CreateUserAsync(Email, confirmed: false);
        using (var refused = await LoginApi.Login(Client, Email, UserPassword))
        {
            await AssertNotVerifiedAsync(refused);
        }

        using (var requested = await AccountApi.RequestVerification(Client, Email))
        {
            await AccountApi.AssertEmptyAsync(requested, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        using (var verified = await AccountApi.Verify(Client, TokenIn(Assert.Single(Mail.Sent))))
        {
            await AccountApi.AssertEmptyAsync(verified, HttpStatusCode.NoContent);
        }

        Clock.Advance(LockoutApi.HumanPace);
        using var login = await LoginApi.Login(Client, Email, UserPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        _ = SessionApi.RefreshCookieOf(login);
    }

    [Fact]
    public async Task Confirmed_accounts_log_in_as_before()
    {
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Second_seed_user_is_created_unconfirmed_and_only_once()
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var first = new AuthAppFactory(Postgres, Keys, database)
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UserPassword);
        using (var client = first.CreateClient())
        {
            using var response = await LoginApi.Login(client, Email, UserPassword);
            await AssertNotVerifiedAsync(response);
        }

        await using var second = new AuthAppFactory(Postgres, Keys, database)   // a restart
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, "Different-Passw0rd");
        using var scope = second.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(Email);
        Assert.NotNull(user);
        Assert.False(user.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(user, UserPassword));          // never reset by the seed
        Assert.True((await users.FindByEmailAsync(second.SeedEmail))!.EmailConfirmed);
    }

    [Fact]
    public async Task Production_host_does_not_seed_the_second_user()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys)
            .WithEnvironment("Production")
            .WithSetting(DevUserSeeder.UnverifiedEmailKey, Email)
            .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UserPassword);

        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(Email));
    }

    [Fact]
    public async Task Without_the_settings_there_is_no_second_user() =>
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(TestContext.Current.CancellationToken)));
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`DevUserSeeder.UnverifiedEmailKey` does not exist).

- [ ] **Step 3: Refuse at login.** In `src/Auth.Server/Login/LoginEndpoint.cs`, right
  after `await streaks.ClearAsync(identifier, CancellationToken.None);` (add
  `using Auth.Server.Account;`):

```csharp
        // The password is proven, so the streak is over either way. But an account whose email is not confirmed
        // gets no session (spec 0004, Decision 3). Only someone who knows the password can see this answer.
        if (!user.EmailConfirmed)
        {
            return AccountResults.EmailNotVerified();
        }
```

  Extend the class summary with one sentence: a correct password for an account
  whose email is not confirmed is refused with `403 email_not_verified`.

- [ ] **Step 4: Seed the second user.** `src/Auth.Server/Seeding/DevUserSeeder.cs` — the
  whole file:

```csharp
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Seeding;

/// <summary>
/// Creates the development users at host startup so there is someone to log in as. It is a no-op outside the
/// <c>Development</c> environment. The first user (<see cref="EmailKey"/>, <see cref="PasswordKey"/>) has a
/// confirmed email. The optional second one (<see cref="UnverifiedEmailKey"/>, <see cref="UnverifiedPasswordKey"/>)
/// has not: until invitations exist it is the only way to have an account that needs verification (spec 0004,
/// Decision 2). Each is created only when both of its settings are present. Seeding is idempotent and never resets
/// the password of an existing user.
/// </summary>
public static partial class DevUserSeeder
{
    public const string EmailKey = "Auth:DevSeed:Email";
    public const string PasswordKey = "Auth:DevSeed:Password";
    public const string UnverifiedEmailKey = "Auth:DevSeed:UnverifiedEmail";
    public const string UnverifiedPasswordKey = "Auth:DevSeed:UnverifiedPassword";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevUserSeeder));

        await SeedOneAsync(users, logger, configuration[EmailKey], configuration[PasswordKey], emailConfirmed: true, ct);
        await SeedOneAsync(users, logger, configuration[UnverifiedEmailKey], configuration[UnverifiedPasswordKey], emailConfirmed: false, ct);
    }

    private static async Task SeedOneAsync(
        UserManager<ApplicationUser> users, ILogger logger, string? email, string? password, bool emailConfirmed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var result = await users.CreateAsync(
            new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed },
            password);
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and the password must never be logged.
            throw new InvalidOperationException(
                "Could not seed the development user: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        LogSeeded(logger, email);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development user {Email}")]
    private static partial void LogSeeded(ILogger logger, string email);
}
```

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS, including the unedited `DevSeedTests`, `LoginTests` and
  `LockoutTests`, and `ResetPasswordTests.Reset_lifts_a_lockout_and_confirms_the_email_in_one_go`.
  If an existing test logs in successfully as a user it created without
  `EmailConfirmed = true`, report it (none was found when this plan was written).

- [ ] **Step 6: Commit** — `feat(login): refuse accounts whose email is not confirmed`

### Task 11: Pruning

**Files:**
- Create: `src/Auth.Server/Email/EmailPruner.cs`, `src/Auth.Server/Email/EmailPruningService.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/EmailPruningTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext.EmailTokens`, `.MailRequestLimits` (Task 3),
  `MailLimitPolicy.Window` (Task 3).
- Produces: `EmailPruner` (singleton) with
  `Task<(int Tokens, int Limits)> PruneOnceAsync(CancellationToken cancellationToken)`:
  removes link tokens that have expired and limit rows without an accepted
  request for an hour (such a row imposes nothing: its window is closed and its
  60 seconds are over).
- Produces: `EmailPruningService` (`BackgroundService`): a pass at host start and
  then every hour — the pattern of `LockoutPruningService`.
- Queue rows are not pruned: the dispatcher drops them itself (Task 6).

- [ ] **Step 1: Write the failing tests** (`EmailPruningTests`):

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class EmailPruningTests : MailTestBase
{
    public EmailPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The service's loop waits on the fake clock and Clock.Advance fires its timer: a background pass would race
        // the explicit pass a test counts. It is left out, and a test drives its pass by hand.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<(int Tokens, int Limits)> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    private Task<int> TokenCountAsync() =>
        InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken));

    private Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Pruning_removes_expired_tokens_and_keeps_the_others()
    {
        await CreateUserAsync("new@example.com", confirmed: false);
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);            // expires after 1 hour
        await EnqueueAsync(MailKind.EmailVerification, "new@example.com");        // expires after 24 hours
        await DispatchAsync();
        Assert.Equal(2, await TokenCountAsync());

        Clock.Advance(EmailTokens.ResetLifetime);

        Assert.Equal(1, (await PruneAsync()).Tokens);
        var left = await InDbAsync(db => db.EmailTokens.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MailKind.EmailVerification, left.Kind);

        Clock.Advance(EmailTokens.VerificationLifetime);
        Assert.Equal(1, (await PruneAsync()).Tokens);
        Assert.Equal(0, await TokenCountAsync());
    }

    [Fact]
    public async Task Pruning_removes_a_limit_row_an_hour_after_its_last_accepted_request()
    {
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Clock.Advance(MailLimitPolicy.Window - TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await PruneAsync()).Limits);
        Assert.Equal(1, await LimitCountAsync());

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await PruneAsync()).Limits);
        Assert.Equal(0, await LimitCountAsync());
    }

    [Fact]
    public async Task Limit_row_that_still_counts_survives_a_pass()   // criterion 9
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await AccountApi.Forgot(Client, Factory.SeedEmail);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(TimeSpan.FromSeconds(61));
        }

        Clock.Advance(TimeSpan.FromMinutes(50));          // 55 minutes into the window

        Assert.Equal(0, (await PruneAsync()).Limits);
        using var sixth = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await LockoutApi.AssertLockedAsync(sixth);
    }

    [Fact]
    public async Task Pruned_address_starts_afresh()
    {
        using (var first = await AccountApi.Forgot(Client, Factory.SeedEmail))
        {
            await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);
        }

        Clock.Advance(MailLimitPolicy.Window);
        await PruneAsync();

        using var again = await AccountApi.Forgot(Client, Factory.SeedEmail);
        await AccountApi.AssertEmptyAsync(again, HttpStatusCode.Accepted);
        Assert.Equal(1, await LimitCountAsync());
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_alone()
    {
        Mail.Failing = true;
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await DispatchAsync();

        Clock.Advance(TimeSpan.FromHours(2));
        await PruneAsync();

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`EmailPruner`, `EmailPruningService` do not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Email/EmailPruner.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// One pruning pass over the tables of spec 0004: link tokens that have expired, and mail-limit rows without an
/// accepted request for <see cref="MailLimitPolicy.Window"/>, which the rules already treat as absent. Anyone can
/// create a limit row by submitting an address, so rows must expire.
/// </summary>
public sealed partial class EmailPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EmailPruner> logger)
{
    public async Task<(int Tokens, int Limits)> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var idleSince = now - MailLimitPolicy.Window;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tokens = await db.EmailTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var limits = await db.MailRequestLimits.Where(l => l.LastAcceptedAt <= idleSince).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, tokens, limits);
        return (tokens, limits);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
    private static partial void LogPruned(ILogger logger, int tokens, int limits);
}
```

`src/Auth.Server/Email/EmailPruningService.cs`:

```csharp
namespace Auth.Server.Email;

/// <summary>Runs <see cref="EmailPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class EmailPruningService(EmailPruner pruner, TimeProvider clock, ILogger<EmailPruningService> logger) : BackgroundService
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Pruning of link tokens and mail limits failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
```

  In `Program.cs`:

```csharp
builder.Services.AddSingleton<EmailPruner>();
builder.Services.AddHostedService<EmailPruningService>();
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  The whole suite: every test host now runs this service too, and `Clock.Advance`
  of an hour or more fires it. Tests of Tasks 4–10 that count token or limit rows
  **after** such an advance are the ones it could disturb; if one fails, give that
  class `Factory.WithoutHostedService<EmailPruningService>()` in its constructor
  (they are this plan's own files) and say so in the report.

- [ ] **Step 5: Commit** — `feat(email): prune expired link tokens and idle mail limits`

### Task 12: Real-network e2e, compose, docs and acceptance map

**Files:**
- Create: `scripts/e2e-email.sh`, `docs/superpowers/plans/0004-acceptance-map.md`
- Modify: `deploy/docker-compose.yml`, `.env.example`, `README.md`

**Interfaces:**
- Consumes: the compose stack and `.env` of slice 1; the endpoints of Tasks 7–10.
- Produces: a `mailpit` service in the compose stack, the second seed user in
  `.env.example`, and `scripts/e2e-email.sh`.

**Compose.** In `deploy/docker-compose.yml` add the service:

```yaml
  mailpit:
    # A mail catcher: the auth service sends to it over SMTP, the e2e script reads the mails from its HTTP API.
    image: axllent/mailpit:v1.31.3
    ports:
      # Loopback only: the web UI and the API (http://localhost:8025). SMTP (1025) stays inside the compose network.
      - "127.0.0.1:8025:8025"
```

  and in the `auth` service: `mailpit: { condition: service_started }` under
  `depends_on`, and these lines under `environment` (the other mail settings come
  from `appsettings.Development.json`):

```yaml
      # Optional second seed user with an unconfirmed email (spec 0004); scripts/e2e-email.sh needs it.
      Auth__DevSeed__UnverifiedEmail: ${AUTH_DEV_SEED_UNVERIFIED_EMAIL:-}
      Auth__DevSeed__UnverifiedPassword: ${AUTH_DEV_SEED_UNVERIFIED_PASSWORD:-}
      Auth__Email__Smtp__Host: mailpit
```

  Update the header comment of the file: the stack is PostgreSQL, a mail catcher
  and the auth service.

**`.env.example`** — append:

```
# Second development seed user, created with an UNCONFIRMED email (spec 0004). Used by scripts/e2e-email.sh.
AUTH_DEV_SEED_UNVERIFIED_EMAIL=new@example.com
AUTH_DEV_SEED_UNVERIFIED_PASSWORD=Dev-Unverified-Passw0rd
```

**The script.** `scripts/e2e-email.sh` — same conventions as
`scripts/e2e-lockout.sh` (`set -euo pipefail`, `BASE_URL`, `env_get`,
`wait_healthy`, `fail` / `pass`, a `mktemp -d` directory removed by an `EXIT`
trap, exit non-zero on the first failure, `ALL PASS` at the end). It **never
prints a password, a token, a cookie or a mail body**: request bodies are built
into files under `$tmp` and sent with `--data-binary @file`; a token goes from the
mail straight into such a file. It does not bring the stack up or down, but its
last step **stops and starts the `mailpit` service** (as `e2e-login.sh` restarts
`auth`). Header comment: what it checks, the full sequence (the three existing
scripts first, this one last), that it takes about a minute, and that it is
**not re-runnable on the same stack** — it confirms the second seed user and
changes its password — so a second run needs `down -v` first. LF line endings;
set the executable bit in the index:
`git update-index --chmod=+x scripts/e2e-email.sh` (after `git add`).

  Reads `AUTH_DEV_SEED_EMAIL`, `AUTH_DEV_SEED_PASSWORD`,
  `AUTH_DEV_SEED_UNVERIFIED_EMAIL`, `AUTH_DEV_SEED_UNVERIFIED_PASSWORD` from the
  repo-root `.env` (parsed, never sourced); fails with a clear message when the
  two new ones are missing. Env: `BASE_URL` (default `http://localhost:8080`),
  `MAILPIT_URL` (default `http://localhost:8025`). The links in the mails start
  with `http://localhost:4200/verify` and `http://localhost:4200/reset`
  (`appsettings.Development.json`).

  Helpers beyond the ones copied from `e2e-lockout.sh` (`env_get`,
  `wait_healthy`, `header`, `header_names`, `make_body`). Files reach `python3` on
  standard input, never as a path argument — the convention of the existing
  scripts, and necessary where `python3` is a Windows interpreter behind a shim,
  which does not understand the paths of Git Bash:

```bash
# post <path> <body-file>  ->  sets HTTP_CODE, BODY, TIME_S (curl's time_total, seconds); response headers in $tmp/hdr
post() {
  local out
  out="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code} %{time_total}' \
    -X POST "$BASE_URL$1" -H 'Content-Type: application/json' --data-binary "@$2")"
  HTTP_CODE="${out%% *}"
  TIME_S="${out##* }"
  BODY="$(cat "$tmp/body")"
}

# email_body <email-env> <file>: {"email": ...} from the given env var name
email_body() {
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]]}))' "$1" > "$2"
}

# mail_count <address>  ->  prints how many mails the catcher holds for the address
mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json"
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves
# the newest one (the search lists newest first) to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json")"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <link-prefix> <file> [new-password-env]: takes the token from the text part of $tmp/mail.json, checks
# that the HTML part holds the same token, and writes the request body. The token is never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if len(sys.argv) > 2 and sys.argv[2]:
    body["new_password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${3:-}" < "$tmp/mail.json" > "$2"
}

mail_subject() { python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["Subject"])' < "$tmp/mail.json"; }
```

  Steps (the second seed user is `NEW`, the first is `SEED`; the new password is
  made in the script: `export E2E_NEW_PASSWORD="E2e-New-Passw0rd-$RANDOM$RANDOM"`):

  1. health; `GET $MAILPIT_URL/readyz` → `200`.
  2. **Unconfirmed login (criteria 13, 16).** `NEW` with a wrong password → `401
     {"error":"invalid_credentials"}`; with the right password → `403` with the
     body `{"error":"email_not_verified"}`, `Cache-Control: no-store`, no
     `Set-Cookie`. Then a login whose email holds the JSON escape `\uFFFE`
     (write the body with `printf '%s'` and single quotes, so the six characters
     reach the file as they are) → `400`: the container image has no ICU, where
     the framework's normaliser would have let that address through.
  3. **Verification request and the limit (criteria 9, 11).**
     `POST /auth/email/verify/request` for `NEW` → `202`, empty body, `no-store`.
     The same request again at once → `429` with the lockout body shape,
     `Retry-After` equal to `retry_after_seconds`, between 1 and 60.
  4. **Verification (criteria 6, 12, 15).** `wait_mail NEW 1 30`; `mail_subject`
     contains `auth-core-dev`; `token_body http://localhost:4200/verify` →
     `POST /auth/email/verify` → `204`, empty body. The same body again → `400
     {"error":"invalid_token"}`.
  5. `NEW` logs in → `200` with an `auth_rt` cookie; keep the cookie value in a
     file under `$tmp` for step 8.
  6. **Forgot, no enumeration (criteria 1, 2).** `POST /auth/password/forgot` for
     an address made for this run (`nobody-$RANDOM$RANDOM@example.invalid`) →
     `202`; keep `header_names` and the body. The same for `NEW` → `202`, with the
     same header names and the same (empty) body.
  7. **Reset (criteria 3, 6, 7).** `wait_mail NEW 2 30` (the newest is the reset
     mail); `mail_count` for the unknown address is `0`. A body with the token and
     the password `abc` (built with `token_body`, the weak password in an env
     var) → `400` with exactly
     `{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}`.
     A body with the same token and `E2E_NEW_PASSWORD` → `204`. That body again →
     `400 {"error":"invalid_token"}`.
  8. **After the reset (criteria 3, 4).** `NEW` with the old password → `401`;
     `POST /auth/refresh` with the cookie of step 5 → `401
     {"error":"invalid_grant"}`; `NEW` with the new password → `200`.
  9. **A mail outage (criterion 14).** `docker compose … stop mailpit`;
     `POST /auth/password/forgot` for `SEED` → `202` with `TIME_S` under 2 seconds
     (the request does not wait for the mail server). Wait, up to 60 seconds,
     until `docker compose … logs auth` holds a line with `failed on attempt 1`:
     the first attempt has really failed (the line carries a request id, the kind
     and an exception type name; nothing secret). `docker compose … start
     mailpit`, wait for `readyz`; `wait_mail SEED 1 180` — one of the dispatcher's
     retries (5 s, 30 s, 2 min after the failures) delivers the mail once the
     server is back. The mail is not used: `SEED` keeps its password for the other
     scripts.

**The acceptance map.** `docs/superpowers/plans/0004-acceptance-map.md`, same
layout as `0003-acceptance-map.md`:

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Forgot → `202`, a reset mail with the configured link | `MailRequestEndpointTests.Forgot_for_an_account_is_accepted_and_a_reset_mail_follows`, `MailDispatcherTests.Request_for_an_account_sends_one_mail_and_stores_only_the_hash_of_its_token`, `MailThroughSmtpTests.Forgot_request_ends_as_a_mail_on_the_mail_server`; e2e steps 6–7 |
| 2 | An address without an account: same response, same statements, no mail | `MailRequestEndpointTests.Address_without_an_account_gets_the_same_answer_the_same_rows_and_no_mail`, `MailDispatcherTests.Request_for_an_address_without_an_account_is_dropped_without_a_mail`, `MailRequestEndpointTests.Request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts`, `MailThroughSmtpTests.Forgot_for_an_unknown_address_sends_nothing`, `MailDispatcherTests.Requests_for_addresses_without_an_account_do_not_hold_up_a_real_mail`; e2e steps 6–7 |
| 3 | Reset → `204`; old password `401`, new one logs in | `ResetPasswordTests.Reset_replaces_the_password`; e2e steps 7–8 |
| 4 | Every refresh token issued before the reset is refused | `ResetPasswordTests.Reset_ends_every_session_of_the_account`, `ResetPasswordTests.Session_whose_login_overlapped_a_password_change_cannot_refresh`; e2e step 8 |
| 5 | Reset lifts the cooldown, confirms the email, kills the other links | `ResetPasswordTests.Reset_lifts_a_lockout_and_confirms_the_email_in_one_go`, `ResetPasswordTests.Reset_makes_the_other_links_of_the_account_unusable` |
| 6 | A token works once; unknown, used, expired, replaced → the same `invalid_token`; one of parallel uses succeeds | `EmailTokensTests.Token_works_once`, `EmailTokensTests.Token_expires_after_its_lifetime`, `EmailTokensTests.Issuing_again_replaces_the_earlier_token_of_that_kind_only`, `EmailTokensTests.Parallel_consumption_succeeds_exactly_once`, `ResetPasswordTests.Token_works_once`, `ResetPasswordTests.Unknown_used_expired_and_replaced_tokens_get_one_answer`, `ResetPasswordTests.Token_is_good_until_just_under_an_hour`, `VerifyEmailTests.Unknown_used_expired_and_replaced_tokens_get_one_answer`, `VerifyEmailTests.Expired_token_confirms_nothing`, `VerifyEmailTests.Token_is_good_until_just_under_24_hours`, `MailDispatcherTests.Newer_mail_replaces_the_earlier_token`; e2e steps 4, 7 |
| 7 | Weak password → `weak_password` with every broken rule; nothing changes; the token stays usable | `ResetPasswordTests.Weak_password_names_every_broken_rule_and_leaves_everything_as_it_was`; e2e step 7 |
| 8 | Policy: 8 characters, upper, lower, digit; no special character needed; letters of any script | `PasswordPolicyTests.Policy_is_eight_characters_upper_lower_and_digit`, `ResetPasswordTests.Password_without_a_special_character_is_accepted`, `ResetPasswordTests.Letters_of_any_script_satisfy_the_policy` |
| 9 | Mail limit: second request within 60 s and sixth within the hour refused; kinds independent; refusals do not add up | `MailLimitPolicyTests` (all), `MailRequestStoreTests.Refused_request_writes_nothing`, `MailRequestStoreTests.Kinds_and_addresses_have_limits_of_their_own`, `MailRequestEndpointTests.Second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait`, `MailRequestEndpointTests.Sixth_request_within_the_hour_is_refused_until_the_hour_is_over`, `MailRequestEndpointTests.Kinds_do_not_share_a_limit`, `EmailPruningTests.Limit_row_that_still_counts_survives_a_pass`; e2e step 3 |
| 10 | The `429` is identical for an address without an account | `MailRequestEndpointTests.Limit_answers_the_same_for_an_address_without_an_account` |
| 11 | A verification mail only for an unconfirmed account; same `202` for all | `MailRequestEndpointTests.Verification_mail_is_sent_only_to_an_unconfirmed_account`, `MailDispatcherTests.Verification_mail_goes_to_an_unconfirmed_account_only`, `VerifyEmailTests.Confirmed_account_gets_no_further_verification_mail` |
| 12 | Verify → `204`; the account can then log in | `VerifyEmailTests.Verification_confirms_the_email`, `UnverifiedLoginTests.After_verification_the_account_logs_in`; e2e steps 4–5 |
| 13 | Unconfirmed login: `403` with the right password, `401` with a wrong one, `429` in a cooldown; the `403` ends the streak | `UnverifiedLoginTests.Correct_password_for_an_unconfirmed_account_is_refused_with_its_own_answer`, `UnverifiedLoginTests.Wrong_password_for_an_unconfirmed_account_is_the_ordinary_401`, `UnverifiedLoginTests.During_a_cooldown_the_answer_is_the_lockout_not_the_refusal`, `UnverifiedLoginTests.Refusal_ends_the_streak_as_a_success_would`; e2e step 2 |
| 14 | A queued mail survives a restart; failed sends are retried on the schedule and dropped after an hour | `MailDispatcherTests.Request_recorded_before_a_restart_is_sent_after_it`, `MailDispatcherTests.Failed_send_is_retried_on_the_schedule_and_sent_once`, `MailDispatcherTests.Request_that_could_not_be_delivered_within_an_hour_is_dropped_and_logged`, `MailDispatcherTests.Two_dispatchers_at_once_try_every_request_exactly_once`, `MailDispatchServiceTests.Failed_send_is_retried_once_its_delay_has_passed_on_the_clock`; e2e step 9 |
| 15 | HTML + text, configured language, application name, lifetime, diacritics; no token in logs or database | `MailComposerTests.Reset_mail_in_english`, `MailComposerTests.Verification_mail_in_polish`, `SmtpMailTransportTests.Mail_arrives_with_both_parts_and_diacritics_intact`, `EmailTokensTests.Issued_token_is_43_url_safe_characters_and_only_its_hash_is_stored`, `MailDispatcherTests.No_token_and_no_mail_body_reach_the_log`; e2e step 4 |
| 16 | An email that cannot be used is a `400` on login and on the mail endpoints, on every host, and is not counted | `EmailInputTests` (all), `MailRequestEndpointTests.Malformed_request_is_a_400_and_is_not_counted`; e2e step 2 (the image without ICU) |
| 17 | A host with a missing or invalid mail setting does not start | `MailSettingsTests` (all) |
| 18 | The e2e script drives both flows; the three existing scripts still pass | `scripts/e2e-email.sh`; `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh` unedited |

  plus a Review Focus table (the five lines above → their tests:
  1 `ResetPasswordTests.Parallel_resets_with_one_token_change_the_password_once`,
  `VerifyEmailTests.Parallel_verifications_with_one_token_succeed_once`;
  2 `MailRequestEndpointTests.Spelling_variants_of_one_address_share_the_limit_and_reach_the_account`;
  3 `MailComposerTests.Application_name_is_encoded_in_the_html_part_only`,
  `MailComposerTests.Link_keeps_a_query_string_the_frontend_url_already_has`;
  4 `MailDispatcherTests.Failed_send_is_retried_on_the_schedule_and_sent_once`,
  `MailDispatcherTests.Failed_send_leaves_the_earlier_link_in_force`,
  `MailDispatcherTests.Two_dispatchers_at_once_try_every_request_exactly_once`;
  5 `ResetPasswordTests.Reset_lifts_a_lockout_and_confirms_the_email_in_one_go`), a
  "Plan-vs-implementation notes" section (every name or behaviour that differed
  from this plan, per task), a short table of the contract sentences that are
  not acceptance criteria and their tests (`ResetPasswordTests.Unusable_token_is_reported_before_a_weak_password`,
  `ResetPasswordTests.Reset_does_not_clear_the_mail_limit`,
  `PasswordPolicyTests.Policy_does_not_block_a_login_with_a_password_set_before_it`,
  `EmailInputTests.Login_does_not_apply_the_length_limit_of_the_mail_endpoints`,
  `MailRequestEndpointTests.Only_post_is_allowed`) and an empty "Local
  verification log" the orchestrator fills after verification.

**README.** In the quickstart block add
`scripts/e2e-email.sh            # verification → reset → sessions end → mail outage (~1 min)`
after the `e2e-lockout.sh` line, and one line below the block: the stack includes
a mail catcher whose inbox is at `http://localhost:8025`. Extend the status note
with one sentence: password reset and email verification by mail are in
([spec 0004](docs/superpowers/specs/0004-email-flows.md)).

- [ ] **Step 1: Write** the compose change, `.env.example`, the script, the
  acceptance map and the README change. Keep every test name in the map in sync
  with the code (`grep` each one).
- [ ] **Step 2: Run the e2e on a clean stack.** `cp .env.example .env` →
  `scripts/dev-keys.sh` → `docker compose -f deploy/docker-compose.yml --env-file .env down -v` →
  `… up -d --build` → `scripts/e2e-login.sh` → `scripts/e2e-refresh.sh` →
  `scripts/e2e-lockout.sh` → `scripts/e2e-email.sh` → `… down -v`. Expected: all
  four end with `ALL PASS` (the first three unedited). Then remove `.env` and
  `.secrets/`. Record in the acceptance map's notes how long the mail of step 9
  took to arrive.
- [ ] **Step 3: Run the final local gate.**
  `dotnet format --verify-no-changes && dotnet build -warnaserror && dotnet test`, then
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`, then
  `git grep -nE "PRIVATE KEY|Password=" -- ':!*.md' ':!.env.example'` (only the two
  known hits: the `${POSTGRES_PASSWORD…}` placeholder in `deploy/docker-compose.yml`
  and an assertion string in `KeyMaterialTests.cs`) and
  `git status --porcelain` (no `.env`, no `.secrets/`).
- [ ] **Step 4: Commit** — `test(e2e): email flows over the real network; acceptance map for spec 0004`

---

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context,
   read-only), as defined in [`docs/workflow.md`](../../workflow.md#verification):
   realization vs **spec** (8 layers, using the acceptance map), API/e2e (clean
   stack with the mail catcher, the four e2e scripts + independent probes),
   security (no enumeration through body, headers or timing on the two mail
   endpoints; no clear token in the database, the queue or the logs; a token
   cannot be used twice; nothing a requester submits reaches a mail or a header;
   the `403` needs the password; diff). Only one of them builds and tests in the
   tree; only one uses compose and ports 8080 and 8025.
2. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the
   issue goes to the owner.
3. Record the outcome: the verification log in the acceptance map, and an
   `## As built (owner, date)` section in spec 0004.
4. When every verifier passes, merge the feature branch into `main` locally.

## Open questions for owner

**All nine were answered by the owner before implementation (2026-01-26):**
questions 1–6 and 9 accepted as proposed; question 7 — letters and digits of any
script count (built into Task 1, spec Decision 20); question 8 — yes, sessions
remember the security stamp (Task 8, spec Decision 17). They are kept here as
asked.

1. **The mail texts are C# tables, not `.resx` files** as design.md sketched. The
   container image runs without ICU, where a culture such as `pl` cannot be
   created, so culture-selected resources would not load there (see "Verified
   before this plan was written"). Accept, or keep `.resx` with two
   culture-neutral files?
2. **The dispatcher keeps one transaction, and the lock on one queue row, open
   while it talks to the mail server** — at most 20 seconds per attempt (the send
   has a deadline). That is what lets a failed send leave the earlier link in
   force. While it lasts, a click on the **earlier** link of the same account
   waits, and is then told `invalid_token` if the new mail went out. The
   alternative commits the new token first and sends afterwards: no transaction
   across the network, but a failed send has already invalidated the link the
   user holds. Default proposal: as planned.
3. **Delivery is at-least-once.** If the process dies between the mail server
   accepting a mail and the commit, the mail goes out again after the restart
   with a new link, and the link of the first mail does not work (its token was
   never committed). Acceptable?
4. **Three more background loops** (the dispatcher, and pruning of tokens and
   limits as one more copy of the pruning service — escalation E3 of slice 3 now
   covers three copies). The dispatcher also runs a pass at host start.
5. **The e2e script is one-shot per stack** (it confirms the second seed user and
   changes its password) and it stops and starts the mail catcher to prove the
   retry. A second run needs `down -v`.
6. **The sender's display name is the application name** (`Auth:App:Name`), and
   the development sender is `no-reply@auth-core.localhost`.
7. **Uppercase, lowercase and digit mean A–Z, a–z and 0–9.** That is how ASP.NET
   Identity checks them: `Zażółć123Ż` has no uppercase letter in its eyes (`Ż` is
   not counted) and would be refused with `requires_upper`. For a Polish product
   that may surprise. Keep Identity's rule, or count any Unicode letter (a small
   validator of our own)?
8. **Sessions now remember the account's security stamp** (Task 8, Step 5). It
   was not in the brainstorm: the review found that someone who knows the old
   password and keeps logging in while the owner resets could keep a session,
   because revocation only ends the sessions that exist at that moment. It adds a
   claim to the refresh token and one comparison to the refresh endpoint, in
   `Sessions/` (slice 2's code). A session issued before this change has no stamp
   and is refused at its next refresh — only development databases hold such.
9. **Left as they are, on purpose:** two requests that change one account at the
   same instant with different tokens (a reset and a verification) — one of them
   gets a `500` and nothing is half-done, its token stays usable; a reset mail
   that is being composed at the very instant another reset of the same account
   commits may leave a working link (only for the mailbox owner); a reset works
   for an account that has no password yet, and confirms it — relevant when
   invitations arrive in week 3.

## As built

Implemented and verified locally. Tasks 1–12 were built as written here: every code block
compiled and passed the analyzers unchanged (the blocks had been run in a scratch copy
before implementation). What changed after the plan — the retry timing after Task 6's
review, the dispatcher's per-row selection and three smaller fixes after the verifiers, and
one check in the e2e script — is listed in the [acceptance map](0004-acceptance-map.md)
("Plan-vs-implementation notes", "Local verification log") and in spec 0004 → "As built".
The owner's answers to the open questions above are recorded at the top of that section.
