using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Runs a step once, just before the first command of a sign-in that takes a row lock: <c>FOR NO KEY UPDATE</c> (the membership lock of a
/// login, of a provider exchange and of a refresh) or <c>FOR KEY SHARE</c> (the membership hold of a refresh that holds, the account re-read).
/// A sign-in locks its membership before it re-reads the account (spec 0015 → Lock order): a step that deletes the account just before the
/// account re-read would wait for the membership lock of the sign-in while the sign-in waits for the delete. Fired here, the delete removes
/// the membership by cascade before any lock is taken, and the sign-in finds its space gone.
/// With <see cref="Pattern"/> set, the step runs just before the (<see cref="Skip"/> + 1)-th command whose text contains it instead.
/// </summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }

    /// <summary>The text of the command to stop before; unset (<see langword="null"/>), any command that takes a row lock: <c>FOR NO KEY UPDATE</c> or <c>FOR KEY SHARE</c>.</summary>
    public string? Pattern { get; set; }

    /// <summary>How many matching commands pass before the step runs (0: the first one).</summary>
    public int Skip { get; set; }

    public bool Fired { get; private set; }

    private bool Matches(string text) => Pattern is null
        ? text.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal) || text.Contains("FOR KEY SHARE", StringComparison.Ordinal)
        : text.Contains(Pattern, StringComparison.Ordinal);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        var step = Before;
        if (step is not null && Matches(command.CommandText) && Skip-- <= 0)
        {
            Before = null;
            Fired = true;
            await step();
        }

        return result;
    }
}
