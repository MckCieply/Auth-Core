using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails: the private mail and the new sentence in the instance's language.</summary>
public sealed class PrivateSignupDispatchPolishTests : SignupTestBase
{
    public PrivateSignupDispatchPolishTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup().WithSetting(MailSettingsLoader.LocaleKey, "pl");
    }

    [Fact]
    public async Task The_private_mail_and_the_notice_are_in_polish()   // criteria 4, 5, 31
    {
        using (var request = await SignupApi.RequestPrivate(Client, "ola@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        }

        await CreatePrivateAccountAsync("jan@poczta.test");
        using (var notice = await SignupApi.RequestPrivate(Client, "jan@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, notice.StatusCode);
        }

        await DispatchAsync();

        var link = Assert.Single(Mail.Sent, m => m.To == "ola@poczta.test");
        Assert.Contains("poprosił o założenie konta prywatnego w " + AuthAppFactory.DefaultAppName, link.TextBody, StringComparison.Ordinal);
        var exists = Assert.Single(Mail.Sent, m => m.To == "jan@poczta.test");
        Assert.Equal("Masz już konto w " + AuthAppFactory.DefaultAppName, exists.Subject);
        Assert.Contains("Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.", exists.TextBody, StringComparison.Ordinal);
    }
}
