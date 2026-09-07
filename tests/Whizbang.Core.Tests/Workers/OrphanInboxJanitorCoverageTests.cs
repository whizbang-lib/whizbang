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
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(janitor.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a canceled schema-gate wait during shutdown must return cleanly rather than leave "
             + "the hosted service reporting faulted");

    await janitor.StopAsync(CancellationToken.None);
  }
}
