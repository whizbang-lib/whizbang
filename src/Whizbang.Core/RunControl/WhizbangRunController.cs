// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.RunControl;

/// <summary>
/// The one run-control path (#2045): see <see cref="IWhizbangRunController"/>. Halts and resumes are serialized, so a
/// stand-down and a host stop that arrive together run one after the other. The permit stays closed while ANY reason
/// holds: a resume lifts only its own reason, and the reported phase follows the strongest reason that remains (a stop,
/// then a stand-down, then a pause). A stop always wins: once stopped, a later pause or stand-down changes nothing and a
/// resume is refused.
/// </summary>
/// <remarks>
/// <para>
/// The caller's token bounds every step (host shutdown passes its stop token), and every step after the permit closes is
/// best-effort and logged: an obeyer that fails to act on the close, a releaser that cannot reach the database, a step the
/// caller's token cuts short, or work that is still in hand when <see cref="WhizbangLifecycleOptions.HeldWorkTimeout"/>
/// runs out does not stop the path, because the instance must still confirm where it stands. The phases are written on a
/// token of their own. What could not be released is taken over by a peer when its lease lapses, as after a crash.
/// </para>
/// <para>
/// The phases are written through <see cref="IWhizbangLifecycleState"/>, whose participant records them on the
/// instance's row. The path writes both phases even when nothing is held and nothing is released: the fast path signals
/// the lifecycle too.
/// </para>
/// </remarks>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControllerTests.cs</tests>
public sealed partial class WhizbangRunController : IWhizbangRunController, IDisposable {
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly IReadOnlyList<IReleasesHeldWork> _work;
  private readonly IReadOnlyList<IReleasesHeldRoles> _roles;
  private readonly WhizbangLifecycleOptions _options;
  private readonly TimeProvider _time;
  private readonly ILogger<WhizbangRunController> _logger;
  private readonly SemaphoreSlim _serialize = new(1, 1);
  private readonly HashSet<RunHaltReason> _reasons = [];

