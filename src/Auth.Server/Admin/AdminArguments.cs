namespace Auth.Server.Admin;

/// <summary>A command of <c>auth-server admin</c>, parsed.</summary>
public abstract record AdminCommand;

public sealed record CreateOrgCommand(string Name) : AdminCommand;

public sealed record InviteCommand(string Org, string Email, string Role) : AdminCommand;

public sealed record ListOrgsCommand : AdminCommand;

public sealed record RemoveMemberCommand(string Org, string Email, bool Force) : AdminCommand;

public sealed record DeleteOrgCommand(string Org, string Confirm) : AdminCommand;

/// <summary>The outcome of reading the arguments: a command, or the code of what is wrong with them.</summary>
public readonly record struct AdminParseResult(AdminCommand? Command, string? Error);

/// <summary>
/// Reads the arguments of the five operator commands by hand (spec 0005 → Operator CLI, spec 0008): <c>--name value</c> or
/// <c>--name=value</c> in any order, and the flag <c>--force</c> on <c>remove-member</c>. Five commands do not need a
/// command-line library, and a library is one more dependency in an image that has to stay small.
/// </summary>
public static class AdminArguments
{
    public const string Usage =
        """
        usage: auth-server admin <command>
          create-org    --name <name>
          invite        --org <id> --email <email> --role <name>
          list-orgs
          remove-member --org <id> --email <email> [--force]
          delete-org    --org <id> --confirm <name>
        """;

    private static readonly Dictionary<string, (string[] Options, string[] Flags)> Commands = new(StringComparer.Ordinal)
    {
        ["create-org"] = (["name"], []),
        ["invite"] = (["org", "email", "role"], []),
        ["list-orgs"] = ([], []),
        ["remove-member"] = (["org", "email"], ["force"]),
        ["delete-org"] = (["org", "confirm"], []),
    };

    /// <param name="args">What follows <c>admin</c> on the command line.</param>
    public static AdminParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return Fail("missing_command");
        }

        if (!Commands.TryGetValue(args[0], out var spec))
        {
            return Fail("unknown_command");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return Fail("unexpected_argument");
            }

            var (name, inline) = Split(argument[2..]);
            if (spec.Flags.Contains(name, StringComparer.Ordinal) && inline is null)
            {
                if (!flags.Add(name))
                {
                    return Fail("duplicate_option");
                }

                continue;
            }

            if (!spec.Options.Contains(name, StringComparer.Ordinal))
            {
                return Fail("unknown_option");
            }

            string value;
            if (inline is not null)
            {
                value = inline;
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                return Fail("missing_value");
            }

            if (!values.TryAdd(name, value))
            {
                return Fail("duplicate_option");
            }
        }

        if (spec.Options.Any(option => !values.ContainsKey(option)))
        {
            return Fail("missing_option");
        }

        return new AdminParseResult(
            args[0] switch
            {
                "create-org" => new CreateOrgCommand(values["name"]),
                "invite" => new InviteCommand(values["org"], values["email"], values["role"]),
                "list-orgs" => new ListOrgsCommand(),
                "delete-org" => new DeleteOrgCommand(values["org"], values["confirm"]),
                _ => new RemoveMemberCommand(values["org"], values["email"], flags.Contains("force")),
            },
            null);
    }

    private static (string Name, string? Value) Split(string option)
    {
        var equals = option.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (option, null) : (option[..equals], option[(equals + 1)..]);
    }

    private static AdminParseResult Fail(string error) => new(null, error);
}
