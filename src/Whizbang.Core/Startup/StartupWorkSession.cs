// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Core.Startup;

/// <summary>
/// The one way a start runs its heavy startup work (#2596): each unit is a declared name, per-schema-per-build work
/// runs once per schema per build, and an instance holds one of the fleet's heavy-start permits while any of its heavy
/// work runs.
/// </summary>
/// <remarks>
/// <para>
/// Per-schema-per-build work is claimed before a permit is asked for, so a start whose shared work a peer already did,
/// or is doing, takes no permit for it. The claim is keyed by the work's fingerprint for this build (the library
/// version, the name and what this build gives the work): the first start of a build claims it, the rest skip it while
/// it runs and after it finished, and a run that fails or does not finish releases the claim for the next start. A start
/// whose own schema pass applied something claims it whatever the record says.
/// </para>
/// <para>
/// The permit is taken once, at the first unit that runs, and held until the session is disposed. Past the cap the start
/// waits in the Converge stage and says so once; past <see cref="StartupWorkOptions.PermitWaitLimit"/> it runs without a
/// permit, at Warning, so stuck holders never keep a new instance from starting.
/// </para>
/// <para>
/// Nothing here is fatal and nothing here weakens what ran before (rolling-deploy integrity): with no ledger, no
/// instance id, a schema whose functions an older release never installed, or a ledger call that fails, each unit runs
/// directly, as before the cap existed. Only the work's own failure reaches the caller, after its claim is released.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/StartupWorkSessionTests.cs</tests>
public sealed partial class StartupWorkSession : IAsyncDisposable {
  private readonly IStartupWorkLedger? _ledger;
  private readonly Guid? _instanceId;
  private readonly StartupWorkOptions _options;
  private readonly bool _passApplied;
  private readonly IStartupStageReporter? _stages;
  private readonly TimeProvider _time;
  private readonly ILogger _logger;
  private bool? _available;
  private bool _permitSettled;

  /// <summary>Creates a session for one start.</summary>
  /// <param name="ledger">The database's side; null runs every unit directly, as before the cap existed.</param>
  /// <param name="instanceId">This instance; null runs every unit directly, since nothing can be claimed for it.</param>
  /// <param name="options">The cap.</param>
  /// <param name="passApplied">Whether this start's own schema pass applied anything, so shared work runs again.</param>
  /// <param name="stages">Where the wait for a permit is reported; null reports nothing.</param>
  /// <param name="timeProvider">The clock the wait is measured and paced by.</param>
  /// <param name="logger">Where skips, waits and failures are said; null for silence.</param>
  public StartupWorkSession(
      IStartupWorkLedger? ledger, Guid? instanceId, StartupWorkOptions options, bool passApplied,
      IStartupStageReporter? stages, TimeProvider timeProvider, ILogger? logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _ledger = ledger;
    _instanceId = instanceId;
    _options = options;
    _passApplied = passApplied;
    _stages = stages;
    _time = timeProvider;
    _logger = logger ?? NullLogger.Instance;
    Delay = (wait, ct) => Task.Delay(wait, _time, ct);
  }

