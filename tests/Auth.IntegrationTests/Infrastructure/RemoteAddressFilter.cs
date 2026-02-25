using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Gives a request of the test host the client address its header names. The test server has no remote address of its own, and
/// the rate limiter and the audit log are about that address. Runs before the pipeline of the service, like the connection of
/// a real server would be.
/// </summary>
public sealed class RemoteAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(AuthAppFactory.RemoteAddressHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware();
            });
            next(app);
        };
    }
}
