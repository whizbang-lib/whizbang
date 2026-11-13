// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Lets only one blue-green rebuild of a table run at a time (#2301): on this instance, and through the table's duty
/// (<see cref="PerspectiveRebuildDuty"/>), across the fleet.
/// </summary>
/// <remarks>
/// <para>
/// Two rebuilds of one table share its shadow table, and creating the shadow drops one left behind, so a second rebuild
/// would destroy the first one's replay. The guard is a duty won through the <see cref="IDutyElector"/>, held from before
/// the shadow is created until the swap is done, and kept alive by a <see cref="DutyGrantKeepalive"/> meanwhile, since a
/// rebuild runs far longer than a lease. It is never an advisory lock: a duty survives a pooler that hands each statement
/// a different backend, and it is the one way the framework holds anything exclusive (#2044).
/// </para>
/// <para>
/// An elector grants a duty this instance already holds again (one tenure, several handles), so the duty alone cannot keep
/// two rebuilds on one instance apart; a table being rebuilt here is refused before anything is voted for.
/// </para>
/// <para>
/// The cases, each locked by a test: granted, the rebuild runs; <see cref="DutyRefusal.Contended"/>, another instance is
/// rebuilding the table and this rebuild is refused as in flight; <see cref="DutyRefusal.Unavailable"/>,
/// <see cref="DutyRefusal.Refused"/> or <see cref="DutyRefusal.Halted"/>, the elector cannot confirm that no other
/// instance is rebuilding, so the rebuild fails closed with the reason; no elector (none registered, or the null
/// default), there is no fleet to coordinate with, so the rebuild runs, guarded on this instance alone, and the host is
/// told so once.
/// </para>
/// </remarks>
/// <param name="logger">Where a host with no elector is told, once, that rebuilds are guarded on this instance alone.</param>
/// <docs>fundamentals/perspectives/rebuild#one-blue-green-rebuild-per-table</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRebuilderBlueGreenTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
internal sealed partial class PerspectiveRebuildGuard(ILogger logger) {
  private readonly ConcurrentDictionary<string, byte> _rebuildingHere = new(StringComparer.Ordinal);
  private int _unguardedReported;

  /// <summary>Takes the guard for <paramref name="table"/>, or says why it cannot be had now.</summary>
  /// <param name="table">The live table the rebuild replaces.</param>
  /// <param name="elector">The elector, or null when none is registered.</param>
  /// <param name="timeProvider">The clock the duty's keepalive ticks on.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The entry: held, or refused as in flight, or refused for another reason.</returns>
  public async Task<PerspectiveRebuildGuardEntry> TryEnterAsync(
      string table, IDutyElector? elector, TimeProvider timeProvider, CancellationToken cancellationToken) {
    if (!_rebuildingHere.TryAdd(table, 0)) {
      return PerspectiveRebuildGuardEntry.InFlight;
    }
    var entered = false;
    try {
      if (elector is not { IsConfigured: true }) {
        if (Interlocked.Exchange(ref _unguardedReported, 1) == 0) {
          LogNoElector(logger);
        }
        entered = true;
        return new(new Hold(this, table, null, null), false, null);
      }
      var duty = PerspectiveRebuildDuty.For(table);
      var attempt = await elector.TryAcquireAsync(duty, cancellationToken).ConfigureAwait(false);
      if (attempt.Grant is { } grant) {
        entered = true;
        return new(new Hold(this, table, grant, DutyGrantKeepalive.Start(grant, timeProvider, logger)), false, null);
      }
      return attempt.Refusal == DutyRefusal.Contended
        ? PerspectiveRebuildGuardEntry.InFlight
        : new(null, false, $"could not take the duty {duty} that keeps one blue-green rebuild of {table} at a time "
          + $"({attempt.Refusal}: {attempt.Detail}), and does not start a rebuild without it");
    } finally {
      if (!entered) {
        _rebuildingHere.TryRemove(table, out _);
      }
    }
  }

  /// <summary>The held guard: its keepalive stops first, then the duty is released, then the table leaves this instance's set.</summary>
  private sealed class Hold(PerspectiveRebuildGuard guard, string table, IDutyGrant? grant, DutyGrantKeepalive? keepalive) : IAsyncDisposable {
    public async ValueTask DisposeAsync() {
      try {
        if (keepalive is not null) {
          await keepalive.DisposeAsync().ConfigureAwait(false);
        }
        if (grant is not null) {
          await grant.DisposeAsync().ConfigureAwait(false);
        }
      } finally {
        guard._rebuildingHere.TryRemove(table, out _);
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Perspective rebuild: no duty elector is registered, so a blue-green rebuild is kept from running twice on a table on this instance only; with no fleet to coordinate with, that is all the guarding there is to do")]
  private static partial void LogNoElector(ILogger logger);
}

/// <summary>What taking the rebuild guard gave: the hold, or that the table is in flight, or why else not.</summary>
/// <param name="Held">The hold, to dispose when the rebuild is done; null when the guard was not had.</param>
/// <param name="IsInFlight">Another rebuild of the table holds it, on this instance or another.</param>
/// <param name="Refusal">Why the guard could not be had otherwise; null when held or in flight.</param>
internal readonly record struct PerspectiveRebuildGuardEntry(IAsyncDisposable? Held, bool IsInFlight, string? Refusal) {
  /// <summary>Another rebuild of the table holds the guard.</summary>
  public static PerspectiveRebuildGuardEntry InFlight { get; } = new(null, true, null);
}