  /// <summary>
  /// How the wait between two permit requests is spent; by default a delay on the session's clock. A test replaces it so
  /// the wait is a signal it controls rather than a timer it races.
  /// </summary>
  internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; }

  /// <summary>Whether this session holds a heavy-start permit now.</summary>
  public bool HoldsPermit { get; private set; }

  /// <summary>Runs the declared work <paramref name="name"/> under its scope's rule.</summary>
  /// <param name="name">A name <see cref="FrameworkStartupWork"/> declares.</param>
  /// <param name="inputs">What this build gives the work; with the library version, its fingerprint for this build.</param>
  /// <param name="work">The work; returns whether it completed, so an unfinished run releases its claim.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How it ended.</returns>
  /// <exception cref="InvalidOperationException">Nothing declares <paramref name="name"/>.</exception>
  public async Task<StartupWorkOutcome> RunAsync(
      string name, string inputs, Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(work);
    var declaration = FrameworkStartupWork.Find(name)
      ?? throw new InvalidOperationException(
        $"'{name}' is not declared startup work. Declare it in FrameworkStartupWork with its scope, so the fleet-wide "
        + "heavy-start cap and the once-per-build rule apply to it.");

    if (_instanceId is not { } instanceId || !await _isAvailableAsync(cancellationToken).ConfigureAwait(false)) {
      await work(cancellationToken).ConfigureAwait(false);
      return StartupWorkOutcome.Ran;
    }
    if (declaration.Scope == StartupWorkScope.PerInstance) {
      await _ensurePermitAsync(instanceId, cancellationToken).ConfigureAwait(false);
      await work(cancellationToken).ConfigureAwait(false);
      return StartupWorkOutcome.Ran;
    }
    return await _runSharedAsync(declaration.Name, Fingerprint(declaration.Name, inputs), instanceId, work, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>The fingerprint of <paramref name="name"/> for <paramref name="inputs"/> under this library version.</summary>
  /// <param name="name">The declared name.</param>
  /// <param name="inputs">What this build gives the work.</param>
  /// <returns>Lowercase hex SHA-256.</returns>
  public static string Fingerprint(string name, string inputs) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(inputs);
    var text = string.Create(CultureInfo.InvariantCulture, $"{Diagnostics.WhizbangVersionInfo.Version}\n{name}\n{inputs}");
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
  }

  /// <summary>Gives the permit back, if this session holds one.</summary>
  /// <returns>A task that completes once the permit is released.</returns>
  public async ValueTask DisposeAsync() {
    if (!HoldsPermit) {
      return;
    }
    HoldsPermit = false;
    try {
      await _ledger!.ReleasePermitAsync(_instanceId!.Value, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      // The permit is reclaimed once this instance is no longer live; until then it counts against the cap.
      LogReleaseFailed(_logger, ex);
    }
  }

  private async Task<StartupWorkOutcome> _runSharedAsync(
      string name, string fingerprint, Guid instanceId, Func<CancellationToken, Task<bool>> work,
      CancellationToken cancellationToken) {
    StartupWorkClaim claim;
    try {
      claim = await _ledger!.ClaimAsync(name, fingerprint, instanceId, _passApplied, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogClaimFailed(_logger, ex, name);
      await work(cancellationToken).ConfigureAwait(false);
      return StartupWorkOutcome.Ran;
    }
    if (claim == StartupWorkClaim.Done) {
      LogSkippedDone(_logger, name);
      return StartupWorkOutcome.SkippedDone;
    }
    if (claim == StartupWorkClaim.RunningElsewhere) {
      LogSkippedRunningElsewhere(_logger, name);
      return StartupWorkOutcome.SkippedRunningElsewhere;
    }

    var completed = false;
    try {
      await _ensurePermitAsync(instanceId, cancellationToken).ConfigureAwait(false);
      completed = await work(cancellationToken).ConfigureAwait(false);
    } finally {
      // Done when it completed; released otherwise (a failure, an unfinished run, a cancellation), so the next start runs
      // it. On an uncanceled token: a canceled start still owes the release.
      await _completeAsync(name, fingerprint, instanceId, completed).ConfigureAwait(false);
    }
    return StartupWorkOutcome.Ran;
  }

  private async Task _completeAsync(string name, string fingerprint, Guid instanceId, bool completed) {
    try {
      await _ledger!.CompleteAsync(name, fingerprint, instanceId, completed, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      // A claim left running is taken over once this instance is no longer live.
      LogCompleteFailed(_logger, ex, name);
    }
  }

  private async Task<bool> _isAvailableAsync(CancellationToken cancellationToken) {
    if (_ledger is null) {
      return false;
    }
    if (_available is { } known) {
      return known;
    }
    try {
      _available = await _ledger.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogAvailabilityFailed(_logger, ex);
      _available = false;
    }
    return _available.Value;
  }

  private async Task _ensurePermitAsync(Guid instanceId, CancellationToken cancellationToken) {
    if (_permitSettled) {
      return;
    }
    _permitSettled = true;
    var started = _time.GetTimestamp();
    var said = false;
    while (true) {
      StartupPermitAnswer answer;
      try {
        answer = await _ledger!.TryAcquirePermitAsync(instanceId, _options.MaxConcurrentHeavyStarts, cancellationToken)
          .ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogPermitFailed(_logger, ex);
        return;
      }
      if (answer.Granted) {
        HoldsPermit = true;
        return;
      }
      if (_time.GetElapsedTime(started) >= _options.PermitWaitLimit) {
        LogPermitWaitExpired(_logger, _options.PermitWaitLimit, answer.Held, _options.MaxConcurrentHeavyStarts);
        return;
      }
      if (!said) {
        said = true;
        var message = string.Create(CultureInfo.InvariantCulture,
          $"waiting for a heavy-start permit ({answer.Held} of {_options.MaxConcurrentHeavyStarts} held across the fleet)");
        LogWaiting(_logger, message);
        if (_stages is not null) {
          await _stages.EnterAsync(StartupStage.Converge, message, cancellationToken).ConfigureAwait(false);
        }
      }
      await Delay(_options.PermitPollInterval, cancellationToken).ConfigureAwait(false);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Startup work {Work} already ran for this build; skipped")]
  static partial void LogSkippedDone(ILogger logger, string work);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Startup work {Work} is being run by another instance of this build; skipped")]
  static partial void LogSkippedRunningElsewhere(ILogger logger, string work);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Startup work: {Message}")]
  static partial void LogWaiting(ILogger logger, string message);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "No heavy-start permit came free within {Limit} ({Held} of {Cap} held); this start runs its heavy work without one")]
  static partial void LogPermitWaitExpired(ILogger logger, TimeSpan limit, int held, int cap);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "A heavy-start permit could not be asked for; this start runs its heavy work without one")]
  static partial void LogPermitFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Startup work {Work} could not be claimed; this instance runs it without the once-per-build record")]
  static partial void LogClaimFailed(ILogger logger, Exception exception, string work);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
    Message = "The claim on startup work {Work} could not be ended; it is taken over once this instance is no longer live")]
  static partial void LogCompleteFailed(ILogger logger, Exception exception, string work);

  [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
    Message = "The heavy-start permit could not be given back; it is reclaimed once this instance is no longer live")]
  static partial void LogReleaseFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
    Message = "The startup work record could not be read; this start runs its heavy work uncapped, as before the cap existed")]
  static partial void LogAvailabilityFailed(ILogger logger, Exception exception);
}
