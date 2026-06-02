using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Regression locks for <see cref="TransportFailureClassifier"/>. The classifier turns raw
/// transport exceptions into typed <see cref="MessageFailureReason"/> values so the worker /
/// failure channel / dashboards can distinguish broker-side throttling from outright
/// outages. Done by name-match + message text so the Core assembly does not have to take a
/// reference on the transport libraries (Azure.Messaging.ServiceBus / RabbitMQ.Client).
/// </summary>
public class TransportFailureClassifierTests {

  [Test]
  public async Task Classify_AzureServiceBusServiceBusy_ReturnsThrottledAsync() {
    var ex = new Azure.Messaging.ServiceBus.ServiceBusException(
      "The request was terminated because the namespace is being throttled. Error code : 50009. (ServiceBusy)");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_AzureServiceBusServiceBusy_NumericCodeOnly_ReturnsThrottledAsync() {
    var ex = new Azure.Messaging.ServiceBus.ServiceBusException("Error code : 50009. Try later.");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_AzureServiceBusOther_ReturnsTransportExceptionAsync() {
    var ex = new Azure.Messaging.ServiceBus.ServiceBusException("Connection lost to broker");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_RabbitMqConnectionBlocked_ReturnsThrottledAsync() {
    var ex = new RabbitMQ.Client.OperationInterruptedException(
      "shutdown reason: connection.blocked, vhost resources alarm");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RabbitMqFlowControl_ReturnsThrottledAsync() {
    var ex = new RabbitMQ.Client.OperationInterruptedException("publisher flow-control nack");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RabbitMqOther_ReturnsTransportExceptionAsync() {
    var ex = new RabbitMQ.Client.OperationInterruptedException("channel closed: 404 NOT_FOUND");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_Timeout_ReturnsTransportExceptionAsync() {
    var ex = new TimeoutException("send timed out");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_GenericException_ReturnsUnknownAsync() {
    var ex = new InvalidOperationException("not transport-related");
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Unknown);
  }
}

// Fakes live in TransportFailureClassifierFakes.cs (separate file) so this file can keep
// the file-scoped namespace declaration. The classifier matches by FullName so the fakes
// MUST be in the production transport namespaces — see the sibling file.
