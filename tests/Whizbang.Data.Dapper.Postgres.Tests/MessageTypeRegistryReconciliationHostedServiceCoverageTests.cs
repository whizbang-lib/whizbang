using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Coverage for the one branch <see cref="MessageTypeRegistryReconciliationHostedServiceTests"/>
/// doesn't reach: the schema gate throwing <see cref="OperationCanceledException"/> while
/// <c>ExecuteAsync</c> is waiting on it. Deliberately does NOT use the
/// StartAsync-then-StopAsync trick (which cancels the stopping token before the loop body runs
/// and proves nothing about this catch) — the fake gate below throws directly, so the signal is
/// the exception itself, not a race against shutdown.
/// </summary>
public class MessageTypeRegistryReconciliationHostedServiceCoverageTests {

  // If a host shuts down while reconciliation is still waiting for the schema gate to open, that
  // wait is canceled — and MUST be treated as an ordinary shutdown, not a startup failure. Absent
  // this catch, BackgroundService's default unhandled-exception behavior would fault the host on a
  // perfectly normal "we were still waiting for migrations when the process was asked to stop"
  // shutdown, instead of exiting quietly with the populator correctly never invoked.
  [Test]
  public async Task ExecuteAsync_SchemaGateWaitCanceled_ReturnsWithoutInvokingPopulateAsync() {
    var catalog = new _stubCatalog();
    var populator = new _spyPopulator();
    var gate = new _canceledSchemaReadyGate();
    var sut = new MessageTypeRegistryReconciliationHostedService(
      NullLogger<MessageTypeRegistryReconciliationHostedService>.Instance,
      catalog,
      populator,
      schemaReadyGate: gate);

    await sut.StartAsync(CancellationToken.None);
    await sut.ExecuteTask!;

    await Assert.That(populator.PopulateAsyncCallCount).IsEqualTo(0)
      .Because("a canceled wait for the schema gate must return quietly -- the registry the "
             + "populator reconciles lives in tables the migration may never have created, so "
             + "nothing may run when the wait itself was cut short");
  }

  private sealed class _stubCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
  }

  private sealed class _spyPopulator : IMessageTypeRegistryPopulator {
    public int PopulateAsyncCallCount { get; private set; }

    public Task PopulateAsync(CancellationToken cancellationToken = default) {
      PopulateAsyncCallCount++;
      return Task.CompletedTask;
    }
  }

  /// <summary>Always reports "not ready" and always fails its wait with a cancellation, regardless
  /// of the token passed in -- so the catch under test fires deterministically, with no dependency
  /// on racing a real CancellationTokenSource.</summary>
  private sealed class _canceledSchemaReadyGate : ISchemaReadyGate {
    public bool IsReady => false;
    public void MarkReady() { }

    public Task WaitForReadyAsync(CancellationToken cancellationToken) =>
      Task.FromCanceled(new CancellationToken(canceled: true));
  }
}
