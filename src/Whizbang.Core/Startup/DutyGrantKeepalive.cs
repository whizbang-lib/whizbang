// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Startup;

/// <summary>
/// Keeps a held duty's lease alive while its holder does one long piece of exclusive work, by verifying the grant on
/// a timer for as long as the keepalive runs (#2127).
/// </summary>
/// <remarks>
/// <para>
/// A holder normally renews from its own work loop, so a holder that is stuck stops renewing and lapses. Startup is
/// the exception: the migrator's work between Elect and the end of Migrate is a handful of phases (the stored-form
/// scans, a wait for the schema lock, an index rebuild, a long migration step), each of which can run for longer than
/// the lease with nothing in it that could verify. A lease that lapses there is refused renewal even when nobody took
/// it, so the migrator loses the role it is still exercising and a waiter takes the same work over.
/// </para>
/// <para>
/// The timer ticks at half the grant's <see cref="IDutyGrant.RenewInterval"/>. A grant throttles its own renewals to
/// one per interval, so the database sees one renewal per interval, and never more than one and a half intervals
/// pass between two renewals, which every lease (at least two intervals) outlasts. Ticks that find the previous verify
/// still running are owed one verify, run as soon as it answers, so a slow database never queues renewals and no tick
/// is lost. A verify that answers false is not an end:
/// the grant says itself whether it lost the role or could not reach the database, and the next tick asks again.
/// </para>
/// <para>
/// A holder that is alive but stuck (a hung statement, a deadlocked phase) would keep the role forever on a timer, so a
/// keepalive may be given a stall limit (#2165): it renews only while the work reports progress
/// (<see cref="NoteProgress"/>), and once nothing has been reported for longer than the limit it stops renewing for
/// good and says so once. The lease then lapses and another instance takes the role over through the normal vote.
/// Progress reported after that does not take the role back: the lease may already belong to another instance.
/// </para>
/// <para>
/// Disposing stops the timer and waits for a verify in flight, so nothing renews the grant once it returns: the
/// owner disposes the keepalive first and the grant after it. It never disposes the grant.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyGrantKeepaliveTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyGrantKeepaliveTests.cs:AHolderThatReportsNoProgress_StopsBeingRenewed_PastTheStallLimit_AndSaysSoOnceAsync</tests>
public sealed partial class DutyGrantKeepalive : IAsyncDisposable {
  private static readonly DutyGrantKeepalive _idle = new();

  private readonly IDutyGrant? _grant;
  private readonly ITimer? _timer;
  private readonly TimeProvider? _timeProvider;
  private readonly TimeSpan? _stallLimit;
  private readonly ILogger _logger;
  private readonly CancellationTokenSource _stopping = new();
  private readonly Lock _sync = new();
  private Task _inFlight = Task.CompletedTask;
  private bool _verifying;
  private bool _owed;
  private bool _stopped;
  private bool _stalled;
  private long _lastProgress;

  /// <summary>The keepalive of nothing: never running.</summary>
  private DutyGrantKeepalive() {
    _logger = NullLogger.Instance;
    _stopped = true;
  }

  private DutyGrantKeepalive(IDutyGrant grant, TimeSpan renewInterval, TimeProvider timeProvider, ILogger logger, TimeSpan? stallLimit) {
    _grant = grant;
    _logger = logger;
    _timeProvider = timeProvider;
    _stallLimit = stallLimit;
    _lastProgress = timeProvider.GetTimestamp();
    TickInterval = renewInterval / 2;
    _timer = timeProvider.CreateTimer(_ => Tick(), null, TickInterval.Value, TickInterval.Value);
  }

  /// <summary>Whether the keepalive is renewing a grant now: started over a grant with a lease, not yet disposed, and not
  /// stalled.</summary>
  public bool IsRunning {
    get {
      lock (_sync) {
        return !_stopped && !_stalled;
      }
    }
  }

  /// <summary>Whether it stopped renewing because the work reported no progress for longer than its stall limit (#2165).</summary>
  public bool IsStalled {
    get {
      lock (_sync) {
        return _stalled;
      }
    }
  }

  /// <summary>How often it verifies the grant: half the grant's renew interval, or null when there is nothing to keep alive.</summary>
  public TimeSpan? TickInterval { get; }

