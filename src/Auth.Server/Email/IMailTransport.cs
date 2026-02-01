namespace Auth.Server.Email;

/// <summary>Hands a mail over to a mail server. Throws when the server did not accept it.</summary>
public interface IMailTransport
{
    Task SendAsync(ComposedMail mail, CancellationToken cancellationToken);
}
