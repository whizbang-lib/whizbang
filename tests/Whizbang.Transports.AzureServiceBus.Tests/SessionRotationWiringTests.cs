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
        new DebugEnabledLogger());

  /// <summary>
  /// Debug-ENABLED no-op logger — NullLogger reports IsEnabled=false, which would skip the
  /// rotation log branch and leave it untested; a swallowed formatting bug there would only
  /// surface in production where debug logging is on.
  /// </summary>
  private sealed class DebugEnabledLogger : Microsoft.Extensions.Logging.ILogger<AzureServiceBusTransport> {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _ = formatter(state, exception);
  }

  private sealed class MinimalFakeClient(string fullyQualifiedNamespace) : ServiceBusClient {
    public RaisableSessionProcessor? LastSessionProcessor { get; private set; }
    public override string FullyQualifiedNamespace => fullyQualifiedNamespace;

    public override ServiceBusSessionProcessor CreateSessionProcessor(
        string topicName, string subscriptionName, ServiceBusSessionProcessorOptions options) {
      LastSessionProcessor = new RaisableSessionProcessor();
      return LastSessionProcessor;
    }
  }

  /// <summary>Exposes the SDK's protected event-raise seams so tests drive the attached lambdas.</summary>
  internal sealed class RaisableSessionProcessor : ServiceBusSessionProcessor {
    private readonly InnerFake _inner = new();
    protected override ServiceBusProcessor InnerProcessor => _inner;
    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RaiseSessionInitializingAsync(ProcessSessionEventArgs args) => OnSessionInitializingAsync(args);
    public Task RaiseSessionClosingAsync(ProcessSessionEventArgs args) => OnSessionClosingAsync(args);

    private sealed class InnerFake : ServiceBusProcessor {
    }
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
  public async Task SubscribedSessionProcessor_LifecycleHooks_DriveTheOccupancyClockAsync() {
    // Through the real subscribe path: the transport attaches SessionInitializing/Closing
    // handlers whose bodies stamp/clear the governor clock. Raising the SDK events proves the
    // attach wiring end-to-end — an initialize stamp makes a budget-later completion rotate,
    // and a close clears the clock so the next completion does not.
    var client = new MinimalFakeClient("sb://unit.servicebus.windows.net/");
    var transport = new AzureServiceBusTransport(
      client,
      new JsonSerializerOptions(),
      new AzureServiceBusOptions {
        AutoProvisionInfrastructure = false,
        EnableSessions = true,
        MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
      },
      new DebugEnabledLogger());
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination);
    var processor = client.LastSessionProcessor;
    await Assert.That(processor is not null).IsTrue()
      .Because("session mode must create a session processor through the client");

    var sessionArgs = new ProcessSessionEventArgs(new RecordingSessionReceiver(), CancellationToken.None);
    await processor!.RaiseSessionInitializingAsync(sessionArgs);
    var args = new RecordingArgs();
    transport.RotateSessionIfPastBudget(args, _destination, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));
    await Assert.That(args.Releases).IsEqualTo(1)
      .Because("the initialize hook stamped the clock, so a budget-later completion rotates");

    await processor.RaiseSessionClosingAsync(sessionArgs);
    var afterClose = new RecordingArgs();
    transport.RotateSessionIfPastBudget(afterClose, _destination, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6));
    await Assert.That(afterClose.Releases).IsEqualTo(0)
      .Because("the close hook cleared the clock — the completion path re-stamps fresh instead "
               + "of rotating on a stale accept time");
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
