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
    }
}
