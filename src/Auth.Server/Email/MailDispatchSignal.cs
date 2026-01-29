using System.Threading.Channels;

namespace Auth.Server.Email;

/// <summary>
/// Lets a request that has just queued a mail wake the dispatcher, so the mail leaves within seconds rather than
/// at the next poll. Notifications that arrive while the dispatcher is busy collapse into one.
/// </summary>
public sealed class MailDispatchSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Returns when notified, or after <paramref name="timeout"/> on <paramref name="clock"/>, whichever is first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, clock, linked.Token);
        var notified = _channel.Reader.WaitToReadAsync(linked.Token).AsTask();
        await Task.WhenAny(delay, notified);
        await linked.CancelAsync();

        while (_channel.Reader.TryRead(out _))
        {
            // Drain: whatever was signalled is about to be handled by the pass that follows.
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
