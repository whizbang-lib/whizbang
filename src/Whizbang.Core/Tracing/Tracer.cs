using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Default implementation of <see cref="ITracer"/> that emits traces via
/// OpenTelemetry ActivitySource and structured logging.
/// </summary>
public sealed partial class Tracer : ITracer {
  private readonly ILogger<Tracer> _logger;

  // Thread-local storage for current activity (to match Begin/End calls)
  private static readonly AsyncLocal<Activity?> _currentActivity = new();

  public Tracer(ILogger<Tracer> logger) {
    _logger = logger;
  }

  public void BeginHandlerTrace(string handlerName, string messageTypeName, int handlerCount, bool isExplicit) {
    var activity = WhizbangActivitySource.Tracing.StartActivity(
      $"Handler: {_extractShortHandlerName(handlerName)}",
      ActivityKind.Internal);

    if (activity != null) {
      activity.SetTag("whizbang.handler.name", handlerName);
      activity.SetTag("whizbang.message.type", messageTypeName);
      activity.SetTag("whizbang.handler.count", handlerCount);
      activity.SetTag("whizbang.trace.explicit", isExplicit);

      _currentActivity.Value = activity;
    }

    if (isExplicit) {
      LogExplicitHandlerBegin(handlerName, messageTypeName, handlerCount);
    } else {
      LogHandlerBegin(handlerName, messageTypeName, handlerCount);
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

    var activity = _currentActivity.Value;
    if (activity != null) {
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

    var isExplicit = activity?.GetTagItem("whizbang.trace.explicit") is true;
    var statusString = status.ToString();

    if (status == HandlerStatus.Failed && exception != null) {
      LogHandlerFailed(handlerName, messageTypeName, durationMs, exception);
    } else if (isExplicit) {
      LogExplicitHandlerEnd(handlerName, messageTypeName, statusString, durationMs);
    } else {
      LogHandlerEnd(handlerName, messageTypeName, statusString, durationMs);
    }
  }

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

  [LoggerMessage(Level = LogLevel.Information, Message = "[TRACE] Handler invocation: {HandlerName} for {MessageType} ({HandlerCount} handlers) - explicit via [TraceHandler]")]
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
