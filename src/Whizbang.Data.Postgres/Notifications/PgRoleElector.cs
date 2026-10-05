// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The role-assignment <see cref="IDutyElector"/>: the advisory lock decides only the vote, and the
/// role is a row from then on. A duty listed in <see cref="RoleAssignmentOptions.Roles"/> is won by
/// <c>wh_vote_role</c> (migration 184) and held as an assignment with an epoch, a lease in database
/// time (its own, when the duty declares one), and no session to keep open. Every other duty is
/// delegated to the session-lock elector unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Liveness comes from the holder's own work loop.</b> <see cref="IDutyGrant.VerifyStillHeldAsync"/>,
/// which a holder already calls before each unit of exclusive work, renews the lease. There is no
/// renewal timer: a holder that is stuck stops verifying and lapses, which is the point. Renewals
/// are throttled to one per <see cref="RoleAssignmentOptions.RenewInterval"/>, measured on the
/// monotonic clock from the moment the last successful renewal was sent. The database computed that
/// lease from a later instant, so answering from memory inside the interval is safe, and it never
/// lets a write through anyway: writes are fenced in SQL by <c>wh_assert_role_epoch</c>.
/// </para>
/// <para>
/// <b>The grant holds no connection</b> unless the bridge is on. Each vote, renewal and release is one
/// statement on an ordinary connection, which a transaction-pooling front end serves correctly and
/// which a database restart does not invalidate: the row survives, and the next renewal presents the
/// same epoch.
/// </para>
/// <para>
/// <b>The bridge</b> (<see cref="RoleAssignmentOptions.HoldLegacySessionLock"/>) keeps mixed-version
/// fleets from ever both acting. Before voting, the elector takes the duty's legacy session lock
/// (<see cref="DutyLockKey"/>, the key the session-lock elector uses) and votes only if it won, and
/// the holder keeps that lock for as long as it holds the role. An old instance therefore sees the
/// duty as held, and a new instance defers to an old holder. With the bridge off, the vote still
/// refuses while another backend holds that lock.
/// </para>
/// <para>
/// A transient failure to renew answers false without marking the grant lost, because the lease may
/// still be valid and the next verify asks again. An explicit refusal marks it lost for good.
/// </para>
/// <para>
/// <b>The decisions of #968.</b> Each vote presents this instance's library version: a vacant role
/// goes to the newest version voting for it, and a live holder on an older version is asked to drain,
/// which it learns on a renewal (<see cref="IDutyGrant.DrainRequested"/>). A holder running one long
/// statement marks that statement's backend (<see cref="MarkDutyBackendAsync"/>) and is treated as
/// live while it runs. While bridged, a lapsed holder whose session still holds the legacy lock has
/// that session, and only that session, ended by the next would-be winner, which is logged and
/// counted. A fleet-wide lapse carries no cool-down (decided in SQL).
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentElectorE2ETests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "The only disposable field is a SemaphoreSlim used as an async lock; it allocates nothing to release unless AvailableWaitHandle is read, which this type never does.")]
public sealed partial class PgRoleElector : IDutyElector, IReleasesDutiesOnShutdown, IRoleAssignmentReader {
  private readonly WhizbangNotificationOptions _options;
  private readonly RoleAssignmentOptions _roleOptions;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IDutyElector _delegate;
  private readonly ILogger<PgRoleElector> _logger;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;
  private readonly TimeProvider _time;
  private readonly RoleAssignmentMetrics? _metrics;
  private readonly int[] _versionKey;
  private readonly ConcurrentDictionary<string, Tenure> _tenures = new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _acquireGate = new(1, 1);

