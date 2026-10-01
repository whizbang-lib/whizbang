using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// Owed duty work in <c>wh_role_pending_work</c> (migration 173). Any instance owes; only the
/// holder completes, and completion is fenced by the holder's epoch in SQL, so work interrupted by
/// a hand-off is finished by the next holder, exactly once.
/// </summary>
/// <remarks>
/// Work owed to a duty that is not held by assignment is not recorded: nothing would ever run it.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PendingDutyWorkE2ETests.cs</tests>
public sealed class PgPendingDutyWorkStore : IPendingDutyWorkStore {
  private const string FENCE_SQLSTATE = "WHF01";

  private readonly WhizbangNotificationOptions _options;
  private readonly RoleAssignmentOptions _roleOptions;
  private readonly IConfiguration _configuration;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly INotificationConnectionStringFallback? _connectionStringFallback;
  private readonly INotificationDataSource? _notificationDataSource;

  /// <summary>Creates the store.</summary>
  /// <param name="options">Where the connection comes from.</param>
  /// <param name="roleOptions">Which duties are held by assignment, and the retry base.</param>
  /// <param name="configuration">Configuration, for connection resolution.</param>
  /// <param name="instanceProvider">This instance's identity, presented to the fence.</param>
  /// <param name="connectionStringFallback">Optional connection-string fallback.</param>
  /// <param name="notificationDataSource">Optional dedicated data source.</param>
  public PgPendingDutyWorkStore(
      IOptions<WhizbangNotificationOptions> options,
      IOptions<RoleAssignmentOptions> roleOptions,
      IConfiguration configuration,
      IServiceInstanceProvider instanceProvider,
      INotificationConnectionStringFallback? connectionStringFallback = null,
      INotificationDataSource? notificationDataSource = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(roleOptions);
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    _options = options.Value;
    _roleOptions = roleOptions.Value;
    _configuration = configuration;
    _instanceProvider = instanceProvider;
    _connectionStringFallback = connectionStringFallback;
    _notificationDataSource = notificationDataSource;
  }

  /// <inheritdoc />
  public async Task OweAsync(string role, string workKey, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(role);
    ArgumentException.ThrowIfNullOrEmpty(workKey);
    if (!_roleOptions.Manages(role)) {
      return;
    }
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT wh_owe_role_work(@role, @key)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("key", workKey);
      _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrEmpty(role);
    var owed = new List<PendingDutyWork>();
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT work_key, first_owed_at, last_owed_at, attempts, last_error, due FROM wh_owed_role_work(@role, @base)";
      cmd.Parameters.AddWithValue(nameof(role), role);
      cmd.Parameters.AddWithValue("base", _roleOptions.OwedWorkRetryBase);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        owed.Add(new PendingDutyWork(
          role,
          reader.GetString(0),
          await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false),
          await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
          reader.GetInt32(3),
          await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
          reader.GetBoolean(5)));
      }
    }
    return owed;
  }

  /// <inheritdoc />
  public async Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) {
    var epoch = _epochOf(work, grant);
    try {
      var deleted = await _fencedAsync(
        "SELECT wh_complete_role_work(@role, @key, @id, @epoch, @listed)", work, epoch,
        cmd => cmd.Parameters.AddWithValue("listed", work.LastOwedAt), cancellationToken).ConfigureAwait(false);
      return deleted is true ? DutyWorkCompletion.Completed : DutyWorkCompletion.OwedAgain;
    } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
      return DutyWorkCompletion.Fenced;
    }
  }

  /// <inheritdoc />
  public async Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(failure);
    var epoch = _epochOf(work, grant);
    try {
      _ = await _fencedAsync(
        "SELECT wh_fail_role_work(@role, @key, @id, @epoch, @error)", work, epoch,
        cmd => cmd.Parameters.AddWithValue("error", failure), cancellationToken).ConfigureAwait(false);
      return true;
    } catch (PostgresException ex) when (ex.SqlState == FENCE_SQLSTATE) {
      return false;
    }
  }

  private static long _epochOf(PendingDutyWork work, IDutyGrant grant) {
    ArgumentNullException.ThrowIfNull(work);
    ArgumentNullException.ThrowIfNull(grant);
    return grant.Epoch
      ?? throw new ArgumentException("Owed duty work is fenced by an epoch; a grant without one cannot complete it.", nameof(grant));
  }

  private async Task<object?> _fencedAsync(
      string sql, PendingDutyWork work, long epoch, Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken) {
    object? answer;
    var connection = await _openAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = sql;
      cmd.Parameters.AddWithValue("role", work.Role);
      cmd.Parameters.AddWithValue("key", work.WorkKey);
      cmd.Parameters.AddWithValue("id", _instanceProvider.InstanceId);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      addParameters(cmd);
      answer = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
    return answer;
  }

  private async ValueTask<NpgsqlConnection> _openAsync(CancellationToken cancellationToken) {
    var resolution = NotificationConnectionStringResolver.Resolve(_options, _configuration, _connectionStringFallback).WithAppliedSearchPath();
    return await NotificationConnectionPlan.Create(_notificationDataSource, resolution).OpenAsync(cancellationToken).ConfigureAwait(false);
  }
}
