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
/// Tail-of-round coverage for <see cref="OrphanInboxJanitor"/>: shutdown while the sweep is still
/// waiting on the schema gate must return cleanly, not fault the hosted-service pipeline.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OrphanInboxJanitor.cs</code-under-test>
public class OrphanInboxJanitorCoverageTests {

  /// <summary>
  /// A host that shuts down before migrations complete cancels every hosted service's stopping
  /// token, including one still parked on the schema gate. If that cancellation escaped
  /// <c>ExecuteAsync</c> instead of being caught, .NET would report a faulted background service
  /// on an ordinary shutdown rather than the clean stop it actually is.
  /// </summary>
  [Test]
  public async Task ExecuteAsync_SchemaGateWaitCanceled_CompletesWithoutFaultingAsync() {
    var gate = new SchemaReadyGate();   // never marked ready
    using var sp = new ServiceCollection().BuildServiceProvider();
    var snapshot = new HandledReceptorTypeSnapshot(Array.Empty<Type>());
    var janitor = new OrphanInboxJanitor(sp, snapshot, schemaReadyGate: gate);

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await janitor.StartAsync(cts.Token);
    // SuppressThrowing, and assert IsCompleted/!IsFaulted rather than an exact TaskStatus: a task
    // that exits through a cancellation catch settles as RanToCompletion OR Canceled depending on
    // thread-pool timing, and a bare WaitAsync rethrows the cancellation into the test itself.
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(janitor.ExecuteTask!.IsCompleted).IsTrue()
      .Because("a canceled schema-gate wait during shutdown must settle rather than hang");
    await Assert.That(janitor.ExecuteTask!.IsFaulted).IsFalse()
      .Because("it must return cleanly rather than leave the hosted service reporting faulted");

    await janitor.StopAsync(CancellationToken.None);
  }
}
