using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Verifies <see cref="TransportFailureClassifier"/> against the REAL RabbitMQ.Client
/// exception types. The Core tests use fake classes in the production namespace; this
/// test closes the gap by exercising the actual SDK types' FullName + Message format.
/// </summary>
/// <remarks>
/// RabbitMQ surfaces broker backpressure in two distinct ways:
/// <list type="bullet">
///   <item><description><c>connection.blocked</c> when the vhost hits memory/disk alarms
///   — bubbles up via <see cref="OperationInterruptedException"/> with the reason in
///   the shutdown text.</description></item>
///   <item><description>Publisher confirms with <c>basic.nack</c> on flow-control —
///   surfaces similarly with "flow-control" in the message.</description></item>
/// </list>
/// Both should classify as <see cref="MessageFailureReason.Throttled"/> so retry kicks
/// in-memory instead of releasing the lease.
/// </remarks>
public class TransportFailureClassifierRabbitMqTests {

  [Test]
  public async Task Classify_RealOperationInterruptedException_ConnectionBlocked_ReturnsThrottledAsync() {
    // RabbitMQ.Client surfaces shutdown reasons via ShutdownEventArgs. We construct the
    // exception with a message that mirrors the broker's connection.blocked shutdown
    // reason text format.
    var ex = new OperationInterruptedException(
      new global::RabbitMQ.Client.Events.ShutdownEventArgs(
        global::RabbitMQ.Client.ShutdownInitiator.Peer,
        replyCode: 0,
        replyText: "shutdown reason: connection.blocked, vhost resources alarm"));

    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RealOperationInterruptedException_FlowControl_ReturnsThrottledAsync() {
    var ex = new OperationInterruptedException(
      new global::RabbitMQ.Client.Events.ShutdownEventArgs(
        global::RabbitMQ.Client.ShutdownInitiator.Peer,
        replyCode: 0,
        replyText: "publisher flow-control nack received"));

    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.Throttled);
  }

  [Test]
  public async Task Classify_RealOperationInterruptedException_ChannelClosed_ReturnsTransportExceptionAsync() {
    // Non-throttling broker shutdown (e.g., a 404 for missing queue) → TransportException.
    var ex = new OperationInterruptedException(
      new global::RabbitMQ.Client.Events.ShutdownEventArgs(
        global::RabbitMQ.Client.ShutdownInitiator.Peer,
        replyCode: 404,
        replyText: "NOT_FOUND - no queue 'doesnotexist'"));

    var reason = TransportFailureClassifier.Classify(ex);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.TransportException);
  }

  [Test]
  public async Task Classify_RealOperationInterruptedExceptionTypeName_MatchesClassifierExpectationAsync() {
    var ex = new OperationInterruptedException(
      new global::RabbitMQ.Client.Events.ShutdownEventArgs(
        global::RabbitMQ.Client.ShutdownInitiator.Application,
        replyCode: 0,
        replyText: "any"));
    await Assert.That(ex.GetType().FullName!)
      .StartsWith("RabbitMQ.Client.")
      .Because("the classifier matches by RabbitMQ.Client.* namespace; a rename here would silently bypass throttle classification");
  }
}
