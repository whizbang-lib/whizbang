using System.Runtime.CompilerServices;

namespace Whizbang.Core.Validation;

/// <summary>
/// Exception thrown when a StreamId validation guard fails.
/// Indicates a bug where an event requiring a StreamId was dispatched without one.
/// </summary>
/// <docs>validation/invalid-stream-id</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/StreamIdGuardTests.cs</tests>
public sealed class InvalidStreamIdException : Exception {
  /// <summary>
  /// Creates a new InvalidStreamIdException.
  /// </summary>
  public InvalidStreamIdException() { }

  /// <summary>
  /// Creates a new InvalidStreamIdException.
  /// </summary>
  /// <param name="message">The error message.</param>
  public InvalidStreamIdException(string message) : base(message) { }

  /// <summary>
  /// Creates a new InvalidStreamIdException with inner exception.
  /// </summary>
  /// <param name="message">The error message.</param>
  /// <param name="innerException">The inner exception.</param>
  public InvalidStreamIdException(string message, Exception innerException)
      : base(message, innerException) { }

  /// <summary>
  /// The StreamId value that failed validation (Guid.Empty).
  /// </summary>
  public Guid StreamId { get; init; }

  /// <summary>
  /// The MessageId of the message that failed StreamId validation.
  /// </summary>
  public Guid MessageId { get; init; }

  /// <summary>
  /// The pipeline context where the guard was triggered (e.g., "Dispatcher.Outbox", "TransportConsumer.Inbox").
  /// </summary>
  public string Context { get; init; } = string.Empty;

  /// <summary>
  /// The name of the calling method where the guard was triggered.
  /// </summary>
  public string CallerMemberName { get; init; } = string.Empty;

  /// <summary>
  /// The source file path where the guard was triggered.
  /// </summary>
  public string CallerFilePath { get; init; } = string.Empty;

  /// <summary>
  /// The source line number where the guard was triggered.
  /// </summary>
  public int CallerLineNumber { get; init; }
}
