// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The Postgres <see cref="IDutyElector"/>, and the one way a duty is held (#2044): every duty is won
/// by <c>wh_vote_role</c> (migrations 184 and 326) and held as an assignment row with an epoch and a
/// lease in database time (its own, when the duty declares one). No duty is held by a session
/// advisory lock, and no session has to stay open while a duty is held.
/// </summary>
/// <remarks>
/// <para>
/// <b>A holder is judged by the one liveness rule (#2140).</b> Past its lease, a holder keeps its role
/// while the rule every reader of instance liveness asks (<c>wh_instance_is_live</c>: a recent heartbeat,
/// or a held alive-lock on the instance's notification connection) calls it live and it made progress:
/// it renewed inside the progress window it declared for its epoch
/// (<see cref="RoleAssignmentOptions.ProgressWindow"/>). Progress comes from the holder's own work loop:
/// <see cref="IDutyGrant.VerifyStillHeldAsync"/>, which a holder calls before each unit of exclusive work,
/// renews. The elector runs no renewal timer, so a holder that is connected but stuck loses its role within
/// its window, and a holder the rule calls dead lapses at its lease. The one holder that keeps a timer is schema
/// initialization, whose migrator runs phases longer than the lease with nothing in them that could verify: it
/// keeps its grant alive with a <see cref="DutyGrantKeepalive"/> on the grant's
/// <see cref="IDutyGrant.RenewInterval"/> from the election until it returns (#2127). Renewals are throttled to one per
/// <see cref="RoleAssignmentOptions.RenewInterval"/>, measured on the monotonic clock from the moment the
/// last successful renewal was sent. The database computed that lease from a later instant, so answering
/// from memory inside the interval is safe, and it never lets a write through anyway: writes are fenced in
/// SQL by <c>wh_assert_role_epoch</c>.
/// </para>
/// <para>
/// <b>A holder learns of a loss within one renew interval.</b> Each grant watches its assignment: once per
/// <see cref="RoleAssignmentOptions.RenewInterval"/> it reads <c>wh_role_is_held</c>, which writes nothing and
/// so never counts as progress. A loss cancels <see cref="IDutyGrant.LostToken"/>, which the holder's exclusive
/// work is linked to. A watch that cannot confirm the role for one lease fences itself the same way, because
/// by then another instance may be entitled to the role.
/// </para>
/// <para>
/// <b>The grant holds no connection.</b> Each vote, renewal and release is one statement on an
/// ordinary connection, which a transaction-pooling front end serves correctly and which a database
/// restart does not invalidate: the row survives, and the next renewal presents the same epoch.
/// </para>
/// <para>
/// <b>Mixed-version fleets.</b> An instance on an older release that holds a duty by its own session
/// lock records the holding; while the one liveness rule calls that instance live, the vote answers
/// that the duty is held outside the table, and a holder that sees such a holding steps aside within
/// one renewal. No lock is read to decide it.
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
/// live while it runs. A fleet-wide lapse carries no cool-down (decided in SQL).
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentElectorE2ETests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "The only disposable field is a SemaphoreSlim used as an async lock; it allocates nothing to release unless AvailableWaitHandle is read, which this type never does.")]
public sealed partial class PgRoleElector : IDutyElector, Whizbang.Core.RunControl.IReleasesHeldRoles, IRoleAssignmentReader,
  Whizbang.Core.RunControl.IObeysRunPermit {
  /// <summary>The vote outcome that hands this instance a role it did not hold.</summary>
  private const string OUTCOME_GRANTED = "granted";

  private readonly WhizbangNotificationOptions _options;
  private readonly RoleAssignmentOptions _roleOptions;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
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
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam, and a parameter object would only move the list
  public PgRoleElector(
      IOptions<WhizbangNotificationOptions> options,
      IOptions<RoleAssignmentOptions> roleOptions,
      IConfiguration configuration,
      IServiceInstanceProvider instanceProvider,
      ILogger<PgRoleElector> logger,
      ILibraryVersionProvider? libraryVersion,
      INotificationConnectionStringFallback? connectionStringFallback = null,
      INotificationDataSource? notificationDataSource = null,
      TimeProvider? timeProvider = null,
      RoleAssignmentMetrics? metrics = null,
      Whizbang.Core.RunControl.WhizbangRunPermit? runPermit = null) {
#pragma warning restore S107
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(roleOptions);
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _options = options.Value;
    _roleOptions = roleOptions.Value;
    _roleOptions.Validate();
    _configuration = configuration;
    _instanceProvider = instanceProvider;
    _logger = logger;
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
    _time = timeProvider ?? TimeProvider.System;
    _metrics = metrics;
    _versionKey = LibraryVersionKey.From(libraryVersion?.LibraryVersion);
    RunPermit = runPermit ?? new Whizbang.Core.RunControl.WhizbangRunPermit();
  }

  /// <summary>
  /// The instance's run permit (#2045). While it is closed every vote is refused as <see cref="DutyRefusal.Halted"/>: a
  /// paused, stood-down or stopping instance holds no role, and the run-control path has released the ones it held.
  /// </summary>
  public Whizbang.Core.RunControl.WhizbangRunPermit RunPermit { get; }

  private DutyAttempt? _haltedRefusal(string duty) => RunPermit.IsRunning
    ? null
    : DutyAttempt.Lost(DutyRefusal.Halted,
      $"duty '{duty}' is not voted for: this instance's run permit is closed (paused, standing down or stopping)");

  /// <summary>
  /// Raised after each grant watch tick with the role and what it found: true confirmed, false not confirmed (lost, or
  /// the database unreachable), null nothing asked (the tenure ended, or the schema has no watch). The test seam that
  /// tells a tick has run.
  /// </summary>
  internal event Action<string, bool?>? WatchChecked;

  /// <summary>How often a holder renews; a holder's work loop wakes at least this often.</summary>
  public TimeSpan RenewInterval => _roleOptions.RenewInterval;

  /// <inheritdoc />
  public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(duty);
    return _haltedRefusal(duty) is { } refused ? Task.FromResult(refused) : _voteAsync(duty, cancellationToken);
  }

  /// <summary>
  /// Votes for several duties at once. The duties this instance does not hold yet are voted for in
  /// ONE statement (<c>wh_vote_roles</c>); a duty already held, or a lone one, is attempted on its own.
  /// </summary>
  /// <param name="duties">The duties.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One attempt per duty, in order.</returns>
  public async Task<IReadOnlyList<DutyAttempt>> TryAcquireManyAsync(IReadOnlyList<string> duties, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(duties);
    if (!RunPermit.IsRunning) {
      return [.. duties.Select(duty => _haltedRefusal(duty)!)];
    }
    var voted = await _voteManyAsync([.. duties.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
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

  /// <summary>The roles this elector holds a tenure of right now, for the exit's account of what it held (#2126).</summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RoleAssignmentElectorE2ETests.cs:ReleaseAll_OnShutdown_ReleasesEveryOutstandingGrantAsync</tests>
  public IReadOnlyCollection<string> HeldRoles => [.. _tenures.Keys.Order(StringComparer.Ordinal)];

  /// <summary>
  /// Releases every role this elector still holds, so a stand-down, a pause or a stop hands each one off at once: the
  /// role step of the run-control path (#2045). Idempotent; a release that cannot reach the database leaves the lease to
  /// lapse.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
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
    var sendStarted = _time.GetTimestamp();
    var vote = await _electAsync(plan, role, cancellationToken).ConfigureAwait(false);
    var (attempt, tenure) = _conclude(plan, role, vote, sendStarted);
    if (tenure is not null) {
      await tenure.DeclareProgressWindowAsync().ConfigureAwait(false);
    }
    return attempt;
  }

  /// <summary>Turns one vote's outcome into an attempt; a grant becomes this elector's tenure, which the caller begins.</summary>
  private (DutyAttempt Attempt, Tenure? Tenure) _conclude(NotificationConnectionPlan plan, string role, Vote vote, long sendStarted) {
    var instanceId = _instanceProvider.InstanceId;
    if (vote.Outcome is OUTCOME_GRANTED or "held") {
      if (vote.Outcome == OUTCOME_GRANTED && vote.PreviousHolder is { } previous) {
        // Logged by the winner only: exactly one vote produces each epoch, so each hand-off is
        // logged exactly once. The vote records the previous holder and its reason together.
        LogHandOff(_logger, role, previous, vote.VoidReason!, instanceId, vote.Epoch);
      }
      if (vote.Outcome == OUTCOME_GRANTED) {
        _metrics?.Elections.Add(1, RoleAssignmentMetrics.RoleTag(role));
        if (vote.PreviousHolder is not null) {
          _metrics?.Handoffs.Add(1, RoleAssignmentMetrics.RoleTag(role),
            new KeyValuePair<string, object?>(RoleAssignmentMetrics.REASON_TAG, vote.VoidReason));
        }
      }
      LogAcquired(_logger, role, instanceId, vote.Epoch);
      // Only a fresh grant took the role from anyone: "held" is this instance keeping what it had.
      var previousHolder = vote.Outcome == OUTCOME_GRANTED ? vote.PreviousHolder : null;
      var tenure = new Tenure(this, plan, role, new TenureGrant(vote.Epoch, instanceId, sendStarted, _time.GetUtcNow(), previousHolder));
      tenure.StartWatch();
      _tenures[role] = tenure;
      _metrics?.Held.Add(1, RoleAssignmentMetrics.RoleTag(role));
      return (DutyAttempt.Granted(tenure.OpenHandle()), tenure);
    }

    if (vote.Outcome == "refused") {
      LogRefused(_logger, role, instanceId);
      return (DutyAttempt.Lost(DutyRefusal.Refused,
        $"instance {instanceId} is evicted or unregistered, so the vote for role '{role}' refused it; "
        + "an operator or a fresh instance id is required"), null);
    }

    return (DutyAttempt.Lost(DutyRefusal.Contended, _contendedDetail(role, vote)), null);
  }

  /// <summary>
  /// One statement for the roles not held yet; each granted role becomes a tenure. Roles
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
          + "FROM wh_vote_roles(@roles, @leases, @id, @cooldown, NULL, @version)";
        cmd.Parameters.AddWithValue(nameof(roles), roles);
        cmd.Parameters.AddWithValue("leases", roles.Select(_roleOptions.LeaseFor).ToArray());
        cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
        cmd.Parameters.AddWithValue("cooldown", _roleOptions.CooldownAfterLapse);
        cmd.Parameters.AddWithValue("version", _versionKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          votes[reader.GetString(0)] = await _readVoteAsync(reader, 1, cancellationToken).ConfigureAwait(false);
        }
      }
      foreach (var role in roles) {
        var (attempt, tenure) = _conclude(plan, role, votes[role], sendStarted);
        if (tenure is not null) {
          await tenure.DeclareProgressWindowAsync().ConfigureAwait(false);
        }
        attempts[role] = attempt;
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
    "legacy_holder" => $"role '{role}' is held outside the role table by an instance on an older release, through its own session lock",
    "deferred" => $"role '{role}' is free, but an instance on a newer library version is voting for it and is preferred",
    "draining" => $"role '{role}' is held by instance {vote.Holder} on an older library version, which has been asked to drain and release it",
    _ => $"role '{role}' is held by instance {vote.Holder} at epoch {vote.Epoch}, "
       + $"lease remaining {vote.LeaseRemaining.GetValueOrDefault().TotalSeconds:F0}s",
  };

  private sealed record Vote(string Outcome, Guid? Holder, long Epoch, TimeSpan? LeaseRemaining, Guid? PreviousHolder, string? VoidReason);

  private async Task<Vote> _electAsync(NotificationConnectionPlan plan, string role, CancellationToken cancellationToken) {
    Vote vote;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT outcome, holder_instance_id, epoch, lease_remaining, previous_holder_instance_id, void_reason "
        + "FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, @version, NULL)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
      cmd.Parameters.AddWithValue("lease", _roleOptions.LeaseFor(role));
      cmd.Parameters.AddWithValue("cooldown", _roleOptions.CooldownAfterLapse);
      cmd.Parameters.AddWithValue("version", _versionKey);
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

  private static async Task<object?> _callAsync(
      NotificationConnectionPlan plan, string commandText, string role, Guid instanceId, long epoch, CancellationToken cancellationToken) {
    object? answered;
    var connection = await plan.OpenAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = commandText;
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("id", instanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "The SemaphoreSlim is an async lock that allocates nothing to release unless AvailableWaitHandle is read, which this type never does; the watch timer and the loss token source are disposed or canceled when the tenure ends, which is this type's end of life.")]
  private sealed class Tenure(PgRoleElector elector, NotificationConnectionPlan plan, string role, TenureGrant grant) {
    private readonly SemaphoreSlim _gate = new(1, 1);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Bug", "S2930:\"IDisposables\" should be disposed", Justification = "Handles read LostToken after the tenure ends, and a disposed source's Token throws; the source registers no timer, so canceling it releases everything it holds.")]
    private readonly CancellationTokenSource _lost = new();
    private long _renewedAtTimestamp = grant.RenewedAtTimestamp;
    // The last moment the database confirmed the role (a renewal or a watch check), on the monotonic clock: when the
    // statement that confirmed it was sent.
    private long _confirmedAtTimestamp = grant.RenewedAtTimestamp;
    private ITimer? _watch;
    private int _checking;
    private int _handles;
    private bool _over;
    private volatile bool _drainRequested;

    public string Role => role;
    public long Epoch => grant.Epoch;
    public Guid InstanceId => grant.InstanceId;
    public DateTimeOffset AcquiredAt => grant.AcquiredAt;
    public Guid? PreviousHolder => grant.PreviousHolder;
    public bool DrainRequested => _drainRequested;
    public CancellationToken LostToken => _lost.Token;
    public TimeSpan RenewInterval => elector._roleOptions.RenewInterval;

    /// <summary>
    /// Starts the watch that observes a loss within one renew interval (#2140). Called once, as the tenure is created and
    /// before anything else can see it, so the watch exists for the tenure's whole life.
    /// </summary>
    public void StartWatch() {
      var interval = elector._roleOptions.RenewInterval;
      _watch = elector._time.CreateTimer(_onWatchTimer, this, interval, interval);
    }

    /// <summary>
    /// Declares this epoch's progress window, so the vote judges the holder by the one liveness rule past its lease
    /// (#2140). One short statement right after the grant, not canceled with the caller: a declaration that cannot reach
    /// the database leaves the holder judged by its lease alone, as before, and is logged, not thrown.
    /// </summary>
    public async Task DeclareProgressWindowAsync() {
      var window = elector._roleOptions.ProgressWindowFor(role);
      if (window <= TimeSpan.Zero) {
        return;
      }
#pragma warning disable CA1031 // not a verdict: without a window the holder is judged by its lease, exactly as before; logged
      try {
        var connection = await plan.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false)) {
          await using var cmd = connection.CreateCommand();
          cmd.CommandText = "SELECT wh_declare_role_progress_window(@role, @id, @epoch, @window)";
          cmd.Parameters.AddWithValue(nameof(role), role);
          cmd.Parameters.AddWithValue("id", InstanceId);
          cmd.Parameters.AddWithValue("epoch", Epoch);
          cmd.Parameters.AddWithValue(nameof(window), window);
          _ = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        LogDeclareFailed(elector._logger, role, InstanceId, Epoch, ex);
      }
#pragma warning restore CA1031
    }

    private static void _onWatchTimer(object? state) => _ = ((Tenure)state!)._watchTickAsync();

    /// <summary>One watch tick; a tick that finds the previous check still running skips.</summary>
    private async Task _watchTickAsync() {
      if (Interlocked.Exchange(ref _checking, 1) == 1) {
        return;
      }
      bool? confirmed;
      try {
        confirmed = await _checkAsync().ConfigureAwait(false);
      } finally {
        Volatile.Write(ref _checking, 0);
      }
      elector.WatchChecked?.Invoke(role, confirmed);
    }

    /// <summary>Asks whether the role is still this tenure's. Null when nothing was asked (the tenure is over, or the schema has no watch).</summary>
    private async Task<bool?> _checkAsync() {
      await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
      try {
        if (_over) {
          return null;
        }
        var sendStarted = elector._time.GetTimestamp();
        object? held;
        try {
          held = await _callAsync(plan, "SELECT wh_role_is_held(@role, @id, @epoch)", role, InstanceId, Epoch, CancellationToken.None)
            .ConfigureAwait(false);
        } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedFunction) {
          // A schema that predates the watch (355): the holder is judged by its lease, as before, and learns of a loss on
          // its next verify. Nothing to fence on.
          LogWatchUnavailable(elector._logger, role, InstanceId);
          _watch!.Dispose();
          return null;
#pragma warning disable CA1031 // not a verdict until a lease has passed without a confirmation; logged either way
        } catch (Exception ex) {
#pragma warning restore CA1031
          if (elector._time.GetElapsedTime(_confirmedAtTimestamp) >= elector._roleOptions.LeaseFor(role)) {
            LogWatchFenced(elector._logger, role, InstanceId, Epoch, ex);
            _lose();
          } else {
            LogWatchFailed(elector._logger, role, InstanceId, ex);
          }
          return false;
        }
        if (held is not true) {
          _lose();
          return false;
        }
        _confirmedAtTimestamp = sendStarted;
        return true;
      } finally {
        _gate.Release();
      }
    }

    public Handle OpenHandle() {
      _ = Interlocked.Increment(ref _handles);
      return new Handle(this);
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken) {
      // The gate only serializes this tenure's own calls, and is held for one round trip at most;
      // the caller's token reaches the round trip itself.
      await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
      try {
        if (_over) {
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
        // A holder on an older release that took the duty outside the table after the vote is seen by
        // the renewal (326), and this one steps aside.
        renewed = await _callAsync(plan, "SELECT wh_renew_role_lease(@role, @id, @epoch)", role, InstanceId, Epoch, cancellationToken)
          .ConfigureAwait(false) as string;
      } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        throw;
      } catch (Exception ex) {
        // Not a verdict: the lease may still be valid, and the next verify asks again.
        LogRenewFailed(elector._logger, role, InstanceId, ex);
        return false;
      }

      if (renewed is not ("renewed" or "drain")) {
        _lose();
        return false;
      }
      if (renewed == "drain" && !_drainRequested) {
        _drainRequested = true;
        LogDrainRequested(elector._logger, role, InstanceId, Epoch);
      }
      _renewedAtTimestamp = sendStarted;
      _confirmedAtTimestamp = sendStarted;
      return true;
    }

    private void _lose() {
      LogGrantLost(elector._logger, role, InstanceId, Epoch, null);
      elector._metrics?.Lost.Add(1, RoleAssignmentMetrics.RoleTag(role));
      _end();
    }

    /// <summary>Ends the tenure once: it stops being this elector's, its watch stops, and its loss token is canceled.</summary>
    private void _end() {
      _over = true;
      _ = elector._tenures.TryRemove(new KeyValuePair<string, Tenure>(role, this));
      elector._metrics?.Held.Add(-1, RoleAssignmentMetrics.RoleTag(role));
      _watch!.Dispose();
      // Callbacks run off this thread, so work linked to the token never runs while this tenure's gate is held.
      _ = _lost.CancelAsync();
    }

    /// <summary>A handle closed; the last one releases the role.</summary>
    public async Task HandleClosedAsync() {
      if (Interlocked.Decrement(ref _handles) == 0) {
        await ReleaseAsync().ConfigureAwait(false);
      }
    }

    /// <summary>Releases the role at once, whatever handles remain. Idempotent.</summary>
    public async Task ReleaseAsync() {
      await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
      try {
        if (_over) {
          return;
        }
#pragma warning disable CA1031, RCS1075 // best-effort clean release: an unreachable database leaves the
        // lease to lapse, which is the crash path the design already bounds; failing a dispose over
        // it would turn a crash-tolerant design into a shutdown error.
        try {
          if (await _callAsync(plan, "SELECT wh_release_role(@role, @id, @epoch)", role, InstanceId, Epoch, CancellationToken.None).ConfigureAwait(false) is true) {
            LogReleased(elector._logger, role, InstanceId, Epoch);
            elector._metrics?.Released.Add(1, RoleAssignmentMetrics.RoleTag(role));
          }
        } catch (Exception ex) {
          LogReleaseFailed(elector._logger, role, InstanceId, ex);
        }
#pragma warning restore CA1031, RCS1075
        _end();
      } finally {
        _gate.Release();
      }
    }
  }

  /// <summary>What one vote decided for a new tenure: its epoch, holder, renewal clock, acquisition time and previous holder.</summary>
  private sealed record TenureGrant(long Epoch, Guid InstanceId, long RenewedAtTimestamp, DateTimeOffset AcquiredAt, Guid? PreviousHolder);

  /// <summary>One holder's handle on a tenure: what <see cref="TryAcquireAsync"/> returns.</summary>
  private sealed class Handle(Tenure tenure) : IDutyGrant {
    private int _disposed;

    public Tenure Tenure => tenure;
    public string Duty => tenure.Role;
    public DateTimeOffset AcquiredAt => tenure.AcquiredAt;
    public long? Epoch => tenure.Epoch;
    public Guid? PreviousHolderInstanceId => tenure.PreviousHolder;
    public bool DrainRequested => tenure.DrainRequested;
    public CancellationToken LostToken => tenure.LostToken;
    public TimeSpan? RenewInterval => tenure.RenewInterval;

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
    Message = "PgRoleElector: instance {InstanceId} could not declare its progress window for role '{Role}' at epoch {Epoch}; it is judged by its lease alone")]
  static partial void LogDeclareFailed(ILogger logger, string role, Guid instanceId, long epoch, Exception ex);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} could not check that it still holds role '{Role}'; the next check retries")]
  static partial void LogWatchFailed(ILogger logger, string role, Guid instanceId, Exception ex);

  [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
    Message = "PgRoleElector: instance {InstanceId} could not confirm role '{Role}' at epoch {Epoch} for a whole lease; it stops its exclusive work, since another instance may now hold the role")]
  static partial void LogWatchFenced(ILogger logger, string role, Guid instanceId, long epoch, Exception ex);

  [LoggerMessage(EventId = 13, Level = LogLevel.Information,
    Message = "PgRoleElector: the schema has no role watch yet (wh_role_is_held); instance {InstanceId} learns of a loss of role '{Role}' on its next verify")]
  static partial void LogWatchUnavailable(ILogger logger, string role, Guid instanceId);
}
