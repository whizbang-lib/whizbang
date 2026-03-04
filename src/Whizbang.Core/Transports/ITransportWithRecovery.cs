namespace Whizbang.Core.Transports;

/// <summary>
/// Interface for transports that support connection recovery notification.
/// </summary>
/// <remarks>
/// <para>
/// When a transport implements this interface, the <see cref="Workers.TransportConsumerWorker"/>
/// can register a recovery handler that will be called when the underlying connection
/// is re-established after a failure. This enables automatic re-subscription after
/// connection recovery.
/// </para>
/// <para>
/// <b>Implementation notes:</b>
/// <list type="bullet">
/// <item>RabbitMQ: Hook into <c>connection.RecoverySucceededAsync</c> event</item>
/// <item>Azure Service Bus: Hook into processor error recovery detection</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/ITransportWithRecoveryTests.cs</tests>
public interface ITransportWithRecovery {
  /// <summary>
  /// Sets the handler to be called when the transport connection recovers.
  /// </summary>
  /// <param name="onRecovered">
  /// The async handler to invoke when connection recovers.
  /// Set to null to remove the handler.
  /// </param>
  /// <remarks>
  /// The handler receives a <see cref="CancellationToken"/> that may be triggered
  /// if the worker is shutting down. Implementations should pass this token through
  /// to any async operations in the handler.
  /// </remarks>
  void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered);
}
