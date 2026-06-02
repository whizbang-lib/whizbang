using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Classifies transport-publish exceptions into <see cref="MessageFailureReason"/> values
/// without taking a direct dependency on any specific transport library.
/// </summary>
/// <remarks>
/// <para>
/// The detection is duck-typed by exception type-name + a property check on
/// <c>Reason</c>/<c>FailureReason</c>/<c>ReasonPhrase</c>-style members. That avoids
/// adding <c>Azure.Messaging.ServiceBus</c> / <c>RabbitMQ.Client</c> as references on
/// the core assembly while still classifying their throttling signals correctly.
/// </para>
/// <para>
/// Recognized throttling signals:
/// </para>
/// <list type="bullet">
///   <item><description>Azure Service Bus: <c>ServiceBusException</c> where
///   <c>Reason == ServiceBusy</c> (error code 50009).</description></item>
///   <item><description>RabbitMQ: <c>OperationInterruptedException</c> / <c>AlreadyClosedException</c>
///   carrying a <c>connection.blocked</c> reason, or any exception with the literal token
///   <c>flow-control</c> / <c>resource-blocked</c> in the message.</description></item>
/// </list>
/// </remarks>
public static class TransportFailureClassifier {
  /// <summary>
  /// Maps a publish-time exception to a <see cref="MessageFailureReason"/>.
  /// </summary>
  /// <param name="ex">The exception thrown by the transport publish call.</param>
  /// <returns>
  /// <see cref="MessageFailureReason.Throttled"/> for broker-side throttling/flow-control,
  /// <see cref="MessageFailureReason.TransportException"/> for transport errors that aren't
  /// throttling, or <see cref="MessageFailureReason.Unknown"/> for non-transport exceptions.
  /// </returns>
  public static MessageFailureReason Classify(Exception ex) {
    ArgumentNullException.ThrowIfNull(ex);
    if (_isAzureServiceBusBusy(ex) || _isRabbitMqFlowControl(ex)) {
      return MessageFailureReason.Throttled;
    }
    // Match by type name to avoid taking direct references on transport libs from Core.
    var typeName = ex.GetType().FullName ?? string.Empty;
    if (typeName.StartsWith("Azure.Messaging.ServiceBus.", StringComparison.Ordinal)
        || typeName.StartsWith("RabbitMQ.Client.", StringComparison.Ordinal)
        || typeName.StartsWith("Whizbang.Transports.", StringComparison.Ordinal)
        || ex is TimeoutException) {
      return MessageFailureReason.TransportException;
    }
    return MessageFailureReason.Unknown;
  }

  private static bool _isAzureServiceBusBusy(Exception ex) {
    // Check by full type name so we don't have to reference Azure.Messaging.ServiceBus
    // from this assembly. The throttling signal could be detected by reflecting on the
    // Reason property (ServiceBusFailureReason.ServiceBusy enum value), but that
    // triggers AOT trim warnings (IL2075) on the unannotated Type returned by
    // GetType(). Instead, match the well-known signal text the SDK emits:
    //   "... is being throttled. Error code : 50009 ... (ServiceBusy) ..."
    // Both the human-readable name and the numeric code are present in the message,
    // so either match path is robust to message-format tweaks across SDK versions.
    var typeName = ex.GetType().FullName;
    if (typeName != "Azure.Messaging.ServiceBus.ServiceBusException") {
      return false;
    }
    var msg = ex.Message ?? string.Empty;
    return msg.Contains("ServiceBusy", StringComparison.Ordinal)
        || msg.Contains("50009", StringComparison.Ordinal);
  }

  private static bool _isRabbitMqFlowControl(Exception ex) {
    // RabbitMQ surfaces two distinct throttling/backpressure signals:
    //   - connection.blocked from the broker when a vhost hits memory/disk alarms
    //   - publisher confirms with basic.nack carrying a flow-control reason
    // Both bubble up as OperationInterruptedException or AlreadyClosedException; the
    // shutdown reason text mentions "connection.blocked" or "flow". Match on message.
    var typeName = ex.GetType().FullName ?? string.Empty;
    if (!typeName.StartsWith("RabbitMQ.Client.", StringComparison.Ordinal)) {
      return false;
    }
    var msg = ex.Message ?? string.Empty;
    return msg.Contains("connection.blocked", StringComparison.Ordinal)
        || msg.Contains("flow-control", StringComparison.Ordinal)
        || msg.Contains("resource-blocked", StringComparison.Ordinal);
  }
}
