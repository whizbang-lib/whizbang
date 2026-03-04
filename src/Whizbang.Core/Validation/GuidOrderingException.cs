namespace Whizbang.Core.Validation;

/// <summary>
/// Exception thrown when a GUID ordering violation occurs with severity set to Error.
/// </summary>
public sealed class GuidOrderingException : Exception {
  /// <summary>
  /// Creates a new GuidOrderingException.
  /// </summary>
  public GuidOrderingException() { }

  /// <summary>
  /// Creates a new GuidOrderingException.
  /// </summary>
  /// <param name="message">The error message.</param>
  public GuidOrderingException(string message) : base(message) { }

  /// <summary>
  /// Creates a new GuidOrderingException with inner exception.
  /// </summary>
  /// <param name="message">The error message.</param>
  /// <param name="innerException">The inner exception.</param>
  public GuidOrderingException(string message, Exception innerException)
      : base(message, innerException) { }
}
