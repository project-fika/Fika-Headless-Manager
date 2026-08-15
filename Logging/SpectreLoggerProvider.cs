using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace FikaHeadlessManager.Logging;

public sealed class SpectreLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new SpectreLogger();
    }

    public void Dispose()
    {
    }

    private sealed class SpectreLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            if (exception != null)
            {
                message += $"\n{exception.Message}";
            }

            var color = logLevel switch
            {
                LogLevel.Warning => "yellow",
                LogLevel.Error or LogLevel.Critical => "red",
                LogLevel.Debug or LogLevel.Trace => "grey",
                _ => "white"
            };

            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(message)}[/]");
        }
    }
}
