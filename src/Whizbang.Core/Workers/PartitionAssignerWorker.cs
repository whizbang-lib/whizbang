// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// The elected partition assigner (#1254): one instance, chosen through the role election, decides which instances
/// are live and publishes the partition assignment every claimer uses, instead of each claimer ranking the peers it
/// believes are alive.
/// </summary>
/// <remarks>
/// <para>
/// Every instance runs this worker and votes for <see cref="PartitionAssignerOptions.ROLE"/>; the holder leads. Each
/// tick of its tenure, one per role renewal interval, it reads the registered instances and judges each by the strategy
/// for its connection mode (<see cref="PartitionAssigner.IsLive"/>). It publishes when that changes the membership (an
/// alive-lock gained or lost, a heartbeat gone stale or fresh), when its tenure has not published yet, and on the slow
/// backstop cadence; otherwise it renews the published assignment's lease while it holds its alive-lock. Its heartbeat
/// renews the lease as well, so the lease runs out exactly when the assigner stops being seen.
/// </para>
/// <para>
/// Every publish is fenced by the role epoch, and a refused publish, a lost role or a drain request ends the tenure.
/// The assignment it leaves behind stays valid until its lease runs out, and claimers fall back to ranking themselves
/// after that, so a gap between assigners never stops a claim.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionAssignerWorkerTests.cs:Tick_TheFirstOfATenure_PublishesTheLiveMembersAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PartitionAssignerWorkerPostgresTests.cs:TwoInstances_OneIsElected_AndPublishesBothAsync</tests>
public sealed partial class PartitionAssignerWorker : BackgroundService {
  private readonly IDutyElector _elector;
  private readonly IPartitionAssignmentStore _store;
  private readonly IInstanceConnectionModeSource _aliveLock;
  private readonly IServiceInstanceProvider _instance;
  private readonly PartitionAssignerOptions _options;
  private readonly RoleAssignmentOptions _roleOptions;
  private readonly ILogger<PartitionAssignerWorker> _logger;
  private readonly TimeProvider _time;
  private PartitionAssignment? _published;
  private long _publishedAtTimestamp;
  // The tenure's role epoch; zero when this instance is not the assigner (role epochs start at 1).
  private long _epoch;

