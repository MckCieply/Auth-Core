using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Throws on the insert of a session's row in <c>SessionStates</c> while armed: the last write of the sign-in's transaction, so that a renewal
/// fails after its logins are deleted (or its hash set) and its sessions revoked. Register it with
/// <c>Factory.WithServices(services => services.ConfigureDbContext&lt;AuthDbContext&gt;(options => options.AddInterceptors(fault)))</c> before the host starts.
/// </summary>
internal sealed class SessionRowFault : DbCommandInterceptor
{
    public bool Armed { get; set; }

    public bool Fired { get; private set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Fail(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Fail(command);
        return ValueTask.FromResult(result);
    }

    private void Fail(DbCommand command)
    {
        if (Armed && command.CommandText.Contains("INSERT INTO \"SessionStates\"", StringComparison.Ordinal))
        {
            Fired = true;
            throw new InvalidOperationException("the renewed session cannot be written, for the test");
        }
    }
}
