using System.Globalization;
using Auth.Server.Admin;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Runs the operator's command the way the container does: the same code, the database and manifest of a test host.</summary>
public static class OperatorCli
{
    public sealed record Result(int Exit, string Out, string Error);

    public static async Task<Result> RunAsync(AuthAppFactory factory, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _ = factory.Services;   // starts the host, which migrates and seeds the database
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            args,
            output,
            error,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Auth"] = factory.ConnectionString,
                [ManifestSettings.PathKey] = factory.ManifestPath,
            }),
            TestContext.Current.CancellationToken);
        return new Result(exit, output.ToString(), error.ToString());
    }
}
