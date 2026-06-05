using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

#pragma warning disable CA1707
#pragma warning disable IDE1006
#pragma warning disable CS0618 // Slice 1's NotifyHealthyPollingInterval is marked [Obsolete] in Slice 4; these tests intentionally exercise the legacy knob's semantics until the stamper backstop loop fully retires.

/// <summary>
/// Slice 1 of the zero-idle-polling architecture — locks the gate-aware polling
/// cadence on <see cref="PgCommitOrderStamperWorker"/>. Mirror of the
/// <see cref="Whizbang.Core.Workers.ClaimWorker"/> pattern: when the
/// <see cref="INotifySignalingGate"/> reports NOTIFY healthy AND a relaxed
/// interval is configured, the wait-loop uses the relaxed interval; otherwise it
/// falls back to the tight <see cref="CommitOrderStamperOptions.PollingInterval"/>
/// correctness floor.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>Gate healthy + relaxed interval set → relaxed interval used.</description></item>
/// <item><description>Gate broken → floor interval used, regardless of relaxed setting.</description></item>
/// <item><description>Gate not wired (null) → floor interval used (preserves legacy behavior).</description></item>
/// <item><description>Relaxed value ≤ floor → floor used (guards against operator misconfiguration).</description></item>
/// <item><description>Relaxed value null → floor used (operator opt-out path).</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence#polling-cadence</docs>
public class PgCommitOrderStamperWorkerPollingIntervalTests {

  [Test]
  public async Task ComputeEffectivePollingInterval_NotifyHealthy_UsesRelaxedIntervalAsync() {
    var options = new CommitOrderStamperOptions {
      PollingInterval = TimeSpan.FromMilliseconds(250),
      NotifyHealthyPollingInterval = TimeSpan.FromSeconds(30),
    };

    var result = PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(options, gateIsAvailable: true);

    await Assert.That(result).IsEqualTo(TimeSpan.FromSeconds(30))
      .Because("When the gate reports NOTIFY healthy, the relaxed interval owns the cadence — NOTIFY-on-wh_committed drives sub-ms wake; the periodic poll is only a backstop.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_NotifyBroken_UsesFloorIntervalAsync() {
    var options = new CommitOrderStamperOptions {
      PollingInterval = TimeSpan.FromMilliseconds(250),
      NotifyHealthyPollingInterval = TimeSpan.FromSeconds(30),
    };

    var result = PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(options, gateIsAvailable: false);

    await Assert.That(result).IsEqualTo(TimeSpan.FromMilliseconds(250))
      .Because("When the gate reports NOTIFY broken, missed-NOTIFY recovery is the dominant failure mode; the floor cadence must remain tight.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_NoGateWired_UsesFloorIntervalAsync() {
    var options = new CommitOrderStamperOptions {
      PollingInterval = TimeSpan.FromMilliseconds(250),
      NotifyHealthyPollingInterval = TimeSpan.FromSeconds(30),
    };

    var result = PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(options, gateIsAvailable: null);

    await Assert.That(result).IsEqualTo(TimeSpan.FromMilliseconds(250))
      .Because("When the gate isn't wired (null), preserve pre-Slice-1 behavior: tight floor polling, no assumption about NOTIFY availability.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_RelaxedSmallerThanFloor_UsesFloorAsync() {
    // Operator misconfiguration: NotifyHealthyPollingInterval set to a value
    // smaller than PollingInterval. The intent of the knob is "RELAX when healthy" —
    // if the operator-set value would TIGHTEN, we ignore it and stick with the floor.
    var options = new CommitOrderStamperOptions {
      PollingInterval = TimeSpan.FromMilliseconds(500),
      NotifyHealthyPollingInterval = TimeSpan.FromMilliseconds(100),
    };

    var result = PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(options, gateIsAvailable: true);

    await Assert.That(result).IsEqualTo(TimeSpan.FromMilliseconds(500))
      .Because("The relaxed knob only relaxes — never tightens. Operator-set value below floor is treated as a no-op.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_RelaxedNull_UsesFloorAsync() {
    var options = new CommitOrderStamperOptions {
      PollingInterval = TimeSpan.FromMilliseconds(250),
      NotifyHealthyPollingInterval = null,
    };

    var result = PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(options, gateIsAvailable: true);

    await Assert.That(result).IsEqualTo(TimeSpan.FromMilliseconds(250))
      .Because("Operator opt-out: NotifyHealthyPollingInterval=null restores pre-Slice-1 tight-polling-always behavior.");
  }

  [Test]
  public async Task ComputeEffectivePollingInterval_NullOptions_ThrowsAsync() {
    await Assert.That(() => PgCommitOrderStamperWorker.ComputeEffectivePollingInterval(null!, gateIsAvailable: true))
      .Throws<ArgumentNullException>()
      .Because("Defensive: null options is a programming bug, not a runtime condition the caller should silently absorb.");
  }
}
