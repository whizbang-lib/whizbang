namespace Whizbang.Core.Tracing;

/// <summary>
/// Represents the completion status of a handler invocation.
/// </summary>
public enum HandlerStatus {
  /// <summary>Handler completed successfully.</summary>
  Success = 0,

  /// <summary>Handler failed with an exception.</summary>
  Failed = 1,

  /// <summary>Handler returned early without processing.</summary>
  EarlyReturn = 2
}
