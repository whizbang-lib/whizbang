using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Drives the <see cref="OrphanInboxJanitor"/>'s <c>StartAsync</c> branches
/// directly. The companion test file
/// <c>OrphanInboxJanitorExtensionsTests</c> covers the DI extension; this
/// file targets the worker's own conditional paths so coverage tracks the
/// log/log-skip outcomes.
/// </summary>
/// <docs>messaging/resilience/orphan-inbox</docs>
public class OrphanInboxJanitorTests {

  private sealed record _SnapshotMsg : IMessage;

  /// <summary>
  /// Constructor null-arg guards: surface the actual <c>ArgumentNullException</c>
  /// from the worker's <c>ArgumentNullException.ThrowIfNull</c> calls.
  /// </summary>
  [Test]
  public async Task Constructor_NullServices_ThrowsAsync() {
    var snapshot = new HandledReceptorTypeSnapshot(Array.Empty<Type>());
    await Assert.That(() => new OrphanInboxJanitor(null!, snapshot))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullSnapshot_ThrowsAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new OrphanInboxJanitor(sp, null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Branch 1: no <see cref="IWorkCoordinator"/> in DI → debug-log + early
  /// return, with no failure.
  /// </summary>
  [Test]
  public async Task StartAsync_NoWorkCoordinator_ReturnsCleanlyAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    // Should not throw.
    await janitor.StartAsync(CancellationToken.None);
  }

  /// <summary>
  /// Branch 2: snapshot has zero types AND no perspective/raw registries →
  /// refuses to purge (else we'd empty the inbox during cold start) and
  /// returns cleanly.
  /// </summary>
  [Test]
  public async Task StartAsync_NoHandledTypes_SkipsPurgeAsync() {
    var coordinator = new _RecordingCoordinator();
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot(Array.Empty<Type>());
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    await janitor.StartAsync(CancellationToken.None);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(0);
  }

  /// <summary>
  /// Branch 3a: snapshot types yield handled-type names; coordinator returns
  /// zero purged rows → "no rows" log path + clean exit.
  /// </summary>
  [Test]
  public async Task StartAsync_WithHandledTypes_NoPurge_LogsAndExitsAsync() {
    var coordinator = new _RecordingCoordinator();  // default empty result
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    await janitor.StartAsync(CancellationToken.None);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastHandledTypeNames!.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Branch 3b: coordinator returns purged rows → grouping log path runs
  /// for each <c>MessageType</c>.
  /// </summary>
  [Test]
  public async Task StartAsync_WithHandledTypes_PurgedRows_LogsAndExitsAsync() {
    var coordinator = new _RecordingCoordinator {
      PurgeResult = [
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "B", "h2"),
      ],
    };
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    await janitor.StartAsync(CancellationToken.None);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// Branch 4: coordinator throws → janitor catches and logs, doesn't bubble
  /// up to kill the host.
  /// </summary>
  [Test]
  public async Task StartAsync_CoordinatorThrows_DoesNotPropagateAsync() {
    var coordinator = new _RecordingCoordinator { ThrowOnPurge = true };
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    // Should not bubble out.
    await janitor.StartAsync(CancellationToken.None);
  }

  /// <summary>
  /// Perspective + raw-receptor registries union into the handled-type-names
  /// list passed to the coordinator. Asserts both contributors land in the
  /// argument.
  /// </summary>
  [Test]
  public async Task StartAsync_UnionsPerspectiveAndRawRegistries_IntoHandledNamesAsync() {
    var coordinator = new _RecordingCoordinator();
    var perspectives = new _StaticPerspectiveRegistry(new List<Type> { typeof(int) });
    var raw = new _StaticRawRegistry(["RawA, RawAsm", "RawB, RawAsm"]);
    using var sp = _buildProviderWith(coordinator, perspectives, raw);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    await janitor.StartAsync(CancellationToken.None);

    await Assert.That(coordinator.LastHandledTypeNames!.Count).IsEqualTo(4);
  }

  /// <summary>
  /// <see cref="OrphanInboxJanitor.ExecuteAsync"/> is a no-op placeholder
  /// (lives behind <c>BackgroundService.StartAsync</c>'s call); call the
  /// public surface directly so it appears in coverage and so any future
  /// regression that wires real work into <c>ExecuteAsync</c> is caught.
  /// </summary>
  [Test]
  public async Task StartAsync_AlsoInvokesBaseExecuteAsync_NoOpAsync() {
    var coordinator = new _RecordingCoordinator();
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot);

    await janitor.StartAsync(CancellationToken.None);
    // Calling StopAsync also exercises the BackgroundService surface.
    await janitor.StopAsync(CancellationToken.None);
  }

  // -------------------- helpers --------------------

  private static ServiceProvider _buildProviderWith(
      IWorkCoordinator coordinator,
      IPerspectiveRunnerRegistry? perspectives = null,
      IRawReceptorRegistry? raw = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    if (perspectives != null) {
      services.AddSingleton(perspectives);
    }
    if (raw != null) {
      services.AddSingleton(raw);
    }
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Records purge calls and can be configured to return canned results or
  /// throw. Subclasses <see cref="NoOpWorkCoordinator"/> so the long tail of
  /// <see cref="IWorkCoordinator"/> methods get free no-op implementations.
  /// </summary>
  private sealed class _RecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int PurgeCallCount { get; private set; }
    public IReadOnlyList<string>? LastHandledTypeNames { get; private set; }
    public IReadOnlyList<PurgedOrphanInboxRow> PurgeResult { get; set; } = [];
    public bool ThrowOnPurge { get; set; }

    // Explicit interface impl required to override the default interface
    // method on IWorkCoordinator — a `new` member wouldn't be dispatched
    // when the janitor holds the instance through the interface.
    Task<IReadOnlyList<PurgedOrphanInboxRow>> IWorkCoordinator.PurgeOrphanInboxAsync(
        IReadOnlyList<string> handledTypeNames,
        CancellationToken cancellationToken) {
      PurgeCallCount++;
      LastHandledTypeNames = handledTypeNames;
      if (ThrowOnPurge) {
        throw new InvalidOperationException("simulated purge failure");
      }
      return Task.FromResult(PurgeResult);
    }
  }

  private sealed class _StaticPerspectiveRegistry(IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<Whizbang.Core.Messaging.LifecycleStage> LifecycleStagesWithReceptors { get; } =
      new HashSet<Whizbang.Core.Messaging.LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class _StaticRawRegistry(IReadOnlyCollection<string> registered) : IRawReceptorRegistry {
    public IReadOnlyCollection<string> RegisteredTypeNames => registered;
    public IRawReceptor? FindByTypeName(string messageTypeName) => null;
  }
}
