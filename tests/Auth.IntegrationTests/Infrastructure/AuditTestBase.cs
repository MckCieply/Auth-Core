using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the audit tests: a company-capable host (invitations are mails) whose requests may name their client address in
/// <see cref="AuthAppFactory.RemoteAddressHeader"/>, and without the hourly pruning pass, which the fake clock would fire.
/// </summary>
public abstract class AuditTestBase : TenancyTestBase
{
    protected const string WrongPassword = "Wrong-Password-1";

    /// <summary>The address <see cref="SendFromCallerAsync"/> sends from, which a row of the request's change carries.</summary>
    protected const string CallerAddress = "203.0.113.5";

    protected AuditTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader().WithoutHostedService<AuditPruningService>();
    }

    /// <summary>A company API call with a bearer token that names <see cref="CallerAddress"/> as its client address.</summary>
    protected async Task<HttpResponseMessage> SendFromCallerAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = TenancyApi.Request(method, path, token, body);
        request.Headers.Add(AuthAppFactory.RemoteAddressHeader, CallerAddress);
        return await Client.SendAsync(request);
    }

    /// <summary>The rows of the audit log, of one kind or all, oldest first.</summary>
    protected Task<List<AuditEvent>> AuditAsync(string? kind = null) =>
        InDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => kind == null || a.Kind == kind)
            .OrderBy(a => a.OccurredAt).ThenBy(a => a.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

    protected async Task<AuditEvent> SingleAsync(string kind) => Assert.Single(await AuditAsync(kind));

    /// <summary>No password, token, link or other secret in any column of any row: the rows are searched for each of the texts.</summary>
    protected async Task AssertNoSecretsAsync(params string[] secrets)
    {
        var rows = await AuditAsync();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var text = string.Join(
                '\n',
                new[] { row.Kind, row.SubjectEmail, row.OrgName, row.ClientIp, row.Details }.Where(v => v is not null));
            foreach (var secret in secrets)
            {
                Assert.False(string.IsNullOrEmpty(secret));
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }
}

public static class AuditApi
{
    /// <summary>A string detail of a row (<c>jsonb</c> comes back in PostgreSQL's own form, so the row is parsed).</summary>
    public static string? Text(AuditEvent row, string detail)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(row.Details);
        return document.RootElement.TryGetProperty(detail, out var value) ? value.ToString() : null;
    }

    /// <summary>A detail of a row as compact JSON text (an array stays an array), or null.</summary>
    public static string? Raw(AuditEvent row, string detail)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(row.Details);
        return document.RootElement.TryGetProperty(detail, out var value)
            ? JsonSerializer.Serialize(value)
            : null;
    }

    /// <summary>The names of the details of a row, sorted.</summary>
    public static string[] DetailNames(AuditEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(row.Details);
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }
}
