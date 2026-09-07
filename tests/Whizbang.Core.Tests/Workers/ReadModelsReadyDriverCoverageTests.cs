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
    var schemaGate = new SchemaReadyGate();   // never marked ready
    var readGate = new ReadModelsReadyGate();
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var driver = new ReadModelsReadyDriver(readGate, schemaGate, sp);

    using var cts = new CancellationTokenSource();
    await driver.StartAsync(cts.Token);
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
}
