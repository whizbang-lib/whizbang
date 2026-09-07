using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="TransportBatchCollector{T}"/>'s double-dispose guard.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportBatchCollector.cs</code-under-test>
[Category("Workers")]
public class TransportBatchCollectorCoverageTests {

  /// <summary>
  /// A second <c>DisposeAsync</c> must be a no-op: without the guard it would re-flush whatever
  /// pending messages a concurrent enqueue had added between the two calls, delivering the same
  /// message twice to the downstream inbox insert.
  /// </summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_OnlyFlushesOnceAsync() {
    var flushCount = 0;
    var flushedCounts = new List<int>();
    var collector = new TransportBatchCollector<int>(
      new TransportBatchOptions { BatchSize = 1000, SlideMs = 5000, MaxWaitMs = 10000 },
      batch => {
        Interlocked.Increment(ref flushCount);
        flushedCounts.Add(batch.Count);
        return Task.CompletedTask;
      });

    collector.Enqueue(1);
    collector.Enqueue(2);

    await collector.DisposeAsync();
    await collector.DisposeAsync();

    await Assert.That(flushCount).IsEqualTo(1)
      .Because("a second dispose must not re-flush the batch the first dispose already delivered");
    await Assert.That(flushedCounts[0]).IsEqualTo(2);
  }
}
