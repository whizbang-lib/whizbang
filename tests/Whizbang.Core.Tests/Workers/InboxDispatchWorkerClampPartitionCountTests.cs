using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 3 of plans/throughput-optimization.md — InboxDispatchWorker's
/// internal-consumer fan-out (<see cref="InboxDispatchWorkerOptions.MaxConcurrentDispatch"/>)
/// cannot exceed the gate's <see cref="Messaging.WorkCoordinatorGate.MaxConcurrent"/>
/// because every dispatched message routes through a gated <c>IWorkCoordinator</c>
/// call upstream. Configuring more dispatch consumers than the gate has slots
/// just means consumers idle in the gate's queue — wasted ThreadPool fan-out
/// with no throughput gain.
/// </summary>
public class InboxDispatchWorkerClampPartitionCountTests {

  /// <summary>
  /// Baseline: when no gate is registered, the configured value passes through
  /// unchanged (subject only to the existing floor-at-1 invariant from the
  /// pre-slice-3 code).
  /// </summary>
  [Test]
  public async Task ClampPartitionCount_NoGate_ReturnsConfiguredValueAsync() {
    var clamped = InboxDispatchWorker.ClampPartitionCount(configured: 8, gateMaxConcurrent: null);
    await Assert.That(clamped).IsEqualTo(8)
      .Because("Without a gate to coordinate against, the worker has no information about an upstream cap — preserve the configured value verbatim.");
  }

  /// <summary>
  /// Gate present, configured value within gate: no clamping.
  /// </summary>
  [Test]
  public async Task ClampPartitionCount_ConfiguredFitsGate_ReturnsConfiguredValueAsync() {
    var clamped = InboxDispatchWorker.ClampPartitionCount(configured: 8, gateMaxConcurrent: 50);
    await Assert.That(clamped).IsEqualTo(8)
      .Because("Configured 8 ≤ gate 50 — nothing to clamp.");
  }

  /// <summary>
  /// The actual slice intent: configured value exceeds gate cap → clamp to gate.
  /// </summary>
  [Test]
  public async Task ClampPartitionCount_ConfiguredExceedsGate_ClampsToGateAsync() {
    var clamped = InboxDispatchWorker.ClampPartitionCount(configured: 100, gateMaxConcurrent: 25);
    await Assert.That(clamped).IsEqualTo(25)
      .Because("Configured 100 spawns 100 dispatch consumers but only 25 can hold a gate slot at once. The extra 75 do nothing but burn ThreadPool waiting — clamp to gate cap so consumer count matches actual concurrency.");
  }

  /// <summary>
  /// Floor at 1 — preserves the pre-slice-3 invariant that the worker always
  /// has at least one consumer queue running. A 0-or-negative configured value
  /// (or a 0-or-negative gate, theoretically) must not produce 0 partitions.
  /// </summary>
  [Test]
  public async Task ClampPartitionCount_ZeroConfigured_FloorsAtOneAsync() {
    var clamped = InboxDispatchWorker.ClampPartitionCount(configured: 0, gateMaxConcurrent: 50);
    await Assert.That(clamped).IsEqualTo(1)
      .Because("Existing invariant from InboxDispatchWorker.ExecuteAsync line 154 — partitionCount = Math.Max(1, ...). Slice 3 must preserve this floor.");
  }

  /// <summary>
  /// Disabled gate (gateMaxConcurrent &lt;= 0) is semantically equivalent to
  /// "no gate" — pass configured through.
  /// </summary>
  [Test]
  public async Task ClampPartitionCount_DisabledGate_TreatsLikeNoGateAsync() {
    var clamped = InboxDispatchWorker.ClampPartitionCount(configured: 8, gateMaxConcurrent: 0);
    await Assert.That(clamped).IsEqualTo(8)
      .Because("Gate with MaxConcurrent=0 is the documented 'disabled' state per WorkCoordinatorGate XML doc. A disabled gate has no upstream cap to clamp against.");
  }
}
