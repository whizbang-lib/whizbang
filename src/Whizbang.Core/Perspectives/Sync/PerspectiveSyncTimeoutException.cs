namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Exception thrown when perspective synchronization times out.
/// </summary>
/// <remarks>
/// This exception is thrown when a receptor decorated with
/// <see cref="AwaitPerspectiveSyncAttribute"/> has <c>ThrowOnTimeout = true</c>
/// and the sync operation times out before the perspective catches up.
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public sealed class PerspectiveSyncTimeoutException : Exception {
  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncTimeoutException"/>.
  /// </summary>
  public PerspectiveSyncTimeoutException() {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncTimeoutException"/>.
  /// </summary>
  /// <param name="message">The exception message.</param>
  public PerspectiveSyncTimeoutException(string message)
      : base(message) {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncTimeoutException"/>.
  /// </summary>
  /// <param name="message">The exception message.</param>
  /// <param name="innerException">The inner exception.</param>
  public PerspectiveSyncTimeoutException(string message, Exception innerException)
      : base(message, innerException) {
  }

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncTimeoutException"/>.
  /// </summary>
  /// <param name="perspectiveType">The type of perspective that timed out.</param>
  /// <param name="timeout">The timeout duration that was exceeded.</param>
  /// <param name="message">The exception message.</param>
  public PerspectiveSyncTimeoutException(Type perspectiveType, TimeSpan timeout, string message)
      : base(message) {
    PerspectiveType = perspectiveType;
    Timeout = timeout;
  }

  /// <summary>
  /// Initializes a new instance of <see cref="PerspectiveSyncTimeoutException"/>.
  /// </summary>
  /// <param name="perspectiveType">The type of perspective that timed out.</param>
  /// <param name="timeout">The timeout duration that was exceeded.</param>
  /// <param name="message">The exception message.</param>
  /// <param name="innerException">The inner exception.</param>
  public PerspectiveSyncTimeoutException(Type perspectiveType, TimeSpan timeout, string message, Exception innerException)
      : base(message, innerException) {
    PerspectiveType = perspectiveType;
    Timeout = timeout;
  }

  /// <summary>
  /// Gets the type of perspective that timed out.
  /// </summary>
  public Type? PerspectiveType { get; }

  /// <summary>
  /// Gets the timeout duration that was exceeded.
  /// </summary>
  public TimeSpan Timeout { get; }
}
