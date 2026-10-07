// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The work listener's remaining decisions: the dead-letter payload's mapping, the optional logger,
/// and the subscription handle's lifecycle when the host stops before it started, or disposes after
/// it stopped.
/// </summary>
/// <remarks>No database: the shared connection and the gate are fakes.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkNotificationListener.cs</code-under-test>
[Category("Shard5")]
public class PgWorkNotificationListenerBranchTests {
  private static readonly IServiceInstanceProvider _instance =
    new ServiceInstanceProvider(Guid.NewGuid(), "listener-svc", "listener-host", processId: 1);

  // The wh_dead_letters insert trigger emits "deadletter"; DeadLetterRecoveryWorker is woken by
  // exactly this category. A payload falling through to "unknown" would leave dead letters waiting
  // for the slow backstop sweep instead of being recovered when their policy elapses.
  [Test]
  public async Task OnNotification_DeadLetterPayload_FiresDeadLetterReadyAsync() {
    using var listener = new PgWorkNotificationListener(new CountingSharedConnection(), new FakeGate(), _instance);
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("deadletter");

    await Assert.That(received.Count).IsEqualTo(1);
    await Assert.That(received[0]).IsEqualTo(WorkSignalCategory.DeadLetterReady);
  }

  // Constructed without a logger, the listener's only logging path (an unknown payload) must still
  // run: the payload is ignored and nothing is raised.
  [Test]
  public async Task OnNotification_UnknownPayloadWithoutALogger_IsIgnoredWithoutThrowingAsync() {
    using var listener = new PgWorkNotificationListener(new CountingSharedConnection(), new FakeGate(), _instance, logger: null);
    var received = new List<WorkSignalCategory>();
    listener.OnSignal += received.Add;

    ((INotifySubscription)listener).OnNotification("not-a-known-category");

    await Assert.That(received.Count).IsEqualTo(0);
    await Assert.That(listener.LastSignalAt).IsNotNull()
      .Because("the notification still arrived; only its category was unrecognized");
  }

  // A host that fails during startup still runs StopAsync on the services it registered. Stopping a
  // listener that never subscribed must be a no-op, not a NullReferenceException that masks the
  // startup failure.
  [Test]
  public async Task StopAsync_BeforeStart_IsANoOpAsync() {
    var shared = new CountingSharedConnection();
    using var listener = new PgWorkNotificationListener(shared, new FakeGate(), _instance);

    await ((Microsoft.Extensions.Hosting.IHostedService)listener).StopAsync(CancellationToken.None);

    await Assert.That(shared.SubscribeCount).IsEqualTo(0);
    await Assert.That(shared.DisposeCount).IsEqualTo(0);
  }

  // Stop releases the subscription; the container's Dispose afterwards must not release it again.
  [Test]
  public async Task Dispose_AfterStop_ReleasesTheSubscriptionOnlyOnceAsync() {
    var shared = new CountingSharedConnection();
    var listener = new PgWorkNotificationListener(shared, new FakeGate(), _instance);
    var hosted = (Microsoft.Extensions.Hosting.IHostedService)listener;

    await hosted.StartAsync(CancellationToken.None);
    await hosted.StopAsync(CancellationToken.None);
    listener.Dispose();

    await Assert.That(shared.SubscribeCount).IsEqualTo(1);
    await Assert.That(shared.DisposeCount).IsEqualTo(1)
      .Because("the handle was released by Stop and must not be released a second time by Dispose");
  }

  private sealed class CountingSharedConnection : ISharedNotifyConnection {
    public int SubscribeCount { get; private set; }
    public int DisposeCount { get; private set; }

    public IDisposable Subscribe(INotifySubscription subscription) {
      SubscribeCount++;
      return new Handle(this);
    }

    private sealed class Handle(CountingSharedConnection owner) : IDisposable {
      public void Dispose() => owner.DisposeCount++;
    }
  }

  private sealed class FakeGate : INotifySignalingGate {
    public bool IsAvailable => true;
    public DateTimeOffset? LastVerifiedAt => null;
    public DateTimeOffset? LastFailureAt => null;
    public string? LastFailureReason => null;
    public event Action<bool>? OnAvailabilityChanged;
    public Task<bool> ProbeNowAsync(CancellationToken cancellationToken = default) {
      OnAvailabilityChanged?.Invoke(true);
      return Task.FromResult(true);
    }
  }
}
