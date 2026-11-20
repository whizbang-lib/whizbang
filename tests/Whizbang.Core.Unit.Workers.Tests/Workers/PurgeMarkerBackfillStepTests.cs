// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2314: rows purged before purge markers existed keep their cursor and snapshots forever, because the cleanup selects
/// only pairs that carry a marker. The step writes the missing markers, but only where the generator proves the row was
/// purged: the cursor's last event is a type whose Apply can only purge. A wrong marker makes the runner skip the
/// stream's later events, so every other missing row is left as it is, and how many there are is reported once.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PurgeMarkerBackfillStep.cs</code-under-test>
[Category("Workers")]
public class PurgeMarkerBackfillStepTests {
  private const string PURGING = "Shop.Contracts.CartAbandoned, Shop.Contracts";

  [Test]
  public async Task Run_PassesOnlyTheEventTypesThatCanOnlyPurge_PerPerspective_AndReportsTheMarkersWrittenAsync() {
    var store = new RecordingStore { Marked = { 3 } };
    var logger = new FakeLogger<PurgeMarkerBackfillStep>();
    var step = new PurgeMarkerBackfillStep(logger);

    await step.RunAsync(_services(store,
      _perspective("Shop.Carts+CartView", PURGING),
      _perspective("Shop.Carts+CartTotals")), CancellationToken.None);

    await Assert.That(store.BackfillCalls).Count().IsEqualTo(1);
    await Assert.That(store.BackfillCalls[0].Types).IsEquivalentTo([("Shop.Carts+CartView", PURGING)])
      .Because("only an event whose Apply can only purge proves the missing row was purged");
    await Assert.That(store.BackfillCalls[0].BatchSize).IsEqualTo(PurgeMarkerBackfillStep.BATCH_SIZE);
    await Assert.That(store.CountCalls).IsEqualTo(0).Because("markers are still being written, so the backfill is not done");
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Information
      && r.Message.Contains("3 purge marker(s)", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Run_OnceNothingIsLeftToMark_ReportsTheMissingRowsLeftUnmarked_OnceAsync() {
    var store = new RecordingStore { Marked = { 0 }, Unmarked = 42 };
    var logger = new FakeLogger<PurgeMarkerBackfillStep>();
    var step = new PurgeMarkerBackfillStep(logger);
    var services = _services(store, _perspective("Shop.Carts+CartView", PURGING));

    await step.RunAsync(services, CancellationToken.None);
    await step.RunAsync(services, CancellationToken.None);

    await Assert.That(store.BackfillCalls).Count().IsEqualTo(1).Because("once done, later cycles ask nothing");
    await Assert.That(store.CountCalls).IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Information
      && r.Message.Contains("42", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the rows left unmarked are made visible once, not every cycle");
  }

  [Test]
  public async Task Run_NoPerspectiveCanOnlyPurge_MarksNothing_AndStillReportsTheMissingRowsAsync() {
    var store = new RecordingStore { Unmarked = 7 };
    var logger = new FakeLogger<PurgeMarkerBackfillStep>();
    var step = new PurgeMarkerBackfillStep(logger);

    await step.RunAsync(_services(store, _perspective("Shop.Carts+CartTotals")), CancellationToken.None);

    await Assert.That(store.BackfillCalls).IsEmpty();
    await Assert.That(store.CountCalls).IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Message.Contains('7', StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Run_NoMissingRowLeftUnmarked_ReportsNothingAsync() {
    var store = new RecordingStore { Marked = { 0 }, Unmarked = 0 };
    var logger = new FakeLogger<PurgeMarkerBackfillStep>();
    var step = new PurgeMarkerBackfillStep(logger);

    await step.RunAsync(_services(store, _perspective("Shop.Carts+CartView", PURGING)), CancellationToken.None);

    await Assert.That(store.CountCalls).IsEqualTo(1);
    await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level >= LogLevel.Information)).IsFalse();
  }

  [Test]
  public async Task Run_WithoutARegistryOrAMarkerStore_DoesNothingAsync() {
    var store = new RecordingStore();
    var step = new PurgeMarkerBackfillStep(new FakeLogger<PurgeMarkerBackfillStep>());

    await step.RunAsync(new ServiceCollection().AddSingleton<IPerspectivePurgeMarkerStore>(store).BuildServiceProvider(), CancellationToken.None);
    await step.RunAsync(new ServiceCollection().AddSingleton<IPerspectiveRunnerRegistry>(new Registry([])).BuildServiceProvider(), CancellationToken.None);

    await Assert.That(store.BackfillCalls).IsEmpty();
    await Assert.That(store.CountCalls).IsEqualTo(0);
    await Assert.That(step.Name).IsEqualTo("purge-marker-backfill");
  }

  [Test]
  public async Task Run_NullServices_ThrowsAsync() {
    var step = new PurgeMarkerBackfillStep(new FakeLogger<PurgeMarkerBackfillStep>());

    await Assert.That(() => step.RunAsync(null!, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
  }

  /// <summary>The step is registered with the workers, once however often they are added.</summary>
  [Test]
  public async Task AddWhizbangWorkers_RegistersTheStepOnceAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var steps = provider.GetServices<IMaintenanceStep>().OfType<PurgeMarkerBackfillStep>().ToArray();

    await Assert.That(steps.Length).IsEqualTo(1);
  }

  private static PerspectiveRegistrationInfo _perspective(string name, params string[] purging) =>
    new(name, "global::" + name.Replace('+', '.'), "Model", []) { UnconditionalPurgeEventTypes = purging };

  private static ServiceProvider _services(RecordingStore store, params PerspectiveRegistrationInfo[] perspectives) =>
    new ServiceCollection()
      .AddSingleton<IPerspectivePurgeMarkerStore>(store)
      .AddSingleton<IPerspectiveRunnerRegistry>(new Registry(perspectives))
      .BuildServiceProvider();

  private sealed class Registry(PerspectiveRegistrationInfo[] perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class RecordingStore : IPerspectivePurgeMarkerStore {
    public List<(List<(string PerspectiveName, string EventType)> Types, int BatchSize)> BackfillCalls { get; } = [];
    public List<int> Marked { get; } = [];
    private int _backfills;
    public long Unmarked { get; init; }
    public int CountCalls { get; private set; }

    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(false);

    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> BackfillMarkersAsync(
        IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int batchSize, CancellationToken cancellationToken = default) {
      BackfillCalls.Add(([.. purgingTypes], batchSize));
      var marked = _backfills < Marked.Count ? Marked[_backfills] : 0;
      _backfills++;
      return Task.FromResult(marked);
    }

    public Task<long> CountUnmarkedRowlessPairsAsync(
        IReadOnlyList<(string PerspectiveName, string EventType)> purgingTypes, int cap, CancellationToken cancellationToken = default) {
      CountCalls++;
      return Task.FromResult(Unmarked);
    }
  }
}
