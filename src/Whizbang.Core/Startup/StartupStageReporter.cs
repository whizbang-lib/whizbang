// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// Default <see cref="IStartupStageReporter"/>: logs each stage, records it on the instance's row through the
/// registered <see cref="IWorkCoordinator"/>, and tells the observers.
/// </summary>
/// <remarks>
/// <para>
/// Every change is serialized, so the row receives the stages in the order they were entered and an observer sees
/// each change once, in order. A write is bounded by its own five-second budget: a database that does not answer
/// cannot hold the lifecycle transition that entered the stage, and the stage is held and written again on the next
/// registration signal.
/// </para>
/// <para>
/// Nothing is written before <see cref="InstanceRegisteredAsync"/> is first called: until then the instance's row does
/// not exist, and a write that could only miss would cost a round trip (and, while the database is still unreachable,
/// the whole write budget) at every stage.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageReporterTests.cs</tests>
public sealed partial class StartupStageReporter : IStartupStageReporter, IDisposable {
  private const string SKIPPED = "skipped: ";
  private static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly IStartupStageObserver[] _observers;
  private readonly ILogger<StartupStageReporter> _logger;
  private readonly TimeProvider _time;
  private readonly string _libraryVersion;
  private readonly SemaphoreSlim _serialize = new(1, 1);
  private volatile StartupStageSnapshot? _current;
  // Whether the instance's row has been registered, so a write can land.
  private volatile bool _registered;
  // Whether the current stage is not on the row yet: entered before registration, or its write failed.
  private volatile bool _held;
  // A phase off the startup ladder (a pause, a stand-by) in force, recorded with the stage; null: the stage's own phase.
  // Read and written only under _serialize.
  private LifecyclePhase? _phase;

  /// <summary>Creates the reporter.</summary>
  /// <param name="scopeFactory">Resolves the work coordinator that writes the instance's row.</param>
  /// <param name="instanceProvider">This instance's identity: the row each stage is written to.</param>
  /// <param name="observers">Told of every change.</param>
  /// <param name="logger">Where each stage is logged.</param>
  /// <param name="libraryVersion">The library version every write records with the stage and phase.</param>
  /// <param name="timeProvider">The clock a stage's entry time and a write's timeout are read from.</param>
  public StartupStageReporter(
      IServiceScopeFactory scopeFactory,
      IServiceInstanceProvider instanceProvider,
      IEnumerable<IStartupStageObserver> observers,
      ILogger<StartupStageReporter> logger,
      ILibraryVersionProvider libraryVersion,
      TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(instanceProvider);
    ArgumentNullException.ThrowIfNull(observers);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(libraryVersion);
    _scopeFactory = scopeFactory;
    _instanceProvider = instanceProvider;
    _observers = [.. observers];
    _logger = logger;
    _time = timeProvider ?? TimeProvider.System;
    _libraryVersion = libraryVersion.LibraryVersion;
  }

  /// <inheritdoc />
  public StartupStageSnapshot? Current => _current;

  /// <inheritdoc />
  public ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(message);
    return _changeAsync(stage, message, skipped: false, cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return _changeAsync(stage, SKIPPED + reason, skipped: true, cancellationToken);
  }

