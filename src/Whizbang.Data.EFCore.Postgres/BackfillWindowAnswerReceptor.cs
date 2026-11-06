// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The requester's side of a paced backfill window (#2092): applies a received <see cref="BackfillWindowAnswer"/> to the
/// backfill's durable progress (<see cref="IWorkCoordinator.RecordSubscriptionBackfillAnswerAsync"/>), so the next window
/// starts exactly where this one stopped (#2098), counts the window's events per type, and logs the type's finish when
/// the owner had nothing more. A late or duplicate answer applies to nothing and counts nothing. Report-only takes no
/// part in backfill, so the answer is not applied there. Lives in the driver assembly for the same reason as
/// <see cref="RedeliveryRequestReceptor"/>, and is registered beside it.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BackfillWindowAnswerReceptorTests.cs</tests>
public sealed partial class BackfillWindowAnswerReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<BackfillWindowAnswerReceptor> logger) : IReceptor<BackfillWindowAnswer> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(BackfillWindowAnswer message, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    await using var scope = scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var integrity = services.GetService<Microsoft.Extensions.Options.IOptions<StreamIntegrityOptions>>()?.Value;
    if (!RepairTraffic.IsRepairEnabled(integrity)) {
      LogDeclined(logger, message.BackfillId);
      return;
    }
    var coordinator = services.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      LogNoCoordinator(logger, message.BackfillId);
      return;
    }
    var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    var progress = await coordinator.RecordSubscriptionBackfillAnswerAsync(message, now, cancellationToken).ConfigureAwait(false);
    if (progress is null) {
      LogStale(logger, message.Window, message.BackfillId);
      return;
    }
    var metrics = services.GetService<Whizbang.Core.Observability.StreamIntegrityMetrics>();
    metrics?.BackfillEventsReceived.Add(message.EventCount, new KeyValuePair<string, object?>("event_type", progress.EventType));
    if (progress.Outcome != SubscriptionBackfillOutcome.Complete) {
      return;
    }
    metrics?.BackfillsEnded.Add(1, new KeyValuePair<string, object?>("outcome", "complete"));
    LogFinished(logger, progress.EventType, progress.OwnerServiceName, progress.EventsReceived, progress.WindowsRequested);
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Backfill of {EventType} from '{OwnerServiceName}' finished: {EventCount} events in {WindowCount} window request(s)")]
  private static partial void LogFinished(ILogger logger, string eventType, string ownerServiceName, long eventCount, long windowCount);

  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Backfill window {Window} answer for backfill {BackfillId} applied to nothing (late, duplicate or ended)")]
  private static partial void LogStale(ILogger logger, long window, Guid backfillId);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Backfill window answer for backfill {BackfillId} not applied: RepairMode is ReportOnly")]
  private static partial void LogDeclined(ILogger logger, Guid backfillId);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Backfill window answer for backfill {BackfillId} received but no work coordinator is registered; ignored")]
  private static partial void LogNoCoordinator(ILogger logger, Guid backfillId);
}
