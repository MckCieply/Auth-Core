using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0017 → Mails (changes spec 0010): the paragraph for an address that has an account of any kind, spelled the same up to ASCII case.</summary>
public sealed class PrivateInvitationMailTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Paragraph = "You already have an account on " + AuthAppFactory.DefaultAppName + " with this email address.";

    private async Task<ComposedMail> InvitationMailAsync(string address)
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return Mail.Sent.Last(m => m.To == address);
    }

    private static void AssertNoParagraphOfSpec0010(ComposedMail mail) =>
        Assert.DoesNotContain("private account on", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);

    [Fact]
    public async Task An_invitation_to_a_private_account_has_the_intro_and_the_paragraph_of_an_existing_account()   // criterion 21
    {
        await CreatePrivateAccountAsync("ola@poczta.test");

        var mail = await InvitationMailAsync("ola@poczta.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(Paragraph, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal("You are invited to " + AuthAppFactory.DefaultAppName, mail.Subject);
        AssertNoParagraphOfSpec0010(mail);
    }

    [Fact]
    public async Task Other_ascii_capitals_of_the_address_get_the_paragraph_too()   // criterion 21: spelled the same up to ASCII case
    {
        await CreatePrivateAccountAsync("Ola@Poczta.test");

        var mail = await InvitationMailAsync("ola@POCZTA.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
        AssertNoParagraphOfSpec0010(mail);
    }

    [Fact]
    public async Task An_address_without_an_account_gets_the_mail_of_spec_0005_and_a_company_account_or_both_kinds_get_the_paragraph()   // criterion 21
    {
        var dev = await DevCompanyIdAsync();
        await AddMemberAsync(dev, "member@poczta.test", "user");
        var (both, _) = await CreatePrivateAccountAsync("both@poczta.test");
        await AddMembershipAsync(both, dev, "user");

        var unknown = await InvitationMailAsync("nobody@poczta.test");
        var member = await InvitationMailAsync("member@poczta.test");
        var bothKinds = await InvitationMailAsync("both@poczta.test");

        Assert.DoesNotContain("account on", unknown.TextBody + unknown.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("choose a password and accept the invitation", unknown.TextBody, StringComparison.Ordinal);
        foreach (var mail in new[] { member, bothKinds })
        {
            Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
            Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
            AssertNoParagraphOfSpec0010(mail);
        }
    }

    [Fact]
    public async Task A_look_alike_spelling_of_a_private_account_gets_no_paragraph()   // criterion 21
    {
        // The Greek small mu and the micro sign: the normaliser folds them into one key, but they are two mailboxes.
        var account = char.ConvertFromUtf32(0x03BC) + "aria@poczta.test";
        var invited = char.ConvertFromUtf32(0x00B5) + "aria@poczta.test";
        await CreatePrivateAccountAsync(account);

        var mail = await InvitationMailAsync(invited);

        Assert.DoesNotContain("account on", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("choose a password and accept the invitation", mail.TextBody, StringComparison.Ordinal);
    }
}
