// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The set-based write holds the apply lock of every pair of a batch across the batch's write. The locks are taken in
/// one fixed order, stream id then perspective name, each pair once, and released in the reverse order, so two batches
/// that share pairs cannot each hold a lock the other is waiting for.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#locks</docs>
public class PerspectiveApplyLockSetTests {
  private static readonly Guid _low = Guid.Parse("00000000-0000-0000-0000-000000000001");
  private static readonly Guid _high = Guid.Parse("00000000-0000-0000-0000-000000000002");

  [Test]
  public async Task AcquireAsync_TakesThePairsByStreamIdThenPerspectiveName_EachOnceAsync() {
    var coordinator = new RecordingCoordinator();

    await using var held = await PerspectiveApplyLockSet.AcquireAsync(coordinator, [
      (_high, "b"), (_low, "b"), (_high, "a"), (_low, "B"), (_low, "b"), (_high, "b"),
    ]);

    await Assert.That(coordinator.Log).IsEquivalentTo([
      "acquire 1 B", "acquire 1 b", "acquire 2 a", "acquire 2 b",
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the order is stream id first, then perspective name compared ordinally, and a pair listed twice is taken once");
  }

  [Test]
  public async Task DisposeAsync_ReleasesInTheReverseOrder_OnceAsync() {
    var coordinator = new RecordingCoordinator();
    var held = await PerspectiveApplyLockSet.AcquireAsync(coordinator, [(_high, "a"), (_low, "a")]);

    await held.DisposeAsync();
    await held.DisposeAsync();

    await Assert.That(coordinator.Log).IsEquivalentTo([
      "acquire 1 a", "acquire 2 a", "release 2 a", "release 1 a",
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a second dispose must not release a lock some other batch has taken since");
  }

  [Test]
  public async Task AcquireAsync_WhenALockCannotBeTaken_ReleasesTheOnesItHeldAndThrowsAsync() {
    var coordinator = new RecordingCoordinator { FailOn = (_high, "a") };

    await Assert.That(async () => await PerspectiveApplyLockSet.AcquireAsync(coordinator, [(_high, "a"), (_low, "a"), (_low, "b")]))
      .Throws<OperationCanceledException>();

    await Assert.That(coordinator.Log).IsEquivalentTo([
      "acquire 1 a", "acquire 1 b", "release 1 b", "release 1 a",
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a batch that cannot take every lock must not keep the ones it took, or the pairs stay locked for good");
  }

  [Test]
  public async Task AcquireAsync_WithNoCoordinator_LocksNothingAsync() {
    var held = await PerspectiveApplyLockSet.AcquireAsync(null, [(_low, "a")]);

    await held.DisposeAsync();

    await Assert.That(held).IsNotNull()
      .Because("a host with no coordinator registered runs unlocked, as the per-pair path does");
  }

  [Test]
  public async Task AcquireAsync_NullPairs_ThrowsAsync() {
    await Assert.That(async () => await PerspectiveApplyLockSet.AcquireAsync(new RecordingCoordinator(), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Order_ComparesTheStreamIdBeforeThePerspectiveNameAsync() {
    var order = PerspectiveApplyLockSet.Order;

    await Assert.That(order.Compare((_low, "z"), (_high, "a"))).IsLessThan(0);
    await Assert.That(order.Compare((_high, "a"), (_low, "z"))).IsGreaterThan(0);
    await Assert.That(order.Compare((_low, "a"), (_low, "b"))).IsLessThan(0);
    await Assert.That(order.Compare((_low, "a"), (_low, "a"))).IsEqualTo(0);
  }

  private sealed class RecordingCoordinator : IPerspectiveApplyCoordinator {
    public List<string> Log { get; } = [];
    public (Guid StreamId, string PerspectiveName)? FailOn { get; init; }

    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (FailOn == (streamId, perspectiveName)) {
        throw new OperationCanceledException();
      }
      var name = $"{streamId.ToString()[^1]} {perspectiveName}";
      Log.Add("acquire " + name);
      return Task.FromResult<IAsyncDisposable>(new Release(Log, name));
    }

    private sealed class Release(List<string> log, string name) : IAsyncDisposable {
      public ValueTask DisposeAsync() {
        log.Add("release " + name);
        return ValueTask.CompletedTask;
      }
    }
  }
}
