#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Default implementation of <see cref="ITracer"/> that emits traces via
/// OpenTelemetry ActivitySource and structured logging.
/// </summary>
/// <remarks>
/// <para>
/// The Tracer respects <see cref="TracingOptions"/> configuration to control
/// when traces are emitted. Key configuration options:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="TracingOptions.Verbosity"/> - Global verbosity level</description></item>
/// <item><description><see cref="TracingOptions.Components"/> - Which components emit traces</description></item>
/// <item><description><see cref="TracingOptions.TracedHandlers"/> - Handlers to trace regardless of verbosity</description></item>
/// <item><description><see cref="TracingOptions.TracedMessages"/> - Messages to trace regardless of verbosity</description></item>
/// <item><description><see cref="TracingOptions.EnableOpenTelemetry"/> - Whether to emit OTel spans</description></item>
/// <item><description><see cref="TracingOptions.EnableStructuredLogging"/> - Whether to emit log messages</description></item>
/// </list>
/// </remarks>
/// <docs>operations/observability/tracing#tracer</docs>
/// <tests>Whizbang.Observability.Tests/TracerTests.cs</tests>
/// <tests>Whizbang.Core.Tests/Tracing/TracerOptionsIntegrationTests.cs</tests>
/// <remarks>
/// Initializes a new instance of the <see cref="Tracer"/> class.
/// </remarks>
/// <param name="logger">Logger for structured logging output.</param>
/// <param name="options">Tracing options monitor for runtime configuration.</param>
public sealed partial class Tracer(ILogger<Tracer> logger, IOptionsMonitor<TracingOptions> options) : ITracer {
#pragma warning disable S4487 // Used by generated [LoggerMessage] partial methods
  private readonly ILogger<Tracer> _logger = logger;
#pragma warning restore S4487
  private readonly IOptionsMonitor<TracingOptions> _options = options;

  // Thread-local storage for current activity (to match Begin/End calls)
  private static readonly AsyncLocal<Activity?> _currentActivity = new();

  // Thread-local storage to track if current trace is explicit (elevated)
  private static readonly AsyncLocal<bool> _isExplicitTrace = new();

  public void BeginHandlerTrace(string handlerName, string messageTypeName, int handlerCount, bool isExplicit) {
    var options = _options.CurrentValue;

    // Check if tracing is completely off
    if (options.Verbosity == TraceVerbosity.Off) {
      return;
    }

    // Check if Handlers component is enabled
    if (!options.IsEnabled(TraceComponents.Handlers)) {
      return;
    }

    // Determine if this trace is elevated (explicit via config or attribute)
    var isElevated = isExplicit ||
                     _matchesTracedHandler(handlerName, options) ||
                     _matchesTracedMessage(messageTypeName, options);

    // Store the elevated state for EndHandlerTrace
    _isExplicitTrace.Value = isElevated;

    // Emit OpenTelemetry span if enabled
    if (options.EnableOpenTelemetry) {
      var activity = WhizbangActivitySource.Tracing.StartActivity(
        $"Handler: {_extractShortHandlerName(handlerName)}",
        ActivityKind.Internal);

      if (activity != null) {
        activity.SetTag("whizbang.handler.name", handlerName);
        activity.SetTag("whizbang.message.type", messageTypeName);
        activity.SetTag("whizbang.handler.count", handlerCount);
        activity.SetTag("whizbang.trace.explicit", isElevated);

        _currentActivity.Value = activity;
      }
    }

    // Emit structured log if enabled
    if (options.EnableStructuredLogging) {
      if (isElevated) {
        LogExplicitHandlerBegin(handlerName, messageTypeName, handlerCount);
      } else {
        LogHandlerBegin(handlerName, messageTypeName, handlerCount);
      }
    }
  }

  public void EndHandlerTrace(
    string handlerName,
    string messageTypeName,
    HandlerStatus status,
    double durationMs,
    long startTimestamp,
    long endTimestamp,
    Exception? exception) {

    var options = _options.CurrentValue;

    // Check if tracing is completely off
    if (options.Verbosity == TraceVerbosity.Off) {
      return;
    }

    // Check if Handlers component is enabled
    if (!options.IsEnabled(TraceComponents.Handlers)) {
      return;
    }

    var activity = _currentActivity.Value;
    if (activity != null && options.EnableOpenTelemetry) {
      activity.SetTag("whizbang.handler.status", status.ToString());
      activity.SetTag("whizbang.handler.duration_ms", durationMs);

      if (exception != null) {
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        // Record exception as an event
        var exceptionTags = new ActivityTagsCollection {
          { "exception.type", exception.GetType().FullName ?? exception.GetType().Name },
          { "exception.message", exception.Message },
          { "exception.stacktrace", exception.StackTrace ?? string.Empty }
        };
        activity.AddEvent(new ActivityEvent("exception", tags: exceptionTags));
      } else {
        activity.SetStatus(ActivityStatusCode.Ok);
      }

      activity.Stop();
      _currentActivity.Value = null;
    }

    // Emit structured log if enabled
    if (options.EnableStructuredLogging) {
      var isExplicit = _isExplicitTrace.Value;
      var statusString = status.ToString();

      if (status == HandlerStatus.Failed && exception != null) {
        LogHandlerFailed(handlerName, messageTypeName, durationMs, exception);
      } else if (isExplicit) {
        LogExplicitHandlerEnd(handlerName, messageTypeName, statusString, durationMs);
      } else {
        LogHandlerEnd(handlerName, messageTypeName, statusString, durationMs);
      }
    }

    // Reset the explicit trace flag
    _isExplicitTrace.Value = false;
  }

