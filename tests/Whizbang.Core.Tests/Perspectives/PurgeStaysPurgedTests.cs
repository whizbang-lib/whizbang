// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>Creates the row.</summary>
public sealed record PurgeStaysCreated : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public string Name { get; init; } = string.Empty;
}

/// <summary>A follow-up that bumps the version, written as create-or-update.</summary>
public sealed record PurgeStaysBumped : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>Purges the row.</summary>
public sealed record PurgeStaysDeleted : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>Brings a purged row back, explicitly.</summary>
public sealed record PurgeStaysReopened : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public string Name { get; init; } = string.Empty;
}

/// <summary>Throws in Apply, to show a purged stream swallows a failing follow-up quietly.</summary>
public sealed record PurgeStaysBroken : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

public sealed class PurgeStaysModel {
  [StreamId]
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int Version { get; set; }
}

/// <summary>
/// The shape issue #1027 describes: a follow-up event is written as create-or-update, so applied to a
/// missing row it builds a mostly default one.
/// </summary>
public sealed class PurgeStaysPerspective :
    IPerspectiveFor<PurgeStaysModel, PurgeStaysCreated>,
    IPerspectiveFor<PurgeStaysModel, PurgeStaysBumped>,
    IPerspectiveFor<PurgeStaysModel, PurgeStaysBroken>,
    IPerspectiveWithActionsFor<PurgeStaysModel, PurgeStaysDeleted>,
    IPerspectiveWithActionsFor<PurgeStaysModel, PurgeStaysReopened> {
  public PurgeStaysModel Apply(PurgeStaysModel currentData, PurgeStaysCreated @event) =>
    new() { Id = @event.StreamId, Name = @event.Name, Version = 1 };

  public PurgeStaysModel Apply(PurgeStaysModel currentData, PurgeStaysBumped @event) =>
    new() { Id = @event.StreamId, Name = currentData.Name, Version = currentData.Version + 1 };

  public PurgeStaysModel Apply(PurgeStaysModel currentData, PurgeStaysBroken @event) =>
    throw new InvalidOperationException("this follow-up cannot be applied");

  public ApplyResult<PurgeStaysModel> Apply(PurgeStaysModel currentData, PurgeStaysDeleted @event) =>
    ApplyResult<PurgeStaysModel>.Purge();

  public ApplyResult<PurgeStaysModel> Apply(PurgeStaysModel currentData, PurgeStaysReopened @event) =>
    ApplyResult<PurgeStaysModel>.Resurrect(new PurgeStaysModel { Id = @event.StreamId, Name = @event.Name, Version = 100 });
}

/// <summary>
/// Issue #1027 over the real generated runner: a purged row stays purged. The purge leaves a marker, a later
/// event on the stream is skipped (logged and counted) rather than applied to an empty model, and only an
/// Apply that returns <see cref="ApplyResult{TModel}.Resurrect"/> brings the row back. Live drain, rewind and
/// rebuild decide the same way, and a stream that was never purged is untouched.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
public class PurgeStaysPurgedTests {
  private const string PERSPECTIVE_NAME = nameof(PurgeStaysPerspective);

