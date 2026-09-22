using System.Threading;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Coverage round 23 tail: <see cref="IntervalUnitOfWorkStrategy.DisposeAsync"/>'s idempotent-dispose
/// guard. No existing test calls <c>DisposeAsync</c> more than once, so the early-return for an
/// already-disposed instance has never run.
/// </summary>
[Category("Messaging")]
public class IntervalUnitOfWorkStrategyCoverageTests {

  /// <summary>
  /// Operator impact: hosts routinely call DisposeAsync twice (an explicit shutdown call
  /// followed by a DI container's own disposal, or a repeated shutdown signal). Without this
  /// guard the second call re-cancels an already-disposed CancellationTokenSource and re-enters
  /// an already-disposed SemaphoreSlim, throwing ObjectDisposedException out of a shutdown path
  /// that must complete cleanly, and would flush the same trailing unit a second time.
  /// </summary>
  [Test]
  public async Task DisposeAsync_CalledTwice_SecondCallIsANoOpAsync() {
    var flushCount = 0;
    var strategy = new IntervalUnitOfWorkStrategy(TimeSpan.FromMinutes(10));
    strategy.OnFlushRequested += (_, _) => {
      Interlocked.Increment(ref flushCount);
      return Task.CompletedTask;
    };
    await strategy.QueueMessageAsync(new object());

    await strategy.DisposeAsync();
    await strategy.DisposeAsync();

    await Assert.That(flushCount).IsEqualTo(1)
      .Because("the first DisposeAsync flushes the one queued unit; a second call must be a "
             + "silent no-op, not a second flush attempt or a thrown ObjectDisposedException");
  }
}
