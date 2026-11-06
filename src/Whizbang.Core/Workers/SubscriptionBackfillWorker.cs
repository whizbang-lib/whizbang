// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// The paced backfill of a grown subscription (#2092). The subscription-expansion reconciler queues one backfill per
/// (owner, event type); this worker walks each in bounded WINDOWS by the owner's commit sequence: it asks for the type's
/// events after the backfill's position, up to <see cref="StreamIntegrityOptions.BackfillWindowSize"/>, and asks for
/// the next window only once the owner's <see cref="BackfillWindowAnswer"/> has moved the position to where that window
/// stopped (#2098). At most <see cref="StreamIntegrityOptions.BackfillMaxConcurrentWindows"/> windows await an answer at
/// once, and every window spends its full size from a token bucket refilled at
/// <see cref="StreamIntegrityOptions.BackfillEventsPerMinute"/>, holding at most one minute's worth (or one window). A
/// window unanswered past <see cref="StreamIntegrityOptions.RepairAnswerTimeoutMinutes"/> is asked again, up to
/// <see cref="StreamIntegrityOptions.BackfillMaxWindowAttempts"/>; then the backfill ends as
/// <see cref="SubscriptionBackfillOutcome.Unanswered"/>, which is what an owner on a release before windowed backfill
/// produces (it answers the legacy way, with bundles and no window answer). Runs on the instance holding the maintainer
/// duty, only once the housekeeping idle gate admits integrity work, and never in report-only or with
/// <see cref="StreamIntegrityOptions.BackfillOnSubscriptionGrowth"/> off.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/SubscriptionBackfillWorkerTests.cs</tests>
#pragma warning disable CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113 // RED stub (#2092)
public sealed partial class SubscriptionBackfillWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<StreamIntegrityOptions> options,
    ILogger<SubscriptionBackfillWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService {
  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

  internal Task TickAsync(double elapsedSeconds, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
}
#pragma warning restore CA1822, S2325, RCS1163, S1144, IDE0052, IDE0060, RCS1213, CS9113
