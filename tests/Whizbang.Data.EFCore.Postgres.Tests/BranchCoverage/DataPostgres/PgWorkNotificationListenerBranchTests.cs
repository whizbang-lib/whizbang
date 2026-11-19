// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The work listener's remaining decisions: the optional logger and clock, and the bus subscriptions' lifecycle when
/// the host stops before it started, or disposes after it stopped (#2046: the listener is a subscriber of the bus).
/// </summary>
/// <remarks>No database: the bus and the gate are fakes.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgWorkNotificationListener.cs</code-under-test>
[Category("Shard5")]
public class PgWorkNotificationListenerBranchTests {
  // A host that fails during startup still runs StopAsync on the services it registered. Stopping a
  // listener that never subscribed must be a no-op, not a NullReferenceException that masks the
  // startup failure.
  [Test]
  public async Task StopAsync_BeforeStart_IsANoOpAsync() {
    var bus = new CountingBus();
    using var listener = new PgWorkNotificationListener(bus, new FakeGate(), logger: null, timeProvider: null);

    await ((Microsoft.Extensions.Hosting.IHostedService)listener).StopAsync(CancellationToken.None);

    await Assert.That(bus.SubscribeCount).IsEqualTo(0);
    await Assert.That(bus.DisposeCount).IsEqualTo(0);
  }

  // Stop releases the subscriptions; the container's Dispose afterwards must not release them again.
  [Test]
  public async Task Dispose_AfterStop_ReleasesTheSubscriptionsOnlyOnceAsync() {
    var bus = new CountingBus();
    var listener = new PgWorkNotificationListener(bus, new FakeGate());
    var hosted = (Microsoft.Extensions.Hosting.IHostedService)listener;

    await hosted.StartAsync(CancellationToken.None);
    await hosted.StopAsync(CancellationToken.None);
    listener.Dispose();

    await Assert.That(bus.SubscribeCount).IsEqualTo(5).Because("one subscription per work doorbell");
    await Assert.That(bus.DisposeCount).IsEqualTo(5)
      .Because("each was released by Stop and must not be released a second time by Dispose");
  }

  private sealed class CountingBus : ISignalBus {
    public int SubscribeCount { get; private set; }
    public int DisposeCount { get; private set; }

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;

    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler) where TSignal : ISignal {
      SubscribeCount++;
      return new Handle(this);
    }

    private sealed class Handle(CountingBus owner) : ISignalSubscription {
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
