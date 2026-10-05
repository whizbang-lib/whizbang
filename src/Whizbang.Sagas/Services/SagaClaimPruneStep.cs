// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Sagas.Services;

/// <summary>
/// The maintenance step that prunes the saga claims nothing will read again.
/// </summary>
/// <remarks>
/// <para>
/// A saga takes claims as it runs: one per stranded-saga sweep tick, one for its completion and one per
/// continuation it asks for. Nothing removed them, so the claim table grew by several rows per saga for
/// good (#1006). A sweep claim is dead once its interval has passed, and a completion or continuation
/// claim is taken only once the saga has completed, so each is pruned once older than
/// <see cref="SagaOptions.ClaimRetention"/>.
/// </para>
/// <para>
/// The abandonment claim is kept. It is the record that stops the sweep re-arming an abandoned saga,
/// and it goes only when an operator re-drives the saga.
/// </para>
/// <para>
/// Where role assignment manages the maintainer duty, only its holder prunes, so the fleet issues one
/// delete per cycle. Elsewhere every instance prunes: the delete is by age and idempotent, so a repeat
/// is a no-op, not a wrong answer.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration#claim-retention</docs>
/// <tests>tests/Whizbang.Sagas.Tests/Services/SagaClaimPruneStepTests.cs</tests>
public sealed partial class SagaClaimPruneStep(ILogger<SagaClaimPruneStep> logger) : IMaintenanceStep {

  /// <summary>
  /// The key prefixes of the spent claims: the sweep's, and the completion and continuation keys of
  /// <see cref="Helpers.SagaCompletionGuard"/> and <see cref="Helpers.SagaContinuationGuard"/>. Not the
  /// abandonment key of <see cref="Helpers.SagaAbandonGuard"/>.
  /// </summary>
  internal static readonly string[] SpentClaimPrefixes = ["saga-watchdog-sweep:", "saga-completed:", "saga-continuation:"];

  /// <summary>The key prefix of <see cref="Helpers.SagaAbandonGuard"/>'s abandonment claims, which are never pruned.</summary>
  internal const string ABANDONED_CLAIM_PREFIX = "saga-abandoned:";

  /// <summary>
  /// Every prefix the saga framework owns: kept out of the general expiry prune, because this step prunes
  /// the spent ones on <see cref="SagaOptions.ClaimRetention"/> and the abandonment claims must never go.
  /// </summary>
  internal static IEnumerable<string> OwnedClaimPrefixes => SpentClaimPrefixes.Append(ABANDONED_CLAIM_PREFIX);

  /// <inheritdoc />
  public string Name => "saga-claim-prune";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetService<IClaimedEmissionStore>() is not { } claims || !_isThisInstancesTurn(services)) {
      return;
    }

    var options = services.GetService<SagaOptions>() ?? new SagaOptions();
    var before = options.TimeProvider.GetUtcNow() - options.ClaimRetention;
    var pruned = 0;
    foreach (var prefix in SpentClaimPrefixes) {
      pruned += await claims.PruneAsync(prefix, before, cancellationToken).ConfigureAwait(false);
    }
    if (pruned > 0) {
      LogPruned(logger, pruned, before);
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

  [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} spent saga claim(s) taken before {Before}")]
  private static partial void LogPruned(ILogger logger, int count, DateTimeOffset before);
}
