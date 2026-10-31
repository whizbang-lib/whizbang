// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A bucket repair cannot change (#1790): once a redelivery has answered with events that were all already stored
/// under their origin, the ledger stops granting repair for the bucket and stops re-reporting it on the cooldown,
/// until its signature changes or it heals.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IIntegrityRepairLedger.cs</code-under-test>
/// <docs>resilience/stream-integrity#unhealable-buckets</docs>
[Category("Messaging")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S6966:Awaitable method should be used", Justification = "The synchronous API is the unit under test.")]
public class IntegrityRepairLedgerUnhealableTests {
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _backoff = TimeSpan.FromMinutes(5);
  private static readonly DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
  private const int MAX_ATTEMPTS = 3;

  private static IntegrityRepairLedger.DivergenceKey _key() => new(
    TrackedGuid.New().Value, "tenant-a", "Contracts.TypeX", TrackedGuid.New().Value);

  private static (IntegrityRepairLedger Ledger, IntegrityRepairLedger.DivergenceKey Key) _tracked() {
    var ledger = new IntegrityRepairLedger();
    var key = _key();
    _ = ledger.TryBeginReport(key, 11, 21, 0, 0, _now, _cooldown);
    return (ledger, key);
  }

  /// <summary>An implementer that only provides the members every ledger must.</summary>
  private sealed class MinimalLedger : IIntegrityRepairLedger {
    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi,
      long localLo, long localHi, DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(true);
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
      TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  [Test]
  public async Task MarkUnhealable_ATrackedBucket_IsReturnedOnceAsync() {
    var (ledger, key) = _tracked();

    var first = await ledger.MarkUnhealableBatchAsync([key], _now);
    var second = await ledger.MarkUnhealableBatchAsync([key], _now.AddMinutes(1));

    await Assert.That(first).IsEquivalentTo([key]);
    await Assert.That(second).IsEmpty().Because("a bucket already marked is reported once, not on every redelivery");
  }

  [Test]
  public async Task MarkUnhealable_ABucketTheLedgerDoesNotTrack_IsNeitherMarkedNorAddedAsync() {
    var ledger = new IntegrityRepairLedger();

    var marked = await ledger.MarkUnhealableBatchAsync([_key()], _now);

    await Assert.That(marked).IsEmpty();
    await Assert.That(ledger.Count).IsEqualTo(0)
      .Because("a redelivery nobody asked this ledger for leaves nothing behind");
  }

  [Test]
  public async Task TryBeginRepair_AnUnhealableBucket_IsRefusedEvenPastTheTerminalCadenceAsync() {
    var (ledger, key) = _tracked();
    await Assert.That(ledger.TryBeginRepair(key, _now, _backoff, MAX_ATTEMPTS)).IsTrue();
    _ = await ledger.MarkUnhealableBatchAsync([key], _now);

    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(30), _backoff, MAX_ATTEMPTS)).IsFalse()
      .Because("a redelivery already showed the events are present; asking again ships them again");
  }

  [Test]
  public async Task TryBeginReport_AnUnhealableBucketUnchanged_IsNotReReportedOnTheCooldownAsync() {
    var (ledger, key) = _tracked();
    _ = await ledger.MarkUnhealableBatchAsync([key], _now);

    await Assert.That(ledger.TryBeginReport(key, 11, 21, 0, 0, _now.AddDays(1), _cooldown)).IsFalse()
      .Because("the bucket was reported once when repair gave up on it");
  }

  [Test]
  public async Task TryBeginReport_ASignatureChange_LiftsTheMarkAndRepairResumesAsync() {
    var (ledger, key) = _tracked();
    _ = await ledger.MarkUnhealableBatchAsync([key], _now);

    var reported = ledger.TryBeginReport(key, 11, 21, 5, 6, _now.AddMinutes(1), _cooldown);

    await Assert.That(reported).IsTrue().Because("a moved digest is a fresh incident");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddMinutes(1), _backoff, MAX_ATTEMPTS)).IsTrue();
    await Assert.That(await ledger.MarkUnhealableBatchAsync([key], _now.AddMinutes(2))).IsEquivalentTo([key])
      .Because("the next unchanged redelivery is reported again, as a new incident");
  }

  [Test]
  public async Task MarkHealed_ForgetsTheMarkAsync() {
    var (ledger, key) = _tracked();
    _ = await ledger.MarkUnhealableBatchAsync([key], _now);

    ledger.MarkHealed(key);

    await Assert.That(ledger.TryBeginRepair(key, _now, _backoff, MAX_ATTEMPTS)).IsTrue()
      .Because("a later divergence of a healed bucket is a new incident");
  }

  [Test]
  public async Task MarkUnhealable_DefaultImplementation_RemembersNothingAsync() {
    IIntegrityRepairLedger ledger = new MinimalLedger();

    var marked = await ledger.MarkUnhealableBatchAsync([_key()], _now);

    await Assert.That(marked).IsEmpty();
  }
}
