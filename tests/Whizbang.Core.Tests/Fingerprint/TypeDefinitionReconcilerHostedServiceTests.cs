using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Fingerprint;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Fingerprint;

/// <summary>
/// The startup pass that reconciles the type-definition fingerprint, seen from the host's side.
/// </summary>
/// <remarks>
/// The reconcile itself is covered against a real database elsewhere. What is asserted here is the
/// hosting contract around it, which had no test at all: that it waits for the schema before
/// touching tables that may not exist yet, and that nothing it does can stop the application from
/// serving. A drift report is worth having; it is not worth an outage, and the drift is re-detected
/// on the next startup either way.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Fingerprint/TypeDefinitionReconcilerHostedService.cs</code-under-test>
[Category("Core")]
public class TypeDefinitionReconcilerHostedServiceTests {

  private const int COMPLETED_EVENT_ID = 9214;
  private const int FAILED_EVENT_ID = 9215;

  [Test]
  [Timeout(30000)]
  public async Task ShutdownDuringTheSchemaWait_ReconcilesNothingAsync(
      CancellationToken cancellationToken) {
    // The reconcile reads the type-definition registry, which the migration that is still running
    // may not have created. A host torn down mid-migration must leave that alone rather than
    // report a failure against a table that was never going to be there.
    var log = new RecordingLogger();
    var gate = new BlockingSchemaGate();
    var service = new TypeDefinitionReconcilerHostedService(_reconciler(), gate, log);

    using var cts = new CancellationTokenSource();
    await service.StartAsync(cts.Token);
    // StartAsync returning does not mean ExecuteAsync has run -- the host starts it on the thread
    // pool -- so without this the assertions below describe a service that never began.
    await gate.WaitEntered.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await service.StopAsync(CancellationToken.None);

    await Assert.That(service.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a shutdown arriving during the schema wait is an ordinary stop; a faulted hosted "
             + "service turns it into a reported crash");
    await Assert.That(log.Events).DoesNotContain(COMPLETED_EVENT_ID)
      .Because("nothing was reconciled, so claiming a completed pass would report drift figures "
             + "for a walk that never happened");
    await Assert.That(log.Events).DoesNotContain(FAILED_EVENT_ID)
      .Because("stopping is not a reconcile failure, and logging it as one would make every "
             + "deploy look like an error");
  }

  [Test]
  [Timeout(30000)]
  public async Task OnceTheSchemaIsReady_TheReconcileRunsAndReportsAsync(
      CancellationToken cancellationToken) {
    // The gate is the only thing holding the pass back, so once it opens the walk must actually
    // happen -- a reconciler that waits forever is indistinguishable from one that found no drift.
    var log = new RecordingLogger();
    var service = new TypeDefinitionReconcilerHostedService(_reconciler(), _readyGate(), log);

    using var cts = new CancellationTokenSource();
    await service.StartAsync(cts.Token);
    await service.ExecuteTask!.WaitAsync(cancellationToken);
    await service.StopAsync(CancellationToken.None);

    await Assert.That(log.Events).Contains(COMPLETED_EVENT_ID)
      .Because("the pass ran to its end and reported what it found, which is the whole reason "
             + "this service is hosted rather than invoked on demand");
  }

  [Test]
  [Timeout(30000)]
  public async Task AReconcileFailureIsLoggedAndNeverFatalAsync(CancellationToken cancellationToken) {
    // This runs before the application serves. Drift detection is a diagnostic: if it cannot run,
    // the right outcome is a logged error and a service that starts anyway, because the drift is
    // re-detected on the next startup. Faulting here would trade a diagnostic for an outage.
    var log = new RecordingLogger();
    var failing = new TypeDefinitionReconciler(
      new ThrowingScopeFactory(),
      Options.Create(new EphemeralOptions()),
      NullLogger<TypeDefinitionReconciler>.Instance,
      new EmptyCatalog());
    var service = new TypeDefinitionReconcilerHostedService(failing, _readyGate(), log);

    using var cts = new CancellationTokenSource();
    await service.StartAsync(cts.Token);
    await service.ExecuteTask!.WaitAsync(cancellationToken);
    await service.StopAsync(CancellationToken.None);

    await Assert.That(service.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a hosted service that faults stops the host from ever serving, which is a far "
             + "worse outcome than an unreported fingerprint drift");
    await Assert.That(log.Events).Contains(FAILED_EVENT_ID)
      .Because("swallowing the failure silently would leave the drift undetected AND unmentioned");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  /// <summary>A reconciler with no catalog: its pass is a well-defined no-op returning Empty.</summary>
  private static TypeDefinitionReconciler _reconciler() =>
    new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new EphemeralOptions()),
      NullLogger<TypeDefinitionReconciler>.Instance);

  private static SchemaReadyGate _readyGate() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }

  /// <summary>A gate that never opens, and reports when the service began waiting on it.</summary>
  private sealed class BlockingSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waitEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitEntered => _waitEntered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

  private sealed class ThrowingScopeFactory : IServiceScopeFactory {
    public IServiceScope CreateScope() =>
      throw new InvalidOperationException("no scope available");
  }

  private sealed class EmptyCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
  }

  /// <summary>Records which log events the service emitted, hence which path it took.</summary>
  private sealed class RecordingLogger : ILogger<TypeDefinitionReconcilerHostedService> {
    private readonly List<int> _events = [];

    public IReadOnlyList<int> Events { get { lock (_events) { return [.. _events]; } } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_events) { _events.Add(eventId.Id); }
    }
  }
}
