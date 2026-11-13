// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

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
/// <docs>operations/workers/orphan-inbox-janitor</docs>
public class OrphanInboxJanitorTests {

  private sealed record SnapshotMsg : IMessage;

  /// <summary>
  /// Constructor null-arg guards: surface the actual <c>ArgumentNullException</c>
  /// from the worker's <c>ArgumentNullException.ThrowIfNull</c> calls.
  /// </summary>
  [Test]
  public async Task Constructor_NullServices_ThrowsAsync() {
    var snapshot = new HandledReceptorTypeSnapshot([]);
    await Assert.That(() => new OrphanInboxJanitor(
  services: null!,
  receptorSnapshot: snapshot,
  logger: NullLogger<OrphanInboxJanitor>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullSnapshot_ThrowsAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: null!,
  logger: NullLogger<OrphanInboxJanitor>.Instance))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// Branch 1: no <see cref="IWorkCoordinator"/> in DI → debug-log + early
  /// return, with no failure.
  /// </summary>
  [Test]
  public async Task StartAsync_NoWorkCoordinator_ReturnsCleanlyAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var logger = new CapturingLogger();
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
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
    var coordinator = new RecordingCoordinator();
    await using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  logger: NullLogger<OrphanInboxJanitor>.Instance);

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(0);
  }

  /// <summary>
  /// Branch 3a: snapshot types yield handled-type names; coordinator returns
  /// zero purged rows → "no rows" log path + clean exit.
  /// </summary>
  [Test]
  public async Task StartAsync_WithHandledTypes_NoPurge_LogsAndExitsAsync() {
    var coordinator = new RecordingCoordinator();  // default empty result
    await using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  logger: NullLogger<OrphanInboxJanitor>.Instance);

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
    var coordinator = new RecordingCoordinator {
      PurgeResult = [
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "A", "h1"),
        new PurgedOrphanInboxRow(Guid.NewGuid(), "B", "h2"),
      ],
    };
    await using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  logger: NullLogger<OrphanInboxJanitor>.Instance);

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
  }

  /// <summary>
  /// Branch 4: coordinator throws → janitor catches and logs, doesn't bubble
  /// up to kill the host.
  /// </summary>
  [Test]
  public async Task StartAsync_CoordinatorThrows_DoesNotPropagateAsync() {
    var coordinator = new RecordingCoordinator { ThrowOnPurge = true };
    await using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var logger = new CapturingLogger();
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
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
  /// Consumer-registry + raw-receptor registries union into the handled-type-names list passed to the coordinator,
  /// alongside the receptor snapshot. The perspective list is not a source of its own (#2058): the consumer registry
  /// already lists every type a perspective folds, so a perspective registry adds nothing.
  /// </summary>
  [Test]
  public async Task StartAsync_UnionsConsumerAndRawRegistries_IntoHandledNamesAsync() {
    var coordinator = new RecordingCoordinator();
    var perspectives = new StaticPerspectiveRegistry([typeof(int)]);
    var raw = new StaticRawRegistry(["RawA, RawAsm", "RawB, RawAsm"]);
    await using var sp = _buildProviderWith(coordinator, perspectives, raw,
      new ConsumedRegistry(new ConsumedMessageInfo("Contracts.Folded, Contracts", IsEvent: true)));
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(
  services: sp,
  receptorSnapshot: snapshot,
  logger: NullLogger<OrphanInboxJanitor>.Instance);

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.LastHandledTypeNames!.Count).IsEqualTo(4)
      .Because("the snapshot's type, the consumer registry's and the two raw types; the perspective list adds none");
    await Assert.That(coordinator.LastHandledTypeNames).Contains("Contracts.Folded, Contracts");
  }

  /// <summary>
  /// #2058: the handled names include everything the consumer registry says this service consumes, so a type that
  /// only a perspective, a tag or the collective sink consumes is never purged as an orphan.
  /// </summary>
  [Test]
  public async Task StartAsync_HandledNames_IncludeEveryTypeTheConsumerRegistryEnumeratesAsync() {
    var coordinator = new RecordingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(new ConsumedRegistry(
      new ConsumedMessageInfo("Contracts.TaggedNotice, Contracts", IsEvent: false),
      new ConsumedMessageInfo("Contracts.FoldedEvent, Contracts", IsEvent: true)));
    await using var sp = services.BuildServiceProvider();
    var janitor = new OrphanInboxJanitor(sp, new HandledReceptorTypeSnapshot([]), NullLogger<OrphanInboxJanitor>.Instance);

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.LastHandledTypeNames).IsEquivalentTo(
      ["Contracts.TaggedNotice, Contracts", "Contracts.FoldedEvent, Contracts"])
      .Because("what the service consumes comes from the one consumer registry, not from the perspective list");
  }

  /// <summary>
  /// #2066: a pending type the receive boundary keeps without a consumer (an absorbed namespace: stored so a later
  /// perspective can rebuild from it) is handed to the purge as handled, so its rows survive; a pending type nothing keeps
  /// is still purged.
  /// </summary>
  [Test]
  public async Task StartAsync_APendingTypeInAnAbsorbedNamespace_IsKeptAsync() {
    var coordinator = new RecordingCoordinator {
      PendingTypes = ["Contracts.Jobs.JobStarted, Contracts", "Other.Gone, Other"],
    };
    var consumers = new ConsumedRegistry(new ConsumedMessageInfo("Contracts.Folded, Contracts", IsEvent: true));
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IConsumerRegistry>(consumers);
    services.AddSingleton<Whizbang.Core.Routing.IMessageDiscardPolicy>(new Whizbang.Core.Routing.MessageDiscardPolicy(
      consumers, NullLogger<Whizbang.Core.Routing.MessageDiscardPolicy>.Instance,
      new System.Diagnostics.Metrics.Meter("orphan-inbox-absorbed-test"),
      Microsoft.Extensions.Options.Options.Create(new Whizbang.Core.Routing.RoutingOptions().AbsorbNamespaces("contracts.jobs")),
      new EventMarkerResolver(NullMessageTypeCatalog.Instance)));
    await using var sp = services.BuildServiceProvider();
    var janitor = new OrphanInboxJanitor(sp, new HandledReceptorTypeSnapshot([]), NullLogger<OrphanInboxJanitor>.Instance);

    await _runToCompletionAsync(janitor);

    await Assert.That(coordinator.LastHandledTypeNames).Contains("Contracts.Jobs.JobStarted, Contracts")
      .Because("an absorbed namespace's events are kept on receive, so the janitor must not purge them as orphans");
    await Assert.That(coordinator.LastHandledTypeNames).DoesNotContain("Other.Gone, Other")
      .Because("a type nothing here keeps is still an orphan");
  }

  /// <summary>
  /// The old shape ran the purge inline  /// <summary>
  /// The old shape ran the purge inline in <c>StartAsync</c>, so every later hosted service
  /// waited on it. StartAsync must now return without purging — the sweep is background work.
  /// </summary>
  [Test]
  public async Task StartAsync_ReturnsWithoutBlockingOnThePurgeAsync() {
    // The purge is HELD in flight until the test lets it answer, so StartAsync returning proves it did not wait for the
    // purge, by construction. The sweep runs on the background task StartAsync schedules, so whether the purge has
    // been CALLED when StartAsync returns is a race; whether it has ANSWERED is not.
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new RecordingCoordinator { HoldPurge = release.Task };
    await using var sp = _buildProviderWith(coordinator);
    var snapshot = new HandledReceptorTypeSnapshot([typeof(SnapshotMsg)]);
    var janitor = new OrphanInboxJanitor(sp, snapshot, logger: NullLogger<OrphanInboxJanitor>.Instance);

    await janitor.StartAsync(CancellationToken.None);

    await Assert.That(coordinator.PurgesCompleted).IsEqualTo(0)
      .Because("host startup must not block on, or be coupled to, the orphan purge");

    release.SetResult();
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1)
      .Because("once migrations complete the sweep must actually run");
    await Assert.That(coordinator.PurgesCompleted).IsEqualTo(1);
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
      IRawReceptorRegistry? raw = null,
      IConsumerRegistry? consumers = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    if (consumers != null) {
      services.AddSingleton(consumers);
    }
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
  private sealed class CapturingLogger : ILogger<OrphanInboxJanitor> {
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
  private sealed class RecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int PurgeCallCount { get; private set; }
    public IReadOnlyList<string>? LastHandledTypeNames { get; private set; }
    public IReadOnlyList<PurgedOrphanInboxRow> PurgeResult { get; set; } = [];
    public bool ThrowOnPurge { get; set; }
    public IReadOnlyList<string> PendingTypes { get; init; } = [];

    /// <summary>When set, a purge waits for it before it answers: a purge the test holds in flight.</summary>
    public Task? HoldPurge { get; init; }

    private int _purgesCompleted;

    /// <summary>Purges that have answered.</summary>
    public int PurgesCompleted => Volatile.Read(ref _purgesCompleted);

    Task<IReadOnlyList<string>> IWorkCoordinator.GetPendingInboxMessageTypesAsync(CancellationToken cancellationToken) =>
      Task.FromResult(PendingTypes);

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
      return HoldPurge is { } hold ? _answerAfterAsync(hold) : _answered();
    }

    private Task<IReadOnlyList<PurgedOrphanInboxRow>> _answered() {
      Interlocked.Increment(ref _purgesCompleted);
      return Task.FromResult(PurgeResult);
    }

    private async Task<IReadOnlyList<PurgedOrphanInboxRow>> _answerAfterAsync(Task hold) {
      await hold;
      return await _answered();
    }
  }

  private sealed class ConsumedRegistry(params ConsumedMessageInfo[] consumed) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }

  private sealed class StaticPerspectiveRegistry(IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlySet<Whizbang.Core.Messaging.LifecycleStage> LifecycleStagesWithReceptors { get; } =
      new HashSet<Whizbang.Core.Messaging.LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }

  private sealed class StaticRawRegistry(IReadOnlyCollection<string> registered) : IRawReceptorRegistry {
    public IReadOnlyCollection<string> RegisteredTypeNames => registered;
    public IRawReceptor? FindByTypeName(string messageTypeName) => null;
  }
}
