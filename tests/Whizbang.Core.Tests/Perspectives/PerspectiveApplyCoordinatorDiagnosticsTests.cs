using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// <para>Locks the slow-acquisition diagnostic on <see cref="PerspectiveApplyCoordinator"/>
/// (#679). The per-key apply lock is awaited with no timeout; when a holder leaks (an
/// abandoned apply task that never releases), every later apply for that key parks
/// silently and forever — a live production wedge showed six hours of total perspective
/// silence with leases renewing throughout. The coordinator cannot know WHY the holder is
/// stuck, but it can refuse to be silent about the wait: a periodic WARN naming the key
/// turns an invisible wedge into a diagnosable one.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveApplyCoordinator.cs</code-under-test>
[Category("Shard2")]
public sealed class PerspectiveApplyCoordinatorDiagnosticsTests {

  /// <summary>Signals on the first Warning so tests await the diagnostic instead of polling.</summary>
  private sealed class WarnSignalLogger : ILogger<PerspectiveApplyCoordinator> {
    public TaskCompletionSource<string> FirstWarning { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int WarningCount;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Interlocked.Increment(ref WarningCount);
        FirstWarning.TrySetResult(formatter(state, exception));
      }
    }
  }

  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(90);

  [Test]
  public async Task SlowAcquisition_WarnsWithTheKey_AndStillAcquiresWhenFreedAsync() {
    var logger = new WarnSignalLogger();
    var coordinator = new PerspectiveApplyCoordinator(logger) {
      WarnInterval = TimeSpan.FromMilliseconds(50),
    };
    var streamId = Guid.NewGuid();

    var holder = await coordinator.AcquireAsync(streamId, "Orders.Projection");
    var waiterTask = coordinator.AcquireAsync(streamId, "Orders.Projection");

    var warning = await logger.FirstWarning.Task.WaitAsync(_timeout);
    await Assert.That(warning).Contains("Orders.Projection")
      .Because("the WARN must name the wedged key — 'something is stuck' is not actionable; "
             + "'THIS perspective on THIS stream is stuck' is");
    await Assert.That(warning).Contains(streamId.ToString());
    await Assert.That(waiterTask.IsCompleted).IsFalse()
      .Because("the diagnostic reports the wait; it must not abandon or fail it");

    await holder.DisposeAsync();
    var handle = await waiterTask.WaitAsync(_timeout);
    await handle.DisposeAsync();
  }

  [Test]
  public async Task FastAcquisition_NeverWarnsAsync() {
    var logger = new WarnSignalLogger();
    var coordinator = new PerspectiveApplyCoordinator(logger) {
      WarnInterval = TimeSpan.FromMilliseconds(50),
    };

    var handle = await coordinator.AcquireAsync(Guid.NewGuid(), "Orders.Projection");
    await handle.DisposeAsync();

    await Assert.That(logger.WarningCount).IsEqualTo(0)
      .Because("the uncontended fast path is the overwhelmingly common case and must stay "
             + "silent and allocation-light");
  }

  [Test]
  public async Task WaitingAcquisition_CancellationStillHonoredAsync() {
    var logger = new WarnSignalLogger();
    var coordinator = new PerspectiveApplyCoordinator(logger) {
      WarnInterval = TimeSpan.FromMilliseconds(50),
    };
    var streamId = Guid.NewGuid();

    var holder = await coordinator.AcquireAsync(streamId, "Orders.Projection");
    using var cts = new CancellationTokenSource();
    var waiterTask = coordinator.AcquireAsync(streamId, "Orders.Projection", cts.Token);
    await logger.FirstWarning.Task.WaitAsync(_timeout);

    cts.Cancel();

    await Assert.That(async () => await waiterTask).Throws<OperationCanceledException>()
      .Because("the lease-tied cancellation path is the ONLY thing that can free a consumer "
             + "parked behind a leaked holder today — the diagnostic must not swallow it");
    await holder.DisposeAsync();
  }

  [Test]
  public async Task Coordinator_SerializesSameKey_AllowsDifferentKeysAsync() {
    // Behavior guard: the diagnostic must not change the locking semantics.
    var logger = new WarnSignalLogger();
    var coordinator = new PerspectiveApplyCoordinator(logger);
    var streamId = Guid.NewGuid();

    var a = await coordinator.AcquireAsync(streamId, "P.One");
    var other = await coordinator.AcquireAsync(streamId, "P.Two").WaitAsync(_timeout);
    await other.DisposeAsync();

    var sameKey = coordinator.AcquireAsync(streamId, "P.One");
    await Assert.That(sameKey.IsCompleted).IsFalse()
      .Because("same (stream, perspective) still serializes — that is the coordinator's job");
    await a.DisposeAsync();
    var b = await sameKey.WaitAsync(_timeout);
    await b.DisposeAsync();
  }
}
