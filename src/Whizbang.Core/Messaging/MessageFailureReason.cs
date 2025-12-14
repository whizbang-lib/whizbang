namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_HasExpectedValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_CanConvertToIntAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessageFailureReasonTests.cs:MessageFailureReason_CanConvertFromIntAsync</tests>
/// Classifies the reason a message failed processing.
/// Enables typed filtering and handling of different failure scenarios.
/// </summary>
public enum MessageFailureReason {
  /// <summary>
  /// No failure - message processed successfully.
  /// </summary>
  None = 0,

  /// <summary>
  /// Transport is not connected/ready to publish messages.
  /// Message is buffered awaiting transport readiness.
  /// </summary>
  TransportNotReady = 1,

  /// <summary>
  /// Transport exception occurred (e.g., ServiceBusException).
  /// Indicates connectivity or service issues with the message broker.
  /// </summary>
  TransportException = 2,

  /// <summary>
  /// JSON serialization/deserialization failed.
  /// Message payload may be malformed or incompatible with schema.
  /// </summary>
  SerializationError = 3,

  /// <summary>
  /// Message validation failed.
  /// Message payload does not meet validation requirements.
  /// </summary>
  ValidationError = 4,

  /// <summary>
  /// Maximum retry attempts exceeded.
  /// Message has been tried too many times without success.
  /// </summary>
  MaxAttemptsExceeded = 5,

  /// <summary>
  /// Message held in buffer too long without successful publish.
  /// Lease could not be renewed or max hold time exceeded.
  /// </summary>
  LeaseExpired = 6,

  /// <summary>
  /// Unclassified error - reason not determined.
  /// </summary>
  Unknown = 99
}
