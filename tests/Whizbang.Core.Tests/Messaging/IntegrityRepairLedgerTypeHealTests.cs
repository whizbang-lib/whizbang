// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Phantom heal: a comparison that proves the whole history of a type complete proves every stream of it complete, so
/// the ledger forgets the type's type lane and every per-stream bucket of it in one step. A bucket of another tenant,
/// type or origin is kept.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IIntegrityRepairLedger.cs</code-under-test>
/// <docs>resilience/stream-integrity#a-matching-type-heals-its-type-lane</docs>
[Category("Core")]
[Category("Messaging")]
public class IntegrityRepairLedgerTypeHealTests {
  private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

  private static async Task _seedAsync(IntegrityRepairLedger ledger, IntegrityRepairLedger.DivergenceKey key) =>
    _ = await ledger.TryBeginReportAsync(key, 1, 2, 0, 0, _t0, TimeSpan.FromMinutes(60));

  [Test]
  public async Task MarkTypesHealed_ForgetsTheTypeLaneAndEveryStreamOfTheType_AndKeepsTheRestAsync() {
    var ledger = new IntegrityRepairLedger();
    var origin = TrackedGuid.New().Value;
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", Guid.Empty);
    var streams = Enumerable.Range(0, 3).Select(_ => lane with { StreamId = TrackedGuid.New().Value }).ToList();
    var otherTenant = streams[0] with { TenantScope = "tenant-b" };
    var otherType = streams[0] with { EventType = "Contracts.TypeY" };
    var otherOrigin = streams[0] with { OriginServiceId = TrackedGuid.New().Value };
    foreach (var key in streams.Append(lane).Append(otherTenant).Append(otherType).Append(otherOrigin)) {
      await _seedAsync(ledger, key);
    }

    var ages = await ledger.MarkTypesHealedWithAgesAsync([streams[1]]);

    await Assert.That(ages.Count).IsEqualTo(4).Because("the type lane and its three streams, each with its age");
    await Assert.That(ledger.Count).IsEqualTo(3).Because("another tenant's, type's and origin's buckets are not proven");
  }

  [Test]
  public async Task MarkTypesHealed_ByDefault_ForgetsTheTypeLanesOnlyAsync() {
    var inner = new IntegrityRepairLedger();
    IIntegrityRepairLedger ledger = new DefaultsOnly(inner);
    var origin = TrackedGuid.New().Value;
    var lane = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", Guid.Empty);
    var stream = lane with { StreamId = TrackedGuid.New().Value };
    await _seedAsync(inner, lane);
    await _seedAsync(inner, stream);

    _ = await ledger.MarkTypesHealedWithAgesAsync([stream]);

    await Assert.That(inner.Count).IsEqualTo(1)
      .Because("a ledger that cannot enumerate a type's streams forgets the type lane the key names, and nothing it cannot find");
  }

  [Test]
  public async Task DivergenceKey_TypeLane_KeepsTheBucketAtTheTypeLevelAsync() {
    var origin = TrackedGuid.New().Value;
    var stream = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", TrackedGuid.New().Value);

    await Assert.That(stream.ToTypeLane())
      .IsEqualTo(new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.TypeX", Guid.Empty));
  }

  [Test]
  public async Task DivergenceKeys_CompareStructurally_WithoutRecursingAsync() {
    // A key that exposed its type lane as a property of its own type made every structural walk of it endless: an
    // equivalence assertion over a list of keys overflowed the stack and took the whole test process down with it.
    var origin = TrackedGuid.New().Value;
    IntegrityRepairLedger.DivergenceKey[] keys = [
      new(origin, null, "Contracts.TypeX", TrackedGuid.New().Value),
      new(origin, "tenant-a", "Contracts.TypeY", Guid.Empty),
    ];

    await Assert.That(keys.Reverse().ToArray()).IsEquivalentTo(keys);
  }

  /// <summary>A ledger that implements the required members only, delegating to the in-memory one, so every optional
  /// batch member keeps its interface default.</summary>
  private sealed class DefaultsOnly(IntegrityRepairLedger inner) : IIntegrityRepairLedger {
    public ValueTask<bool> TryBeginReportAsync(
        IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
        DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
      inner.TryBeginReportAsync(key, originLo, originHi, localLo, localHi, now, cooldown, cancellationToken);

    public ValueTask<bool> TryBeginRepairAsync(
        IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        CancellationToken cancellationToken = default) =>
      inner.TryBeginRepairAsync(key, now, baseBackoff, maxAttempts, cancellationToken);

    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      inner.MarkHealedAsync(key, cancellationToken);
  }
}
