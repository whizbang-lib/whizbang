using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The transport-side wiring of the <see cref="SessionOccupancyGovernor"/>: the rotation check
/// runs after each successful completion, calls <c>ReleaseSession()</c> exactly when the
/// governor's occupancy budget is reached, and the session lifecycle hooks stamp/clear the
/// occupancy clock. Uses the SDK's mocking seams (public <see cref="ProcessSessionMessageEventArgs"/>
/// constructor, virtual <c>ReleaseSession</c>, subclassable receiver) so the wiring is exercised
/// without a broker, and internal time-parameterized entry points so no test ever sleeps.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code>
public class SessionRotationWiringTests {
  private const string SESSION_ID = "stream-42";

  private static readonly TransportDestination _destination = new("inbox") { RoutingKey = "consumer-sub" };

  private static AzureServiceBusTransport _transport(TimeSpan renewalWindow) =>
    new(new MinimalFakeClient("sb://unit.servicebus.windows.net/"),
        new JsonSerializerOptions(),
        new AzureServiceBusOptions { MaxAutoLockRenewalDuration = renewalWindow },
        NullLogger<AzureServiceBusTransport>.Instance);

  private sealed class MinimalFakeClient(string fullyQualifiedNamespace) : ServiceBusClient {
    public override string FullyQualifiedNamespace => fullyQualifiedNamespace;
  }

  private sealed class RecordingSessionReceiver : ServiceBusSessionReceiver {
    public override string SessionId => SESSION_ID;
  }

  private sealed class RecordingArgs() : ProcessSessionMessageEventArgs(
      ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: SESSION_ID),
      new RecordingSessionReceiver(),
      CancellationToken.None) {
    public int Releases { get; private set; }
    public override void ReleaseSession() => Releases++;
  }

  [Test]
  public async Task Completion_BelowTheBudget_DoesNotReleaseTheSessionAsync() {
    var transport = _transport(TimeSpan.FromMinutes(5));
    var args = new RecordingArgs();
    var t0 = DateTimeOffset.UtcNow;

    transport.OnSessionInitializing(_destination, SESSION_ID, t0);
    transport.RotateSessionIfPastBudget(args, _destination, t0 + TimeSpan.FromMinutes(1));

    await Assert.That(args.Releases).IsEqualTo(0)
      .Because("a session comfortably inside its renewal window must keep its lock — rotation "
               + "below the budget would churn accepts for no safety gain");
  }

  [Test]
  public async Task Completion_AtTheBudget_ReleasesTheSessionExactlyOnceAsync() {
    var transport = _transport(TimeSpan.FromMinutes(5));
    var args = new RecordingArgs();
    var t0 = DateTimeOffset.UtcNow;

    transport.OnSessionInitializing(_destination, SESSION_ID, t0);
    transport.RotateSessionIfPastBudget(args, _destination, t0 + TimeSpan.FromMinutes(5));

    await Assert.That(args.Releases).IsEqualTo(1)
      .Because("past the occupancy budget the lock is about to stop being renewed — the only "
               + "completion-safe exit is handing it back while it is still valid");
  }

  [Test]
  public async Task AfterRotation_TheNextAcceptStartsAFreshClockAsync() {
    var transport = _transport(TimeSpan.FromMinutes(5));
    var t0 = DateTimeOffset.UtcNow;
    transport.OnSessionInitializing(_destination, SESSION_ID, t0);
    transport.RotateSessionIfPastBudget(new RecordingArgs(), _destination, t0 + TimeSpan.FromMinutes(5));

    var reaccept = t0 + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
    transport.OnSessionInitializing(_destination, SESSION_ID, reaccept);
    var args = new RecordingArgs();
    transport.RotateSessionIfPastBudget(args, _destination, reaccept + TimeSpan.FromMinutes(1));

    await Assert.That(args.Releases).IsEqualTo(0)
      .Because("rotation hands back the lock; the re-accepted session holds a FRESH one whose "
               + "renewal window has just begun");
  }

  [Test]
  public async Task NaturalSessionClose_ClearsTheClock_SoTheNextAcceptIsNotRotatedImmediatelyAsync() {
    var transport = _transport(TimeSpan.FromMinutes(5));
    var t0 = DateTimeOffset.UtcNow;
    transport.OnSessionInitializing(_destination, SESSION_ID, t0);
    transport.OnSessionClosing(_destination, SESSION_ID);   // idle timeout closed the session

    var hoursLater = t0 + TimeSpan.FromHours(3);
    transport.OnSessionInitializing(_destination, SESSION_ID, hoursLater);
    var args = new RecordingArgs();
    transport.RotateSessionIfPastBudget(args, _destination, hoursLater + TimeSpan.FromSeconds(5));

    await Assert.That(args.Releases).IsEqualTo(0)
      .Because("an idle-closed session's next accept must not inherit a stale clock — a spurious "
               + "immediate rotation on every re-accept is exactly the churn the governor prevents");
  }

  [Test]
  public async Task WithoutAnAcceptStamp_TheCompletionPathStampsItselfAsync() {
    // Processors without the SessionInitializing hook still get rotation protection: the first
    // completion stamps the clock, and the budget is measured from there.
    var transport = _transport(TimeSpan.FromMinutes(5));
    var args = new RecordingArgs();
    var t0 = DateTimeOffset.UtcNow;

    transport.RotateSessionIfPastBudget(args, _destination, t0);
    await Assert.That(args.Releases).IsEqualTo(0);

    transport.RotateSessionIfPastBudget(args, _destination, t0 + TimeSpan.FromMinutes(5));
    await Assert.That(args.Releases).IsEqualTo(1)
      .Because("the fallback stamp makes rotation self-sufficient at the completion site");
  }
}
