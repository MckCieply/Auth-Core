using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Invitations, the mail: the paragraph for an address that has a private account spelled the same up to ASCII case.</summary>
public sealed class PrivateInvitationMailTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Paragraph = "You already have a private account on " + AuthAppFactory.DefaultAppName + " with this email address.";

    private async Task<ComposedMail> InvitationMailAsync(string address)
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return Mail.Sent.Last(m => m.To == address);
    }

    [Fact]
    public async Task An_invitation_to_an_address_with_a_private_account_has_the_changed_intro_and_the_paragraph()   // criterion 16
    {
        await CreatePrivateAccountAsync("ola@poczta.test");

        var mail = await InvitationMailAsync("ola@poczta.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(Paragraph, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal("You are invited to " + AuthAppFactory.DefaultAppName, mail.Subject);
    }

    [Fact]
    public async Task Other_ascii_capitals_of_the_address_get_the_paragraph_too()   // criterion 16: spelled the same up to ASCII case
    {
        await CreatePrivateAccountAsync("Ola@Poczta.test");

        var mail = await InvitationMailAsync("ola@POCZTA.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_without_an_account_or_with_a_company_account_gets_the_mail_of_spec_0005()   // criterion 16
    {
        await AddMemberAsync(await DevCompanyIdAsync(), "member@poczta.test", "user");

        var unknown = await InvitationMailAsync("nobody@poczta.test");
        var member = await InvitationMailAsync("member@poczta.test");

        foreach (var mail in new[] { unknown, member })
        {
            Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("choose a password and accept the invitation", mail.TextBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_look_alike_spelling_of_a_private_account_gets_no_paragraph()   // criterion 16
    {
        // The Greek small mu and the micro sign: the normaliser folds them into one key, but they are two mailboxes.
        var account = char.ConvertFromUtf32(0x03BC) + "aria@poczta.test";
        var invited = char.ConvertFromUtf32(0x00B5) + "aria@poczta.test";
        await CreatePrivateAccountAsync(account);

        var mail = await InvitationMailAsync(invited);

        Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }
}
