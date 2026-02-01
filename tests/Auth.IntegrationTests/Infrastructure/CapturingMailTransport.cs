using System.Collections.Concurrent;
using Auth.Server.Email;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the mail server: keeps what was sent and what was tried, fails every send while
/// <see cref="Failing"/> is set, and takes <see cref="Delay"/> of real time over each one.
/// </summary>
public sealed class CapturingMailTransport : IMailTransport
{
    private readonly ConcurrentQueue<ComposedMail> _sent = new();
    private readonly ConcurrentQueue<ComposedMail> _attempted = new();
    private int _attempts;
    private volatile bool _failing;

    public IReadOnlyList<ComposedMail> Sent => [.. _sent];

    /// <summary>Every mail a send was tried for, failed ones included, in order.</summary>
    public IReadOnlyList<ComposedMail> Attempted => [.. _attempted];

    /// <summary>Sends tried, failed ones included.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>How long a send takes, so that two dispatchers can be made to overlap.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Runs inside every send, after <see cref="Delay"/> and before the outcome is decided; a test moves the clock here.</summary>
    public Func<Task>? DuringSend { get; set; }

    public bool Failing
    {
        get => _failing;
        set => _failing = value;
    }

    public async Task SendAsync(ComposedMail mail, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        _attempted.Enqueue(mail);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (DuringSend is { } during)
        {
            await during();
        }

        if (_failing)
        {
            throw new IOException("The mail server is down (test).");
        }

        _sent.Enqueue(mail);
    }
}
