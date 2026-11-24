using Auth.Infrastructure.Persistence;

namespace Auth.IntegrationTests;

public sealed class MailKindTests
{
    [Fact]
    public void The_values_of_the_kinds_are_stored_and_never_renumbered()
    {
        Assert.Equal((short)1, (short)MailKind.PasswordReset);
        Assert.Equal((short)2, (short)MailKind.EmailVerification);
        Assert.Equal((short)3, (short)MailKind.Invitation);
        Assert.Equal((short)4, (short)MailKind.Signup);
        Assert.Equal((short)5, (short)MailKind.ProviderLinked);   // spec 0012 → Mails: the three notices
        Assert.Equal((short)6, (short)MailKind.ProviderUnlinked);
        Assert.Equal((short)7, (short)MailKind.PasswordChanged);
    }

    [Fact]
    public void The_key_of_the_notice_limit_is_a_value_of_its_own()   // spec 0012, Amendment A10: never the kind of a queued mail
    {
        Assert.Equal((short)100, (short)MailKind.NoticeLimit);
        Assert.Equal(8, Enum.GetValues<MailKind>().Length);
    }
}