  [Test]
  public async Task LaterBatch_AfterPurge_IsSkipped_AndNoRowIsRecreatedAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var created = _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" });
    var deleted = _envelope(new PurgeStaysDeleted { StreamId = streamId });
    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [created, deleted], CancellationToken.None);

    var bumped = _envelope(new PurgeStaysBumped { StreamId = streamId });
    var completion = await harness.Runner.RunWithEventsAsync(
      streamId, PERSPECTIVE_NAME, deleted.MessageId.Value, [bumped], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("the delayed follow-up must not resurrect an empty row");
    await Assert.That(harness.Markers.Marked).Contains((streamId, PERSPECTIVE_NAME, (Guid?)deleted.MessageId.Value))
      .Because("the purge records a marker naming the event that purged");
    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed)
      .Because("the skipped event is done, so its work row is released and the cursor moves past it");
    await Assert.That(completion.LastEventId).IsEqualTo(bumped.MessageId.Value);
    await Assert.That(harness.Skipped()).IsEqualTo(1);
  }

  [Test]
  public async Task SameBatch_PurgeThenFollowUp_LeavesNoRow_AndMarksAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var created = _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" });
    var deleted = _envelope(new PurgeStaysDeleted { StreamId = streamId });

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [
      created,
      deleted,
      _envelope(new PurgeStaysBumped { StreamId = streamId }),
    ], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
    await Assert.That(harness.Markers.Marked).Contains((streamId, PERSPECTIVE_NAME, (Guid?)deleted.MessageId.Value));
    await Assert.That(harness.Skipped()).IsEqualTo(1);
  }

  [Test]
  public async Task Resurrect_OnPurgedStream_RecreatesRow_AndClearsMarkerAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    harness.Markers.Purge(streamId, PERSPECTIVE_NAME);

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [
      _envelope(new PurgeStaysBumped { StreamId = streamId }),
      _envelope(new PurgeStaysReopened { StreamId = streamId, Name = "reopened" }),
      _envelope(new PurgeStaysBumped { StreamId = streamId }),
    ], CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull().Because("Resurrect is the explicit opt-in");
    await Assert.That(row!.Name).IsEqualTo("reopened");
    await Assert.That(row.Version).IsEqualTo(101)
      .Because("the bump before the resurrect is skipped; the one after applies to the resurrected row");
    await Assert.That(harness.Markers.Cleared).Contains((streamId, PERSPECTIVE_NAME));
    await Assert.That(harness.Markers.IsMarked(streamId, PERSPECTIVE_NAME)).IsFalse();
  }

  [Test]
  public async Task PurgeThenResurrect_InOneBatch_OnExistingRow_KeepsRow_WithoutMarkerAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    await harness.Rows.UpsertAsync(streamId, new PurgeStaysModel { Id = streamId, Name = "old", Version = 7 });

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [
      _envelope(new PurgeStaysDeleted { StreamId = streamId }),
      _envelope(new PurgeStaysReopened { StreamId = streamId, Name = "again" }),
    ], CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Name).IsEqualTo("again");
    await Assert.That(harness.Markers.Marked).IsEmpty()
      .Because("the batch ends resurrected, so nothing is left purged");
    await Assert.That(harness.Markers.Cleared).IsEmpty()
      .Because("no marker existed when the batch began");
  }

  [Test]
  public async Task NewStream_WithoutMarker_IsCreated_AndAskedOnceAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new PurgeStaysBumped { StreamId = streamId })], CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull().Because("a marker only exists after a purge; a new stream is never affected");
    await Assert.That(row!.Version).IsEqualTo(1)
      .Because("with no row, Apply receives an empty model (stream key set, everything else default), never null");
    await Assert.That(row.Id).IsEqualTo(streamId);
    await Assert.That(harness.Markers.Lookups).IsEqualTo(1);
    await Assert.That(harness.Skipped()).IsEqualTo(0);
  }

  [Test]
  public async Task ExistingRow_NeverConsultsMarkersAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    await harness.Rows.UpsertAsync(streamId, new PurgeStaysModel { Id = streamId, Name = "live", Version = 3 });

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new PurgeStaysBumped { StreamId = streamId })], CancellationToken.None);

    await Assert.That(harness.Markers.Lookups).IsEqualTo(0)
      .Because("the marker is consulted only when the row is missing, so the steady state pays nothing");
    await Assert.That((await harness.Rows.GetByStreamIdAsync(streamId))!.Version).IsEqualTo(4);
  }

  [Test]
  public async Task StreamLevelMarker_SkipsEveryPerspectiveAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    harness.Markers.Purge(streamId, PerspectivePurgeMarkers.ALL_PERSPECTIVES);

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new PurgeStaysBumped { StreamId = streamId })], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("an operator purge marks the whole stream for every perspective");
  }

  [Test]
  public async Task FailingApply_OnPurgedStream_IsSkipped_NotAnErrorAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    harness.Markers.Purge(streamId, PERSPECTIVE_NAME);
    var broken = _envelope(new PurgeStaysBroken { StreamId = streamId });

    var completion = await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [broken], CancellationToken.None);

    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(completion.LastEventId).IsEqualTo(broken.MessageId.Value);
    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
    await Assert.That(harness.Skipped()).IsEqualTo(1);
  }

  [Test]
  public async Task WithoutMarkerStore_SameBatchPurge_StillStaysPurgedAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness(withMarkers: false, withMetrics: false);

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [
      _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" }),
      _envelope(new PurgeStaysDeleted { StreamId = streamId }),
      _envelope(new PurgeStaysBumped { StreamId = streamId }),
    ], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
  }

  [Test]
  public async Task Rebuild_OfDeletedThenBumpedStream_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    await harness.Events.AppendAsync(streamId, _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" }));
    var deleted = _envelope(new PurgeStaysDeleted { StreamId = streamId });
    await harness.Events.AppendAsync(streamId, deleted);
    await harness.Events.AppendAsync(streamId, _envelope(new PurgeStaysBumped { StreamId = streamId }));

    await harness.Runner.RunRebuildAsync(streamId, PERSPECTIVE_NAME, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
    await Assert.That(harness.Skipped()).IsEqualTo(1)
      .Because("the create applies, the delete purges, and only the bump after it is skipped");
    await Assert.That(harness.Markers.Marked).Contains((streamId, PERSPECTIVE_NAME, (Guid?)deleted.MessageId.Value));
  }

  [Test]
  public async Task Rebuild_WithMarker_AndHistoryGone_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    harness.Markers.Purge(streamId, PERSPECTIVE_NAME);
    // Only the follow-up survives: the history before it was destroyed (an operator purge, or reaping).
    await harness.Events.AppendAsync(streamId, _envelope(new PurgeStaysBumped { StreamId = streamId }));

    await harness.Runner.RunRebuildAsync(streamId, PERSPECTIVE_NAME, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
  }

  [Test]
  public async Task Rewind_FullReplay_WithStreamMarker_LeavesNoRow_ButHonorsResurrectAsync() {
    var purged = Guid.NewGuid();
    var reopened = Guid.NewGuid();
    var harness = new Harness();
    harness.Markers.Purge(purged, PerspectivePurgeMarkers.ALL_PERSPECTIVES);
    harness.Markers.Purge(reopened, PERSPECTIVE_NAME);
    var late = _envelope(new PurgeStaysBumped { StreamId = purged });
    await harness.Events.AppendAsync(purged, late);
    await harness.Events.AppendAsync(reopened, _envelope(new PurgeStaysBumped { StreamId = reopened }));
    var reopen = _envelope(new PurgeStaysReopened { StreamId = reopened, Name = "back" });
    await harness.Events.AppendAsync(reopened, reopen);

    await harness.Runner.RewindAndRunAsync(purged, PERSPECTIVE_NAME, late.MessageId.Value, CancellationToken.None);
    await harness.Runner.RewindAndRunAsync(reopened, PERSPECTIVE_NAME, reopen.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(purged)).IsNull();
    var row = await harness.Rows.GetByStreamIdAsync(reopened);
    await Assert.That(row!.Name).IsEqualTo("back");
    await Assert.That(harness.Markers.IsMarked(reopened, PERSPECTIVE_NAME)).IsFalse();
  }

  [Test]
  public async Task Rewind_OnLiveRow_WhoseHistoryPurges_RemovesRow_AndMarksAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    await harness.Rows.UpsertAsync(streamId, new PurgeStaysModel { Id = streamId, Name = "order", Version = 2 });
    var created = _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" });
    var deleted = _envelope(new PurgeStaysDeleted { StreamId = streamId });
    var bumped = _envelope(new PurgeStaysBumped { StreamId = streamId });
    await harness.Events.AppendAsync(streamId, created);
    await harness.Events.AppendAsync(streamId, deleted);
    await harness.Events.AppendAsync(streamId, bumped);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, created.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("a replay reproduces the purge, and the bump after it stays skipped");
    await Assert.That(harness.Markers.Marked).Contains((streamId, PERSPECTIVE_NAME, (Guid?)deleted.MessageId.Value));
  }

  [Test]
  public async Task OutOfOrderChild_OlderThanThePurge_ArrivingAfterIt_StaysPurgedAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var created = _envelope(new PurgeStaysCreated { StreamId = streamId, Name = "order" });
    // A child written before the delete but delivered after it: its id sorts before the purge.
    var child = _envelope(new PurgeStaysBumped { StreamId = streamId });
    var deleted = _envelope(new PurgeStaysDeleted { StreamId = streamId });
    await harness.Events.AppendAsync(streamId, created);
    await harness.Events.AppendAsync(streamId, deleted);
    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [created, deleted], CancellationToken.None);
    await harness.Events.AppendAsync(streamId, child);

    // Live path: the straggler reaches the missing row.
    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, deleted.MessageId.Value, [child], CancellationToken.None);
    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();

    // Rewind path: the straggler is detected as late and the stream replays in order.
    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, child.MessageId.Value, CancellationToken.None);
    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
  }

  // -------------------------------------------------------------------------------------------

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public RowStore Rows { get; } = new();
    public RecordingPurgeMarkers Markers { get; } = new();
    public MetricAssertionHelper Metrics { get; }
    public IPerspectiveRunner Runner { get; }

    /// <summary>Events counted as skipped for this perspective (the tagged series; the untagged one is always 0).</summary>
    public double Skipped() {
      var tagged = Metrics.GetByName("whizbang.perspective.purged_events_skipped")
        .Where(m => m.Tags.TryGetValue("perspective_name", out var name) && name == PERSPECTIVE_NAME)
        .ToList();
      return tagged.Count switch {
        0 => 0,
        1 => tagged[0].Value,
        _ => throw new InvalidOperationException($"expected one tagged series, got {tagged.Count}: {string.Join(", ", tagged.Select(t => t.Value))}"),
      };
    }

    public Harness(bool withMarkers = true, bool withMetrics = true) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<PurgeStaysPerspective>();
      var meterFactory = new TestMeterFactory();
      var perspectiveMetrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
      Metrics = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
      if (withMarkers) {
        services.AddSingleton<IPerspectivePurgeMarkerStore>(Markers);
      }
      if (withMetrics) {
        services.AddSingleton(perspectiveMetrics);
      }
      var provider = services.BuildServiceProvider();
      // The real generated runner, resolved by name: naming a generated type in source does not
      // compile in a workspace that loads without running the generators (the formatting gate).
      var runnerType = typeof(PurgeStaysPurgedTests).Assembly
        .GetType("Whizbang.Core.Tests.Generated.PurgeStaysPerspectiveRunner", throwOnError: true)!;
      var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runnerType))!;
      var ctor = runnerType.GetConstructors().Single();
      var args = new object?[ctor.GetParameters().Length];
      args[0] = provider;
      args[1] = logger;
      args[2] = Events;
      args[3] = Rows;
      args[4] = provider.GetRequiredService<IServiceScopeFactory>();
      Runner = (IPerspectiveRunner)ctor.Invoke(args);
    }
  }

  private static long _clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

  /// <summary>
  /// An envelope whose id sorts after every earlier one: a millisecond apart, so the runner's id order is the
  /// order the test wrote the events in (two ids minted in the same millisecond need not sort that way).
  /// </summary>
  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.From(Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _clock)))),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>Holds the markers in memory and records what the runner asked and wrote.</summary>
  private sealed class RecordingPurgeMarkers : IPerspectivePurgeMarkerStore {
    private readonly HashSet<(Guid StreamId, string PerspectiveName)> _markers = [];
    public List<(Guid StreamId, string PerspectiveName, Guid? PurgeEventId)> Marked { get; } = [];
    public List<(Guid StreamId, string PerspectiveName)> Cleared { get; } = [];
    public int Lookups { get; private set; }

    public void Purge(Guid streamId, string perspectiveName) => _markers.Add((streamId, perspectiveName));

    public bool IsMarked(Guid streamId, string perspectiveName) => _markers.Contains((streamId, perspectiveName));

    public Task<bool> IsPurgedAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      Lookups++;
      return Task.FromResult(_markers.Contains((streamId, perspectiveName))
        || _markers.Contains((streamId, PerspectivePurgeMarkers.ALL_PERSPECTIVES)));
    }

    public Task MarkPurgedAsync(Guid streamId, string perspectiveName, Guid? purgeEventId, CancellationToken cancellationToken = default) {
      _markers.Add((streamId, perspectiveName));
      Marked.Add((streamId, perspectiveName, purgeEventId));
      return Task.CompletedTask;
    }

    public Task ClearAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      _markers.Remove((streamId, perspectiveName));
      Cleared.Add((streamId, perspectiveName));
      return Task.CompletedTask;
    }
  }

  private sealed class RowStore : IPerspectiveStore<PurgeStaysModel> {
    private readonly Dictionary<Guid, PurgeStaysModel> _rows = [];

    public Task<PurgeStaysModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, PurgeStaysModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, PurgeStaysModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<PurgeStaysModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<PurgeStaysModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, PurgeStaysModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
      _rows.Remove(streamId);
      return Task.CompletedTask;
    }

    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;
  }
}
