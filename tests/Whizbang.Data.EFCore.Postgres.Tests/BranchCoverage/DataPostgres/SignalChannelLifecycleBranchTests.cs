// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// Subscription lifecycles on the shared connection: the signal transport disposed before it ever
/// started (a host that failed during startup), the transport disposed after start, and an
/// application-signal topic whose shared connection handed back no handle.
/// </summary>
/// <remarks>No database: the shared connection is a fake that counts subscriptions and releases.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresSignalTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs</code-under-test>
[Category("Shard5")]
public class SignalChannelLifecycleBranchTests {
  private static readonly IOptions<WhizbangNotificationOptions> _options = Options.Create(new WhizbangNotificationOptions());
  private static readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

  [Test]
  public async Task Transport_DisposedBeforeStart_ReleasesNothingAsync() {
    var shared = new CountingSharedConnection();
    var transport = _transport(shared);

    transport.Dispose();

    await Assert.That(shared.SubscribeCount).IsEqualTo(0);
    await Assert.That(shared.ReleaseCount).IsEqualTo(0);
  }

  [Test]
  public async Task Transport_DisposedAfterStart_ReleasesBothListensAsync() {
    var shared = new CountingSharedConnection();
    var transport = _transport(shared);
    await transport.StartAsync(new NoopSink());

    transport.Dispose();

    await Assert.That(shared.SubscribeCount).IsEqualTo(2)
      .Because("the transport listens on the broadcast channel and on its own instance channel");
    await Assert.That(shared.ReleaseCount).IsEqualTo(2);
  }

  // ISharedNotifyConnection is a public seam. A host-supplied implementation that hands back no
  // handle must not turn the last unsubscribe from a topic into a NullReferenceException.
  [Test]
  public async Task AppChannel_TopicWhoseSharedConnectionReturnedNoHandle_UnsubscribesCleanlyAsync() {
    var channel = new PgAppSignalChannel(_options, _configuration, new HandlelessSharedConnection(), NullLogger<PgAppSignalChannel>.Instance);
    var handle = channel.Subscribe("branch_handleless", (_, _) => Task.CompletedTask);

    await Assert.That(() => handle.Dispose()).ThrowsNothing();
  }

  private static PostgresSignalTransport _transport(ISharedNotifyConnection shared) => new(
    _options, _configuration, shared,
    new ServiceInstanceProvider(Guid.NewGuid(), "lifecycle-svc", "lifecycle-host", processId: 1),
    NullLogger<PostgresSignalTransport>.Instance);

  private sealed class CountingSharedConnection : ISharedNotifyConnection {
    public int SubscribeCount { get; private set; }
    public int ReleaseCount { get; private set; }

    public IDisposable Subscribe(INotifySubscription subscription) {
      SubscribeCount++;
      return new Release(this);
    }

    private sealed class Release(CountingSharedConnection owner) : IDisposable {
      public void Dispose() => owner.ReleaseCount++;
    }
  }

  private sealed class HandlelessSharedConnection : ISharedNotifyConnection {
    public IDisposable Subscribe(INotifySubscription subscription) => null!;
  }

  private sealed class NoopSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal => ValueTask.CompletedTask;
  }
}