  /// <summary>Creates the elector.</summary>
  /// <param name="options">Notification options: where the coordination connection comes from.</param>
  /// <param name="roleOptions">Role-assignment tuning; validated here.</param>
  /// <param name="configuration">Configuration, for connection resolution.</param>
  /// <param name="instanceProvider">This instance's identity.</param>
  /// <param name="delegateElector">The elector for every duty this one does not manage.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="libraryVersion">This instance's library version, presented to every vote. Required,
  /// and null only by an explicit choice: without it this instance counts as the oldest version, so it
  /// never asks for a drain and is never preferred.</param>
  /// <param name="connectionStringFallback">Optional connection-string fallback.</param>
  /// <param name="notificationDataSource">Optional dedicated data source.</param>
  /// <param name="timeProvider">Optional clock for the renew throttle; the system clock when null.</param>
  /// <param name="metrics">Optional meters.</param>
  /// <exception cref="ArgumentNullException">A required argument is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The role options describe an unworkable lease.</exception>
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam (same reasoning as PgDutyElector)
  public PgRoleElector(
      IOptions<WhizbangNotificationOptions> options,
      IOptions<RoleAssignmentOptions> roleOptions,
      IConfiguration configuration,
      IServiceInstanceProvider instanceProvider,
      IDutyElector delegateElector,
      ILogger<PgRoleElector> logger,
      ILibraryVersionProvider? libraryVersion,
      INotificationConnectionStringFallback? connectionStringFallback = null,
      INotificationDataSource? notificationDataSource = null,
      TimeProvider? timeProvider = null,
      RoleAssignmentMetrics? metrics = null) {
#pragma warning restore S107
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(roleOptions);
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    ArgumentNullException.ThrowIfNull(delegateElector);
    ArgumentNullException.ThrowIfNull(logger);
    _options = options.Value;
    _roleOptions = roleOptions.Value;
    _roleOptions.Validate();
    _configuration = configuration;
    _instanceProvider = instanceProvider;
    _delegate = delegateElector;
    _logger = logger;
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
    _time = timeProvider ?? TimeProvider.System;
    _metrics = metrics;
    _versionKey = LibraryVersionKey.From(libraryVersion?.LibraryVersion);
  }

  /// <summary>Whether <paramref name="duty"/> is held by assignment here rather than delegated.</summary>
  /// <param name="duty">The duty.</param>
  /// <returns>True when the role options manage it.</returns>
  public bool Manages(string duty) => _roleOptions.Manages(duty);

  /// <summary>How often a holder renews; a holder's work loop wakes at least this often.</summary>
  public TimeSpan RenewInterval => _roleOptions.RenewInterval;

