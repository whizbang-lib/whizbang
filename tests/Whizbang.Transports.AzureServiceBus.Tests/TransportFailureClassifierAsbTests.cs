using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Verifies <see cref="TransportFailureClassifier"/> against the REAL
/// <see cref="ServiceBusException"/> from <c>Azure.Messaging.ServiceBus</c>. The
/// Whizbang.Core.Tests classifier tests use fake exception classes (since Core can't
/// reference the ASB package); these tests close the gap by exercising the production
/// type's actual FullName + Message format.
/// </summary>
/// <remarks>
/// These remain UNIT tests — no broker, no network, no emulator. They only assert that
/// the classifier correctly recognizes the exception type that Azure's SDK actually
/// throws when ServiceBusy fires. A separate integration test would be needed to verify
/// the SDK actually throws this exception under real throttling pressure.
/// </remarks>
public class TransportFailureClassifierAsbTests {

  [Test]
  public async Task Classify_RealServiceBusException_WithServiceBusyReason_ReturnsThrottledAsync() {
    // The SDK's ServiceBusException ctor accepts a Reason enum which is stringified into
    // the Message at the production code path. We simulate the same shape by including
    // the reason text + 50009 code that the SDK's real exception message carries.
    var ex = new ServiceBusException(
      message: "The request was terminated because the namespace is being throttled. Error code : 50009. (ServiceBusy)",
      reason: ServiceBusFailureReason.ServiceBusy);

    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RealServiceBusException_NumericCodeOnlyInMessage_ReturnsThrottledAsync() {
    // Defensive: even if the SDK changes the human-readable reason wording, the 50009
    // numeric code remains. Confirm we match on either signal.
    var ex = new ServiceBusException("Error code : 50009. Try again later.", ServiceBusFailureReason.ServiceBusy);
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RealServiceBusException_ServiceCommunicationProblem_ReturnsTransportExceptionAsync() {
    // Non-throttling broker error → should be TransportException, not Throttled.
    var ex = new ServiceBusException("connection lost", ServiceBusFailureReason.ServiceCommunicationProblem);
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_RealServiceBusException_MessageLockLost_ReturnsTransportExceptionAsync() {
    // Receive-side failure (not relevant on publish path, but ensure it's still classified
    // as transport-level, not as throttling).
    var ex = new ServiceBusException("lock lost", ServiceBusFailureReason.MessageLockLost);
    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_RealServiceBusExceptionTypeName_MatchesClassifierExpectationAsync() {
    // Production guard: if the SDK ever rehomes ServiceBusException to a different
    // namespace, our FullName-based classifier silently stops matching. This test pins
    // the expected FullName so a rename surfaces as a test break.
    var ex = new ServiceBusException("any", ServiceBusFailureReason.ServiceBusy);
    await Assert.That(ex.GetType().FullName).IsEqualTo("Azure.Messaging.ServiceBus.ServiceBusException")
      .Because("the classifier uses FullName matching; a rename here would silently bypass throttle classification");
  }
}
