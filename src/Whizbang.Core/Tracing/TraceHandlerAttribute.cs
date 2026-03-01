namespace Whizbang.Core.Tracing;

/// <summary>
/// Marks a handler class for explicit tracing.
/// When applied, the handler will always emit traces regardless of global verbosity settings.
/// Traces will include whizbang.trace.explicit=true in OpenTelemetry attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TraceHandlerAttribute : Attribute {
  /// <summary>
  /// Optional verbosity level for this handler's traces.
  /// If not specified, uses the handler's default elevated tracing level.
  /// </summary>
  public TraceVerbosity Verbosity { get; init; } = TraceVerbosity.Normal;
}

/// <summary>
/// Verbosity levels for tracing output.
/// </summary>
public enum TraceVerbosity {
  /// <summary>No tracing output.</summary>
  Off = 0,

  /// <summary>Errors, failures, and explicitly marked traces only.</summary>
  Minimal = 1,

  /// <summary>Command/Event lifecycle stage transitions.</summary>
  Normal = 2,

  /// <summary>Outbox/Inbox operations, handler discovery.</summary>
  Verbose = 3,

  /// <summary>Full payload, timing breakdown, perspectives.</summary>
  Debug = 4
}