  /// <summary>
  /// Checks if a handler name matches any pattern in TracedHandlers configuration.
  /// </summary>
  private static bool _matchesTracedHandler(string handlerName, TracingOptions options) {
    foreach (var pattern in options.TracedHandlers.Keys) {
      if (_matchesPattern(handlerName, pattern)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Checks if a message type name matches any pattern in TracedMessages configuration.
  /// </summary>
  private static bool _matchesTracedMessage(string messageTypeName, TracingOptions options) {
    foreach (var pattern in options.TracedMessages.Keys) {
      if (_matchesPattern(messageTypeName, pattern)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Matches a name against a pattern that may include wildcards.
  /// </summary>
  /// <remarks>
  /// Supports:
  /// - Exact match: "OrderReceptor"
  /// - Prefix wildcard: "Order*" matches OrderReceptor, OrderValidator, etc.
  /// - Suffix wildcard: "*Receptor" matches OrderReceptor, PaymentReceptor, etc.
  /// - Namespace match: Handler name contains the pattern (e.g., "OrderReceptor" matches "MyApp.Handlers.OrderReceptor")
  /// </remarks>
  private static bool _matchesPattern(string name, string pattern) {
    // Exact match (case-insensitive)
    if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) {
      return true;
    }

    // Check if pattern has wildcards
    if (pattern.Contains('*')) {
      // Convert glob pattern to regex
      var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
      // Use timeout to prevent ReDoS attacks (although patterns come from config, not user input)
      var timeout = TimeSpan.FromSeconds(1);
      if (Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase, timeout)) {
        return true;
      }

      // Also check the short name (last segment after dot)
      var shortName = _extractShortName(name);
      if (Regex.IsMatch(shortName, regexPattern, RegexOptions.IgnoreCase, timeout)) {
        return true;
      }
    } else {
      // No wildcard - check if the pattern matches the end of the name
      // This allows "OrderReceptor" to match "MyApp.Handlers.OrderReceptor"
      if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Extracts the short name (class name only) from a fully qualified name.
  /// </summary>
  private static string _extractShortName(string fullName) =>
    TypeNameFormatter.GetSimpleName(fullName);

  private static string _extractShortHandlerName(string fullName) {
    // Extract just the class.method name from fully qualified name
    var lastDot = fullName.LastIndexOf('.');
    if (lastDot > 0) {
      var secondLastDot = fullName.LastIndexOf('.', lastDot - 1);
      if (secondLastDot > 0) {
        return fullName[(secondLastDot + 1)..];
      }
    }
    return fullName;
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "[TRACE] Handler invocation: {HandlerName} for {MessageType} ({HandlerCount} handlers) - explicit via [WhizbangTrace]")]
  private partial void LogExplicitHandlerBegin(string handlerName, string messageType, int handlerCount);

  [LoggerMessage(Level = LogLevel.Debug, Message = "[trace] Handler invocation: {HandlerName} for {MessageType} ({HandlerCount} handlers)")]
  private partial void LogHandlerBegin(string handlerName, string messageType, int handlerCount);

  [LoggerMessage(Level = LogLevel.Information, Message = "[TRACE] Handler completed: {HandlerName} for {MessageType} - {Status} in {DurationMs:F2}ms - explicit")]
  private partial void LogExplicitHandlerEnd(string handlerName, string messageType, string status, double durationMs);

  [LoggerMessage(Level = LogLevel.Debug, Message = "[trace] Handler completed: {HandlerName} for {MessageType} - {Status} in {DurationMs:F2}ms")]
  private partial void LogHandlerEnd(string handlerName, string messageType, string status, double durationMs);

  [LoggerMessage(Level = LogLevel.Error, Message = "[TRACE] Handler FAILED: {HandlerName} for {MessageType} after {DurationMs:F2}ms")]
  private partial void LogHandlerFailed(string handlerName, string messageType, double durationMs, Exception exception);
}
