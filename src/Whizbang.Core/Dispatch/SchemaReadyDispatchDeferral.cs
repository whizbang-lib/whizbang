// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Holds a dispatch made before the migration completes until it does (#1877). A dispatch writes to the event
/// store and the outbox, which the migration provides, so one that arrives while <see cref="ISchemaReadyGate"/>
/// is closed waits on the gate's own completion signal (never a poll) and then runs. The wait honors the caller's
/// cancellation token and is bounded by <see cref="SchemaInitializationOptions.DispatchMaxWait"/>; past it the
/// dispatch fails with <see cref="WhizbangNotReadyException"/>, so a migration that never completes cannot hang
/// requests forever. The first deferral is logged once.
/// </summary>
/// <remarks>
/// The bound also keeps a dispatch made from startup work that the migration itself depends on from waiting
/// forever: it fails after the maximum wait, with the deferral already in the log, instead of deadlocking.
/// </remarks>
/// <docs>operations/workers/database-readiness</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:Send_WithTheGateClosed_DefersUntilTheMigrationCompletesAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchExecutorParityComponentTests.cs:EveryEntry_WhileTheSchemaIsNotReady_DefersUntilTheMigrationCompletesAsync</tests>
internal sealed partial class SchemaReadyDispatchDeferral {
  private readonly ISchemaReadyGate _gate;
  private readonly TimeSpan _maxWait;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;
  private int _deferralLogged;

  /// <summary>Creates the deferral over the gate, the maximum wait and the clock that times it.</summary>
  /// <docs>operations/workers/database-readiness</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:DeferredDispatch_WithAConfiguredMaxWait_ThrowsNotReadyWhenThatWaitElapsesAsync</tests>
  internal SchemaReadyDispatchDeferral(ISchemaReadyGate gate, TimeSpan maxWait, TimeProvider timeProvider, ILogger logger) {
    _gate = gate;
    _maxWait = maxWait;
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>
  /// The deferral for this host, or <see langword="null"/> when it registers no schema-ready gate (a fixture or a
  /// host without the worker pipeline), which stays ungated.
  /// </summary>
  /// <docs>operations/workers/database-readiness</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:Send_WithNoGateRegistered_StaysUngatedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:Send_OnceTheGateOpens_ProceedsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:DeferredDispatch_WithAConfiguredMaxWait_ThrowsNotReadyWhenThatWaitElapsesAsync</tests>
  internal static SchemaReadyDispatchDeferral? Resolve(IServiceProvider services) {
    if (services.GetService<ISchemaReadyGate>() is not { } gate) {
      return null;
    }
    var maxWait = services.GetService<IOptions<SchemaInitializationOptions>>()?.Value.DispatchMaxWait
      ?? SchemaInitializationOptions.DefaultDispatchMaxWait;
    return new SchemaReadyDispatchDeferral(
      gate,
      maxWait,
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      (ILogger?)services.GetService<ILogger<SchemaReadyDispatchDeferral>>() ?? NullLogger.Instance);
  }

  /// <summary>True once the migration has completed; a dispatch then runs without waiting.</summary>
  /// <docs>operations/workers/database-readiness</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:Send_OnceTheGateOpens_ProceedsAsync</tests>
  internal bool IsReady => _gate.IsReady;

  /// <summary>
  /// Waits for the migration to complete. Logs the deferral the first time any dispatch waits.
  /// </summary>
  /// <param name="cancellationToken">The caller's token; canceling it stops the wait.</param>
  /// <exception cref="WhizbangNotReadyException">The migration did not complete within the maximum wait.</exception>
  /// <docs>operations/workers/database-readiness</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:DeferredDispatch_ByDefault_WaitsAtMostFiveMinutesThenThrowsNotReadyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:DeferredDispatch_WhenTheCallerCancels_StopsWaitingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSchemaGateTests.cs:DeferredDispatch_LogsOnceThatDispatchIsDeferredUntilTheMigrationCompletesAsync</tests>
  internal async ValueTask WaitAsync(CancellationToken cancellationToken) {
    if (Interlocked.Exchange(ref _deferralLogged, 1) == 0) {
      LogDispatchDeferred(_logger, _maxWait);
    }
    try {
      await _gate.WaitForReadyAsync(cancellationToken)
        .WaitAsync(_maxWait, _timeProvider, cancellationToken)
        .ConfigureAwait(false);
    } catch (TimeoutException timeout) {
      throw new WhizbangNotReadyException(
        $"dispatch refused: the schema is not ready (Migrate has not completed) after waiting {_maxWait}. Writes "
        + "need the event store and outbox the migration provides. Raise "
        + "Whizbang:SchemaInitialization:DispatchMaxWait to wait longer.", timeout);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Dispatch is deferred until the migration completes: a dispatch arrived before the schema was ready, "
            + "so it waits (at most {MaxWait}) and then runs. Later dispatches made before the migration completes "
            + "wait too, without logging again.")]
  private static partial void LogDispatchDeferred(ILogger logger, TimeSpan maxWait);
}
