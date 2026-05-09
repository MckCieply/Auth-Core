using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary><c>DELETE /auth/org</c> as the sample sends it: the access token, the body and, for a ticket, its <c>auth_pt</c>.</summary>
public static class OrgApi
{
    public const string Path = "/auth/org";

    public static async Task<HttpResponseMessage> Delete(HttpClient client, string accessToken, object body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{ProviderApi.BindingCookie}={binding}");
        }

        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> DeleteWithTicket(HttpClient client, string accessToken, string name, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Delete(client, accessToken, new { name, provider_ticket = ticket.Value }, ticket.Binding);
    }
}
