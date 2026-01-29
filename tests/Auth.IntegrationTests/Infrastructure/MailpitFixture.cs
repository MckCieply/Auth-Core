using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Auth.IntegrationTests.Infrastructure;

public sealed record MailpitMessage(string Subject, string Text, string Html, string FromName, string FromAddress, string Raw);

/// <summary>
/// One Mailpit container (a mail catcher: SMTP in, HTTP API out) for a test class that needs a real SMTP server.
/// Tests keep apart by sending to an address of their own and searching for it.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private const int Smtp = 1025;
    private const int Api = 8025;

    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.31.3")
        .WithPortBinding(Smtp, assignRandomHostPort: true)
        .WithPortBinding(Api, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Api).ForPath("/readyz")))
        .Build();

    private HttpClient _api = null!;

    public string Host => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(Smtp);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _api = new HttpClient { BaseAddress = new Uri($"http://{Host}:{_container.GetMappedPublicPort(Api)}") };
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        await _container.DisposeAsync();
    }

    public async Task<int> CountAsync(string to)
    {
        var found = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
        return found.GetProperty("messages_count").GetInt32();
    }

    /// <summary>The one mail sent to <paramref name="to"/>, waiting up to 15 seconds for it to arrive.</summary>
    public async Task<MailpitMessage> WaitForMailAsync(string to)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var found = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
            if (found.GetProperty("messages_count").GetInt32() > 0)
            {
                var id = found.GetProperty("messages")[0].GetProperty("ID").GetString();
                var message = await _api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}");
                var raw = await _api.GetStringAsync($"/api/v1/message/{id}/raw");
                var from = message.GetProperty("From");
                return new MailpitMessage(
                    message.GetProperty("Subject").GetString()!,
                    message.GetProperty("Text").GetString()!,
                    message.GetProperty("HTML").GetString()!,
                    from.GetProperty("Name").GetString()!,
                    from.GetProperty("Address").GetString()!,
                    raw);
            }

            Assert.True(DateTime.UtcNow < deadline, $"No mail for {to} arrived within 15 seconds.");
            await Task.Delay(100);
        }
    }
}
