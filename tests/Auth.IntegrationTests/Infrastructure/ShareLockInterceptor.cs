using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Runs a step once, just before the first command of a sign-in that takes a row lock: <c>FOR NO KEY UPDATE</c> (the membership lock of a
/// login, of a provider exchange and of a refresh) or <c>FOR KEY SHARE</c> (the membership hold of a refresh that holds, the account re-read).
/// A sign-in locks its membership before it re-reads the account (spec 0015 → Lock order): a step that deletes the account just before the
/// account re-read would wait for the membership lock of the sign-in while the sign-in waits for the delete. Fired here, the delete removes
/// the membership by cascade before any lock is taken, and the sign-in finds its space gone.
/// </summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }

    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        var step = Before;
        if (step is not null && (command.CommandText.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)
                || command.CommandText.Contains("FOR KEY SHARE", StringComparison.Ordinal)))
        {
            Before = null;
            Fired = true;
            await step();
        }

        return result;
    }
}
