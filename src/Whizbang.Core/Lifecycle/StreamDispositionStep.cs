// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
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
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var ledger = services.GetRequiredService<IStreamDispositionLedger>();
    var lifecycle = services.GetRequiredService<IOptions<StreamLifecycleOptions>>().Value;
    var backoff = TimeSpan.FromSeconds(services.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.DestructionRetryBackoffSeconds);
    var now = _timeProvider.GetUtcNow();
    _ = await ledger.ScheduleAsync(lifecycle.FinalizeGrace, lifecycle.SweepBatchSize, now, cancellationToken).ConfigureAwait(false);
    foreach (var due in await ledger.GetDueAsync(now, lifecycle.SweepBatchSize, cancellationToken).ConfigureAwait(false)) {
      try {
        await _disposeAsync(services, ledger, due, now + backoff, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogDispositionThrew(_logger, due.StreamId, ex);
        await ledger.SetDueAsync(due.StreamId, now + backoff, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  // Runs one due stream and writes its next due time: cleared when its disposition ran (or it was done already), before
  // the PostDestruction hook, so a slow or failing notification never leaves a disposed stream scheduled; otherwise when
  // to try again.
  private async Task _disposeAsync(
      IServiceProvider services, IStreamDispositionLedger ledger, DueStreamDisposition due, DateTimeOffset retryAt, CancellationToken ct) {
    if (due.Readiness == StreamDispositionReadiness.Done) {
      await ledger.SetDueAsync(due.StreamId, null, ct).ConfigureAwait(false);
      return;
    }
    if (due.Readiness != StreamDispositionReadiness.Ready) {
      LogNotReady(_logger, due.StreamId, due.Readiness, retryAt);
      await ledger.SetDueAsync(due.StreamId, retryAt, ct).ConfigureAwait(false);
      return;
    }
    await _takeFinalSnapshotsAsync(services, ledger, due.StreamId, ct).ConfigureAwait(false);

    var context = new DestructionContext {
      Reason = DestructionReason.FinalizeGraceElapsed,
      Granularity = DestructionGranularity.Stream,
      StreamId = due.StreamId,
      DeclaredDefault = Disposition.Archive,
    };
    var hook = services.GetService<IDestructionHook>();
    var decision = hook is null
      ? DestructionResult.Proceed()
      : await hook.OnBeforeDestructionAsync(context, ct).ConfigureAwait(false);
    if (decision.Cancel || decision.DeferUntil.HasValue) {
      var until = decision.DeferUntil ?? retryAt;
      LogVetoed(_logger, due.StreamId, until);
      await ledger.SetDueAsync(due.StreamId, until, ct).ConfigureAwait(false);
      return;
    }
    if (!await _runAsync(services, decision.Disposition ?? context.DeclaredDefault, context, ct).ConfigureAwait(false)) {
      await ledger.SetDueAsync(due.StreamId, retryAt, ct).ConfigureAwait(false);
      return;
    }
    await ledger.SetDueAsync(due.StreamId, null, ct).ConfigureAwait(false);
    if (hook is not null) {
      try {
        await hook.OnAfterDestructionAsync(context, ct).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogPostHookThrew(_logger, due.StreamId, ex);
      }
    }
  }

  // The final snapshot: each perspective with a cursor on the stream that this host can run snapshots its row at the
  // cursor's event. A perspective this host has no runner for, or a cursor at no event, is skipped; a NewGeneration
  // append to the stream then reads as AutoRestore, which loses nothing.
  private static async Task _takeFinalSnapshotsAsync(IServiceProvider services, IStreamDispositionLedger ledger, Guid streamId, CancellationToken ct) {
    var registry = services.GetService<IPerspectiveRunnerRegistry>();
    if (registry is null) {
      return;
    }
    foreach (var cursor in await ledger.GetCursorsAsync(streamId, ct).ConfigureAwait(false)) {
      if (cursor.LastEventId is not { } eventId || registry.GetRunner(cursor.PerspectiveName, services) is not { } runner) {
        continue;
      }
      await runner.BootstrapSnapshotAsync(streamId, cursor.PerspectiveName, eventId, ct).ConfigureAwait(false);
    }
  }

  // Runs the disposition; true when it ran.
  private async Task<bool> _runAsync(IServiceProvider services, Disposition disposition, DestructionContext context, CancellationToken ct) {
    var streamId = context.StreamId!.Value;
    switch (disposition) {
      case Disposition.Archive: {
          var written = await services.GetRequiredService<IArchiveStore>()
            .WriteAsync(ArchiveBatch.WholeStream(streamId), ct).ConfigureAwait(false);
          if (!written.Archived) {
            LogArchiveRefused(_logger, streamId, written.Status);
          }
          return written.Archived;
        }
      case Disposition.Delete:
        await _purgeAsync(services, streamId, ct).ConfigureAwait(false);
        return true;
      case Disposition.Compact when services.GetService<ICompactionHandler>() is { } compactor:
        await compactor.CompactAsync(context, ct).ConfigureAwait(false);
        await _purgeAsync(services, streamId, ct).ConfigureAwait(false);
        return true;
      default:
        LogUnavailable(_logger, streamId, disposition,
          disposition == Disposition.Compact ? "it needs an ICompactionHandler registered, and none is" : "it has not shipped");
        return false;
    }
  }

  private static Task<StreamPurgeReport> _purgeAsync(IServiceProvider services, Guid streamId, CancellationToken ct) =>
    services.GetRequiredService<IStreamPurger>().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [streamId],
      RequestedBy = STEP_NAME,
      Reason = "The sealed stream's FinalizeGrace passed and its disposition purges it",
    }, ct);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Disposition of sealed stream {StreamId} deferred until {RetryAt}: {Readiness}")]
  static partial void LogNotReady(ILogger logger, Guid streamId, StreamDispositionReadiness readiness, DateTimeOffset retryAt);

  [LoggerMessage(Level = LogLevel.Information, Message = "Disposition of sealed stream {StreamId} deferred by the PreDestruction hook until {Until}")]
  static partial void LogVetoed(ILogger logger, Guid streamId, DateTimeOffset until);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Archiving sealed stream {StreamId} was refused ({Status}); its disposition is deferred")]
  static partial void LogArchiveRefused(ILogger logger, Guid streamId, string status);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Sealed stream {StreamId} kept: its disposition is {Disposition}, which cannot run: {Reason}")]
  static partial void LogUnavailable(ILogger logger, Guid streamId, Disposition disposition, string reason);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Disposition of sealed stream {StreamId} failed and is deferred")]
  static partial void LogDispositionThrew(ILogger logger, Guid streamId, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning, Message = "PostDestruction hook threw for sealed stream {StreamId}; its disposition already ran")]
  static partial void LogPostHookThrew(ILogger logger, Guid streamId, Exception exception);
}
