using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RTSPWallpaperStudio.Infrastructure.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(_directory, name));

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private static readonly object Gate = new();
        private static readonly Regex RtspSecret = new(@"(?<prefix>rtsp://[^/:@\s]+)(?::[^@\s]*)?@", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        private readonly string _directory;
        private readonly string _category;

        public FileLogger(string directory, string category)
        {
            _directory = directory;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {_category}: {Redact(message)}";
            if (exception is not null)
            {
                line += $" | {Redact(exception.ToString())}";
            }

            var file = Path.Combine(_directory, $"app-{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
            lock (Gate)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }

        private static string Redact(string value) => RtspSecret.Replace(value, "${prefix}@");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
