namespace Whizbang.Core.Tracing;

/// <summary>
/// Represents the completion status of a handler invocation.
/// Used for tracing and metrics to track handler outcomes.
/// </summary>
/// <docs>tracing/overview</docs>
/// <tests>tests/Whizbang.Core.Tests/Tracing/HandlerStatusTests.cs</tests>
public enum HandlerStatus {
  /// <summary>
  /// Handler completed successfully.
  /// </summary>
  Success = 0,

  /// <summary>
  /// Handler failed with an exception.
  /// </summary>
  Failed = 1,

  /// <summary>
  /// Handler returned early (e.g., validation failure, no-op).
  /// </summary>
  EarlyReturn = 2,

  /// <summary>
  /// Handler was cancelled via CancellationToken.
  /// </summary>
  Cancelled = 3
}
