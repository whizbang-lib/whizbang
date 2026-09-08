using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="ReadModelsReadyDriver"/>: shutdown while still waiting on
/// the schema gate must complete cleanly, leaving the read-model barrier closed rather than
/// faulting the hosted-service pipeline.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyDriver.cs</code-under-test>
[Category("Startup")]
public class ReadModelsReadyDriverCoverageTests {

  /// <summary>
  /// Fail-closed only works if a canceled wait actually ends the driver's task instead of
  /// escaping as a fault — an escaping exception would make the host's shutdown sequence report a
  /// crashed hosted service on an ordinary deploy, not the graceful stop that actually happened.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_CanceledWhileWaitingForSchemaGate_CompletesWithoutFaultingAsync() {
    var schemaGate = new SignallingSchemaGate();   // never marked ready
    var readGate = new ReadModelsReadyGate();
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var driver = new ReadModelsReadyDriver(readGate, schemaGate, sp);

    using var cts = new CancellationTokenSource();
    await driver.StartAsync(cts.Token);

    // Wait until the driver is provably parked on the gate before canceling. StartAsync only
    // queues ExecuteAsync via Task.Run(_, stoppingToken), and Task.Run never invokes the delegate
    // at all when the token is already canceled at dequeue time — the task settles Canceled, which
    // satisfies IsCompleted && !IsFaulted just as well as a real graceful exit does. Canceling
    // straight after StartAsync therefore passed whether the body ran or not; measured under load
    // it took the never-ran branch and still reported green.
    await schemaGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

    await cts.CancelAsync();

    // SuppressThrowing, and assert IsCompleted/!IsFaulted rather than an exact TaskStatus: a task
    // exiting through a cancellation catch settles as RanToCompletion OR Canceled depending on
    // thread-pool timing, so a status equality check passes alone and fails under full-suite load.
    await driver.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(driver.ExecuteTask!.IsCompleted).IsTrue()
      .Because("the driver must settle rather than hang when the gate wait is canceled");
    await Assert.That(driver.ExecuteTask!.IsFaulted).IsFalse()
      .Because("host shutdown before the schema migrates must not fault the hosted-service "
             + "pipeline — the barrier is simply left closed");
    await Assert.That(readGate.IsReady).IsFalse()
      .Because("a canceled wait must never open the barrier — lens reads must keep refusing");

    await driver.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// A never-ready schema gate that publishes when a waiter arrives.
  /// </summary>
  /// <remarks>
  /// Lets the test tell "the driver is parked on the closed gate" apart from "the thread pool has
  /// not dequeued ExecuteAsync yet" — indistinguishable otherwise, and only the first makes the
  /// graceful-exit assertions mean anything.
  /// </remarks>
  private sealed class SignallingSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the moment the driver begins waiting on this gate.</summary>
    public Task Entered => _entered.Task;

    public bool IsReady => _ready.Task.IsCompleted;
    public void MarkReady() => _ready.TrySetResult();

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      return _ready.Task.WaitAsync(cancellationToken);
    }
  }
}
