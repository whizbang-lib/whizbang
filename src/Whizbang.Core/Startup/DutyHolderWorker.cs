using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Startup;

/// <summary>
/// Holds the roles that have duty work, and runs that work when this instance becomes the holder
/// (#966: the acquisition hook, requirement 7). Every pass either verifies the role it holds, which
/// renews the lease from this loop, or votes for a role it does not hold, and then runs every owed
/// piece of work that is due.
/// </summary>
/// <remarks>
/// <para>
/// This loop is the liveness signal. A handler that hangs stops the loop, so the lease stops being
/// renewed and the role lapses to another instance, whose own pass finishes the work. The stuck
/// instance cannot mark the work done afterwards, because completion is fenced by its epoch.
/// </para>
/// <para>
/// A pass runs once per <see cref="RoleAssignmentOptions.RenewInterval"/>, and at once when a
/// release is announced on <see cref="RELEASE_CHANNEL"/>, so a graceful hand-off does not wait for
/// the next pass. On stop, held roles are released.
/// </para>
/// <para>
/// Which instance runs work is the vote's call, not this loop's: work owed during a rolling deploy
/// runs when the old holder stops and a new instance wins the role, which is the gap a single
/// startup-time attempt left open.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/DutyHolderWorkerTests.cs</tests>
public sealed partial class DutyHolderWorker : BackgroundService {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The channel a role release is announced on; the payload is the role.</summary>
  public const string RELEASE_CHANNEL = "wh_role_released";
#pragma warning restore CA1707

  private readonly IDutyElector _elector;
  private readonly IPendingDutyWorkStore _store;
  private readonly Dictionary<(string Role, string Key), IDutyWorkHandler> _handlers;
  private readonly IReadOnlyList<string> _roles;
  private readonly RoleAssignmentOptions _options;
  private readonly ILogger<DutyHolderWorker> _logger;
  private readonly ISharedNotifyConnection? _notify;
  private readonly RoleAssignmentMetrics? _metrics;
  private readonly TimeProvider _time;
  private readonly Dictionary<string, IDutyGrant> _held = new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _passGate = new(1, 1);
  private TaskCompletionSource _wake = _newWake();

  /// <summary>Creates the worker.</summary>
  /// <param name="elector">The elector that decides who holds each role.</param>
  /// <param name="store">The owed duty work.</param>
  /// <param name="handlers">What runs each kind of owed work.</param>
  /// <param name="options">Role-assignment tuning; only roles it manages are held here.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="notify">The shared notify connection, for release announcements. Required, and
  /// null only by an explicit choice: without it a hand-off waits for the next pass instead of
  /// waking one.</param>
  /// <param name="metrics">Optional meters.</param>
  /// <param name="timeProvider">Optional clock for the pass cadence.</param>
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam
  public DutyHolderWorker(
      IDutyElector elector,
      IPendingDutyWorkStore store,
      IEnumerable<IDutyWorkHandler> handlers,
      IOptions<RoleAssignmentOptions> options,
      ILogger<DutyHolderWorker> logger,
      ISharedNotifyConnection? notify,
      RoleAssignmentMetrics? metrics = null,
      TimeProvider? timeProvider = null) {
#pragma warning restore S107
    ArgumentNullException.ThrowIfNull(elector);
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(handlers);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    _elector = elector;
    _store = store;
    _options = options.Value;
    _logger = logger;
    _notify = notify;
    _metrics = metrics;
    _time = timeProvider ?? TimeProvider.System;
    // First registration of a (role, key) wins, so a host can override a framework handler by
    // registering its own first.
    _handlers = new Dictionary<(string, string), IDutyWorkHandler>();
    foreach (var handler in handlers.Where(h => _options.Manages(h.Role))) {
      _ = _handlers.TryAdd((handler.Role, handler.WorkKey), handler);
    }
    _roles = [.. _handlers.Keys.Select(k => k.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
  }

  /// <summary>The roles this worker holds work for.</summary>
  public IReadOnlyList<string> Roles => _roles;

  /// <summary>Whether this instance currently holds <paramref name="role"/>, as of the last pass.</summary>
  /// <param name="role">The role.</param>
  /// <returns>True when held.</returns>
  public bool Holds(string role) => _held.ContainsKey(role);

  /// <summary>Runs the next pass at once instead of at the next interval.</summary>
  public void Wake() => Volatile.Read(ref _wake).TrySetResult();

  /// <summary>
  /// One pass over every role: keep or win it, then run its owed work. Public as the deterministic
  /// seam; the hosted loop calls exactly this.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task RunOnceAsync(CancellationToken cancellationToken) {
    await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      foreach (var role in _roles) {
        var grant = await _holdAsync(role, cancellationToken).ConfigureAwait(false);
        if (grant is not null) {
          await _runOwedAsync(role, grant, cancellationToken).ConfigureAwait(false);
        }
      }
    } finally {
      _passGate.Release();
    }
  }

