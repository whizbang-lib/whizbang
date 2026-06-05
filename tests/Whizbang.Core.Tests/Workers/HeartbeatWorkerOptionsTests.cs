using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 5 of zero-idle-polling — regression-locks
/// <see cref="HeartbeatWorkerOptions"/>'s defaults.
///
/// <para>
/// The default <see cref="HeartbeatWorkerOptions.IntervalSeconds"/> moves
/// from 5 s (pre-Slice 5) to 30 s in Slice 5. The change is safe because
/// Slice 2's LISTEN-connection liveness signal (visible via
/// <c>wh_live_instances</c>'s <c>listen_alive</c> column) plus Slice 2b's
/// claim_orphaned_* updates give orphan detection two independent
/// freshness signals — the timer-based heartbeat row AND the TCP-fresh
/// LISTEN connection in <c>pg_stat_activity</c>.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class HeartbeatWorkerOptionsTests {

  [Test]
  public async Task IntervalSeconds_Default_IsThirtySecondsAsync() {
    var options = new HeartbeatWorkerOptions();

    await Assert.That(options.IntervalSeconds).IsEqualTo(30)
      .Because("Slice 5 of zero-idle-polling. 30 s heartbeat cadence is safe because Slice 2's LISTEN-as-liveness signal in claim_orphaned_outbox/inbox/perspective_events covers the gap between heartbeat row updates — orphan detection has two independent freshness signals.");
  }

  [Test]
  public async Task Enabled_DefaultIsTrueAsync() {
    var options = new HeartbeatWorkerOptions();

    await Assert.That(options.Enabled).IsTrue();
  }
}
