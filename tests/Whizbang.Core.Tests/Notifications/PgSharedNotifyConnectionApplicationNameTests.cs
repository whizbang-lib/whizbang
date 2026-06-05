using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 2 of zero-idle-polling — locks the application_name format that
/// <see cref="PgSharedNotifyConnection"/> sets on the per-pod LISTEN connection.
/// The <c>wh_live_instances</c> view (migration 052) joins
/// <c>wh_service_instances</c> against <c>pg_stat_activity</c> on this exact
/// format; if it ever drifts between writer and reader, the view returns false
/// negatives (rows that look orphan-eligible when their owning pod is actually
/// alive). This test class is the single regression lock for that contract.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#listen-as-heartbeat</docs>
public class PgSharedNotifyConnectionApplicationNameTests {

  [Test]
  public async Task ComputeApplicationName_HasWhizbangPrefixAsync() {
    var id = Guid.Parse("019e93d6-b0ae-764a-ab5f-055a52222687");

    var name = PgSharedNotifyConnection.ComputeApplicationName(id);

    await Assert.That(name).StartsWith("whizbang-")
      .Because("The prefix is the discriminator that lets wh_live_instances filter Whizbang connections from any other application connections on the same Postgres backend.");
  }

  [Test]
  public async Task ComputeApplicationName_FormatsAsExpectedAsync() {
    var id = Guid.Parse("019e93d6-b0ae-764a-ab5f-055a52222687");

    var name = PgSharedNotifyConnection.ComputeApplicationName(id);

    await Assert.That(name).IsEqualTo("whizbang-019e93d6-b0ae-764a-ab5f-055a52222687")
      .Because("The view's join predicate ('whizbang-' || instance_id::text) requires lowercased dash-separated GUID — no curly braces, no uppercase, no padding.");
  }

  [Test]
  public async Task ComputeApplicationName_StableForSameInstanceIdAsync() {
    var id = Guid.NewGuid();

    var first = PgSharedNotifyConnection.ComputeApplicationName(id);
    var second = PgSharedNotifyConnection.ComputeApplicationName(id);

    await Assert.That(first).IsEqualTo(second)
      .Because("Pure function: same instance_id always yields the same application_name. No clock-dependence, no random padding, no per-call salt.");
  }

  [Test]
  public async Task ComputeApplicationName_DifferentInstanceIdYieldsDifferentNameAsync() {
    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();

    var first = PgSharedNotifyConnection.ComputeApplicationName(firstId);
    var second = PgSharedNotifyConnection.ComputeApplicationName(secondId);

    await Assert.That(first).IsNotEqualTo(second)
      .Because("Each pod's instance_id must produce a unique application_name; otherwise pg_stat_activity can't distinguish liveness signals between pods sharing a service DB.");
  }

  [Test]
  public async Task ComputeApplicationName_WithinPostgresNameLimitAsync() {
    // Postgres caps application_name at NAMEDATALEN-1 = 63 chars by default.
    // 'whizbang-' (9) + 36 char GUID = 45. Headroom for future format tweaks.
    var id = Guid.NewGuid();

    var name = PgSharedNotifyConnection.ComputeApplicationName(id);

    await Assert.That(name.Length).IsLessThanOrEqualTo(63)
      .Because("Postgres silently truncates application_name beyond NAMEDATALEN-1 = 63 chars; the view's join would fail if the writer wrote a longer name and the reader tried to match the untruncated form.");
  }
}
