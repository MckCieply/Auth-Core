using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Keeps every log line the host writes, so a test can assert what is and is not logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries => [.. _entries];

    public string Text => string.Join('\n', _entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"));

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(LogLevel, string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
