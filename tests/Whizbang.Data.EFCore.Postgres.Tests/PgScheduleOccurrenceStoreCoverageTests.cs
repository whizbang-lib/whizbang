#pragma warning disable CA1707

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgScheduleOccurrenceStore"/>'s "no connection is configured" paths —
/// <see cref="PgScheduleOccurrenceStoreIntegrationTests"/> exercises the DB-backed behavior
/// against a real Postgres container. Every case here builds the store with no
/// <c>DirectConnectionString</c>, no <c>ConnectionStringKey</c>, no fallback, and no data source,
/// so <c>_openAsync</c>'s <c>NotificationConnectionPlan.IsAvailable</c> check is false before a
/// socket is ever touched — no database anywhere in this file.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
[Category("Shard1")]
public class PgScheduleOccurrenceStoreCoverageTests {
  private static PgScheduleOccurrenceStore _storeWithNoConnectionConfigured() {
    var options = new WhizbangNotificationOptions();
    var configuration = new ConfigurationBuilder().Build();
    return new PgScheduleOccurrenceStore(
      Options.Create(options), configuration, NullLogger<PgScheduleOccurrenceStore>.Instance);
  }

  // The pre-fire gate defers the SAME in-flight occurrence rather than dropping it. With no
  // connection configured there is nothing to defer against; if this threw instead of no-op'ing,
  // every caller in a minimal (notification-store-less) setup would crash on a path meant to be
  // optional infrastructure.
  [Test]
  public async Task DeferAsync_WithNoConnectionConfigured_CompletesWithoutThrowingAsync() {
    var store = _storeWithNoConnectionConfigured();

    await store.DeferAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
  }

  // LogRunAsync records the pre-fire gate's outcome for operator visibility. With no connection
  // configured there is no wh_schedule_runs table to write to; it must no-op rather than throw,
  // matching every other optional-infrastructure path on this store.
  [Test]
  public async Task LogRunAsync_WithNoConnectionConfigured_CompletesWithoutThrowingAsync() {
    var store = _storeWithNoConnectionConfigured();

    await store.LogRunAsync(Guid.NewGuid(), Guid.NewGuid(), status: 1, note: "coverage");
  }

  // RefreshAuthorityClaimsAsync writes back the snapshot every subsequent fire reads. A dropped
  // occurrence here is silent — with no connection configured it must no-op, not throw, so a
  // minimal setup that never configured schedule persistence doesn't crash the pre-fire hook.
  [Test]
  public async Task RefreshAuthorityClaimsAsync_WithNoConnectionConfigured_CompletesWithoutThrowingAsync() {
    var store = _storeWithNoConnectionConfigured();

    await store.RefreshAuthorityClaimsAsync(Guid.NewGuid(), """{"roles":["billing"]}""");
  }

  // RefreshAuthorityClaimsAsync validates its own input before ever touching a connection — blank
  // claims JSON would corrupt the authority snapshot every subsequent fire reads, so it must fail
  // fast at the call site rather than write garbage.
  [Test]
  public async Task RefreshAuthorityClaimsAsync_BlankClaimsJson_ThrowsArgumentAsync() {
    var store = _storeWithNoConnectionConfigured();

    await Assert.That(() => store.RefreshAuthorityClaimsAsync(Guid.NewGuid(), "   "))
      .Throws<ArgumentException>();
  }
}