  /// <summary>Creates the worker.</summary>
  /// <param name="elector">The role election.</param>
  /// <param name="store">The assignment store.</param>
  /// <param name="aliveLock">This instance's connection mode and alive-lock, whose tick renews the assignment's lease.</param>
  /// <param name="instance">This instance's identity.</param>
  /// <param name="options">The assigner's options.</param>
  /// <param name="roleOptions">The role election's options: whether the role is managed, and its renew interval.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">The clock; the system clock by default.</param>
  public PartitionAssignerWorker(
      IDutyElector elector,
      IPartitionAssignmentStore store,
      IInstanceConnectionModeSource aliveLock,
      IServiceInstanceProvider instance,
      IOptions<PartitionAssignerOptions> options,
      IOptions<RoleAssignmentOptions> roleOptions,
      ILogger<PartitionAssignerWorker> logger,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(elector);
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(aliveLock);
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(roleOptions);
    ArgumentNullException.ThrowIfNull(logger);
    _elector = elector;
    _store = store;
    _aliveLock = aliveLock;
    _instance = instance;
    _options = options.Value;
    _roleOptions = roleOptions.Value;
    _logger = logger;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <summary>Whether this instance is the assigner now.</summary>
  public bool IsAssigner => Interlocked.Read(ref _epoch) > 0;

  /// <summary>The role epoch of this instance's tenure as assigner, or null when it is not the assigner.</summary>
  public long? Epoch => Interlocked.Read(ref _epoch) is var epoch and > 0 ? epoch : null;

  /// <summary>What this instance last published as assigner, or null.</summary>
  public PartitionAssignment? LastPublished => Volatile.Read(ref _published);

  /// <summary>Raised when this instance becomes the assigner, with its role epoch. Must not block.</summary>
  public event Action<long>? OnBecameAssigner;

  /// <summary>Raised when this instance's tenure as assigner ends, with the epoch it held. Must not block.</summary>
  public event Action<long>? OnStoppedAssigning;

  /// <summary>Raised after every evaluation of a tenure, whether or not it published. Must not block.</summary>
  public event Action<PartitionAssignerEvaluation>? OnEvaluated;

  /// <summary>Raised after each publish, with the assignment as the store recorded it. Must not block.</summary>
  public event Action<PartitionAssignment>? OnPublished;

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (!_options.Enabled || !_roleOptions.Manages(PartitionAssignerOptions.ROLE)) {
      return;
    }
    _options.Validate();
    while (!stoppingToken.IsCancellationRequested) {
      try {
        var attempt = await _elector.TryAcquireAsync(PartitionAssignerOptions.ROLE, stoppingToken).ConfigureAwait(false);
        if (attempt.Grant is { } grant) {
          await _leadAsync(grant, stoppingToken).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        // A failed vote or tenure is reported and the worker votes again; it never stops the host. A vote cut short by
        // shutdown lands here too, and the loop then ends on the stopping token.
        LogIterationFailed(_logger, ex);
      }
      await _pauseAsync(stoppingToken).ConfigureAwait(false);
    }
  }

  /// <summary>One renewal interval, or less when the host stops; never throws.</summary>
  private async Task _pauseAsync(CancellationToken stoppingToken) =>
    await Task.Delay(_roleOptions.RenewInterval, _time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

  private async Task _leadAsync(IDutyGrant grant, CancellationToken stoppingToken) {
    await using (grant.ConfigureAwait(false)) {
      if (grant.Epoch is not { } epoch) {
        // Without an epoch nothing can be fenced, so this elector cannot host the assigner.
        LogNoEpoch(_logger);
        return;
      }
      _begin(epoch);
      try {
        while (!stoppingToken.IsCancellationRequested && await _tickOrKeepAsync(grant, stoppingToken).ConfigureAwait(false)) {
          await _pauseAsync(stoppingToken).ConfigureAwait(false);
          if (!await grant.VerifyStillHeldAsync(stoppingToken).ConfigureAwait(false) || grant.DrainRequested) {
            break;
          }
        }
      } finally {
        _end(epoch);
      }
    }
  }

  /// <summary>A failed tick is logged and the tenure goes on: a transient read error is no reason to give the role back.</summary>
  private async Task<bool> _tickOrKeepAsync(IDutyGrant grant, CancellationToken stoppingToken) {
    try {
      return await TickAsync(grant, stoppingToken).ConfigureAwait(false);
    } catch (Exception ex) {
      // A tick cut short by shutdown lands here too; the tenure loop then ends on the stopping token.
      LogTickFailed(_logger, ex);
      return true;
    }
  }

  /// <summary>
  /// One evaluation of a tenure: read the candidates, judge them, publish when that calls for it, and otherwise renew
  /// the lease while the alive-lock is held. Returns false when the tenure has ended (the fence refused the publish).
  /// </summary>
  /// <param name="grant">The tenure's grant; its epoch fences the publish.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>Whether the tenure continues.</returns>
  internal async Task<bool> TickAsync(IDutyGrant grant, CancellationToken cancellationToken) {
    var epoch = grant.Epoch ?? throw new InvalidOperationException("A tenure needs an epoch.");
    if (Epoch != epoch) {
      _begin(epoch);
    }
    var self = _instance.InstanceId;
    var candidates = await _store.ReadCandidatesAsync(cancellationToken).ConfigureAwait(false);
    var members = PartitionAssigner.LiveMembers(candidates, self, _options);
    var published = LastPublished;
    var sincePublished = published is null ? TimeSpan.MaxValue : _time.GetElapsedTime(Volatile.Read(ref _publishedAtTimestamp));
    var evaluation = PartitionAssigner.Evaluate(members, published, epoch, sincePublished, _options);

    if (evaluation.Publishes) {
      var assignment = await _store.PublishAsync(
        PartitionAssignerOptions.ROLE, self, epoch, members, _options.AssignmentLease, cancellationToken).ConfigureAwait(false);
      if (assignment is null) {
        LogFenced(_logger, epoch);
        OnEvaluated?.Invoke(evaluation);
        return false;
      }
      Volatile.Write(ref _published, assignment);
      Volatile.Write(ref _publishedAtTimestamp, _time.GetTimestamp());
      LogPublished(_logger, assignment.Epoch, assignment.Revision, assignment.Members.Count, evaluation.Reason);
      OnPublished?.Invoke(assignment);
    } else if (_aliveLock.IsAliveLockHeld) {
      // The alive-lock is this assigner's liveness, so its tick renews the lease; the heartbeat renews it as well.
      _ = await _store.RenewAsync(self, epoch, cancellationToken).ConfigureAwait(false);
    }
    OnEvaluated?.Invoke(evaluation);
    return true;
  }

  private void _begin(long epoch) {
    _ = Interlocked.Exchange(ref _epoch, epoch);
    Volatile.Write(ref _published, null);
    LogBecameAssigner(_logger, epoch);
    OnBecameAssigner?.Invoke(epoch);
  }

  private void _end(long epoch) {
    _ = Interlocked.Exchange(ref _epoch, 0);
    LogStoppedAssigning(_logger, epoch);
    OnStoppedAssigning?.Invoke(epoch);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "This instance is the partition assigner at epoch {Epoch}.")]
  static partial void LogBecameAssigner(ILogger logger, long epoch);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "This instance stopped being the partition assigner (epoch {Epoch}).")]
  static partial void LogStoppedAssigning(ILogger logger, long epoch);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Published partition assignment {Epoch}:{Revision} with {MemberCount} members ({Reason}).")]
  static partial void LogPublished(ILogger logger, long epoch, long revision, int memberCount, PartitionAssignerReason reason);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "The partition assignment publish at epoch {Epoch} was refused: this instance no longer holds the role. The tenure ends.")]
  static partial void LogFenced(ILogger logger, long epoch);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "The role election granted the partition assigner role without an epoch, so its publishes cannot be fenced; the role is given back.")]
  static partial void LogNoEpoch(ILogger logger);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "A partition assigner iteration failed; it votes again after the renew interval.")]
  static partial void LogIterationFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "A partition assigner evaluation failed; the published assignment stands and the next tick tries again.")]
  static partial void LogTickFailed(ILogger logger, Exception exception);
}