  /// <inheritdoc />
  public async ValueTask EnterPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken = default) {
    if (!_isOffTheLadder(phase)) {
      throw new ArgumentOutOfRangeException(nameof(phase), phase,
        "Not a phase off the startup ladder: a phase on it is the stage's own, recorded by entering the stage.");
    }
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if (_phase == phase) {
        return;
      }
      _phase = phase;
      // A phase is not a stage: no stage line, and no stage observer has anything new to see. The row records it with
      // the stage the instance is in, in the one write a transition makes.
      _held = !(_registered && await _writeAsync(cancellationToken).ConfigureAwait(false));
    } finally {
      _serialize.Release();
    }
  }

  /// <inheritdoc />
  public async ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default) {
    // Called after every accepted heartbeat: once the row is known and nothing is held, there is nothing to do and no
    // reason to wait for a transition in progress.
    if (_registered && !_held) {
      return;
    }
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      _registered = true;
      if (_held) {
        var recorded = await _writeAsync(cancellationToken).ConfigureAwait(false);
        _held = !recorded;
        if (_current is { } current) {
          await _tellAsync(current, recorded, cancellationToken).ConfigureAwait(false);
        }
      }
    } finally {
      _serialize.Release();
    }
  }

  /// <summary>Releases the lock that serializes changes.</summary>
  public void Dispose() => _serialize.Dispose();

  private async ValueTask _changeAsync(StartupStage stage, string message, bool skipped, CancellationToken cancellationToken) {
    if (!Enum.IsDefined(stage)) {
      throw new ArgumentOutOfRangeException(nameof(stage), stage, "Not a startup stage.");
    }
    await _serialize.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      var current = _current;
      if (!_moves(current, stage, message, skipped) && !_resumes(current!, stage, skipped)) {
        return;
      }
      // A stage records its own phase: entering one ends a pause or a stand-by recorded with the stage before.
      _phase = null;
      // Progress within a stage keeps the time it was entered.
      var enteredAt = current?.Stage == stage ? current.EnteredAt : _time.GetUtcNow();
      var snapshot = new StartupStageSnapshot(stage, message, skipped, enteredAt);
      _current = snapshot;
      if (StartupStages.OrdinalOf(stage) is { } position) {
        LogStartupStage(_logger, position, StartupStages.Startup.Count, snapshot.Name, message);
      } else {
        LogShutdownStage(_logger, snapshot.Name, message);
      }
      var recorded = _registered && await _writeAsync(cancellationToken).ConfigureAwait(false);
      _held = !recorded;
      await _tellAsync(snapshot, recorded, cancellationToken).ConfigureAwait(false);
    } finally {
      _serialize.Release();
    }
  }

  /// <summary>
  /// Whether entering <paramref name="stage"/> changes anything: a later stage always does; the current stage does
  /// with a new message (progress), unless it is being skipped; an earlier stage never does.
  /// </summary>
  private static bool _moves(StartupStageSnapshot? current, StartupStage stage, string message, bool skipped) {
    if (current is null || stage > current.Stage) {
      return true;
    }
    return !skipped && stage == current.Stage && !string.Equals(message, current.Message, StringComparison.Ordinal);
  }

  /// <summary>
  /// Whether entering the current stage again ends a phase recorded off the ladder (a resume back into Running): the
  /// row's phase changes though the stage does not. Asked only when <see cref="_moves"/> said no, so a stage is current.
  /// </summary>
  private bool _resumes(StartupStageSnapshot current, StartupStage stage, bool skipped) =>
    _phase is not null && !skipped && stage == current.Stage;

  /// <summary>The phases that are not a stage's own: a pause, a stand-by, a revival, a fault, a halt.</summary>
  private static bool _isOffTheLadder(LifecyclePhase phase) => phase is LifecyclePhase.Pausing or LifecyclePhase.Paused
    or LifecyclePhase.StandingBy or LifecyclePhase.Resuming or LifecyclePhase.Faulted or LifecyclePhase.Halted;

  private async Task _tellAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
    foreach (var observer in _observers) {
      await observer.OnStageChangedAsync(snapshot, recorded, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Writes what the instance is in now, in one update (#1636): the current stage and its message, the phase (an
  /// off-ladder phase in force, else the stage's own) and the library version. With no stage yet, the stage is left as
  /// the row records it.
  /// </summary>
  private async Task<bool> _writeAsync(CancellationToken cancellationToken) {
    var current = _current;
    var transition = new InstanceTransition(_instanceProvider.InstanceId, current?.Stage, current?.Message,
      _phase ?? StartupStages.LifecyclePhaseOf(current!.Stage), _libraryVersion);
    try {
      await using var scope = _scopeFactory.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        return false;   // no storage in this host: nothing reads instance rows either
      }
      using var timeout = new CancellationTokenSource(_writeTimeout, _time);
      using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
      return await coordinator.RecordInstanceTransitionAsync(transition, bounded.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    }
#pragma warning disable CA1031, RCS1075 // a failed write is held and written again; it must never break the transition
    // that entered the stage. The stage columns and the recorder are Preflight's (277, 278), so from registration on a
    // write fails only when the database cannot be reached in time, and the next registration signal writes it again.
    catch (Exception ex) {
      LogWriteFailed(_logger, transition.Stage, transition.Phase, ex);
      return false;
    }
#pragma warning restore CA1031, RCS1075
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Stage {Position} of {Count}: {StageName} - {StageMessage}")]
  static partial void LogStartupStage(ILogger logger, int position, int count, string stageName, string stageMessage);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Shutdown stage: {StageName} - {StageMessage}")]
  static partial void LogShutdownStage(ILogger logger, string stageName, string stageMessage);

  [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
    Message = "Recording startup stage {Stage} and phase {Phase} on the instance row failed; it is held and written on the next registration signal")]
  static partial void LogWriteFailed(ILogger logger, StartupStage? stage, LifecyclePhase phase, Exception ex);
}
