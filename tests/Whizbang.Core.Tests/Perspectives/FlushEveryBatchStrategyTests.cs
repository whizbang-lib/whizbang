// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>The default flush policy of set-based writes (#2246): every batch's rows are written at once, through the sink given.</summary>
/// <docs>fundamentals/perspectives/write-modes#flush-policy</docs>
public class FlushEveryBatchStrategyTests {
  [Test]
  public async Task FlushAsync_WritesTheBatchThroughTheSinkAndReturnsItsOutcomesAsync() {
    var sink = new RecordingSink();
    IReadOnlyList<PendingPerspectiveWrite> writes = [PendingPerspectiveWrite.Purge("wh_per_a", Guid.NewGuid())];
    using var cts = new CancellationTokenSource();

    var outcomes = await new FlushEveryBatchStrategy().FlushAsync(sink, writes, cts.Token);

    await Assert.That(sink.Written).IsSameReferenceAs(writes);
    await Assert.That(sink.Token).IsEqualTo(cts.Token);
    await Assert.That(outcomes).IsSameReferenceAs(sink.Outcomes);
  }

  [Test]
  public async Task FlushAsync_RequiresItsArgumentsAsync() {
    var policy = new FlushEveryBatchStrategy();

    await Assert.That(async () => await policy.FlushAsync(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(async () => await policy.FlushAsync(new RecordingSink(), null!)).Throws<ArgumentNullException>();
  }

  private sealed class RecordingSink : IPerspectiveWriteSink {
    public IReadOnlyList<PendingPerspectiveWrite>? Written { get; private set; }
    public CancellationToken Token { get; private set; }
    public IReadOnlyList<PerspectiveWriteOutcome> Outcomes { get; } = [PerspectiveWriteOutcome.Written];

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Written = writes;
      Token = cancellationToken;
      return Task.FromResult(Outcomes);
    }
  }
}
