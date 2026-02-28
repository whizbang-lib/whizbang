namespace Whizbang.Core.Tracing;

/// <summary>
/// Context for a trace operation containing all metadata about the traced item.
/// </summary>
/// <remarks>
/// <para>
/// TraceContext is passed to <see cref="ITraceOutput"/> implementations when
/// tracing begins. It contains envelope data, handler information, and trace settings.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public void BeginTrace(TraceContext context) {
///   _logger.LogInformation(
///     "[{Prefix}] {MessageType} started (CorrelationId: {CorrelationId})",
///     context.IsExplicit ? "TRACE" : "trace",
///     context.MessageType,
///     context.CorrelationId);
/// }
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceContextTests.cs</tests>
public sealed record TraceContext {
  /// <summary>
  /// Gets the message ID from the envelope.
  /// </summary>
  public required Guid MessageId { get; init; }

  /// <summary>
  /// Gets the correlation ID for distributed tracing.
  /// </summary>
  public required string CorrelationId { get; init; }

  /// <summary>
  /// Gets the causation ID for event chain tracking.
  /// </summary>
  public string? CausationId { get; init; }

  /// <summary>
  /// Gets the type name of the message being traced.
  /// </summary>
  public required string MessageType { get; init; }

  /// <summary>
  /// Gets the handler name (null for message-level traces).
  /// </summary>
  public string? HandlerName { get; init; }

  /// <summary>
  /// Gets the component being traced.
  /// </summary>
  public required TraceComponents Component { get; init; }

  /// <summary>
  /// Gets the verbosity level for this trace.
  /// </summary>
  public required TraceVerbosity Verbosity { get; init; }

  /// <summary>
  /// Gets whether this is an explicit trace (via attribute or config).
  /// </summary>
  public bool IsExplicit { get; init; }

  /// <summary>
  /// Gets the source of explicit trace ("attribute" or "config", null if not explicit).
  /// </summary>
  public string? ExplicitSource { get; init; }

  /// <summary>
  /// Gets the hop count from the envelope.
  /// </summary>
  public int HopCount { get; init; }

  /// <summary>
  /// Gets the timestamp when the trace began.
  /// </summary>
  public required DateTimeOffset StartTime { get; init; }

  /// <summary>
  /// Gets custom properties for extensibility.
  /// </summary>
  public Dictionary<string, object?> Properties { get; } = [];
}
