// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// What a rebuild reports about itself, measured against a deployed named-record rebuild: a deleted record was
/// reported as failed (#1370), <c>EventsReplayed</c> counted records instead of events (#1371), and a missing row
/// digest could not be told apart from an unchanged one (#1372).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderOutcomeTests {
  private const string PERSPECTIVE = "Test.Perspective";

  /// <summary>A runner that reports, per stream, a fold of <see cref="Events"/> events that did or did not write the row.</summary>
  private sealed class ScriptedRunner(bool rowChanged, int events) : IPerspectiveRunner {
    public int Events { get; } = events;
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed,
        RowChanged = rowChanged,
        EventsProcessed = Events,
      });

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class Registry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class PurgeMarkers(params Guid[] purged) : IPerspectivePurgeMarkerStore {
    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult(purged.Contains(streamId));
    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class FixedDigest : IPerspectiveRowDigest {
    public Task<string?> ComputeAsync(string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default) =>
      Task.FromResult<string?>("digest");
  }

  private sealed class ThrowingDigest : IPerspectiveRowDigest {
    public Task<string?> ComputeAsync(string perspectiveName, IReadOnlyCollection<Guid> streamIds, CancellationToken ct = default) =>
      throw new InvalidOperationException("the digest query was refused");
  }

  private static (PerspectiveRebuilder Rebuilder, RebuildEventDispatcherDouble Dispatcher, FakeLogger<PerspectiveRebuilder> Logger) _build(
      IPerspectiveRunner runner, IPerspectivePurgeMarkerStore? markers = null, IPerspectiveRowDigest? digest = null) {
    var dispatcher = new RebuildEventDispatcherDouble();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(runner));
    services.AddSingleton<IDispatcher>(dispatcher);
    if (markers is not null) {
      services.AddSingleton(markers);
    }
    if (digest is not null) {
      services.AddSingleton(digest);
    }
    var logger = new FakeLogger<PerspectiveRebuilder>();
    var sp = services.BuildServiceProvider();
    return (new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), logger), dispatcher, logger);
  }

  // --- #1370 ---

  [Test]
  public async Task ARecordThatIsPurged_AndStaysPurged_IsRebuilt_NotFailedAsync() {
    // A deleted record: the replay starts purged and stays purged, so it writes nothing, and "no row" is the
    // correct result. Reporting it as a failure made a named-record run retry it and count it failed.
    var deleted = Guid.NewGuid();
    var (rebuilder, dispatcher, _) = _build(new ScriptedRunner(rowChanged: false, events: 5), new PurgeMarkers(deleted));

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [deleted]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(dispatcher.Published.OfType<PerspectiveRebuildFailed>()).IsEmpty();
  }

  [Test]
  public async Task ARecordThatFoldsWithoutWriting_AndIsNotPurged_StillFailsAsync() {
    // The other half of the old bucket is unchanged: a fold the store refused is still not a rebuild.
    var stream = Guid.NewGuid();
    var (rebuilder, _, _) = _build(new ScriptedRunner(rowChanged: false, events: 5), new PurgeMarkers());

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error!).Contains("1 folded but left the row unchanged");
  }

  [Test]
  public async Task WithNoPurgeMarkerStore_AFoldWithoutAWrite_StillFailsAsync() {
    var (rebuilder, _, _) = _build(new ScriptedRunner(rowChanged: false, events: 5));

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid()]);

    await Assert.That(result.Success).IsFalse();
  }

  // --- #1371 ---

  [Test]
  public async Task EventsReplayed_IsTheEventsTheRunnerApplied_SummedOverStreamsAsync() {
    var (rebuilder, dispatcher, _) = _build(new ScriptedRunner(rowChanged: true, events: 37));

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid(), Guid.NewGuid()]);

    await Assert.That(result.EventsReplayed).IsEqualTo(74);
    var completed = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().Single();
    await Assert.That(completed.EventsReplayed).IsEqualTo(74).Because("events applied, not records completed");
  }

  // --- #1372 ---

  [Test]
  public async Task AFailedDigest_IsAWarningNamingThePerspective_AndTheCompletionSaysWhyAsync() {
    var (rebuilder, dispatcher, logger) = _build(new ScriptedRunner(rowChanged: true, events: 1), digest: new ThrowingDigest());

    await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid()]);

    // Taken before and after the replay, so a digest that cannot run says so for each.
    var warnings = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(2);
    foreach (var warning in warnings) {
      await Assert.That(warning.Message).Contains(PERSPECTIVE);
      await Assert.That(warning.Exception).IsTypeOf<InvalidOperationException>();
    }
    var completed = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().Single();
    await Assert.That(completed.RowDigestBefore).IsNull();
    await Assert.That(completed.RowDigestUnavailableReason!).Contains("the digest query was refused");
  }

  [Test]
  public async Task WithNoDigestRegistered_TheCompletionSaysSo_AndItIsLoggedOnceAsync() {
    var (rebuilder, dispatcher, logger) = _build(new ScriptedRunner(rowChanged: true, events: 1));

    await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid()]);
    await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid()]);

    var completions = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().ToList();
    await Assert.That(completions.Count).IsEqualTo(2);
    await Assert.That(completions.All(c => c.RowDigestUnavailableReason == PerspectiveRebuilder.ROW_DIGEST_NOT_REGISTERED)).IsTrue();
    await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Message.Contains(nameof(IPerspectiveRowDigest)))).IsEqualTo(1)
      .Because("said once per rebuilder, not once per rebuild");
  }

  [Test]
  public async Task WithADigest_ThereIsNoReasonAsync() {
    var (rebuilder, dispatcher, _) = _build(new ScriptedRunner(rowChanged: true, events: 1), digest: new FixedDigest());

    await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [Guid.NewGuid()]);

    var completed = dispatcher.Published.OfType<PerspectiveRebuildCompleted>().Single();
    await Assert.That(completed.RowDigestBefore).IsEqualTo("digest");
    await Assert.That(completed.RowDigestUnavailableReason).IsNull();
  }
}
