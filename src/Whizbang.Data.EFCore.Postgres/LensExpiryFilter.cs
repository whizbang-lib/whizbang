using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Applies the E2-4d logical-expiry read filter to a lens query: a <c>TransientStorage.TtlRow</c> perspective
/// hides rows whose <c>expires_at</c> shadow property has passed, so a lens read never returns a logically
/// expired row (the maintenance reaper deletes them physically later).
/// </summary>
/// <remarks>
/// Applied ONLY for models registered in <see cref="PerspectiveTtlRegistry"/> (TtlRow). A non-TtlRow model's
/// rows never expire, so the query is returned unchanged and never references the <c>expires_at</c> shadow
/// property — which its (hand-configured / non-production) context may not declare. This gate is what keeps
/// the filter zero-fan-out: it is inert for every Sourced / PersistedRow / InMemory perspective.
/// This is a LENS-ONLY filter — the writer/replay path (<c>GetByStreamIdAsync</c>, the upsert's existing-row
/// lookup) does NOT route through here, so continued applies still see an expired-but-unreaped row.
/// </remarks>
internal static class LensExpiryFilter {
  /// <summary>Wraps a lens base query with the expiry predicate for TtlRow models; a no-op otherwise.</summary>
  internal static IQueryable<PerspectiveRow<TModel>> Apply<TModel>(IQueryable<PerspectiveRow<TModel>> query)
      where TModel : class {
    var ttlSeconds = PerspectiveTtlRegistry.ResolveSeconds(typeof(TModel));
    if (ttlSeconds < 0) {
      // Ungoverned: unregistered model, per-model override set to null, or the global kill switch
      // off. ResolveSeconds signals all three with -1 rather than null, so deriving a window here
      // would place expiry one second BEFORE the row's own business time — every row of every
      // ungoverned perspective would read as expired, and the switch that exists to STOP expiry
      // would instead hide the fleet.
      return query;
    }

    // The sliding cutoff is computed here rather than in the predicate so the comparison stays
    // sargable: `updated_at > cutoff` uses the updated_at index, where `updated_at + interval > now`
    // would force a sequential scan of the table.
    var now = DateTime.UtcNow;
    var slidingCutoff = now.AddSeconds(-ttlSeconds);

    // The ladder: an explicit expiry REPLACES the sliding rule (pinning a row); otherwise the rule
    // derives from business time. A NULL expiry therefore means "fall through to the rule", not
    // "never expires" — which is what governs rows written before the perspective declared
    // retention, with no backfill.
    return query.Where(r =>
      EF.Property<DateTime?>(r, "expires_at") != null
        ? EF.Property<DateTime?>(r, "expires_at") > now
        : r.UpdatedAt > slidingCutoff);
  }
}