  /// <summary>Releases every role this worker holds.</summary>
  public async Task ReleaseHeldAsync() {
    await _passGate.WaitAsync().ConfigureAwait(false);
    try {
      foreach (var grant in _held.Values) {
        await grant.DisposeAsync().ConfigureAwait(false);
      }
      _held.Clear();
    } finally {
      _passGate.Release();
    }
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using var subscription = _notify?.Subscribe(new ReleaseSubscription(this));
    while (!stoppingToken.IsCancellationRequested) {
      // Both armed before the pass: a release announced during the pass wakes the next one, and
      // the cadence is measured from the start of this pass.
      var wake = _armWake();
      var next = Task.Delay(_options.RenewInterval, _time, stoppingToken);
      try {
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        break;
      }
      _ = await Task.WhenAny(wake.Task, next).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public override async Task StopAsync(CancellationToken cancellationToken) {
    await base.StopAsync(cancellationToken).ConfigureAwait(false);
    await ReleaseHeldAsync().ConfigureAwait(false);
  }

  /// <inheritdoc />
  public override void Dispose() {
    _passGate.Dispose();
    base.Dispose();
  }

  private TaskCompletionSource _armWake() {
    var next = _newWake();
    Volatile.Write(ref _wake, next);
    return next;
  }

  private static TaskCompletionSource _newWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Keeps the role this instance holds, or votes for one it does not. Null when not held.</summary>
  private async Task<IDutyGrant?> _holdAsync(string role, CancellationToken cancellationToken) {
    if (_held.TryGetValue(role, out var held)) {
      if (await held.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false)) {
        return held;
      }
      _ = _held.Remove(role);
      await held.DisposeAsync().ConfigureAwait(false);
      LogNoLongerHolder(_logger, role);
    }

    DutyAttempt attempt;
    try {
      attempt = await _elector.TryAcquireAsync(role, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // a vote that could not run is retried on the next pass; it is logged, not swallowed
    } catch (Exception ex) {
#pragma warning restore CA1031
      LogVoteFailed(_logger, role, ex);
      return null;
    }

    if (attempt.Grant is not { } grant) {
      return null;
    }
    _held[role] = grant;
    LogBecameHolder(_logger, role, grant.Epoch);
    return grant;
  }

  /// <summary>The acquisition hook: runs every due piece of owed work this instance has a handler for.</summary>
  private async Task _runOwedAsync(string role, IDutyGrant grant, CancellationToken cancellationToken) {
    IReadOnlyList<PendingDutyWork> owed;
    try {
      owed = await _store.ListOwedAsync(role, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // retried on the next pass; logged, not swallowed
    } catch (Exception ex) {
#pragma warning restore CA1031
      LogListFailed(_logger, role, ex);
      return;
    }

    foreach (var work in owed) {
      if (!work.IsDue || !_handlers.TryGetValue((role, work.WorkKey), out var handler)) {
        // Backing off after a failure, or owed by a version of the service that has a handler this
        // one does not: leave it for a holder that can run it.
        continue;
      }
      // Before each unit of exclusive work, as the grant contract asks. A loss stops the pass; the
      // next pass votes again.
      if (!await grant.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false)) {
        return;
      }
      if (!await _runOneAsync(work, handler, grant, cancellationToken).ConfigureAwait(false)) {
        // The fence refused this grant: that is proof the role is gone, whatever the grant's own
        // throttled answer would say for the rest of its renew interval. Let it go now.
        _ = _held.Remove(role);
        await grant.DisposeAsync().ConfigureAwait(false);
        LogNoLongerHolder(_logger, role);
        return;
      }
    }
  }

  /// <summary>Runs one piece of work and records how it ended. False when the grant was fenced.</summary>
  private async Task<bool> _runOneAsync(
      PendingDutyWork work, IDutyWorkHandler handler, IDutyGrant grant, CancellationToken cancellationToken) {
    DutyWorkResult result;
    try {
      result = await handler.RunAsync(grant, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // recorded against the work as its last error; the work stays owed
    } catch (Exception ex) {
#pragma warning restore CA1031
      result = DutyWorkResult.NotDone($"{ex.GetType().Name}: {ex.Message}");
    }
    if (result.Status == DutyWorkStatus.Deferred) {
      return true;   // not attempted, so nothing to record; a later pass runs it
    }

    string outcome;
    try {
      if (result.Status == DutyWorkStatus.Done) {
        var completion = await _store.CompleteAsync(work, grant, cancellationToken).ConfigureAwait(false);
        outcome = completion switch {
          DutyWorkCompletion.Completed => RoleAssignmentMetrics.OUTCOME_COMPLETED,
          DutyWorkCompletion.OwedAgain => RoleAssignmentMetrics.OUTCOME_LEFT_OWED,
          _ => RoleAssignmentMetrics.OUTCOME_FENCED,
        };
      } else {
        var detail = result.Detail ?? "not done";
        var recorded = await _store.RecordFailureAsync(work, grant, detail, cancellationToken).ConfigureAwait(false);
        outcome = recorded ? RoleAssignmentMetrics.OUTCOME_LEFT_OWED : RoleAssignmentMetrics.OUTCOME_FENCED;
        LogWorkNotDone(_logger, work.Role, work.WorkKey, detail);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // the work stays owed and runs again; logged, not swallowed
    } catch (Exception ex) {
#pragma warning restore CA1031
      LogRecordFailed(_logger, work.Role, work.WorkKey, ex);
      outcome = RoleAssignmentMetrics.OUTCOME_LEFT_OWED;
    }

    _metrics?.WorkRuns.Add(1, RoleAssignmentMetrics.RoleTag(work.Role),
      new KeyValuePair<string, object?>(RoleAssignmentMetrics.OUTCOME_TAG, outcome));
    if (outcome == RoleAssignmentMetrics.OUTCOME_FENCED) {
      LogFenced(_logger, work.Role, work.WorkKey);
      return false;
    }
    if (outcome == RoleAssignmentMetrics.OUTCOME_COMPLETED) {
      LogWorkCompleted(_logger, work.Role, work.WorkKey, grant.Epoch);
    }
    return true;
  }

  private sealed class ReleaseSubscription(DutyHolderWorker worker) : INotifySubscription {
    public string ChannelName => RELEASE_CHANNEL;

    public void OnNotification(string payload) {
      if (worker._handlers.Keys.Any(k => string.Equals(k.Role, payload, StringComparison.Ordinal))) {
        worker.Wake();
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "DutyHolderWorker: this instance holds role '{Role}' (epoch {Epoch}); running its owed duty work")]
  static partial void LogBecameHolder(ILogger logger, string role, long? epoch);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: this instance no longer holds role '{Role}'; voting again")]
  static partial void LogNoLongerHolder(ILogger logger, string role);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: the vote for role '{Role}' could not run; retrying on the next pass")]
  static partial void LogVoteFailed(ILogger logger, string role, Exception ex);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: the owed work for role '{Role}' could not be listed; retrying on the next pass")]
  static partial void LogListFailed(ILogger logger, string role, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: owed work '{WorkKey}' for role '{Role}' is not done and stays owed: {Detail}")]
  static partial void LogWorkNotDone(ILogger logger, string role, string workKey, string detail);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: the outcome of owed work '{WorkKey}' for role '{Role}' could not be recorded; it stays owed")]
  static partial void LogRecordFailed(ILogger logger, string role, string workKey, Exception ex);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "DutyHolderWorker: role '{Role}' was lost while running owed work '{WorkKey}'; the next holder finishes it")]
  static partial void LogFenced(ILogger logger, string role, string workKey);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information,
    Message = "DutyHolderWorker: owed work '{WorkKey}' for role '{Role}' is done (epoch {Epoch})")]
  static partial void LogWorkCompleted(ILogger logger, string role, string workKey, long? epoch);
}
