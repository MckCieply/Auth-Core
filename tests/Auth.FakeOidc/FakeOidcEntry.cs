using Microsoft.AspNetCore.Builder;

namespace Auth.FakeOidc;

/// <summary>
/// The fake provider as a process: the end-to-end stack runs it as a container (<c>tests/Auth.FakeOidc/compose.yml</c>). Every request goes to
/// <see cref="FakeOidcServer"/>. The port comes from <c>ASPNETCORE_HTTP_PORTS</c> (8099 in the image), the two origins from
/// <c>FakeOidc__IssuerOrigin</c> and <c>FakeOidc__BrowserOrigin</c> (<see cref="FakeOidcOptions"/>).
/// </summary>
public static class FakeOidcEntry
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        using var server = new FakeOidcServer(FakeOidcOptions.From(app.Configuration), TimeProvider.System);
        app.Run(server.HandleAsync);
        await app.RunAsync();
    }
}
