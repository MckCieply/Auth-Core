using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 14: one answer for every dead ticket, and one sign-in of parallel exchanges.</summary>
public sealed class ProviderTicketTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private async Task<ProviderApi.Ticket> LinkedTicketAsync(string query = "")
    {
        var user = await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return await TicketAsync("google", GooglePerson(), query);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("reauth")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    public async Task A_ticket_that_is_unknown_used_expired_of_another_purpose_or_without_its_binding_is_invalid_ticket(string how)   // criterion 14, Review Focus 2
    {
        var ticket = await LinkedTicketAsync(how == "reauth" ? "?purpose=reauth" : "");
        var value = ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "unknown":
                value = "unknownunknown" + ticket.Value[14..];
                break;
            case "used":
                using (var first = await ProviderApi.Exchange(Client, ticket))
                {
                    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                }

                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(10));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        using var response = await ProviderApi.Exchange(Client, value, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task Of_parallel_exchanges_with_one_ticket_exactly_one_signs_in()   // criterion 14
    {
        var ticket = await LinkedTicketAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ProviderApi.Exchange(Client, ticket)));
        try
        {
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Equal(4, answers.Count(a => a.StatusCode == HttpStatusCode.BadRequest));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_second_round_trip_in_the_same_browser_makes_the_first_ticket_unusable()   // Settled 9, Review Focus 2
    {
        var first = await LinkedTicketAsync();
        var second = await TicketAsync("google", GooglePerson());

        // The browser holds the auth_pt of the second round trip only: the callback replaced it.
        using var older = await ProviderApi.Exchange(Client, first.Value, second.Binding);
        using var newer = await ProviderApi.Exchange(Client, second);

        await TenancyApi.AssertErrorAsync(older, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
    }
}
