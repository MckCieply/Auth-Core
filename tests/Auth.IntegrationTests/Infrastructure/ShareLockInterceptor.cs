using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Runs a step once, just before the command that executes as the (<see cref="Skip"/> + 1)-th one whose text contains <see cref="Pattern"/>
/// (by default the first read of a row under <c>FOR KEY SHARE</c>); <see cref="Skip"/> counts down as matching commands pass while a step is armed.
/// </summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }

    /// <summary>The text of the command to stop before: <c>FOR KEY SHARE</c> (the account's row at a sign-in) unless set.</summary>
    public string Pattern { get; set; } = "FOR KEY SHARE";

    /// <summary>How many commands with the <see cref="Pattern"/> pass before the step runs (0: the first one).</summary>
    public int Skip { get; set; }

    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        var step = Before;
        if (step is not null && command.CommandText.Contains(Pattern, StringComparison.Ordinal) && Skip-- <= 0)
        {
            Before = null;
            Fired = true;
            await step();
        }

        return result;
    }
}