  /// <summary>Creates the path.</summary>
  /// <param name="permit">The instance's one run permit.</param>
  /// <param name="lifecycle">Writes each phase the path reports.</param>
  /// <param name="work">What releases held work, in order.</param>
  /// <param name="roles">What releases held roles, in order.</param>
  /// <param name="options">The bound on waiting for held work.</param>
  /// <param name="timeProvider">The clock that bound runs on; the system clock when null.</param>
  /// <param name="logger">Logs each step that could not complete.</param>
  /// <exception cref="ArgumentNullException">A required argument is null.</exception>
  public WhizbangRunController(
      WhizbangRunPermit permit,
      IWhizbangLifecycleState lifecycle,
      IEnumerable<IReleasesHeldWork> work,
      IEnumerable<IReleasesHeldRoles> roles,
      WhizbangLifecycleOptions options,
      TimeProvider? timeProvider,
      ILogger<WhizbangRunController> logger) {
    ArgumentNullException.ThrowIfNull(permit);
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(work);
    ArgumentNullException.ThrowIfNull(roles);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);
    Permit = permit;
    _lifecycle = lifecycle;
    _work = [.. work];
    _roles = [.. roles];
    _options = options;
    _time = timeProvider ?? TimeProvider.System;
    _logger = logger;
  }

  /// <inheritdoc />
  public WhizbangRunPermit Permit { get; }

  /// <inheritdoc />
  public RunHaltReason? HaltedFor {
    get {
      lock (_reasons) {
        return _strongest();
      }
    }
  }

  /// <inheritdoc />
  public IReadOnlyCollection<RunHaltReason> HaltedReasons {
    get {
      lock (_reasons) {
        return [.. _reasons.Order()];
      }
    }
  }

  /// <inheritdoc />
  public async Task HaltAsync(RunHaltReason reason, CancellationToken cancellationToken) {
    // Not the caller's token: a host stop whose budget is already spent still closes the permit and writes the phase.
    await _serialize.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    try {
      LifecyclePhase settled;
      lock (_reasons) {
        if (_reasons.Contains(RunHaltReason.Stop) && reason != RunHaltReason.Stop) {
          return;   // a stop is final: a later pause or stand-down is not a step back
        }
        _reasons.Add(reason);
        settled = _settledPhaseOf(_strongest()!.Value);
      }
      var stopping = reason == RunHaltReason.Stop;
      LogHalting(_logger, reason);

      // 1. Close the permit: no new work is taken from here on, and every obeyer has acted on it.
      await _changeAsync(stopping ? RunState.Stopped : RunState.Paused, cancellationToken).ConfigureAwait(false);
      await _lifecycle.AdvanceToAsync(stopping ? LifecyclePhase.Stopping : LifecyclePhase.Pausing, CancellationToken.None)
        .ConfigureAwait(false);

      // 2. Finish what is in hand, then release what is still held.
      await _awaitNothingHeldAsync(cancellationToken).ConfigureAwait(false);
      foreach (var releaser in _work) {
        await _bestEffortAsync(releaser.ReleaseHeldWorkAsync, "held work", cancellationToken).ConfigureAwait(false);
      }

      // 3. Release the roles.
      foreach (var releaser in _roles) {
        await _bestEffortAsync(releaser.ReleaseAllAsync, "roles", cancellationToken).ConfigureAwait(false);
      }

      // 4. Confirm by writing the settled phase of the strongest reason that holds, on a token of its own.
      await _lifecycle.AdvanceToAsync(settled, CancellationToken.None).ConfigureAwait(false);
      LogHalted(_logger, reason);
    } finally {
      _serialize.Release();
    }
  }

  /// <inheritdoc />
  public async Task ResumeAsync(RunHaltReason reason, CancellationToken cancellationToken) {
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      LifecyclePhase? stillHalted;
      lock (_reasons) {
        if (_reasons.Contains(RunHaltReason.Stop)) {
          throw new InvalidOperationException("The instance was stopped; a stop is final and the instance does not resume.");
        }
        if (!_reasons.Remove(reason)) {
          return;   // that reason does not hold: nothing to lift
        }
        stillHalted = _strongest() is { } remaining ? _settledPhaseOf(remaining) : null;
      }
      if (stillHalted is { } phase) {
        // Another reason still holds the permit closed: only the reported phase follows the strongest of them.
        await _lifecycle.AdvanceToAsync(phase, cancellationToken).ConfigureAwait(false);
        LogStillHalted(_logger, reason, HaltedFor!.Value);
        return;
      }
      await _lifecycle.AdvanceToAsync(LifecyclePhase.Resuming, cancellationToken).ConfigureAwait(false);
      await _changeAsync(RunState.Running, cancellationToken).ConfigureAwait(false);
      await _lifecycle.AdvanceToAsync(LifecyclePhase.Running, cancellationToken).ConfigureAwait(false);
      LogResumed(_logger);
    } finally {
      _serialize.Release();
    }
  }

  /// <summary>The strongest reason that holds (a stop over a stand-down over a pause), or null when none does.</summary>
  private RunHaltReason? _strongest() => _reasons.Count == 0 ? null : _reasons.Max();

  /// <inheritdoc />
  public void Dispose() => _serialize.Dispose();

  private static LifecyclePhase _settledPhaseOf(RunHaltReason reason) => reason switch {
    RunHaltReason.Pause => LifecyclePhase.Paused,
    RunHaltReason.StandDown => LifecyclePhase.StandingBy,
    _ => LifecyclePhase.Stopped,
  };

  private async Task _changeAsync(RunState state, CancellationToken cancellationToken) {
    try {
      await Permit.ChangeAsync(state, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      // The permit itself has changed; an obeyer (a transport consumer) failed to act on it, or was cut short by the
      // caller's token, and says why.
      LogObeyerFailed(_logger, ex, state);
    }
  }

  private async Task _awaitNothingHeldAsync(CancellationToken cancellationToken) {
    try {
      await Permit.WhenNothingHeldAsync(cancellationToken)
        .WaitAsync(_options.HeldWorkTimeout, _time, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) {
      LogHeldWorkOutlastedTheBound(_logger, Permit.Held, _options.HeldWorkTimeout);
    }
  }

  private async Task _bestEffortAsync(Func<CancellationToken, Task> release, string what, CancellationToken cancellationToken) {
    try {
      await release(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      // A failure, or the caller's token cutting the step short: either way the halt goes on to confirm itself.
      LogReleaseFailed(_logger, ex, what);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Run control: halting for {Reason}; the run permit closes and no new work is taken")]
  static partial void LogHalting(ILogger logger, RunHaltReason reason);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Run control: halted for {Reason}; held work and roles are released and the phase is recorded")]
  static partial void LogHalted(ILogger logger, RunHaltReason reason);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Run control: resumed; the run permit is open")]
  static partial void LogResumed(ILogger logger);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "Run control: {Lifted} is lifted, but the instance stays halted for {Remaining}; the run permit stays closed")]
  static partial void LogStillHalted(ILogger logger, RunHaltReason lifted, RunHaltReason remaining);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Run control: an obeyer of the run permit failed to act on the change to {State}; the permit has changed, "
      + "and the halt goes on")]
  static partial void LogObeyerFailed(ILogger logger, Exception exception, RunState state);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "Run control: {Held} unit(s) of work were still in hand when the wait ended (bound {Bound}, or the caller's "
      + "stop budget ran out); the halt goes on, and what they hold is taken over by a peer when its lease lapses")]
  static partial void LogHeldWorkOutlastedTheBound(ILogger logger, int held, TimeSpan bound);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Run control: releasing {What} failed; the leases lapse instead, and a peer takes over after the lapse bound")]
  static partial void LogReleaseFailed(ILogger logger, Exception exception, string what);
}
