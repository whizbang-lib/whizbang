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
    if (PerspectiveTtlRegistry.ResolveSeconds(typeof(TModel)) < 0) {
      return query;
    }
    return query.Where(r =>
      EF.Property<DateTime?>(r, "expires_at") == null
      || EF.Property<DateTime?>(r, "expires_at") > DateTime.UtcNow);
  }
}
