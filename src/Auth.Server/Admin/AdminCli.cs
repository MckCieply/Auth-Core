using System.Globalization;
using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.Server.Admin;

/// <summary>
/// <c>auth-server admin ...</c>: the operator's commands (spec 0005 → Operator CLI), run by the same binary and image as the
/// service and working directly on its database with its configuration. It builds the host's services and never runs the
/// host: no listener, no background service, no key material. It does not migrate: a database the service has not
/// migrated is refused. Output is plain text, a refusal is <c>error: &lt;code&gt;</c> on the error stream and a non-zero
/// exit code, and nothing it prints or logs carries a connection string, a password or a token.
/// </summary>
public static class AdminCli
{
    public const int Done = 0;

    /// <summary>The command was understood and refused, with the error code it printed.</summary>
    public const int Refused = 1;

    /// <summary>The arguments were not understood.</summary>
    public const int UsageError = 2;

    /// <summary>The command could not be carried out: no database, a database that is not migrated, a broken setting.</summary>
    public const int Failed = 3;

    /// <param name="args">What follows <c>admin</c> on the command line.</param>
    /// <param name="configure">Lets a test add configuration; the command line itself never reaches the configuration.</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Action<WebApplicationBuilder>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var parsed = AdminArguments.Parse(args);
        if (parsed.Command is not { } command)
        {
            await error.WriteLineAsync($"error: {parsed.Error}");
            await error.WriteLineAsync(AdminArguments.Usage);
            return UsageError;
        }

        try
        {
            // An empty argument list: the operator's own words (--force, --name) are not configuration.
            var builder = WebApplication.CreateBuilder([]);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            // Before the registrations below, which read the configuration as they are made.
            configure?.Invoke(builder);
            builder.Services.AddAuthPersistence(builder.Configuration);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AuthDbContext>());
            builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);

            await using var app = builder.Build();
            await app.Services.GetRequiredService<ManifestActivator>().ActivateAsync(cancellationToken);

            using var scope = app.Services.CreateScope();
            var operatorCommands = scope.ServiceProvider.GetRequiredService<OperatorCommands>();
            return await ExecuteAsync(command, operatorCommands, output, error, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type name only: the text of a database failure can quote the connection string. The one message that is
            // safe, and what the operator needs when a setting is missing, is our own: it names the key and never a value.
            var detail = exception is InvalidOperationException && exception.Message.StartsWith("Configuration value '", StringComparison.Ordinal)
                ? $": {exception.Message}"
                : "";
            await error.WriteLineAsync($"error: failed ({exception.GetType().Name}){detail}");
            return Failed;
        }
    }

    private static async Task<int> ExecuteAsync(
        AdminCommand command, OperatorCommands operatorCommands, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case CreateOrgCommand create:
                var created = await operatorCommands.CreateOrgAsync(create.Name, cancellationToken);
                if (!created.Succeeded)
                {
                    return await RefusedAsync(created.Without, error);
                }

                await output.WriteLineAsync(created.Value.ToString("D", CultureInfo.InvariantCulture));
                return Done;

            case ListOrgsCommand:
                foreach (var org in await operatorCommands.ListOrgsAsync(cancellationToken))
                {
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{org.Id:D}\t{org.Name}\t{org.Members}"));
                }

                return Done;

            case InviteCommand invite:
                var invited = await operatorCommands.InviteAsync(invite.Org, invite.Email, invite.Role, cancellationToken);
                if (!invited.Succeeded)
                {
                    return await RefusedAsync(invited, error);
                }

                await output.WriteLineAsync("Invitation queued. The server mails it at its next poll, within a minute, and only while it is running.");
                return Done;

            case RemoveMemberCommand remove:
                var removed = await operatorCommands.RemoveMemberAsync(remove.Org, remove.Email, remove.Force, cancellationToken);
                if (!removed.Succeeded)
                {
                    return await RefusedAsync(removed, error);
                }

                await output.WriteLineAsync("Member removed; every session of the account has ended.");
                return Done;

            default:
                throw new InvalidOperationException("A command that the parser makes is not handled.");
        }
    }

    private static async Task<int> RefusedAsync(Outcome outcome, TextWriter error)
    {
        var wait = outcome.Error == TenancyErrors.TooManyAttempts
            ? string.Create(CultureInfo.InvariantCulture, $" (retry in {Math.Max(1, (long)Math.Ceiling(outcome.RetryAfter.TotalSeconds))} seconds)")
            : "";
        await error.WriteLineAsync($"error: {outcome.Error}{wait}");
        return Refused;
    }
}
