using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Startup;

/// <summary>
/// The migrator side of the standby handshake: record the single fleet-wide request, wait for
/// every LIVE older peer to post <c>StandingBy</c>, and afterwards withdraw the request (on
/// rollback) or leave the ledger to speak (on commit — peers observe the new version and shut
/// down). The wait is bounded by liveness, never by the goodwill of a process that may already
/// be dead: an instance that stops heartbeating stops counting.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#the-standby-handshake</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StandbyHandshakeE2ETests.cs</tests>
public sealed partial class StandbyHandshake {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IStartupFleetStatusSource _fleetSource;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly StandbyWatcherOptions _options;
  private readonly ILogger<StandbyHandshake> _logger;

  /// <summary>Creates the handshake over the coordinator (requests) and the fleet source (acks).</summary>
  public StandbyHandshake(
      IServiceScopeFactory scopeFactory,
      IStartupFleetStatusSource fleetSource,
      IServiceInstanceProvider instanceProvider,
      StandbyWatcherOptions? options = null,
      ILogger<StandbyHandshake>? logger = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(fleetSource);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    _scopeFactory = scopeFactory;
    _fleetSource = fleetSource;
    _instanceProvider = instanceProvider;
    _options = options ?? new StandbyWatcherOptions();
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StandbyHandshake>.Instance;
  }

  /// <summary>Records the request. False when another instance's request is active — one
  /// handshake at a time, which is what duty election already guarantees for the migrator.</summary>
  public async Task<bool> RequestAsync(string version, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(version);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var granted = await coordinator.RequestStandbyAsync(_instanceProvider.InstanceId, version, cancellationToken)
      .ConfigureAwait(false);
    if (granted) {
      LogRequested(_logger, version);
    }
    return granted;
  }

  /// <summary>
  /// Waits until every LIVE peer older than <paramref name="version"/> has posted
  /// <c>StandingBy</c>. Peers whose heartbeat lapses stop counting — the wait is bounded by
  /// lease expiry. Returns the ids of the peers that acknowledged.
  /// </summary>
  public async Task<IReadOnlyList<Guid>> AwaitPeersStandingByAsync(string version, CancellationToken cancellationToken) {
    if (!SemanticVersion.TryParse(version, out var mine)) {
      throw new ArgumentException($"'{version}' is not a readable version — refusing to run a handshake on a guess.", nameof(version));
    }

    while (true) {
      cancellationToken.ThrowIfCancellationRequested();
      var fleet = await _fleetSource.GetFleetAsync(cancellationToken).ConfigureAwait(false);
      var blocking = new List<Guid>();
      var acknowledged = new List<Guid>();
      var now = DateTimeOffset.UtcNow;
      foreach (var peer in fleet) {
        if (peer.InstanceId == _instanceProvider.InstanceId) {
          continue;   // our own row does not acknowledge to itself
        }
        if (peer.Evicted) {
          continue;   // the deliberate fence: an evicted peer no longer counts, and the
                      // handshake completes without it
        }
        if (now - peer.LastHeartbeatAt > _options.RequesterLivenessWindow) {
          continue;   // a peer that stopped heartbeating stops counting
        }
        if (!SemanticVersion.TryParse(peer.LibraryVersion, out var theirs) || theirs.CompareTo(mine) >= 0) {
          continue;   // same-or-newer peers (and unranked ones) are not asked to stand by
        }
        if (string.Equals(peer.LifecyclePhase, nameof(Whizbang.Core.RunControl.LifecyclePhase.StandingBy), StringComparison.Ordinal)) {
          acknowledged.Add(peer.InstanceId);
        } else {
          blocking.Add(peer.InstanceId);
        }
      }

      if (blocking.Count == 0) {
        LogPeersStandingBy(_logger, acknowledged.Count);
        return acknowledged;
      }
      LogWaitingForPeers(_logger, blocking.Count);
      await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Withdraws this instance's request — the rollback path. On commit the ledger speaks
  /// for itself (peers re-assess, see the newer version, and shut down), but the request should
  /// still be withdrawn so the next handshake can begin.</summary>
  public async Task ClearAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    _ = await coordinator.ClearStandbyRequestAsync(_instanceProvider.InstanceId, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>
  /// The deliberate fence for a peer that will not acknowledge: tombstone it so its heartbeats,
  /// capability acquisitions and claims are refused, and the handshake completes without it.
  /// Never automatic — evicting a healthy-but-slow instance is itself an outage.
  /// </summary>
  public async Task EvictUnresponsivePeerAsync(Guid peerId, string reason, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(reason);
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    await coordinator.EvictInstanceAsync(peerId, _instanceProvider.InstanceId, reason, cancellationToken)
      .ConfigureAwait(false);
    LogEvicted(_logger, peerId, reason);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Standby requested for migration to {Version} — live older peers will drain and acknowledge")]
  static partial void LogRequested(ILogger logger, string version);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Waiting for {Count} live older peer(s) to post StandingBy")]
  static partial void LogWaitingForPeers(ILogger logger, int count);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "All live older peers standing by ({Count} acknowledged)")]
  static partial void LogPeersStandingBy(ILogger logger, int count);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Evicted unresponsive peer {PeerId}: {Reason}")]
  static partial void LogEvicted(ILogger logger, Guid peerId, string reason);
}
