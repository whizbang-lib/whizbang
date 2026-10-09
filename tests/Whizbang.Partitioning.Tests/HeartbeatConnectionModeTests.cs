// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Signals;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// The registration records how an instance reaches the database (#1254), so the partition assigner judges it by its
/// alive-lock where its peers can see one and by its heartbeat otherwise. Pure: the request is built, never sent.
/// </summary>
[Category("Workers")]
public class HeartbeatConnectionModeTests {
  private sealed class Lifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase => LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
  }

  private sealed class Version : ILibraryVersionProvider {
    public string LibraryVersion => "1.0.0";
  }

  private sealed class DirectConnection : IInstanceConnectionModeSource {
    public InstanceConnectionMode ConnectionMode => InstanceConnectionMode.Direct;
    public bool IsAliveLockHeld => true;
  }

  private static HeartbeatWorker _worker(IInstanceConnectionModeSource? source) {
    var sp = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new HeartbeatWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(Guid.CreateVersion7(), "svc", "host", processId: 1),
      schemaReadyGate: gate,
      options: Options.Create(new HeartbeatWorkerOptions()),
      logger: NullLogger<HeartbeatWorker>.Instance,
      lifecycleState: new Lifecycle(),
      libraryVersion: new Version(),
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      aliveLockSource: NullInstanceAliveLockSource.Instance,
      signalBus: NullSignalBus.Instance,
      connectionModeSource: source);
  }

  [Test]
  public async Task BuildRequest_WithADirectConnection_RegistersDirectAsync() {
    await Assert.That(_worker(new DirectConnection()).BuildRequest().ConnectionMode).IsEqualTo(InstanceConnectionMode.Direct);
  }

  [Test]
  public async Task BuildRequest_WithTheNullSource_RegistersPooledAsync() {
    await Assert.That(_worker(NullInstanceConnectionModeSource.Instance).BuildRequest().ConnectionMode)
      .IsEqualTo(InstanceConnectionMode.Pooled);
  }

  [Test]
  public async Task BuildRequest_WithNoSource_RegistersPooledAsync() {
    await Assert.That(_worker(null).BuildRequest().ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled)
      .Because("an instance that cannot say is judged by its heartbeat");
  }

  [Test]
  public async Task NullSource_IsPooledWithNoLockAsync() {
    await Assert.That(NullInstanceConnectionModeSource.Instance.ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled);
    await Assert.That(NullInstanceConnectionModeSource.Instance.IsAliveLockHeld).IsFalse();
  }
}
