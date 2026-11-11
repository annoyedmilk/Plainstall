using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Plainstall.Core;

/// <summary>
///     Writes log lines to a text file that a person can read: time, level, message. One file per run.
///     Kept deliberately small. Plainstall needs no log framework.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;

    /// <summary>Opens the file. Lines of level Information and higher are written.</summary>
    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Write(LogLevel level, string message, Exception? exception)
    {
        var label = level switch
        {
            LogLevel.Warning => "WARN ",
            LogLevel.Error or LogLevel.Critical => "ERROR",
            _ => "     "
        };
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss} {label} {message}"));
                if (exception is not null) _writer.WriteLine(exception);
            }
            catch (ObjectDisposedException)
            {
                // A late log line after shutdown is not worth an exception.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel is >= LogLevel.Information and not LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(logLevel, formatter(state, exception), exception);
        }
    }
}