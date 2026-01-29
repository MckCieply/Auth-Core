using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace Auth.Server.Email;

/// <summary>
/// Sends through the configured SMTP server with MailKit: one connection per mail, which is plenty for the two
/// mails this service sends. Any failure surfaces as an exception; the dispatcher decides about retries.
/// </summary>
public sealed class SmtpMailTransport(MailSettings settings, TimeProvider clock) : IMailTransport
{
    /// <summary>How long one network operation may take before the attempt counts as failed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a whole send may take. <see cref="Timeout"/> is per operation, and a send is about ten of them: a
    /// server that answers each one slowly must not hold the dispatcher, and the queue row it has locked, for minutes.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public async Task SendAsync(ComposedMail mail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mail);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.AppName, settings.FromAddress));
        message.To.Add(new MailboxAddress(string.Empty, mail.To));
        message.Subject = mail.Subject;
        message.Date = clock.GetUtcNow();
        // MimeKit would otherwise build the id from this machine's host name, and MailKit would greet the server
        // with it (the server then writes it into a Received header). The machine's name is nobody's business.
        var senderDomain = settings.FromAddress[(settings.FromAddress.LastIndexOf('@') + 1)..];
        message.MessageId = MimeUtils.GenerateMessageId(senderDomain);
        message.Body = new BodyBuilder { TextBody = mail.TextBody, HtmlBody = mail.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds, LocalDomain = senderDomain };
        var security = settings.Smtp.Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.SslOnConnect,
        };
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, security, deadline.Token);
        if (settings.Smtp is { Username: { } username, Password: { } password })
        {
            await client.AuthenticateAsync(username, password, deadline.Token);
        }

        await client.SendAsync(message, deadline.Token);
        try
        {
            await client.DisconnectAsync(quit: true, deadline.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The server has accepted the mail. A goodbye that fails must not make the send count as failed,
            // or the mail would go out a second time.
        }
    }
}
