using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Diagnostics;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// Mirrors the application's own <c>ILogger</c> output at Warning and above into
/// <see cref="IGatewayLogStore"/>, so the admin Logs tab shows real failures without every call
/// site having to be taught about a second logging path.
/// </summary>
/// <remarks>
/// The store is resolved lazily. Constructing it eagerly would mean building part of the DI graph
/// while the logging system is still being assembled, which deadlocks on first log.
/// </remarks>
public sealed class GatewayLogSinkProvider(Func<IGatewayLogStore> storeAccessor) : ILoggerProvider
{
    public const LogLevel MinimumLevel = LogLevel.Warning;

    private readonly ConcurrentDictionary<string, GatewayLogSinkLogger> _loggers = new(StringComparer.Ordinal);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new GatewayLogSinkLogger(name, storeAccessor));

    public void Dispose() => _loggers.Clear();

    private sealed class GatewayLogSinkLogger(string category, Func<IGatewayLogStore> storeAccessor) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            try
            {
                var store = storeAccessor();
                store.Record(new GatewayLogEntry
                {
                    Id = $"log_{Guid.NewGuid():N}",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Level = ToGatewayLevel(logLevel).ToString(),
                    Category = ShortCategory(category),
                    EventCode = eventId.Name,
                    Message = formatter(state, exception),
                    Detail = exception?.ToString(),
                    Hint = GatewayLogHints.ForException(exception),
                });
            }
            catch
            {
                // A diagnostics sink must never be the reason a request fails.
            }
        }

        private static GatewayLogLevel ToGatewayLevel(LogLevel level) => level switch
        {
            LogLevel.Critical => GatewayLogLevel.Critical,
            LogLevel.Error => GatewayLogLevel.Error,
            LogLevel.Warning => GatewayLogLevel.Warning,
            _ => GatewayLogLevel.Info,
        };

        /// <summary>
        /// Trims the namespace off the logger category. Operators scanning the Logs tab need
        /// "ModelRouterMiddleware", not the fully qualified type name in every row.
        /// </summary>
        private static string ShortCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        }
    }
}
