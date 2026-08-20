namespace RabbitMQ.Client;

/// <summary>
/// The failure classifier deliberately matches broker throttling by TYPE NAME, so Core takes no
/// reference on a transport SDK. A test double therefore has to live in the namespace the
/// classifier matches — this is the only way to exercise the throttle branch from Core's own test
/// assembly, and it keeps the classifier's contract (namespace + message text) under test too.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportFailureClassifier.cs</code-under-test>
internal sealed class ThrottleSignalException : System.Exception {
  /// <summary>Creates the double carrying the broker's flow-control signal text.</summary>
  public ThrottleSignalException() : base("connection.blocked: vhost resource alarm") { }

  /// <summary>Creates the double with a custom message.</summary>
  /// <param name="message">The message.</param>
  public ThrottleSignalException(string message) : base(message) { }

  /// <summary>Creates the double with a custom message and inner exception.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The inner exception.</param>
  public ThrottleSignalException(string message, System.Exception innerException)
    : base(message, innerException) { }
}
