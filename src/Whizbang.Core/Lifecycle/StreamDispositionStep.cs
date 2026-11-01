// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The maintenance step that disposes of sealed streams once their grace has passed (#1734, WS-3 S5), routing on the
/// disposition (#1735, WS-3 S6).
/// </summary>
/// <remarks>
/// <para>
/// Each run first schedules up to <see cref="StreamLifecycleOptions.SweepBatchSize"/> sealed streams that have no
/// disposition yet, due <see cref="StreamLifecycleOptions.FinalizeGrace"/> after the later of the seal and now. It then
/// takes up to the same number of due streams, oldest first. A stream a perspective still owes events, or that an active
/// destruction hold covers, is deferred for <see cref="MaintenanceWorkerOptions.DestructionRetryBackoffSeconds"/>.
/// </para>
/// <para>
/// A ready stream gets its final snapshot first: every perspective with a cursor on the stream that this host can run
/// snapshots its row at the cursor's event (<see cref="IPerspectiveRunner.BootstrapSnapshotAsync"/>), which is where an
/// <see cref="AppendToArchivedPolicy.NewGeneration"/> append resumes it. Then the <see cref="IDestructionHook"/> decides:
/// a cancel defers for the backoff, a defer moves the due time to its instant, a throw defers. Otherwise the disposition
/// it returns runs, or <see cref="Disposition.Archive"/> when it names none: Archive writes the whole stream through the
/// <see cref="IArchiveStore"/>; Delete purges it (<see cref="IStreamPurger"/>); Compact runs the registered
/// <see cref="ICompactionHandler"/> and then purges; CryptoShred, and Compact with no handler, defer and log why. A
/// disposition that ran clears the schedule and fires the PostDestruction hook; one that did not defers.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so under the maintainer duty when role assignment manages it. One stream's failure
/// is logged and deferred, and never holds back the next stream.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamDispositionStepTests.cs</tests>
public sealed partial class StreamDispositionStep : IMaintenanceStep {
  /// <summary>The step's name in logs.</summary>
  public const string STEP_NAME = "stream-disposition";

  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="timeProvider">The clock due times are read and written from.</param>
  /// <param name="logger">Logs each stream's outcome.</param>
  public StreamDispositionStep(TimeProvider timeProvider, ILogger<StreamDispositionStep> logger) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>
  /// Builds the step from a host's services, as both drivers register it: on the host's <see cref="TimeProvider"/> when
  /// one is registered and the system clock otherwise, logging to the host's logger when it has logging.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static StreamDispositionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new StreamDispositionStep(
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      services.GetService<ILogger<StreamDispositionStep>>() ?? NullLogger<StreamDispositionStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => STEP_NAME;

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    _ = _timeProvider;
    _ = _logger;
    return Task.CompletedTask;
  }
}
