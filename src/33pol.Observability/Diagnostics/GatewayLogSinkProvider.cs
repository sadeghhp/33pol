using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Diagnostics;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// Mirrors the application's own <c>ILogger</c> output at Warning and above into
/// <see cref="IGatewayLogStore"/>, and at Error and above into <see cref="IGatewayErrorRecorder"/>,
/// so the admin console shows real failures without every call site having to be taught about a
/// second logging path.
/// </summary>
/// <remarks>
/// The stores are resolved lazily. Constructing them eagerly would mean building part of the DI
/// graph while the logging system is still being assembled, which deadlocks on first log.
/// <para>
/// This provider only receives anything because the host passes <c>writeToProviders: true</c> to
/// <c>UseSerilog</c>. Without it Serilog replaces the logger factory with one whose
/// <c>AddProvider</c> is a no-op, and this class runs but is never called.
/// </para>
/// </remarks>
public sealed class GatewayLogSinkProvider : ILoggerProvider
{
    public const LogLevel MinimumLevel = LogLevel.Warning;

    /// <summary>Level at and above which a log also becomes a tracked error record.</summary>
    public const LogLevel ErrorRecordLevel = LogLevel.Error;

    private readonly ConcurrentDictionary<string, GatewayLogSinkLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Func<IGatewayLogStore> _storeAccessor;
    private readonly Func<IGatewayErrorRecorder>? _errorRecorderAccessor;
    private readonly GatewayErrorTrackingOptions _options;

    public GatewayLogSinkProvider(Func<IGatewayLogStore> storeAccessor)
        : this(storeAccessor, null, new GatewayErrorTrackingOptions())
    {
    }

    public GatewayLogSinkProvider(
        Func<IGatewayLogStore> storeAccessor,
        Func<IGatewayErrorRecorder>? errorRecorderAccessor,
        GatewayErrorTrackingOptions options)
    {
        ArgumentNullException.ThrowIfNull(storeAccessor);
        ArgumentNullException.ThrowIfNull(options);
        _storeAccessor = storeAccessor;
        _errorRecorderAccessor = errorRecorderAccessor;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(
            categoryName,
            name => new GatewayLogSinkLogger(name, _storeAccessor, _errorRecorderAccessor, _options));

    public void Dispose() => _loggers.Clear();

    private sealed class GatewayLogSinkLogger(
        string category,
        Func<IGatewayLogStore> storeAccessor,
        Func<IGatewayErrorRecorder>? errorRecorderAccessor,
        GatewayErrorTrackingOptions options) : ILogger
    {
        /// <summary>
        /// The ambient scope stack. Standard <c>ILogger</c> shape, and the reason a log written deep
        /// inside a request can carry its request id: the middleware opens one scope and every
        /// logger in the process can read it.
        /// </summary>
        private static readonly AsyncLocal<ScopeNode?> CurrentScope = new();

        private readonly bool _ignored = options.IgnoredCategories.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) &&
            category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// This component publishes its own error records, so its log lines go to the Logs buffer
        /// only. Mirroring them too would put a thinner duplicate of every upstream failure in the
        /// Errors tab — and, since the log line is written before the detailed record, it would be
        /// the one shown first.
        /// </summary>
        private readonly bool _selfReporting = options.SelfReportingCategories.Any(name =>
            !string.IsNullOrWhiteSpace(name) &&
            string.Equals(ShortCategory(category), name, StringComparison.OrdinalIgnoreCase));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var node = new ScopeNode(state, CurrentScope.Value);
            CurrentScope.Value = node;
            return node;
        }

        public bool IsEnabled(LogLevel logLevel) =>
            !_ignored && logLevel >= MinimumLevel && logLevel != LogLevel.None;

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
                var message = formatter(state, exception);
                var timestamp = DateTimeOffset.UtcNow;
                var shortCategory = ShortCategory(category);
                var requestId = ResolveScopeValue(state, GatewayLogScopeKeys.RequestId);
                var modelId = ResolveScopeValue(state, GatewayLogScopeKeys.ModelId);
                var tenantId = ResolveScopeValue(state, GatewayLogScopeKeys.TenantId);

                storeAccessor().Record(new GatewayLogEntry
                {
                    Id = $"log_{Guid.NewGuid():N}",
                    TimestampUtc = timestamp,
                    Level = ToGatewayLevel(logLevel).ToString(),
                    Category = shortCategory,
                    EventCode = eventId.Name,
                    Message = message,
                    Detail = exception?.ToString(),
                    Hint = GatewayLogHints.ForException(exception),
                    ModelId = modelId,
                    RequestId = requestId,
                });

                if (logLevel < ErrorRecordLevel || errorRecorderAccessor is null || _selfReporting)
                {
                    return;
                }

                errorRecorderAccessor().Record(new GatewayErrorRecord
                {
                    Id = $"err_{Guid.NewGuid():N}",
                    Fingerprint = string.Empty,
                    OccurredAt = timestamp,
                    Level = ToGatewayLevel(logLevel).ToString(),
                    Source = GatewayErrorSourceNames.Log,
                    Category = shortCategory,
                    EventCode = eventId.Name,
                    Message = message,
                    ExceptionType = exception?.GetType().FullName,
                    StackTrace = exception?.ToString(),
                    ModelId = modelId,
                    TenantId = tenantId,
                    RequestId = requestId,
                    Hint = GatewayLogHints.ForException(exception),
                });
            }
            catch
            {
                // A diagnostics sink must never be the reason a request fails.
            }
        }

        /// <summary>
        /// Looks for a property on the log's own state first, then walks outward through the scope
        /// stack. Structured log statements that already write <c>{ModelId}</c> get picked up for
        /// free this way, with no call-site change.
        /// </summary>
        private static string? ResolveScopeValue<TState>(TState state, string key) =>
            ReadProperty(state, key) ?? WalkScopes(key);

        private static string? WalkScopes(string key)
        {
            for (var node = CurrentScope.Value; node is not null; node = node.Parent)
            {
                var value = ReadProperty(node.State, key);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        private static string? ReadProperty(object? state, string key)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            {
                return null;
            }

            foreach (var (name, value) in properties)
            {
                if (string.Equals(name, key, StringComparison.Ordinal) && value is not null)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return null;
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

        private sealed class ScopeNode(object? state, ScopeNode? parent) : IDisposable
        {
            public object? State { get; } = state;

            public ScopeNode? Parent { get; } = parent;

            public void Dispose() => CurrentScope.Value = Parent;
        }
    }
}
