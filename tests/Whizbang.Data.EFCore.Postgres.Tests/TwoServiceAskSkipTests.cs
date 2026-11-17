// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2349 across two services (<see cref="TwoServiceHarness"/>): the consumer's real audit worker against the origin's
/// real answer receptor, over two real stores. An idle pair is asked once per maximum skip instead of once per audit,
/// and an event that settles on the origin after a match, and never reached the consumer, is still found by the bounded
/// ask, though nothing the consumer remembers moved.
/// </summary>
/// <docs>resilience/stream-integrity#unchanged-origins-are-not-asked</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServiceAskSkipTests : EFCoreTestBase {
  private const string DURABLE = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string PAYLOAD = """{"k":"v"}""";
  private static readonly int[] _theFirstAskAndEachBound = [0, 60, 120];
  private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

  private static StreamIntegrityOptions _audit() => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    AuditSettleWindowMinutes = 0,
    MaxAskSkipMinutes = 60,
    RepairDrainEnabled = false,
  };

  private static TwoServiceEmit _event() => new((Guid)TrackedGuid.New(), DURABLE, 0, null, PAYLOAD);

  [Test]
  public async Task AnIdlePair_IsAskedOncePerMaximumSkip_NotOncePerAuditAsync() {
    var clock = new FakeTimeProvider(_t0);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [], [DURABLE], clock);
    await h.DeliverAsync(await h.EmitAsync(_event(), _event(), _event()));
    await h.CheckpointAsync();

    // An audit every five minutes for two hours (25 cycles), with a heartbeat checkpoint before each, nothing emitted.
    var asksAt = new List<int>();
    for (var minute = 0; minute <= 120; minute += 5) {
      clock.SetUtcNow(_t0.AddMinutes(minute));
      await h.CheckpointAsync();
      var before = h.ManifestAsks();
      await h.RunConsumerAuditAsync(_audit());
      if (h.ManifestAsks() > before) {
        asksAt.Add(minute);
      }
    }

    await Assert.That(await h.SealAsync()).IsGreaterThan(0L).Because("positive control: the first ask verified and sealed the window");
    await Assert.That(asksAt).IsEquivalentTo(_theFirstAskAndEachBound)
      .Because("after the first full match nothing moved, so the pair is asked only when the hour bound comes round");
    await Assert.That(h.Tally().Reports).IsEqualTo(0);
  }

  [Test]
  public async Task AnEventThatSettlesAfterTheMatch_AndNeverArrived_IsFoundByTheBoundedAskAsync() {
    var clock = new FakeTimeProvider(_t0);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [], [DURABLE], clock);
    await h.DeliverAsync(await h.EmitAsync(_event(), _event()));
    // A third event the transport lost, still inside the origin's settle window when the pair matches.
    var lost = (await h.EmitAsync(_event()))[0];
    await TwoServiceHarness.ExecuteAsync(h.Origin,
      "UPDATE wh_event_store SET created_at = NOW() + INTERVAL '1 day' WHERE event_id = @id", ("id", lost));
    await h.CheckpointAsync();

    await h.RunConsumerAuditAsync(_audit());
    var sealAtMatch = await h.SealAsync();
    await Assert.That(sealAtMatch).IsGreaterThan(0L).Because("the settled part matched and sealed");

    // The lost event settles. No checkpoint position moves (it was announced before the match), nothing arrives here.
    await TwoServiceHarness.ExecuteAsync(h.Origin,
      "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '1 minute' WHERE event_id = @id", ("id", lost));
    for (var minute = 5; minute < 60; minute += 5) {
      clock.SetUtcNow(_t0.AddMinutes(minute));
      await h.CheckpointAsync();
      await h.RunConsumerAuditAsync(_audit());
    }
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsFalse()
      .Because("nothing the consumer remembers moved, so every audit inside the hour skipped the ask");
    await Assert.That(h.Tally().RepairRequests).IsEqualTo(0);

    clock.SetUtcNow(_t0.AddMinutes(60));
    await h.CheckpointAsync();
    await h.RunConsumerAuditAsync(_audit());

    await Assert.That(h.Tally().RepairRequests).IsGreaterThan(0)
      .Because("the bounded ask compared the window above the seal, which now holds the settled event the consumer lacks");
    await Assert.That(await h.ConsumerHoldsAsync(lost)).IsTrue().Because("the repair redelivered it");
  }
}
