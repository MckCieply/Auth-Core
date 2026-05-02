using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Runs a step once, just before the first command that reads a row under a share lock.</summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }

    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        var step = Before;
        if (step is not null && command.CommandText.Contains("FOR KEY SHARE", StringComparison.Ordinal))
        {
            Before = null;
            Fired = true;
            await step();
        }

        return result;
    }
}
