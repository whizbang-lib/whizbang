// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Slice 26 commit-order stamper. Allocates <c>commit_sequence</c> values via
/// <c>stamp_pending_commit_sequences</c> on every wake. Singleton per service schema —
/// every instance of the service runs the worker but only the one holding the schema's
/// <c>pg_try_advisory_lock</c> (see <see cref="CommitOrderStamperLockKey"/>) stamps.
/// Non-holders sleep on a retry interval.
///
/// <para>
/// Wake sources:
/// </para>
/// <list type="bullet">
/// <item><description><strong>Shared-conn LISTEN <c>wh_committed</c></strong> — sub-ms wake from
/// <c>_emit_event_store_chain</c> at commit time. Routed through
/// <see cref="ISharedNotifyConnection"/> (slice 33.5) instead of a dedicated direct connection
/// so all per-pod LISTEN traffic multiplexes onto one direct Postgres connection.</description></item>
/// <item><description><strong>Polling tick</strong> — <see cref="CommitOrderStamperOptions.PollingInterval"/>.
/// Correctness floor; runs unconditionally on the lock-holder, so the system stamps
/// even when LISTEN is unavailable (gate reports IsAvailable=false → wake won't fire from
/// NOTIFY; polling tick keeps stamping anyway).</description></item>
/// </list>
///
/// <para>
/// The advisory lock is still held on a dedicated short-lived connection — not the shared
/// conn — because the lock is session-scoped and would pin the shared conn for the worker's
/// entire leader tenure. That's incompatible with the shared conn's role as the per-pod
/// multiplexer.
/// </para>
///
/// <para>
/// Restart safety: the advisory lock is session-scoped, so a crash auto-releases.
/// The next instance's retry tick picks it up within <see cref="CommitOrderStamperOptions.LeaderElectionRetry"/>.
/// </para>
/// <para>
/// <b>Leadership by role assignment (#966).</b> When the duty elector holds
/// <see cref="CommitOrderStamperOptions.ROLE"/> by assignment (the default), leadership is that role
/// instead of the session lock: one holder per schema, won by a vote, kept by the stamping loop
/// itself (each iteration verifies the grant, which renews the lease, and the loop never sleeps
/// longer than one renew interval), and every stamp is fenced by the holder's epoch in the same
/// transaction, so a stamper that lost the role cannot stamp. A non-holder waits for the retry
/// interval or for the role's release to be announced. A newer-version instance asking for the role
/// gets it after the stamp in progress.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>tests/Whizbang.Core.Tests/Notifications/PgNotificationStackStartupGateTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Dependency-injection constructor: every parameter is a registered service or an optional seam the container fills, and a parameter object would only move the list. Same reasoning as Dispatcher.")]
public sealed partial class PgCommitOrderStamperWorker(
  IOptions<WhizbangNotificationOptions> notificationOptions,
  IOptions<CommitOrderStamperOptions> stamperOptions,
  IConfiguration configuration,
  ISharedNotifyConnection sharedConnection,
  ILogger<PgCommitOrderStamperWorker> logger,
  IDutyElector? dutyElector = null,
  INotificationConnectionStringFallback? connectionStringFallback = null,
  INotificationDataSource? notificationDataSource = null,
  INotifySignalingGate? notifySignalingGate = null,
  Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null
) : BackgroundService {
  private readonly WhizbangNotificationOptions _notificationOptions = notificationOptions?.Value ?? throw new ArgumentNullException(nameof(notificationOptions));
  private readonly CommitOrderStamperOptions _stamperOptions = stamperOptions?.Value ?? throw new ArgumentNullException(nameof(stamperOptions));
  private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
  private readonly ISharedNotifyConnection _sharedConnection = sharedConnection ?? throw new ArgumentNullException(nameof(sharedConnection));
  private readonly ILogger<PgCommitOrderStamperWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly INotificationConnectionStringFallback? _connectionStringFallback = connectionStringFallback;
  private readonly INotifySignalingGate? _notifySignalingGate = notifySignalingGate;
  // The size the next stamp asks for. Starts at the steady-state size and grows while calls keep
  // filling, because each call costs a scan of the whole pending set whatever size it asks for.
  // Only the loop below reads or writes it, and only one instance holds the role, so it needs no
  // synchronization of its own.
  private int _batchSize = stamperOptions?.Value?.BatchSize ?? 1000;
  private readonly Whizbang.Core.Workers.ISchemaReadyGate? _schemaReadyGate = schemaReadyGate;
  // Opt-in: register an INotificationDataSource via DI when the DbContext is
  // configured via UseNpgsql(NpgsqlDataSource) — that's the only path that
  // works because Npgsql strips credentials from both NpgsqlConnection.
  // ConnectionString and NpgsqlDataSource.ConnectionString. Marker-interface
  // wrap so we don't accidentally grab EF Core's own NpgsqlDataSource and
  // exhaust its small connection pool.
  private readonly NpgsqlDataSource? _dataSource = notificationDataSource?.DataSource;

  private const string CHANNEL_NAME = "wh_committed";
  private const string FENCE_SQLSTATE = "WHF01";
  private readonly PgRoleElector? _roleElector =
    dutyElector is PgRoleElector role && role.Manages(CommitOrderStamperOptions.ROLE) ? role : null;
  private readonly SemaphoreSlim _wake = new(initialCount: 1, maxCount: 1);
  private readonly SemaphoreSlim _vacancy = new(initialCount: 0, maxCount: 1);
  private bool _isLeader;
  private int _totalStamped;

  /// <summary>True while this instance holds the advisory lock (i.e. is the active stamper).</summary>
  public bool IsLeader => _isLeader;

  /// <summary>Cumulative count of rows this instance has stamped since start.</summary>
  public int TotalStamped => Volatile.Read(ref _totalStamped);

  /// <summary>Fires when this instance acquires the advisory lock and becomes the active stamper.</summary>
  public event Action? OnBecameLeader;

  /// <summary>
  /// Fires when this instance stops being the active stamper: its tenure ended (the role was lost,
  /// handed to a newer instance, or refused by the fence) or the worker is stopping. Under role
  /// assignment the role has already been released when it fires.
  /// </summary>
  public event Action? OnStoppedLeading;

  /// <summary>Fires after each <c>stamp_pending_commit_sequences</c> call with the count stamped this call.</summary>
  public event Action<int>? OnStampCompleted;

  /// <summary>
  /// Fires when a wake found nothing unstamped and the stamp was not run.
  /// </summary>
  /// <remarks>
  /// The stamp's eligibility query sorts every unstamped row by transaction id before taking a
  /// batch, and it used to run on every wake whether or not anything was unstamped: about half a
  /// core per busy database on the backstop tick under a bulk load. The partial-index existence
  /// probe costs nothing, so it decides whether the stamp runs at all.
  /// </remarks>
  public event Action? OnStampSkipped;

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Stamper drives the full leader-election + NOTIFY-driven wake + back-pressured stamping protocol. Splitting would require sharing the lock-conn lifetime + leader-state semaphore across helpers and lose the visible try/finally structure.")]
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_stamperOptions.DisableStamper) {
      LogDisabled(_logger);
      return;
    }

    // The leader loop calls stamp_pending_commit_sequences — a function the migration defines.
    // Hold at the schema gate before election; a disabled stamper (above) never waits.
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    var resolution = NotificationConnectionStringResolver.Resolve(_notificationOptions, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    // One stamper per SCHEMA, not per database: the advisory lock spans the database, and a
    // key shared by every schema let one service's stamper exclude every other service's.
    var lockKey = CommitOrderStamperLockKey.Compute(resolution.SearchPath, _stamperOptions.AdvisoryLockKey);
    // Prefer a DI-registered NpgsqlDataSource: when the DbContext is configured
    // via UseNpgsql(NpgsqlDataSource), neither the connection string nor the
    // data source's public ConnectionString carry the password (Npgsql strips
    // them eagerly), so the only way to authenticate is to ask the data source
    // to open the connection itself.
    if (resolution.ConnectionString is null && _dataSource is null) {
      LogDisabledNoConnection(_logger);
      return;
    }

    var keyForDiagnostics = _notificationOptions.ConnectionStringKey ?? "(unset)";
    var effectiveSource = _dataSource is not null
      ? NotificationConnectionStringResolver.ResolutionSource.DbContextFallback
      : resolution.Source;
    LogStarted(_logger, effectiveSource, keyForDiagnostics);

    // Production triage: Azure SCRAM-SHA-256 failures look identical regardless of which
    // resolution branch produced the password-less string. Log the source + which
    // credential markers we DID get, so operators can tell at a glance whether the
    // problem is:
    //   - config (the configured ConnectionStrings:<key>{-direct} entry lacks a password)
    //   - fallback (DbContext path returned a password-less string — e.g., Whizbang
    //     version pre-dates the RelationalOptionsExtension fix, or the DbContext was
    //     configured with NpgsqlDataSource which doesn't expose the original string)
    //   - explicit option (WhizbangNotificationOptions.DirectConnectionString missing password)
    var (HasUsername, HasSecret) = ConnectionStringCredentialMarkerSummary.Summarize(resolution.ConnectionString);
    LogConnectionDiagnostics(_logger, resolution.Source, keyForDiagnostics, HasUsername, HasSecret);

    // Loud-and-early warning mirroring PgSharedNotifyConnection: PooledKeyFallback
    // means the leader-lock connection routes through pgbouncer in tx-pooling mode.
    // pg_try_advisory_lock is session-scoped, so a tx-pool front-end will not preserve
    // the lock across the leader's tenure — operators MUST switch to `-direct` for the
    // stamper to function.
    if (effectiveSource == NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback) {
      LogPooledFallbackWarning(_logger, keyForDiagnostics);
    }

    // Slice 33.5 — subscribe to wh_committed via the shared connection for the entire
    // worker lifetime. Even when this pod is not the leader, the subscription is harmless:
    // the wake semaphore saturates at maxCount of 1 and the stamping loop never starts,
    // so this becomes a no-op. When this pod is the leader, the wake fires sub-millisecond
    // on each committed event.
    var subscription = new CommitNotificationSubscription(this);
    using var subscriptionHandle = _sharedConnection.Subscribe(subscription);

    // Snap-to-floor on gate-flip: when the gate transitions to false, the relaxed
    // cadence is no longer safe (NOTIFY may not arrive), so wake immediately and
    // let the next loop iteration recompute the effective interval. When the gate
    // flips back to true, wake too — if we were mid-sleep in floor cadence, the
    // next iteration picks up the relaxed cadence right away.
    void handleGateChange(bool _) => Wake();
    if (_notifySignalingGate is not null) {
      _notifySignalingGate.OnAvailabilityChanged += handleGateChange;
    }

    try {
      if (_roleElector is not null) {
        await _runAsRoleHolderAsync(_roleElector, resolution, stoppingToken);
        LogStopped(_logger);
        return;
      }
      while (!stoppingToken.IsCancellationRequested) {
        NpgsqlConnection? lockConn = null;
        try {
          // Prefer NpgsqlDataSource.OpenConnectionAsync — it carries the
          // credentials internally and is the only path that works with the
          // UseNpgsql(NpgsqlDataSource) DbContext configuration. Falls back to
          // the resolved connection string when no data source is registered.
          lockConn = _dataSource is not null
            ? await _dataSource.OpenConnectionAsync(stoppingToken)
            : new NpgsqlConnection(resolution.ConnectionString);
          if (_dataSource is null) {
            await lockConn.OpenAsync(stoppingToken);
          }

          var gotLock = await _tryAcquireLeaderLockAsync(lockConn, lockKey, stoppingToken);
          if (!gotLock) {
            await lockConn.DisposeAsync();
            lockConn = null;
            await Task.Delay(_stamperOptions.LeaderElectionRetry, stoppingToken);
            continue;
          }

          _setLeader(true);

          // Cancellation anywhere below means shutdown, and one handler answers it: the
          // per-iteration catch further down, reached after this iteration's finally has
          // released the advisory lock and cleared the leader flag. Answering it here too sent
          // the same shutdown out through a different line depending on which await happened to
          // observe the token first, so which line ran was a matter of timing.
          await _stampWhileLeaderAsync(lockConn, grant: null, stoppingToken);
        } catch (OperationCanceledException) {
          // The one shutdown exit: every canceled await in the iteration above lands here.
          break;
        } catch (Exception ex) {
          LogIterationError(_logger, ex.Message, resolution.Source, _notificationOptions.ConnectionStringKey ?? "(unset)");
          // Fall through to retry loop; lockConn finally below will release.
        } finally {
          _setLeader(false);
          if (lockConn is not null) {
            try { await _releaseLeaderLockAsync(lockConn, lockKey); } catch { /* best effort */ }
            await lockConn.DisposeAsync();
          }
        }

        // Brief pause before re-attempting lock acquisition on next iteration.
        try { await Task.Delay(_stamperOptions.LeaderElectionRetry, stoppingToken); } catch (OperationCanceledException) { break; }
      }
    } finally {
      if (_notifySignalingGate is not null) {
        _notifySignalingGate.OnAvailabilityChanged -= handleGateChange;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>
  /// The stamping loop while this instance leads. Under the session lock (<paramref name="grant"/>
  /// null) it runs until shutdown or an error. Under role assignment it also returns when the grant
  /// is lost, when a newer-version instance asked for the role (after the stamp in progress), or when
  /// a stamp is refused by the epoch fence; and it wakes at least once per renew interval, because
  /// its own verification is what renews the lease.
  /// </summary>
  private async Task _stampWhileLeaderAsync(NpgsqlConnection conn, IDutyGrant? grant, CancellationToken stoppingToken) {
    var skipWakeWait = false;
    var fencedDrain = false;
    while (!stoppingToken.IsCancellationRequested) {
      if (grant is not null && !await _stillLeadsAsync(grant, stoppingToken)) {
        return;
      }
      // Wait for NOTIFY-fired wake OR polling-interval timeout. Either path fires
      // the same stamp. Skipped when the previous iteration left known work behind
      // (mid-drain or fenced) — pending work never waits on an external wake.
      if (!skipWakeWait) {
        // Returning to wake-waiting ends any fenced-drain episode: subsequent stamps
        // are steady-state again and must not ring the make-up doorbell.
        fencedDrain = false;
        await _waitForWakeAsync(stoppingToken);
      }
      skipWakeWait = false;

      // Nothing unstamped, nothing to sort: the probe hits the partial index and costs
      // nothing, the stamp's eligibility CTE orders every unstamped row and does not.
      if (!await _hasPendingUnstampedAsync(conn, stoppingToken)) {
        OnStampSkipped?.Invoke();
        continue;
      }

      int stamped;
      try {
        stamped = await _stampOnceAsync(conn, notifyOwners: fencedDrain, grant, stoppingToken);
      } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
        // The database refused this holder's epoch: another instance holds the role now.
        LogFenced(_logger);
        return;
      }
      _ = Interlocked.Add(ref _totalStamped, stamped);
      OnStampCompleted?.Invoke(stamped);

      // Resized from the count the call returned rather than from a count of what is left: a probe for
      // the backlog size would be another scan of the set that is already too expensive to scan.
      _batchSize = CommitOrderStampBatch.Next(
        _batchSize, stamped, _stamperOptions.BatchSize, _stamperOptions.DrainBatchSize);

      if (stamped > 0) {
        // A full batch may have left more behind — drain immediately instead of
        // waiting for another wake.
        skipWakeWait = true;
      } else if (await _hasPendingUnstampedAsync(conn, stoppingToken)) {
        // Fenced: unstamped rows exist but an in-flight same-database transaction
        // holds the ordering fence, so this wake stamped nothing. The rows' own
        // committing wake has already fired and will not repeat — without this
        // retry they would sit invisible to perspective fetches until the next
        // external backstop tick. Keep re-stamping on the tight interval until
        // the fence clears and the pending set drains — and have those stamps ring
        // the owners' make-up doorbell (the commit-time doorbell was consumed by a
        // pre-visibility claim; nothing else re-wakes the appliers).
        fencedDrain = true;
        await Task.Delay(_stamperOptions.FencedRetryInterval, stoppingToken);
        skipWakeWait = true;
      }
    }
  }

  /// <summary>
  /// Waits for a NOTIFY-fired wake or the polling interval. Under role assignment the wait is capped at the
  /// renew interval, because this loop's own verification is what renews the lease.
  /// </summary>
  private async Task _waitForWakeAsync(CancellationToken stoppingToken) {
    var effectiveInterval = ComputeEffectivePollingInterval(_stamperOptions, _notifySignalingGate?.IsAvailable);
    if (_roleElector is not null && effectiveInterval > _roleElector.RenewInterval) {
      effectiveInterval = _roleElector.RenewInterval;
    }
    _ = await _wake.WaitAsync(effectiveInterval, stoppingToken);
  }

  /// <summary>
  /// Verifies the role before each iteration, which renews its lease from this loop; a lost grant
  /// or a drain request ends the tenure.
  /// </summary>
  private async Task<bool> _stillLeadsAsync(IDutyGrant grant, CancellationToken stoppingToken) {
    if (!await grant.VerifyStillHeldAsync(stoppingToken)) {
      LogRoleLost(_logger);
      return false;
    }
    if (grant.DrainRequested) {
      LogRoleDrained(_logger);
      return false;
    }
    return true;
  }

  /// <summary>
  /// Leadership as a role: vote, stamp while held, release, and vote again. A non-holder waits for
  /// the retry interval or for the role's release to be announced, whichever comes first.
  /// </summary>
  private async Task _runAsRoleHolderAsync(
      PgRoleElector elector, NotificationConnectionStringResolver.Resolution resolution, CancellationToken stoppingToken) {
    using var releases = _sharedConnection.Subscribe(new RoleReleaseSubscription(this));
    while (!stoppingToken.IsCancellationRequested) {
      try {
        var attempt = await elector.TryAcquireAsync(CommitOrderStamperOptions.ROLE, stoppingToken);
        if (attempt.Grant is { } grant) {
          await _leadAsRoleHolderAsync(grant, resolution, stoppingToken);
        }
        _ = await _vacancy.WaitAsync(_stamperOptions.LeaderElectionRetry, stoppingToken);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        LogIterationError(_logger, ex.Message, resolution.Source, _notificationOptions.ConnectionStringKey ?? "(unset)");
        try { await _vacancy.WaitAsync(_stamperOptions.LeaderElectionRetry, stoppingToken); } catch (OperationCanceledException) { break; }
      }
    }
  }

  /// <summary>One tenure: stamp on its own connection until it ends, then give the role back.</summary>
  private async Task _leadAsRoleHolderAsync(
      IDutyGrant grant, NotificationConnectionStringResolver.Resolution resolution, CancellationToken stoppingToken) {
    try {
      var conn = _dataSource is not null
        ? await _dataSource.OpenConnectionAsync(stoppingToken)
        : new NpgsqlConnection(resolution.ConnectionString);
      await using (conn) {
        if (_dataSource is null) {
          await conn.OpenAsync(stoppingToken);
        }
        _setLeader(true);
        await _stampWhileLeaderAsync(conn, grant, stoppingToken);
      }
    } finally {
      // The role is given back before this instance reports that it stopped leading, so whoever
      // hears that can win the role at once.
      await grant.DisposeAsync();
      _setLeader(false);
    }
  }

  /// <summary>A release of the stamper's role wakes a waiting non-holder at once.</summary>
  private sealed class RoleReleaseSubscription(PgCommitOrderStamperWorker owner) : INotifySubscription {
    public string ChannelName => DutyHolderWorker.RELEASE_CHANNEL;

    // Notifications are dispatched one at a time and the waiter only ever lowers the count, so a
    // count of zero here means a release cannot overflow the semaphore.
    public void OnNotification(string payload) {
      if (string.Equals(payload, CommitOrderStamperOptions.ROLE, StringComparison.Ordinal) && owner._vacancy.CurrentCount == 0) {
        _ = owner._vacancy.Release();
      }
    }
  }

  /// <summary>
  /// Computes the effective polling interval the wait-loop should use on the next tick.
  /// Returns <see cref="CommitOrderStamperOptions.PollingInterval"/> when the NOTIFY gate
  /// is unavailable (or not wired) — that's the "correctness floor" cadence. Returns
  /// <see cref="CommitOrderStamperOptions.NotifyHealthyPollingInterval"/> when the gate
  /// reports healthy AND the relaxed value is strictly greater than the floor; otherwise
  /// falls back to the floor.
  /// </summary>
  /// <remarks>
  /// Pure function so it's trivially unit-testable without spinning up the worker.
  /// Extracted as <c>internal static</c> so test assemblies can call it directly via
  /// <c>InternalsVisibleTo</c>.
  /// </remarks>
  internal static TimeSpan ComputeEffectivePollingInterval(
      CommitOrderStamperOptions options,
      bool? gateIsAvailable) {
    ArgumentNullException.ThrowIfNull(options);
    // Gate broken (false) or not wired (null) → floor cadence. Missed NOTIFY recovery
    // is the dominant failure mode; the poll must stay tight.
    if (gateIsAvailable != true) {
      return options.PollingInterval;
    }
    // Gate healthy AND relaxed value strictly greater than floor → use relaxed.
    // The "strictly greater" guard handles operator misconfiguration where
    // NotifyHealthyPollingInterval is set below the floor — the knob only relaxes,
    // never tightens.
#pragma warning disable CS0618 // Honoring the Slice 1 knob for backward compat until the stamper backstop loop retires in a follow-up slice.
    var relaxed = options.NotifyHealthyPollingInterval;
#pragma warning restore CS0618
    if (relaxed > options.PollingInterval) {
      return relaxed.Value;
    }
    return options.PollingInterval;
  }

  /// <summary>
  /// Fires the wake semaphore from the shared-conn dispatch path. Called by
  /// <see cref="CommitNotificationSubscription.OnNotification"/> on every wh_committed
  /// notification. Idempotent saturation — overlapping NOTIFYs collapse to a single
  /// pending wake.
  /// </summary>
  internal void Wake() {
    try {
      _ = _wake.Release();
    } catch (SemaphoreFullException) {
      // Already a wake pending — fine, the loop will pick up both at once.
    }
  }

  private static async Task<bool> _tryAcquireLeaderLockAsync(NpgsqlConnection conn, long lockKey, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
    cmd.Parameters.AddWithValue("k", lockKey);
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is bool b && b;
  }

  private static async Task _releaseLeaderLockAsync(NpgsqlConnection conn, long lockKey) {
    await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
    cmd.Parameters.AddWithValue("k", lockKey);
    _ = await cmd.ExecuteScalarAsync();
  }

  private async Task<int> _stampOnceAsync(NpgsqlConnection conn, bool notifyOwners, IDutyGrant? grant, CancellationToken ct) {
    // notifyOwners: TRUE only on the fenced-retry drain — the one case where the rows'
    // commit-time doorbell was provably consumed before they became visible, so the stamp
    // must ring the make-up doorbell (migration 118). Steady-state stamps pass FALSE and
    // keep the pre-117 doorbell rate: per-batch rings herd every owner's wake loops during
    // bulk stamping (startup backlogs, imports) and starve tightly-pooled hosts.
    int stamped;
    // A role holder's stamp is fenced: the epoch check and the stamp commit together, so a
    // holder that lost the role cannot stamp (#966, requirement 1).
    await using (var tx = grant is null ? null : await conn.BeginTransactionAsync(ct)) {
      if (grant is not null) {
        await PgRoleElector.AssertEpochAsync(conn, grant, ct);
      }
      await using var cmd = new NpgsqlCommand("SELECT stamp_pending_commit_sequences(@bs, @notify)", conn);
      cmd.Parameters.AddWithValue("bs", _batchSize);
      cmd.Parameters.AddWithValue("notify", notifyOwners);
      var result = await cmd.ExecuteScalarAsync(ct);
      stamped = Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
      if (tx is not null) {
        await tx.CommitAsync(ct);
      }
    }
    if (notifyOwners && stamped > 0) {
      // #720: the make-up doorbells were queued inside the stamp's transaction; ring them after it commits.
      await DoorbellRinger.RingAsync(conn, DoorbellRinger.FUNCTION_NAME, _logger, ct);
    }
    return stamped;
  }

  /// <summary>
  /// Cheap fenced-work probe — hits the idx_event_store_unstamped partial index. Same
  /// unqualified addressing as <see cref="_stampOnceAsync"/> (search_path-resolved).
  /// </summary>
  private static async Task<bool> _hasPendingUnstampedAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = new NpgsqlCommand(
      "SELECT EXISTS(SELECT 1 FROM wh_event_store WHERE commit_sequence IS NULL)", conn);
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is true;
  }

  private void _setLeader(bool isLeader) {
    if (_isLeader == isLeader) { return; }
    _isLeader = isLeader;
    if (isLeader) {
      LogBecameLeader(_logger);
      OnBecameLeader?.Invoke();
    } else {
      LogReleasedLeader(_logger);
      OnStoppedLeading?.Invoke();
    }
  }

  private sealed class CommitNotificationSubscription(PgCommitOrderStamperWorker owner) : INotifySubscription {
    public string ChannelName => CHANNEL_NAME;
    public void OnNotification(string payload) => owner.Wake();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker disabled by DisableStamper=true")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker disabled — no connection string resolved (set WhizbangNotificationOptions.ConnectionStringKey or DirectConnectionString)")]
  static partial void LogDisabledNoConnection(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker started with connection from {Source} for key '{ConnectionStringKey}'")]
  static partial void LogStarted(ILogger logger, NotificationConnectionStringResolver.ResolutionSource source, string connectionStringKey);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
    Message = "PgCommitOrderStamperWorker resolved the POOLED connection (key '{ConnectionStringKey}') — pg_try_advisory_lock cannot be held " +
              "through pgbouncer in transaction-pooling mode. Add ConnectionStrings:{ConnectionStringKey}-direct pointing to a direct " +
              "(non-pgbouncer) Postgres connection. Until then, leader election fails and commit-order stamping stalls.")]
  static partial void LogPooledFallbackWarning(ILogger logger, string connectionStringKey);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker became leader — actively stamping")]
  static partial void LogBecameLeader(ILogger logger);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker released leader role")]
  static partial void LogReleasedLeader(ILogger logger);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker iteration failed: {Reason}. Resolved connection from {ResolutionSource} for key '{ConnectionStringKey}'. " +
    "If Source=PooledKeyFallback, the connection routes through pgbouncer — set ConnectionStrings:{ConnectionStringKey}-direct with a direct (non-pgbouncer) Postgres string.")]
  static partial void LogIterationError(ILogger logger, string reason, NotificationConnectionStringResolver.ResolutionSource resolutionSource, string connectionStringKey);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "PgCommitOrderStamperWorker connection diagnostics — Source={Source}, key='{ConnectionStringKey}', has user-id marker={HasUsername}, has secret marker={HasSecret}. If HasSecret=false and Azure rejects with SCRAM-SHA-256, the resolved string is missing a credential — check ConnectionStrings:{ConnectionStringKey}(-direct) config or upgrade Whizbang for the RelationalOptionsExtension fallback fix.")]
  static partial void LogConnectionDiagnostics(
    ILogger logger,
    NotificationConnectionStringResolver.ResolutionSource source,
    string connectionStringKey,
    bool hasUsername,
    bool hasSecret);

  [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker lost the stamper role; voting again")]
  static partial void LogRoleLost(ILogger logger);

  [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "PgCommitOrderStamperWorker released the stamper role to a newer-version instance that asked for it")]
  static partial void LogRoleDrained(ILogger logger);

  [LoggerMessage(EventId = 17, Level = LogLevel.Warning, Message = "PgCommitOrderStamperWorker: the epoch fence refused a stamp; another instance holds the stamper role")]
  static partial void LogFenced(ILogger logger);
}
