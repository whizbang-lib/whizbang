using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Drives the <see cref="OrphanInboxJanitor"/>'s <c>StartAsync</c> branches
/// directly. The companion test file
/// <c>OrphanInboxJanitorExtensionsTests</c> covers the DI extension; this
/// file targets the worker's own conditional paths so coverage tracks the
/// log/log-skip outcomes.
///
/// <para>The sweep runs in <c>ExecuteAsync</c> — in the background, after the schema gate —
/// rather than inline in <c>StartAsync</c>. The old shape both BLOCKED host startup (every later
/// hosted service waited on this purge) and skipped the gate (a purge issued against a possibly
/// unmigrated schema). Tests drive <c>StartAsync</c> then await the exposed
/// <c>BackgroundService.ExecuteTask</c> for determinism.</para>
/// </summary>
/// <docs>messaging/resilience/orphan-inbox</docs>
public class OrphanInboxJanitorTests {

  private sealed record _SnapshotMsg : IMessage;

  /// <summary>
  /// Constructor null-arg guards: surface the actual <c>ArgumentNullException</c>
  /// from the worker's <c>ArgumentNullException.ThrowIfNull</c> calls.
  /// </summary>
  [Test]
  public async Task Constructor_NullServices_ThrowsAsync() {
    var snapshot = new HandledReceptorTypeSnapshot(Array.Empty<Type>());
    await Assert.That(() => new OrphanInboxJanitor(
  services: null!,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady()))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullSnapshot_ThrowsAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: null!,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady()))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Branch 1: no <see cref="IWorkCoordinator"/> in DI → debug-log + early
  /// return, with no failure.
  /// </summary>
  [Test]
  public async Task StartAsync_NoWorkCoordinator_ReturnsCleanlyAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var logger = new _CapturingLogger();
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
  logger: logger);

    await _runToCompletionAsync(janitor);

    // "Returns cleanly" has two halves, and only asserting the second leaves the first free to
    // rot: the sweep must take the no-coordinator branch (not fail its way to the same silence),
    // and it must leave the background task faulted-free so the host keeps running.
    await Assert.That(janitor.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("a faulted ExecuteTask is an unobserved exception on the host, not a clean return");
    await Assert.That(logger.Exceptions.Count).IsEqualTo(0)
      .Because("no IWorkCoordinator is a supported configuration, not an error to log");
    await Assert.That(logger.Messages.Count(m => m.Contains("no IWorkCoordinator registered", StringComparison.Ordinal)))
      .IsEqualTo(1)
      .Because("the skip must be traceable — a silent no-op looks identical to a sweep that ran and found nothing");
  }

  /// <summary>
  /// Branch 2: snapshot has zero types AND no perspective/raw registries →
  /// refuses to purge (else we'd empty the inbox during cold start) and
  /// returns cleanly.
  /// </summary>
  [Test]
  public async Task StartAsync_NoHandledTypes_SkipsPurgeAsync() {
    var coordinator = new _RecordingCoordinator();
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot(Array.Empty<Type>());
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(0);
  }

  /// <summary>
  /// Branch 3a: snapshot types yield handled-type names; coordinator returns
  /// zero purged rows → "no rows" log path + clean exit.
  /// </summary>
  [Test]
  public async Task StartAsync_WithHandledTypes_NoPurge_LogsAndExitsAsync() {
    var coordinator = new _RecordingCoordinator();  // default empty result
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastHandledTypeNames!.Count).IsEqualTo(1);
  }

  /// <summary>
  /// Branch 3b: coordinator returns purged rows → grouping log path runs
  /// for each <c>MessageType</c>.
  /// </summary>
  [Test]
  public async Task StartAsync_WithHandledTypes_PurgedRows_LogsAndExitsAsync() {
    var coordinator = new _RecordingCoordinator {
      PurgeResult = [
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "B", "h2"),
      ],
    };
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// Branch 4: coordinator throws → janitor catches and logs, doesn't bubble
  /// up to kill the host.
  /// </summary>
  [Test]
  public async Task StartAsync_CoordinatorThrows_DoesNotPropagateAsync() {
    var coordinator = new _RecordingCoordinator { ThrowOnPurge = true };
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var logger = new _CapturingLogger();
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
  logger: logger);

    await _runToCompletionAsync(janitor);

    // The failure has to actually be injected — otherwise this test passes by never reaching
    // the purge at all, which is how a rotted fake goes unnoticed.
    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1)
      .Because("the throwing seam must be the one the janitor calls, or this is the success path in disguise");
    await Assert.That(janitor.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("a purge failure must be contained: the background task may not fault and take the host's startup with it");

    // Contained is not the same as swallowed: the operator has to be able to see what failed.
    var logged = logger.Exceptions;
    await Assert.That(logged.Count).IsEqualTo(1);
    await Assert.That(logged[0]).IsTypeOf<InvalidOperationException>();
    await Assert.That(logged[0].Message).IsEqualTo("simulated purge failure")
      .Because("the contained exception's identity must reach the log, not just its existence");
  }

  /// <summary>
  /// Perspective + raw-receptor registries union into the handled-type-names
  /// list passed to the coordinator. Asserts both contributors land in the
  /// argument.
  /// </summary>
  [Test]
  public async Task StartAsync_UnionsPerspectiveAndRawRegistries_IntoHandledNamesAsync() {
    var coordinator = new _RecordingCoordinator();
    var perspectives = new _StaticPerspectiveRegistry(new List<Type> { typeof(int) });
    var raw = new _StaticRawRegistry(["RawA, RawAsm", "RawB, RawAsm"]);
    using var sp = _buildProviderWith(coordinator, perspectives, raw);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.LastHandledTypeNames!.Count).IsEqualTo(4);
  }

  /// <summary>
  /// The old shape ran the purge inline in <c>StartAsync</c>, so every later hosted service
  /// waited on it. StartAsync must now return without purging — the sweep is background work.
  /// </summary>
  [Test]
  public async Task StartAsync_ReturnsWithoutBlockingOnThePurgeAsync() {
    var gate = new SchemaReadyGate();   // NOT ready — the sweep cannot even begin
    var coordinator = new _RecordingCoordinator();
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot, schemaReadyGate: gate);

    await janitor.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(0)
      .Because("host startup must not block on — or be coupled to — the orphan purge");

    gate.MarkReady();
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1)
      .Because("once migrations complete the sweep must actually run");
    await janitor.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The purge is a DELETE against wh_inbox — it must never run before migrations complete.
  /// </summary>
  [Test]
  public async Task Sweep_DoesNotPurgeWhileTheGateIsClosedAsync() {
    var gate = new SchemaReadyGate();
    var coordinator = new _RecordingCoordinator();
    using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(_SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot, schemaReadyGate: gate);

    await janitor.StartAsync(CancellationToken.None);
    await Task.Delay(300);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(0)
      .Because("a purge issued against a possibly-unmigrated schema is the second half of the defect");

    gate.MarkReady();
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    await janitor.StopAsync(CancellationToken.None);
  }

  /// <summary>Starts the janitor and awaits its background sweep to completion.</summary>
  private static async Task _runToCompletionAsync(OrphanInboxJanitor janitor) {
    await janitor.StartAsync(CancellationToken.None);
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    await janitor.StopAsync(CancellationToken.None);
  }

  // -------------------- helpers --------------------

  private static ServiceProvider _buildProviderWith(
      IWorkCoordinator coordinator,
      IPerspectiveRunnerRegistry? perspectives = null,
      IRawReceptorRegistry? raw = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    if (perspectives != null) {
      services.AddSingleton(perspectives);
    }
    if (raw != null) {
      services.AddSingleton(raw);
    }
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Captures formatted messages and attached exceptions. The formatted message never carries
  /// the exception, so <see cref="Exceptions"/> is the only way to see a fault the janitor
  /// contained rather than propagated.
  /// </summary>
  private sealed class _CapturingLogger : ILogger<OrphanInboxJanitor> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    private readonly List<Exception> _exceptions = [];

    public IReadOnlyList<string> Messages {
      get {
        lock (_lock) {
          return [.. _messages];
        }
      }
    }

    public IReadOnlyList<Exception> Exceptions {
      get {
        lock (_lock) {
          return [.. _exceptions];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      lock (_lock) {
        _messages.Add(message);
        if (exception is not null) {
          _exceptions.Add(exception);
        }
      }
    }
  }

  /// <summary>
  /// Records purge calls and can be configured to return canned results or
  /// throw. Subclasses <see cref="NoOpWorkCoordinator"/> so the long tail of
  /// <see cref="IWorkCoordinator"/> methods get free no-op implementations.
  /// </summary>
  private sealed class _RecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int PurgeCallCount { get; private set; }
    public IReadOnlyList<string>? LastHandledTypeNames { get; private set; }
    public IReadOnlyList<PurgedOrphanInboxRow> PurgeResult { get; set; } = [];
    public bool ThrowOnPurge { get; set; }

    // Explicit interface impl required to override the default interface
    // method on IWorkCoordinator — a `new` member wouldn't be dispatched
    // when the janitor holds the instance through the interface.
    Task<IReadOnlyList<PurgedOrphanInboxRow>> IWorkCoordinator.PurgeOrphanInboxAsync(
        IReadOnlyList<string> handledTypeNames,
        CancellationToken cancellationToken) {
      PurgeCallCount++;
      LastHandledTypeNames = handledTypeNames;
      if (ThrowOnPurge) {
        throw new InvalidOperationException("simulated purge failure");
      }
      return Task.FromResult(PurgeResult);
    }
  }

  private sealed class _StaticPerspectiveRegistry(IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<Whizbang.Core.Messaging.LifecycleStage> LifecycleStagesWithReceptors { get; } =
      new HashSet<Whizbang.Core.Messaging.LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class _StaticRawRegistry(IReadOnlyCollection<string> registered) : IRawReceptorRegistry {
    public IReadOnlyCollection<string> RegisteredTypeNames => registered;
    public IRawReceptor? FindByTypeName(string messageTypeName) => null;
  }
}
