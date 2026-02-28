using System.Diagnostics;
using Microsoft.Extensions.Options;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tracing;

/// <summary>
/// Main tracer implementation that orchestrates tracing decisions and delegates to outputs.
/// </summary>
/// <remarks>
/// <para>
/// Tracer coordinates tracing decisions based on <see cref="TracingOptions"/> and delegates
/// output to registered <see cref="ITraceOutput"/> implementations. It uses IOptionsMonitor
/// for live reload of configuration changes.
/// </para>
/// <para>
/// Tracing decisions follow this priority:
/// <list type="number">
///   <item>Explicit config (<c>TracedHandlers</c>/<c>TracedMessages</c>) - always traces</item>
///   <item>Attribute (<c>[TraceHandler]</c>/<c>[TraceMessage]</c>) - always traces</item>
///   <item>Global <c>Verbosity</c> + <c>Components</c> - baseline for everything else</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Registration
/// services.AddSingleton&lt;ITracer, Tracer&gt;();
/// services.AddSingleton&lt;ITraceOutput, LoggerTraceOutput&gt;();
/// services.AddSingleton&lt;ITraceOutput, OpenTelemetryTraceOutput&gt;();
///
/// // Usage in handlers
/// if (_tracer.ShouldTrace(TraceComponents.Handlers, "MyHandler", "MyMessage")) {
///   using var scope = _tracer.BeginTrace(context);
///   try {
///     await ProcessAsync();
///     scope.Complete();
///   } catch (Exception ex) {
///     scope.Fail(ex);
///     throw;
///   }
/// }
/// </code>
/// </example>
/// <docs>tracing/overview</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TracerTests.cs</tests>
public sealed class Tracer : ITracer {
  private readonly IOptions<TracingOptions> _options;
  private readonly IReadOnlyList<ITraceOutput> _outputs;

  /// <summary>
  /// Creates a new Tracer with the specified options and outputs.
  /// </summary>
  /// <param name="options">Tracing configuration options.</param>
  /// <param name="outputs">Collection of trace outputs to receive trace events.</param>
  public Tracer(IOptions<TracingOptions> options, IEnumerable<ITraceOutput> outputs) {
    _options = options;
    _outputs = outputs.ToList();
  }

  /// <inheritdoc />
  public bool ShouldTrace(TraceComponents component, string? handlerName = null, string? messageType = null) {
    var opts = _options.Value;

    // Check explicit config first - these always trace
    if (handlerName != null && opts.TracedHandlers.Count > 0) {
      foreach (var pattern in opts.TracedHandlers.Keys) {
        if (TracePatternMatcher.IsMatch(pattern, handlerName)) {
          return true;
        }
      }
    }

    if (messageType != null && opts.TracedMessages.Count > 0) {
      foreach (var pattern in opts.TracedMessages.Keys) {
        if (TracePatternMatcher.IsMatch(pattern, messageType)) {
          return true;
        }
      }
    }

    // Check global settings
    if (opts.Verbosity == TraceVerbosity.Off) {
      return false;
    }

    return opts.IsEnabled(component);
  }

  /// <inheritdoc />
  public TraceVerbosity GetEffectiveVerbosity(string? handlerName, string? messageType, TraceVerbosity? attributeVerbosity) {
    // Attribute takes highest priority
    if (attributeVerbosity.HasValue) {
      return attributeVerbosity.Value;
    }

    var opts = _options.Value;

    // Check config for handler
    if (handlerName != null && opts.TracedHandlers.Count > 0) {
      foreach (var (pattern, verbosity) in opts.TracedHandlers) {
        if (TracePatternMatcher.IsMatch(pattern, handlerName)) {
          return verbosity;
        }
      }
    }

    // Check config for message
    if (messageType != null && opts.TracedMessages.Count > 0) {
      foreach (var (pattern, verbosity) in opts.TracedMessages) {
        if (TracePatternMatcher.IsMatch(pattern, messageType)) {
          return verbosity;
        }
      }
    }

    // Fall back to global
    return opts.Verbosity;
  }

  /// <inheritdoc />
  public ITraceScope BeginTrace(TraceContext context) {
    // Call BeginTrace on all outputs
    foreach (var output in _outputs) {
      output.BeginTrace(context);
    }

    return new TraceScope(context, _outputs);
  }

