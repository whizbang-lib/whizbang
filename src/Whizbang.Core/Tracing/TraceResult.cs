namespace Whizbang.Core.Tracing;

/// <summary>
/// Result of a traced operation.
/// </summary>
/// <remarks>
/// <para>
/// TraceResult is passed to <see cref="ITraceOutput.EndTrace"/> when
/// a traced operation completes, containing outcome and timing information.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public void EndTrace(TraceContext context, TraceResult result) {
///   _logger.Log(
///     result.Success ? LogLevel.Debug : LogLevel.Error,
///     "[{Prefix}] {MessageType} {Status} in {Duration}ms",
///     context.IsExplicit ? "TRACE" : "trace",
///     context.MessageType,
///     result.Status,
///     result.Duration.TotalMilliseconds);
/// }
/// </code>
/// </example>
/// <docs>tracing/custom-outputs</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/TraceResultTests.cs</tests>
public sealed record TraceResult {
  /// <summary>
  /// Gets whether the operation succeeded.
  /// </summary>
  public required bool Success { get; init; }

  /// <summary>
  /// Gets the duration of the operation.
  /// </summary>
  public required TimeSpan Duration { get; init; }

  /// <summary>
  /// Gets the exception if the operation failed.
  /// </summary>
  public Exception? Exception { get; init; }

  /// <summary>
  /// Gets the result status (e.g., "Completed", "EarlyReturn", "Failed").
  /// </summary>
  public required string Status { get; init; }

  /// <summary>
  /// Gets custom properties for extensibility.
  /// </summary>
  public Dictionary<string, object?> Properties { get; } = [];

  /// <summary>
  /// Creates a successful completion result.
  /// </summary>
  /// <param name="duration">The operation duration.</param>
  /// <returns>A successful TraceResult.</returns>
  public static TraceResult Completed(TimeSpan duration) => new() {
    Success = true,
    Duration = duration,
    Status = "Completed"
  };

  /// <summary>
  /// Creates a failure result.
  /// </summary>
  /// <param name="duration">The operation duration.</param>
  /// <param name="exception">The exception that caused the failure.</param>
  /// <returns>A failed TraceResult.</returns>
  public static TraceResult Failed(TimeSpan duration, Exception exception) => new() {
    Success = false,
    Duration = duration,
    Status = "Failed",
    Exception = exception
  };

  /// <summary>
  /// Creates an early return result (handler completed without full processing).
  /// </summary>
  /// <param name="duration">The operation duration.</param>
  /// <returns>An early return TraceResult.</returns>
  public static TraceResult EarlyReturn(TimeSpan duration) => new() {
    Success = true,
    Duration = duration,
    Status = "EarlyReturn"
  };
}
