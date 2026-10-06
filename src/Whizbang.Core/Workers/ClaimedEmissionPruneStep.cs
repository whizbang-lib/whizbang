// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Workers;

/// <summary>
/// The maintenance step that deletes expired claims from the claimed-emission store.
/// </summary>
/// <remarks>
/// <para>
/// Every claim records when it expires, and the store's documentation said expired claims were pruned,
/// but nothing pruned them, so the table grew by one row per <c>PublishOnceAsync</c> key and per
/// maintenance window for good (#999). This step deletes a claim once it is <see cref="Grace"/> past its
/// expiry. The grace keeps a claim well beyond the window it guards: the maintenance steps' window
/// claims last up to an hour, and a <c>PublishOnceAsync</c> claim keeps its key's emission once-only for
/// at least a day after it was taken.
/// </para>
/// <para>
/// A claim whose key starts with a <see cref="RetainedClaimKeyPrefix"/> is never deleted here: its owner
/// prunes it. That is how the saga framework keeps its abandonment claims, which stop the stranded-saga
/// sweep re-arming an abandoned saga and must not expire.
/// </para>
/// <para>
/// Each run deletes at most <see cref="MAX_BATCHES_PER_RUN"/> batches of <see cref="BATCH_SIZE"/>
/// claims, so a table that grew for a long time drains over several cycles rather than in one long
/// delete. Where role assignment manages the maintainer duty, only its holder prunes; elsewhere every
/// instance does, and the repeat is a no-op because the delete is by age.
/// </para>
/// </remarks>
/// <param name="logger">The logger; registration supplies a null logger where the host has none.</param>
/// <param name="timeProvider">The clock expiry is read against.</param>
/// <docs>fundamentals/dispatcher/publish-once#claim-expiry</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ClaimedEmissionPruneStepTests.cs</tests>
public sealed partial class ClaimedEmissionPruneStep(
    ILogger<ClaimedEmissionPruneStep> logger,
    TimeProvider? timeProvider = null) : IMaintenanceStep {

  /// <summary>How many claims one delete removes at most.</summary>
  public const int BATCH_SIZE = 5_000;

  /// <summary>How many deletes one run issues at most.</summary>
  public const int MAX_BATCHES_PER_RUN = 10;

  /// <summary>How long past its expiry a claim is kept before it is deleted.</summary>
  public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

  private readonly ILogger _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "claimed-emission-prune";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetService<IClaimedEmissionStore>() is not { } claims || !_isThisInstancesTurn(services)) {
      return;
    }

    var retained = services.GetServices<RetainedClaimKeyPrefix>()
      .Select(p => p.KeyPrefix)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
    var expiredBefore = _timeProvider.GetUtcNow() - Grace;
    var pruned = 0;
    for (var batch = 0; batch < MAX_BATCHES_PER_RUN; batch++) {
      var deleted = await claims.PruneExpiredAsync(expiredBefore, retained, BATCH_SIZE, cancellationToken).ConfigureAwait(false);
      pruned += deleted;
      if (deleted < BATCH_SIZE) {
        break;
      }
    }
    if (pruned > 0) {
      LogPruned(_logger, pruned, expiredBefore);
    }
  }

  /// <summary>
  /// Whether this instance prunes: always, unless role assignment manages the maintainer duty, in which
  /// case only on its holder.
  /// </summary>
  private static bool _isThisInstancesTurn(IServiceProvider services) =>
    services.GetService<DutyHolderWorker>() is not { } holder
    || !holder.Roles.Contains(StartupDuties.MAINTAINER, StringComparer.Ordinal)
    || holder.Holds(StartupDuties.MAINTAINER);

  [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} claimed-emission claim(s) that expired before {ExpiredBefore}")]
  private static partial void LogPruned(ILogger logger, int count, DateTimeOffset expiredBefore);
}
