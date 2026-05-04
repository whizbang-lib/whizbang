using System.Reflection;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.AzureServiceBus;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Change-level tests for the a consumer 2026-05-04 hardening of ASB
/// Complete/Abandon/DeadLetter call sites against settlement-time exceptions
/// (lock lost, message not found, processor disposed). Mirrors the RabbitMQ
/// transport hardening committed earlier on this branch.
///
/// <para>
/// The hardening introduces <c>_safeAbandonAsync</c>, <c>_safeDeadLetterAsync</c>
/// (one overload each for <see cref="ProcessMessageEventArgs"/> and
/// <see cref="ProcessSessionMessageEventArgs"/>), and a shared policy method
/// <c>_isSettlementShouldSwallow(Exception)</c>. The wrappers all share the
/// same policy, so the policy is the right unit to test — the args types are
/// sealed Azure-SDK records with internal constructors that can't be mocked.
/// </para>
/// </summary>
public class AzureServiceBusSettlementSwallowPolicyTests {

  private static bool _invokePolicy(Exception ex) {
    var method = typeof(AzureServiceBusTransport).GetMethod(
      "_isSettlementShouldSwallow",
      BindingFlags.Static | BindingFlags.NonPublic)!;
    return (bool)method.Invoke(null, [ex])!;
  }

  // Constructs a ServiceBusException with a chosen Reason. ServiceBusException's
  // public ctor only takes (message), then sets Reason via internal API. We use
  // reflection to set Reason — fragile but stable enough for change-level tests.
  private static ServiceBusException _newServiceBusException(ServiceBusFailureReason reason) {
    var ex = new ServiceBusException("test", reason);
    return ex;
  }

  /// <summary>
  /// MessageLockLost — the most common scenario when an Abandon/Complete/DeadLetter
  /// fires after the message lock has timed out. Must swallow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_MessageLockLost_ReturnsTrueAsync() {
    var ex = _newServiceBusException(ServiceBusFailureReason.MessageLockLost);
    await Assert.That(_invokePolicy(ex)).IsTrue()
      .Because("MessageLockLost during settlement is the broker telling us the message will be redelivered. Swallow.");
  }

  /// <summary>
  /// SessionLockLost — equivalent for session-receive paths. Must swallow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_SessionLockLost_ReturnsTrueAsync() {
    var ex = _newServiceBusException(ServiceBusFailureReason.SessionLockLost);
    await Assert.That(_invokePolicy(ex)).IsTrue()
      .Because("SessionLockLost during settlement on a session message will trigger redelivery via the session lock-renewal mechanism. Swallow.");
  }

  /// <summary>
  /// MessageNotFound — message has already been settled by another consumer
  /// or removed from the entity. Must swallow (nothing left to do).
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_MessageNotFound_ReturnsTrueAsync() {
    var ex = _newServiceBusException(ServiceBusFailureReason.MessageNotFound);
    await Assert.That(_invokePolicy(ex)).IsTrue()
      .Because("MessageNotFound means the message is already gone — settlement is a no-op anyway.");
  }

  /// <summary>
  /// ObjectDisposedException — processor disposed during shutdown. Must swallow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_ObjectDisposed_ReturnsTrueAsync() {
    var ex = new ObjectDisposedException("processor");
    await Assert.That(_invokePolicy(ex)).IsTrue()
      .Because("ObjectDisposedException during settlement at shutdown is expected — the broker will redeliver.");
  }

  /// <summary>
  /// Service-level connection issues (ServiceBusy, ServiceTimeout) are NOT settlement
  /// issues — the broker still has the message and will require a retry. Don't swallow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_ServiceBusy_ReturnsFalseAsync() {
    var ex = _newServiceBusException(ServiceBusFailureReason.ServiceBusy);
    await Assert.That(_invokePolicy(ex)).IsFalse()
      .Because("ServiceBusy is a transient connection-level issue, not a settlement-acceptable outcome. Caller should retry / surface.");
  }

  /// <summary>
  /// Quota / size limit issues are application bugs, not lock-loss. Don't swallow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_QuotaExceeded_ReturnsFalseAsync() {
    var ex = _newServiceBusException(ServiceBusFailureReason.QuotaExceeded);
    await Assert.That(_invokePolicy(ex)).IsFalse();
  }

  /// <summary>
  /// Arbitrary non-ServiceBusException must NOT be swallowed. The policy is
  /// intentionally narrow.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_InvalidOperationException_ReturnsFalseAsync() {
    var ex = new InvalidOperationException("unrelated bug");
    await Assert.That(_invokePolicy(ex)).IsFalse()
      .Because("Only lock-lost / message-not-found / disposed are swallowed. Unrelated exceptions must propagate so they can be diagnosed.");
  }

  /// <summary>
  /// ArgumentException must propagate — that's a code defect, not a
  /// settlement failure.
  /// </summary>
  [Test]
  public async Task IsSettlementShouldSwallow_ArgumentException_ReturnsFalseAsync() {
    var ex = new ArgumentException("test");
    await Assert.That(_invokePolicy(ex)).IsFalse();
  }
}