  /// <summary>
  /// Starts keeping <paramref name="grant"/> alive. A null grant, or one with no <see cref="IDutyGrant.RenewInterval"/>,
  /// starts nothing, and the keepalive returned reports that it is not running.
  /// </summary>
  /// <param name="grant">The grant, or null when nothing is held.</param>
  /// <param name="timeProvider">The clock the ticks are measured on.</param>
  /// <param name="logger">Where a verify that throws, and a stall, are reported.</param>
  /// <param name="stallLimit">How long the work may report no progress before the keepalive stops renewing (#2165);
  /// null renews for as long as the keepalive runs.</param>
  /// <returns>The keepalive; dispose it before the grant.</returns>
  /// <exception cref="ArgumentOutOfRangeException">A stall limit that is not positive.</exception>
  public static DutyGrantKeepalive Start(
      IDutyGrant? grant, TimeProvider timeProvider, ILogger? logger = null, TimeSpan? stallLimit = null) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    if (stallLimit is { } limit && limit <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(stallLimit), limit, "A stall limit must be positive; null has none.");
    }
    return grant?.RenewInterval is { } interval
      ? new DutyGrantKeepalive(grant, interval, timeProvider, logger ?? NullLogger.Instance, stallLimit)
      : _idle;
  }

  /// <summary>
  /// The work the grant is held for made progress (#2165): the stall limit is measured from now. Harmless on a
  /// keepalive with no limit, of nothing, stopped or stalled.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyGrantKeepaliveTests.cs:AHolderThatReportsProgress_IsRenewedPastTheStallLimit_ForAsLongAsItDoesAsync</tests>
  public void NoteProgress() {
    if (_timeProvider is null) {
      return;
    }
    Interlocked.Exchange(ref _lastProgress, _timeProvider.GetTimestamp());
  }

  /// <summary>
  /// One tick: verify now, or owe one verify to the verify still running. A tick that arrives once the keepalive has
  /// stopped (a timer callback already on its way when the timer was disposed) does nothing.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Startup/DutyGrantKeepaliveTests.cs:ATickThatArrivesAfterTheStop_VerifiesNothingAsync</tests>
  internal void Tick() {
    lock (_sync) {
      if (_stopped || _stalled || _hasStalled()) {
        return;
      }
      if (_verifying) {
        _owed = true;
        return;
      }
      _verifying = true;
      _inFlight = _verifyUntilNothingIsOwedAsync();
    }
  }

  /// <summary>Whether the work has reported nothing for longer than the stall limit; the first time, stops and says so.</summary>
  private bool _hasStalled() {
    if (_stallLimit is not { } limit) {
      return false;
    }
    var silent = _timeProvider!.GetElapsedTime(Interlocked.Read(ref _lastProgress));
    if (silent <= limit) {
      return false;
    }
    _stalled = true;
    LogStalled(_logger, _grant!.Duty, limit);
    return true;
  }

  private async Task _verifyUntilNothingIsOwedAsync() {
    while (true) {
      await _verifyOnceAsync().ConfigureAwait(false);
      lock (_sync) {
        if (!_owed || _stopped || _stalled) {
          _verifying = false;
          return;
        }
        _owed = false;
      }
    }
  }

  private async Task _verifyOnceAsync() {
    try {
      // The answer is the grant's own business: false means lost, or not reachable now, and the grant logs which.
      _ = await _grant!.VerifyStillHeldAsync(_stopping.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (_stopping.IsCancellationRequested) {
      // The keepalive is stopping; the verify it canceled is not a failure.
    } catch (Exception ex) {
      // Never fatal: the lease may still stand, and the next tick asks again. Said, because a keepalive that cannot
      // renew is a role about to be lost.
      LogVerifyFailed(_logger, _grant!.Duty, ex);
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    Task inFlight;
    lock (_sync) {
      if (_stopped) {
        return;
      }
      _stopped = true;
      inFlight = _inFlight;
    }
    _timer!.Dispose();
    await _stopping.CancelAsync().ConfigureAwait(false);
    await inFlight.ConfigureAwait(false);
    _stopping.Dispose();
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "The keepalive of the {Duty} role could not verify its grant; the lease stands until it lapses, and the next tick asks again")]
  static partial void LogVerifyFailed(ILogger logger, string duty, Exception exception);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "The {Duty} role's holder reported no progress for longer than {StallLimit}; its keepalive stops renewing, so the lease lapses and another instance can take the role over")]
  static partial void LogStalled(ILogger logger, string duty, TimeSpan stallLimit);
}