  /// <inheritdoc />
  public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(duty);
    return _roleOptions.Manages(duty)
      ? _voteAsync(duty, cancellationToken)
      : _delegate.TryAcquireAsync(duty, cancellationToken);
  }

  /// <summary>
  /// Votes for several duties at once. The roles managed here that this instance does not hold yet
  /// are voted for in ONE statement (<c>wh_vote_roles</c>), unless bridged, when each role's legacy
  /// lock must be taken first; everything else is attempted one at a time.
  /// </summary>
  /// <param name="duties">The duties.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One attempt per duty, in order.</returns>
  public async Task<IReadOnlyList<DutyAttempt>> TryAcquireManyAsync(IReadOnlyList<string> duties, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(duties);
    var voted = _roleOptions.HoldLegacySessionLock
      ? new Dictionary<string, DutyAttempt>(StringComparer.Ordinal)
      : await _voteManyAsync([.. duties.Where(Manages).Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
    var attempts = new List<DutyAttempt>(duties.Count);
    foreach (var duty in duties) {
      attempts.Add(voted.Remove(duty, out var attempt)
        ? attempt
        : await TryAcquireAsync(duty, cancellationToken).ConfigureAwait(false));
    }
    return attempts;
  }

  /// <summary>
  /// Marks the backend <paramref name="connection"/> is on as the backend running
  /// <paramref name="grant"/>'s duty, until the returned scope is disposed. While a statement runs on
  /// that backend, the assignment does not lapse, however long the statement outlasts the lease
  /// (#968, decision 1). Dispose the scope as soon as the statement ends, on the same connection, so a
  /// pooled connection reused for other work never keeps the role alive.
  /// </summary>
  /// <param name="connection">An open connection: the one about to run the duty's long statement.</param>
  /// <param name="grant">The grant. Anything but a role-assignment grant marks nothing.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The scope that clears the mark; a no-op scope when nothing was marked.</returns>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentResilienceE2ETests.cs:MarkDutyBackend_KeepsTheRoleThroughAStatementThatOutlastsTheLeaseAsync</tests>
  public static async Task<IAsyncDisposable> MarkDutyBackendAsync(
      NpgsqlConnection connection, IDutyGrant? grant, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    if (grant is not Handle handle
        || !await _markAsync(connection, handle.Tenure, mark: true, cancellationToken).ConfigureAwait(false)) {
      return NoMark.Instance;
    }
    return new DutyBackendMark(connection, handle.Tenure);
  }

  /// <summary>
  /// The epoch fence for exclusive work: presents <paramref name="grant"/>'s holder and epoch to
  /// <c>wh_assert_role_epoch</c> on <paramref name="connection"/>, inside the caller's transaction, so
  /// the writes that follow in it commit only while this grant is the current assignment. Raises
  /// SQLSTATE <c>WHF01</c> when it is not. A grant that is not a role assignment has no epoch to
  /// present, and passes.
  /// </summary>
  /// <param name="connection">The connection whose transaction carries the exclusive writes.</param>
  /// <param name="grant">The grant.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="PostgresException">SQLSTATE <c>WHF01</c>: the grant no longer holds the role.</exception>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CommitOrderStamperRoleTests.cs</tests>
  public static async Task AssertEpochAsync(NpgsqlConnection connection, IDutyGrant grant, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(grant);
    if (grant is not Handle handle) {
      return;
    }
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_assert_role_epoch(@role, @id, @epoch)";
    cmd.Parameters.AddWithValue("role", handle.Tenure.Role);
    cmd.Parameters.AddWithValue("id", handle.Tenure.InstanceId);
    cmd.Parameters.AddWithValue("epoch", handle.Tenure.Epoch);
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static async Task<bool> _markAsync(NpgsqlConnection connection, Tenure tenure, bool mark, CancellationToken cancellationToken) {
    // The mark is a backstop, and on a database that has not yet applied 184 the function does not
    // exist: the migrator marks its backend before running the migrations that create it. Probing
    // first, rather than catching the error, keeps a failed statement from aborting the caller's
    // transaction.
    await using (var probe = connection.CreateCommand()) {
      probe.CommandText = "SELECT to_regprocedure('wh_mark_role_duty_backend(text, uuid, bigint, boolean)') IS NOT NULL";
      if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return false;
      }
    }
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_mark_role_duty_backend(@role, @id, @epoch, @mark)";
    cmd.Parameters.AddWithValue("role", tenure.Role);
    cmd.Parameters.AddWithValue("id", tenure.InstanceId);
    cmd.Parameters.AddWithValue("epoch", tenure.Epoch);
    cmd.Parameters.AddWithValue(nameof(mark), mark);
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  private sealed class NoMark : IAsyncDisposable {
    public static readonly NoMark Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>Clears the mark on the connection that set it. A connection that died cleared it with its backend.</summary>
  private sealed class DutyBackendMark(NpgsqlConnection connection, Tenure tenure) : IAsyncDisposable {
    public async ValueTask DisposeAsync() {
#pragma warning disable CA1031, RCS1075 // best-effort: the mark names this backend, and a dead backend is never active
      try {
        _ = await _markAsync(connection, tenure, mark: false, CancellationToken.None).ConfigureAwait(false);
      } catch (Exception) {
        // intentionally swallowed — see pragma justification
      }
#pragma warning restore CA1031, RCS1075
    }
  }

  /// <summary>
  /// Releases every role this elector still holds, so a graceful stop hands each one off at once.
  /// Idempotent; a release that cannot reach the database leaves the lease to lapse.
  /// </summary>
  /// <param name="cancellationToken">The host's stop token.</param>
  public async Task ReleaseAllAsync(CancellationToken cancellationToken) {
    foreach (var tenure in _tenures.Values) {
      cancellationToken.ThrowIfCancellationRequested();
      await tenure.ReleaseAsync().ConfigureAwait(false);
    }
  }

  /// <summary>
  /// A role this elector already holds is answered from its tenure (verified, which renews it when
  /// due), so one process never holds two tenures of one assignment. Serialized, so two callers
  /// asking at once cannot both vote.
  /// </summary>
  private async Task<DutyAttempt> _voteAsync(string role, CancellationToken cancellationToken) {
    await _acquireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (_tenures.TryGetValue(role, out var tenure) && await tenure.VerifyAsync(cancellationToken).ConfigureAwait(false)) {
        return DutyAttempt.Granted(tenure.OpenHandle());
      }
      return await _voteFreshAsync(role, cancellationToken).ConfigureAwait(false);
    } finally {
      _acquireGate.Release();
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<RoleAssignmentSnapshot>> ReadAssignmentsAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    var snapshots = new List<RoleAssignmentSnapshot>();
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT role, state, holder_instance_id, epoch, assigned_at, renewed_at, lease_remaining, election_count, "
        + "void_reason, last_holder_instance_id, last_vacated_at, last_vacated_reason, pending_work, drain_requested_at, duty_backend_active "
        + "FROM wh_role_assignment_status()";
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        snapshots.Add(new RoleAssignmentSnapshot(
          reader.GetString(0),
          _state(reader.GetString(1)),
          await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
          reader.GetInt64(3),
          await _instantAsync(reader, 4, cancellationToken).ConfigureAwait(false),
          await _instantAsync(reader, 5, cancellationToken).ConfigureAwait(false),
          await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetTimeSpan(6),
          reader.GetInt64(7),
          await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(8),
          await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(9),
          await _instantAsync(reader, 10, cancellationToken).ConfigureAwait(false),
          await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11),
          reader.GetInt64(12)) {
          DrainRequestedAt = await _instantAsync(reader, 13, cancellationToken).ConfigureAwait(false),
          DutyBackendActive = reader.GetBoolean(14),
        });
      }
    }
    return snapshots;
  }

  /// <summary>The SQL state names map one to one; <c>vacant</c> is the last arm because the function emits nothing else.</summary>
  private static RoleAssignmentState _state(string state) => state switch {
    "held" => RoleAssignmentState.Held,
    "lapsed" => RoleAssignmentState.Lapsed,
    _ => RoleAssignmentState.Vacant,
  };

  private static async Task<DateTimeOffset?> _instantAsync(NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
    await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(ordinal, cancellationToken).ConfigureAwait(false);

  private async Task<DutyAttempt> _voteFreshAsync(string role, CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return _unavailable(role);
    }

    var legacyKey = _legacyKey(resolution.SearchPath, role);
    var slot = new BridgeSlot();
    var voting = _voteUnderBridgeAsync(plan, role, legacyKey, slot, cancellationToken);
    // Wait for the vote however it ends without throwing yet, because the bridge must be released
    // first; awaiting it again afterwards rethrows a failure with its original stack. (A finally
    // that awaits would do the same but leaves a compiler branch no test can take.)
    await ((Task)voting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    if (slot.Connection is not null) {
      // Anything but a grant gives the session lock back, including a vote that threw.
      await _releaseBridgeAsync(slot.Connection, legacyKey).ConfigureAwait(false);
    }
    return await voting.ConfigureAwait(false);
  }

  /// <summary>How an instance on the session-lock elector locks <paramref name="role"/> in this schema.</summary>
  private long _legacyKey(string? searchPath, string role) =>
    _roleOptions.LegacyLockKeys.TryGetValue(role, out var key) ? key(searchPath) : DutyLockKey.Compute(searchPath, role);

  /// <summary>The bridge connection a vote holds until a grant takes it over.</summary>
  private sealed class BridgeSlot {
    public NpgsqlConnection? Connection { get; set; }
  }

  private async Task<DutyAttempt> _voteUnderBridgeAsync(
      NotificationConnectionPlan plan, string role, long legacyKey, BridgeSlot slot, CancellationToken cancellationToken) {
    if (!_roleOptions.HoldLegacySessionLock) {
      // No bridge: the vote itself refuses while another backend holds the legacy lock.
      return await _decideAsync(plan, role, legacyKey, null, legacyKey, cancellationToken).ConfigureAwait(false);
    }
    var bridge = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    slot.Connection = bridge;
    if (!await _tryLockAsync(bridge, legacyKey, cancellationToken).ConfigureAwait(false)
        && !await _endLapsedBridgeAndRetryAsync(plan, bridge, role, legacyKey, cancellationToken).ConfigureAwait(false)) {
      slot.Connection = null;
      await bridge.DisposeAsync().ConfigureAwait(false);   // lost the lock, so there is nothing to unlock
      // Still a candidate, and still able to ask an older holder to drain: the bridge must not cost
      // the newest-version preference or the cooperative drain.
      return await _requestDrainAsync(plan, role, cancellationToken).ConfigureAwait(false) == "draining"
        ? DutyAttempt.Lost(DutyRefusal.Contended,
            $"role '{role}' is held on an older library version, which has been asked to drain and release it")
        : DutyAttempt.Lost(DutyRefusal.Contended,
            $"the session lock for role '{role}' is held by another instance (a session-lock or bridged holder)");
    }
    var attempt = await _decideAsync(plan, role, null, bridge, legacyKey, cancellationToken).ConfigureAwait(false);
    if (attempt.Grant is not null) {
      slot.Connection = null;   // the grant owns the bridge now
    }
    return attempt;
  }

  /// <summary>
  /// The legacy lock is taken. If the row says its holder is a bridged holder that has lapsed, end that
  /// holder's lock session (decision 4 of #968), which waits for the session to exit, and try the lock
  /// once more. True when this instance now holds the lock.
  /// </summary>
  private async Task<bool> _endLapsedBridgeAndRetryAsync(
      NotificationConnectionPlan plan, NpgsqlConnection bridge, string role, long legacyKey, CancellationToken cancellationToken) {
    int? ended;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_end_lapsed_bridge(@role, @key)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("key", legacyKey);
      ended = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as int?;
    }
    if (ended is not { } pid) {
      return false;
    }
    LogBridgeSessionEnded(_logger, role, pid, _instanceProvider.InstanceId);
    _metrics?.BridgeSessionsEnded.Add(1, RoleAssignmentMetrics.RoleTag(role));
    return await _tryLockAsync(bridge, legacyKey, cancellationToken).ConfigureAwait(false);
  }

  private async Task<string?> _requestDrainAsync(NotificationConnectionPlan plan, string role, CancellationToken cancellationToken) {
    string? answer;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_request_role_drain(@role, @id, @lease, @version)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
      cmd.Parameters.AddWithValue("lease", _roleOptions.LeaseFor(role));
      cmd.Parameters.AddWithValue("version", _versionKey);
      answer = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
    return answer;
  }

  /// <summary>Runs the vote and turns its outcome into an attempt; a grant takes ownership of the bridge.</summary>
  private async Task<DutyAttempt> _decideAsync(
      NotificationConnectionPlan plan, string role, long? voteLegacyKey, NpgsqlConnection? bridge, long legacyKey,
      CancellationToken cancellationToken) {
    var sendStarted = _time.GetTimestamp();
    var vote = await _electAsync(plan, role, voteLegacyKey, bridge?.ProcessID, cancellationToken).ConfigureAwait(false);
    return _conclude(plan, role, vote, bridge, legacyKey, sendStarted);
  }

  /// <summary>Turns one vote's outcome into an attempt; a grant becomes this elector's tenure.</summary>
  private DutyAttempt _conclude(
      NotificationConnectionPlan plan, string role, Vote vote, NpgsqlConnection? bridge, long legacyKey, long sendStarted) {
    var instanceId = _instanceProvider.InstanceId;
    if (vote.Outcome is "granted" or "held") {
      if (vote.Outcome == "granted" && vote.PreviousHolder is { } previous) {
        // Logged by the winner only: exactly one vote produces each epoch, so each hand-off is
        // logged exactly once. The vote records the previous holder and its reason together.
        LogHandOff(_logger, role, previous, vote.VoidReason!, instanceId, vote.Epoch);
      }
      if (vote.Outcome == "granted") {
        _metrics?.Elections.Add(1, RoleAssignmentMetrics.RoleTag(role));
        if (vote.PreviousHolder is not null) {
          _metrics?.Handoffs.Add(1, RoleAssignmentMetrics.RoleTag(role),
            new KeyValuePair<string, object?>(RoleAssignmentMetrics.REASON_TAG, vote.VoidReason));
        }
      }
      LogAcquired(_logger, role, instanceId, vote.Epoch);
      var tenure = new Tenure(this, plan, role, vote.Epoch, instanceId, bridge, legacyKey, sendStarted, _time.GetUtcNow());
      _tenures[role] = tenure;
      _metrics?.Held.Add(1, RoleAssignmentMetrics.RoleTag(role));
      return DutyAttempt.Granted(tenure.OpenHandle());
    }

    if (vote.Outcome == "refused") {
      LogRefused(_logger, role, instanceId);
      return DutyAttempt.Lost(DutyRefusal.Refused,
        $"instance {instanceId} is evicted or unregistered, so the vote for role '{role}' refused it; "
        + "an operator or a fresh instance id is required");
    }

    return DutyAttempt.Lost(DutyRefusal.Contended, _contendedDetail(role, vote));
  }

  /// <summary>
  /// One statement for the roles not held yet (bridge off); each granted role becomes a tenure. Roles
  /// already held, and a lone role, are left out of the result for the caller to attempt one at a time.
  /// </summary>
  private async Task<Dictionary<string, DutyAttempt>> _voteManyAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken) {
    var attempts = new Dictionary<string, DutyAttempt>(StringComparer.Ordinal);
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    var plan = NotificationConnectionPlan.Create(_notificationDataSource, resolution);
    if (!plan.IsAvailable) {
      return attempts;   // each one-at-a-time attempt reports the missing connection itself
    }
    await _acquireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      // Read under the gate: a role another caller won since is answered from its tenure instead.
      var roles = candidates.Where(role => !_tenures.ContainsKey(role)).ToArray();
      if (roles.Length < 2) {
        return attempts;
      }
      var sendStarted = _time.GetTimestamp();
      var votes = new Dictionary<string, Vote>(StringComparer.Ordinal);
      var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
      await using (connection.ConfigureAwait(false)) {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT role, outcome, holder_instance_id, epoch, lease_remaining, previous_holder_instance_id, void_reason "
          + "FROM wh_vote_roles(@roles, @leases, @id, @cooldown, @legacy, @version)";
        cmd.Parameters.AddWithValue(nameof(roles), roles);
        cmd.Parameters.AddWithValue("leases", roles.Select(_roleOptions.LeaseFor).ToArray());
        cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
        cmd.Parameters.AddWithValue("cooldown", _roleOptions.CooldownAfterLapse);
        cmd.Parameters.AddWithValue("legacy", roles.Select(role => _legacyKey(resolution.SearchPath, role)).ToArray());
        cmd.Parameters.AddWithValue("version", _versionKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          votes[reader.GetString(0)] = await _readVoteAsync(reader, 1, cancellationToken).ConfigureAwait(false);
        }
      }
      foreach (var role in roles) {
        attempts[role] = _conclude(plan, role, votes[role], null, _legacyKey(resolution.SearchPath, role), sendStarted);
      }
      return attempts;
    } finally {
      _acquireGate.Release();
    }
  }

  private DutyAttempt _unavailable(string role) {
    LogNoConnection(_logger, role);
    return DutyAttempt.Lost(DutyRefusal.Unavailable,
      $"no connection is available to vote for role '{role}'; "
      + "configure Whizbang:Database (ConnectionStringKey / DirectConnectionString)");
  }

  private static string _contendedDetail(string role, Vote vote) => vote.Outcome switch {
    "cooling_down" => $"this instance's assignment to role '{role}' lapsed, and it is cooling down before it may be elected again",
    "legacy_holder" => $"role '{role}' is held through its session lock by an instance on the session-lock elector",
    "deferred" => $"role '{role}' is free, but an instance on a newer library version is voting for it and is preferred",
    "draining" => $"role '{role}' is held by instance {vote.Holder} on an older library version, which has been asked to drain and release it",
    _ => $"role '{role}' is held by instance {vote.Holder} at epoch {vote.Epoch}, "
       + $"lease remaining {vote.LeaseRemaining.GetValueOrDefault().TotalSeconds:F0}s",
  };

  private sealed record Vote(string Outcome, Guid? Holder, long Epoch, TimeSpan? LeaseRemaining, Guid? PreviousHolder, string? VoidReason);

  private async Task<Vote> _electAsync(
      NotificationConnectionPlan plan, string role, long? legacyKey, int? bridgePid, CancellationToken cancellationToken) {
    Vote vote;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT outcome, holder_instance_id, epoch, lease_remaining, previous_holder_instance_id, void_reason "
        + "FROM wh_vote_role(@role, @id, @lease, @cooldown, @legacy, @version, @bridge)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
      cmd.Parameters.AddWithValue("lease", _roleOptions.LeaseFor(role));
      cmd.Parameters.AddWithValue("cooldown", _roleOptions.CooldownAfterLapse);
      cmd.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Bigint) { Value = (object?)legacyKey ?? DBNull.Value });
      cmd.Parameters.AddWithValue("version", _versionKey);
      cmd.Parameters.Add(new NpgsqlParameter("bridge", NpgsqlDbType.Integer) { Value = (object?)bridgePid ?? DBNull.Value });
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      vote = await _readVoteAsync(reader, 0, cancellationToken).ConfigureAwait(false);
    }
    return vote;
  }

  private static async Task<Vote> _readVoteAsync(NpgsqlDataReader reader, int first, CancellationToken cancellationToken) => new(
    reader.GetString(first),
    await reader.IsDBNullAsync(first + 1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(first + 1),
    reader.GetInt64(first + 2),
    await reader.IsDBNullAsync(first + 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetTimeSpan(first + 3),
    await reader.IsDBNullAsync(first + 4, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(first + 4),
    await reader.IsDBNullAsync(first + 5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(first + 5));

  private static async Task<bool> _tryLockAsync(NpgsqlConnection connection, long key, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
    cmd.Parameters.AddWithValue(nameof(key), key);
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }

  /// <summary>
  /// Unlocks and closes a bridge connection. The unlock is explicit because disposing a pooled
  /// connection returns its session to the pool with the lock still held, and the pool resets the
  /// session only when it is next handed out. A session that has already died released the lock
  /// with it, so a failed unlock is not an error.
  /// </summary>
  private static async Task _releaseBridgeAsync(NpgsqlConnection bridge, long key) {
#pragma warning disable CA1031, RCS1075 // best-effort: a dead session already released the lock server-side
    try {
      await using var unlock = bridge.CreateCommand();
      unlock.CommandText = "SELECT pg_advisory_unlock(@key)";
      unlock.Parameters.AddWithValue(nameof(key), key);
      _ = await unlock.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
    } catch (Exception) {
      // intentionally swallowed — see pragma justification
    }
#pragma warning restore CA1031, RCS1075
    await bridge.DisposeAsync().ConfigureAwait(false);
  }

  private static async Task<object?> _callAsync(
      NotificationConnectionPlan plan, string commandText, string role, Guid instanceId, long epoch, long? legacyKey,
      CancellationToken cancellationToken) {
    object? answered;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = commandText;
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("id", instanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      if (legacyKey is not null) {
        cmd.Parameters.AddWithValue("legacy", legacyKey.Value);
      }
      answered = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
    return answered;
  }

  /// <summary>
  /// This instance's hold on one assignment. Every <see cref="TryAcquireAsync"/> for a role this
  /// instance already holds returns another handle on the same tenure instead of voting again, so
  /// the holder loop and a startup step can hold the role at the same time without one of them
  /// releasing it under the other: the role is released when the last handle is disposed, or on
  /// shutdown.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "The only disposable field is a SemaphoreSlim used as an async lock; it allocates nothing to release unless AvailableWaitHandle is read, which this type never does.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A private tenure records everything one grant decided (role, epoch, holder, bridge session and key, renewal clock, acquisition time), and is constructed in exactly one place, from the vote that granted it.")]
  private sealed class Tenure(
      PgRoleElector elector,
      NotificationConnectionPlan plan,
      string role,
      long epoch,
      Guid instanceId,
      NpgsqlConnection? bridge,
      long legacyKey,
      long renewedAtTimestamp,
      DateTimeOffset acquiredAt) {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _renewedAtTimestamp = renewedAtTimestamp;
    private int _handles;
    private bool _over;
    private volatile bool _drainRequested;

    public string Role => role;
    public long Epoch => epoch;
    public Guid InstanceId => instanceId;
    public DateTimeOffset AcquiredAt => acquiredAt;
    public bool DrainRequested => _drainRequested;

    public Handle OpenHandle() {
      _ = Interlocked.Increment(ref _handles);
      return new Handle(this);
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken) {
      // The gate only serializes this tenure's own calls (the bridge connection cannot run two
      // commands at once), and is held for one round trip at most; the caller's token reaches the
      // round trip itself.
      await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
      try {
        if (_over) {
          return false;
        }
        // The bridge is checked on every verify, not throttled: the moment its session dies, an old
        // instance can take the session lock, so "held" can no longer be answered from memory.
        if (!await _bridgeAliveAsync(cancellationToken).ConfigureAwait(false)) {
          return false;
        }
        if (elector._time.GetElapsedTime(_renewedAtTimestamp) < elector._roleOptions.RenewInterval) {
          return true;
        }
        return await _renewAsync(cancellationToken).ConfigureAwait(false);
      } finally {
        _gate.Release();
      }
    }

    private async Task<bool> _renewAsync(CancellationToken cancellationToken) {
      var sendStarted = elector._time.GetTimestamp();
      string? renewed;
      try {
        // Unbridged, the legacy key goes with the renewal, so a session-lock holder that took the
        // duty after the vote (an unsafe mixed fleet) is seen within one renewal and this one steps
        // aside. Bridged, this holder holds that lock itself.
        renewed = bridge is null
          ? await _callAsync(plan, "SELECT wh_renew_role_lease(@role, @id, @epoch, @legacy)", role, instanceId, epoch, legacyKey, cancellationToken).ConfigureAwait(false) as string
          : await _callAsync(plan, "SELECT wh_renew_role_lease(@role, @id, @epoch)", role, instanceId, epoch, null, cancellationToken).ConfigureAwait(false) as string;
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        // Not a verdict: the lease may still be valid, and the next verify asks again.
        LogRenewFailed(elector._logger, role, instanceId, ex);
        return false;
      }

      if (renewed is not ("renewed" or "drain")) {
        await _loseAsync(null).ConfigureAwait(false);
        return false;
      }
      if (renewed == "drain" && !_drainRequested) {
        _drainRequested = true;
        LogDrainRequested(elector._logger, role, instanceId, epoch);
      }
      _renewedAtTimestamp = sendStarted;
      return true;
    }

    /// <summary>
    /// While bridged, the session lock is what keeps an old instance from acting, so a bridge
    /// session that has died means the tenure is lost, not merely unconfirmed.
    /// </summary>
    private async Task<bool> _bridgeAliveAsync(CancellationToken cancellationToken) {
      if (bridge is null) {
        return true;
      }
      try {
        await using var ping = bridge.CreateCommand();
        ping.CommandText = "SELECT 1";
        _ = await ping.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return true;
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        await _loseAsync(ex).ConfigureAwait(false);
        return false;
      }
    }

    private async Task _loseAsync(Exception? cause) {
      LogGrantLost(elector._logger, role, instanceId, epoch, cause);
      elector._metrics?.Lost.Add(1, RoleAssignmentMetrics.RoleTag(role));
      await _endAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the tenure once: it stops being this elector's, and the bridge closes.</summary>
    private async Task _endAsync() {
      _over = true;
      _ = elector._tenures.TryRemove(new KeyValuePair<string, Tenure>(role, this));
      elector._metrics?.Held.Add(-1, RoleAssignmentMetrics.RoleTag(role));
      if (bridge is not null) {
        await _releaseBridgeAsync(bridge, legacyKey).ConfigureAwait(false);
      }
    }

    /// <summary>A handle closed; the last one releases the role.</summary>
    public async Task HandleClosedAsync() {
      if (Interlocked.Decrement(ref _handles) == 0) {
        await ReleaseAsync().ConfigureAwait(false);
      }
    }

    /// <summary>Releases the role at once, whatever handles remain. Idempotent.</summary>
    public async Task ReleaseAsync() {
      await _gate.WaitAsync().ConfigureAwait(false);
      try {
        if (_over) {
          return;
        }
#pragma warning disable CA1031, RCS1075 // best-effort clean release: an unreachable database leaves the
        // lease to lapse, which is the crash path the design already bounds; failing a dispose over
        // it would turn a crash-tolerant design into a shutdown error.
        try {
          if (await _callAsync(plan, "SELECT wh_release_role(@role, @id, @epoch)", role, instanceId, epoch, null, CancellationToken.None).ConfigureAwait(false) is true) {
            LogReleased(elector._logger, role, instanceId, epoch);
            elector._metrics?.Released.Add(1, RoleAssignmentMetrics.RoleTag(role));
          }
        } catch (Exception ex) {
          LogReleaseFailed(elector._logger, role, instanceId, ex);
        }
#pragma warning restore CA1031, RCS1075
        await _endAsync().ConfigureAwait(false);
      } finally {
        _gate.Release();
      }
    }
  }

  /// <summary>One holder's handle on a tenure: what <see cref="TryAcquireAsync"/> returns.</summary>
  private sealed class Handle(Tenure tenure) : IDutyGrant {
    private int _disposed;

    public Tenure Tenure => tenure;
    public string Duty => tenure.Role;
    public DateTimeOffset AcquiredAt => tenure.AcquiredAt;
    public long? Epoch => tenure.Epoch;
    public bool DrainRequested => tenure.DrainRequested;

    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) =>
      Volatile.Read(ref _disposed) == 1 ? Task.FromResult(false) : tenure.VerifyAsync(cancellationToken);

    public async ValueTask DisposeAsync() {
      if (Interlocked.Exchange(ref _disposed, 1) == 0) {
        await tenure.HandleClosedAsync().ConfigureAwait(false);
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "PgRoleElector: no connection available; cannot vote for role '{Role}'")]
  static partial void LogNoConnection(ILogger logger, string role);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "PgRoleElector: instance {InstanceId} holds role '{Role}' at epoch {Epoch}")]
  static partial void LogAcquired(ILogger logger, string role, Guid instanceId, long epoch);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "PgRoleElector: role '{Role}' handed off from instance {PreviousInstanceId} ({Reason}) to instance {InstanceId} at epoch {Epoch}")]
  static partial void LogHandOff(ILogger logger, string role, Guid previousInstanceId, string reason, Guid instanceId, long epoch);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} was refused role '{Role}' (evicted or unregistered)")]
  static partial void LogRefused(ILogger logger, string role, Guid instanceId);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} lost role '{Role}' at epoch {Epoch}; its exclusive work must stop")]
  static partial void LogGrantLost(ILogger logger, string role, Guid instanceId, long epoch, Exception? ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} could not renew role '{Role}'; the lease stands until it lapses, and the next verify retries")]
  static partial void LogRenewFailed(ILogger logger, string role, Guid instanceId, Exception ex);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "PgRoleElector: instance {InstanceId} released role '{Role}' at epoch {Epoch}")]
  static partial void LogReleased(ILogger logger, string role, Guid instanceId, long epoch);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} could not release role '{Role}'; the lease will lapse instead")]
  static partial void LogReleaseFailed(ILogger logger, string role, Guid instanceId, Exception ex);

  [LoggerMessage(EventId = 9, Level = LogLevel.Information,
    Message = "PgRoleElector: instance {InstanceId} holding role '{Role}' at epoch {Epoch} was asked by a newer-version instance to drain; it releases after its current step")]
  static partial void LogDrainRequested(ILogger logger, string role, Guid instanceId, long epoch);

  [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} ended backend {BackendPid}, the legacy-lock session of a lapsed bridged holder of role '{Role}', so the role can be voted again")]
  static partial void LogBridgeSessionEnded(ILogger logger, string role, int backendPid, Guid instanceId);
}
