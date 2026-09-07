using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Coverage for <see cref="StandbyHandshake.AwaitPeersStandingByAsync"/> branches the E2E suite
/// (which drives a real migration end to end) never isolates: an unreadable version string, a peer
/// whose heartbeat lapsed, and a peer running a same-or-newer version. A standby handshake decides
/// which instance is live during a rolling upgrade — a peer wrongly counted as still blocking means
/// the migrator waits forever for a peer that is actually dead; a peer wrongly excused means the
/// migrator proceeds while a live older peer is still serving traffic against the pre-migration
/// schema.
/// </summary>
public class StandbyHandshakeCoverageTests {

  private sealed class _fakeFleetSource(IReadOnlyList<FleetInstanceStatus> fleet) : IStartupFleetStatusSource {
    public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) =>
      Task.FromResult(fleet);
  }

  private sealed class _fakeInstanceProvider(Guid instanceId) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = instanceId;
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static IServiceScopeFactory _emptyScopeFactory() =>
    new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

  /// <summary>What breaks: a migration must never run a handshake against a version string it
  /// cannot even parse — comparing peers against a guess instead of a real version could either
  /// wait forever or proceed prematurely.</summary>
  [Test]
  public async Task AwaitPeersStandingByAsync_UnreadableVersion_ThrowsArgumentExceptionAsync() {
    var handshake = new StandbyHandshake(
      _emptyScopeFactory(), new _fakeFleetSource([]), new _fakeInstanceProvider(Guid.NewGuid()));

    await Assert.That(async () => await handshake.AwaitPeersStandingByAsync("not-a-version", CancellationToken.None))
      .Throws<ArgumentException>()
      .Because("a migration must never run a handshake against a version string it cannot even parse — that is a guess, not a comparison");
  }

  /// <summary>What breaks: a peer that stopped heartbeating must stop counting (the wait is bounded
  /// by liveness, not by the goodwill of a possibly-dead process), and a same-or-newer peer must
  /// never be asked to stand by for an OLDER version. With both correctly excluded and nobody else
  /// blocking, the wait completes on the first pass instead of hanging on peers that will never
  /// acknowledge.</summary>
  [Test]
  public async Task AwaitPeersStandingByAsync_StaleHeartbeatAndNewerPeer_NeitherCountsAsBlockingAsync() {
    var self = Guid.NewGuid();
    var stalePeer = Guid.NewGuid();
    var newerPeer = Guid.NewGuid();
    var fleet = new List<FleetInstanceStatus> {
      new(self, "svc", "host", DateTimeOffset.UtcNow, [], LibraryVersion: "0.5.0"),
      new(stalePeer, "svc", "host", DateTimeOffset.UtcNow.AddMinutes(-10), [], LifecyclePhase: "Running", LibraryVersion: "0.5.0"),
      new(newerPeer, "svc", "host", DateTimeOffset.UtcNow, [], LifecyclePhase: "Running", LibraryVersion: "99.0.0"),
    };
    var handshake = new StandbyHandshake(
      _emptyScopeFactory(), new _fakeFleetSource(fleet), new _fakeInstanceProvider(self));

    var acknowledged = await handshake.AwaitPeersStandingByAsync("1.0.0", CancellationToken.None);

    await Assert.That(acknowledged).IsEmpty()
      .Because("a peer that stopped heartbeating stops counting, and a same-or-newer peer is never asked to stand by — with both excluded and nothing else blocking, the wait must complete on the first pass instead of looping on peers that will never acknowledge");
  }
}