  /// <inheritdoc />
  public void BeginHandlerTrace(
      string handlerName,
      string messageTypeName,
      int? attributeVerbosity,
      bool hasTraceAttribute) {
    // Convert attribute verbosity to enum
    var verbosity = attributeVerbosity.HasValue
        ? (TraceVerbosity)attributeVerbosity.Value
        : (TraceVerbosity?)null;

    // Get effective verbosity considering attribute, config, and global settings
    var effectiveVerbosity = GetEffectiveVerbosity(handlerName, messageTypeName, verbosity);

    // Check if we should trace this handler
    if (!ShouldTrace(TraceComponents.Handlers, handlerName, messageTypeName) && !hasTraceAttribute) {
      return;
    }

    // Create trace context for this handler
    // For handler traces, we generate a unique MessageId since we don't have envelope context
    // Use TrackedGuid.NewMedo() for UUIDv7 time-ordered IDs
    var traceId = TrackedGuid.NewMedo();
    var context = new TraceContext {
      MessageId = traceId,
      CorrelationId = $"handler-trace-{traceId:N}",
      MessageType = messageTypeName,
      Component = TraceComponents.Handlers,
      Verbosity = effectiveVerbosity,
      StartTime = DateTimeOffset.UtcNow,
      HandlerName = handlerName,
      IsExplicit = hasTraceAttribute,
      ExplicitSource = hasTraceAttribute ? "attribute" : null
    };

    // Notify outputs of trace start
    foreach (var output in _outputs) {
      output.BeginTrace(context);
    }
  }

  /// <inheritdoc />
  public void EndHandlerTrace(
      string handlerName,
      string messageTypeName,
      HandlerStatus status,
      double durationMs,
      long startTimestamp,
      long endTimestamp,
      Exception? exception) {
    // Check if we should trace this handler
    if (!ShouldTrace(TraceComponents.Handlers, handlerName, messageTypeName)) {
      return;
    }

    // Create trace context
    // Use TrackedGuid.NewMedo() for UUIDv7 time-ordered IDs
    var traceId = TrackedGuid.NewMedo();
    var context = new TraceContext {
      MessageId = traceId,
      CorrelationId = $"handler-trace-{traceId:N}",
      MessageType = messageTypeName,
      Component = TraceComponents.Handlers,
      Verbosity = TraceVerbosity.Verbose,
      StartTime = DateTimeOffset.UtcNow,
      HandlerName = handlerName
    };

    // Create trace result
    var result = status switch {
      HandlerStatus.Success => TraceResult.Completed(TimeSpan.FromMilliseconds(durationMs)),
      HandlerStatus.Failed => TraceResult.Failed(TimeSpan.FromMilliseconds(durationMs), exception!),
      HandlerStatus.EarlyReturn => TraceResult.EarlyReturn(TimeSpan.FromMilliseconds(durationMs)),
      HandlerStatus.Cancelled => new TraceResult {
        Success = false,
        Duration = TimeSpan.FromMilliseconds(durationMs),
        Status = "Cancelled"
      },
      _ => TraceResult.Completed(TimeSpan.FromMilliseconds(durationMs))
    };

    // Notify outputs of trace end
    foreach (var output in _outputs) {
      output.EndTrace(context, result);
    }
  }

  /// <summary>
  /// Internal trace scope implementation that tracks operation lifecycle.
  /// </summary>
  private sealed class TraceScope : ITraceScope {
    private readonly TraceContext _context;
    private readonly IReadOnlyList<ITraceOutput> _outputs;
    private readonly Stopwatch _stopwatch;
    private bool _completed;

    public TraceScope(TraceContext context, IReadOnlyList<ITraceOutput> outputs) {
      _context = context;
      _outputs = outputs;
      _stopwatch = Stopwatch.StartNew();
    }

    public void Complete() {
      if (_completed) {
        return;
      }

      _completed = true;
      _stopwatch.Stop();

      var result = TraceResult.Completed(_stopwatch.Elapsed);
      _notifyOutputs(result);
    }

    public void Fail(Exception exception) {
      if (_completed) {
        return;
      }

      _completed = true;
      _stopwatch.Stop();

      var result = TraceResult.Failed(_stopwatch.Elapsed, exception);
      _notifyOutputs(result);
    }

    public void EarlyReturn() {
      if (_completed) {
        return;
      }

      _completed = true;
      _stopwatch.Stop();

      var result = TraceResult.EarlyReturn(_stopwatch.Elapsed);
      _notifyOutputs(result);
    }

    public void Dispose() {
      if (!_completed) {
        // If scope wasn't explicitly completed, still notify outputs
        _stopwatch.Stop();
        var result = new TraceResult {
          Success = true,
          Duration = _stopwatch.Elapsed,
          Status = "Disposed"
        };
        _notifyOutputs(result);
        _completed = true;
      }
    }

    private void _notifyOutputs(TraceResult result) {
      foreach (var output in _outputs) {
        output.EndTrace(_context, result);
      }
    }
  }
}
