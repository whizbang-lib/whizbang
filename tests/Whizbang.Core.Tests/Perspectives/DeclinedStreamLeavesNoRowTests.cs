// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A run of some saga starts; a progress perspective keeps only its own saga's runs.</summary>
public sealed record DeclineRunStarted : IEvent {
  [StreamId]
  public Guid RunId { get; init; }
  public string SagaName { get; init; } = string.Empty;
  public int TotalItems { get; init; }
}

/// <summary>A run of some saga dispatched more items.</summary>
public sealed record DeclineRunAdvanced : IEvent {
  [StreamId]
  public Guid RunId { get; init; }
  public string SagaName { get; init; } = string.Empty;
  public int Items { get; init; }
}

/// <summary>A run of some saga was withdrawn; the perspective soft-deletes its own runs.</summary>
public sealed record DeclineRunWithdrawn : IEvent {
  [StreamId]
  public Guid RunId { get; init; }
}

public sealed class DeclineProgressModel {
  [StreamId]
  public Guid Id { get; set; }
  public string SagaName { get; set; } = string.Empty;
  public int TotalItems { get; set; }
  public int Dispatched { get; set; }
}

/// <summary>
/// The shape issue #1677 describes: several progress perspectives subscribe to the shared saga lifecycle events, and
/// each declines every event of a saga that is not its own with <see cref="ApplyResult{TModel}.None"/>.
/// </summary>
public sealed class DeclineProgressPerspective :
    IPerspectiveWithActionsFor<DeclineProgressModel, DeclineRunStarted>,
    IPerspectiveWithActionsFor<DeclineProgressModel, DeclineRunAdvanced>,
    IPerspectiveWithActionsFor<DeclineProgressModel, DeclineRunWithdrawn> {
  public const string OWN_SAGA = "own-saga";

  public ApplyResult<DeclineProgressModel> Apply(DeclineProgressModel currentData, DeclineRunStarted @event) =>
    @event.SagaName != OWN_SAGA
      ? ApplyResult<DeclineProgressModel>.None()
      : ApplyResult<DeclineProgressModel>.Update(new DeclineProgressModel {
        Id = @event.RunId,
        SagaName = @event.SagaName,
        TotalItems = @event.TotalItems,
      });

  public ApplyResult<DeclineProgressModel> Apply(DeclineProgressModel currentData, DeclineRunAdvanced @event) =>
    @event.SagaName != OWN_SAGA
      ? ApplyResult<DeclineProgressModel>.None()
      : ApplyResult<DeclineProgressModel>.Update(new DeclineProgressModel {
        Id = currentData.Id,
        SagaName = currentData.SagaName,
        TotalItems = currentData.TotalItems,
        Dispatched = currentData.Dispatched + @event.Items,
      });

  public ApplyResult<DeclineProgressModel> Apply(DeclineProgressModel currentData, DeclineRunWithdrawn @event) =>
    ApplyResult<DeclineProgressModel>.Delete();
}

/// <summary>
/// Issue #1677 over the real generated runner: a perspective that declines every event of a stream leaves no row for
/// it, whatever the batching, rewind or concurrency. The live drain already kept the scaffolded default it builds as
/// Apply input from being written; the rewind wrote it, so a foreign stream that was ever rewound got a blank row.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions</docs>
public class DeclinedStreamLeavesNoRowTests {
  private const string PERSPECTIVE_NAME = nameof(DeclineProgressPerspective);
  private const string FOREIGN_SAGA = "another-saga";

  [Test]
  public async Task LiveBatch_FirstEventDeclined_AndALaterEventDeclined_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = FOREIGN_SAGA, TotalItems = 5 });
    var advanced = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 2 });

    var completion = await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [started, advanced], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("every event of the stream was declined, so no Apply produced a model to store");
    await Assert.That(completion.Status).IsEqualTo(PerspectiveProcessingStatus.Completed)
      .Because("declined events are done: their work rows are released and the cursor moves past them");
    await Assert.That(completion.RowChanged).IsFalse();
  }

  [Test]
  public async Task Rewind_OfAStreamWhoseEventsAreAllDeclined_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = FOREIGN_SAGA, TotalItems = 5 });
    var advanced = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 2 });
    await harness.Events.AppendAsync(streamId, started);
    await harness.Events.AppendAsync(streamId, advanced);

    var completion = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("a full replay starts from a scaffolded default for Apply to read; it is not a model any Apply produced");
    await Assert.That(completion.RowChanged).IsFalse();
    await Assert.That(completion.LastEventId).IsEqualTo(advanced.MessageId.Value)
      .Because("the replayed events are still done, so the cursor moves to the frontier");
  }

  [Test]
  public async Task ConcurrentClaims_TheLaterEventFirst_ThenTheEarlierOneRewound_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    // Committed in this order, but split across two claims: the claim holding the later event finishes first.
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = FOREIGN_SAGA, TotalItems = 5 });
    var advanced = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 2 });
    await harness.Events.AppendAsync(streamId, started);
    await harness.Events.AppendAsync(streamId, advanced);

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [advanced], CancellationToken.None);
    // The earlier event now sits behind the cursor, so the worker rewinds the stream for it.
    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("however the stream's events were claimed, none was accepted, so the stream has no row");
  }

  [Test]
  public async Task Rewind_WithSnapshotsOn_OfAStreamWhoseEventsAreAllDeclined_SnapshotsNothingAsync() {
    var streamId = Guid.NewGuid();
    var snapshots = new RecordingSnapshotStore();
    var harness = new Harness(snapshots, new PerspectiveSnapshotOptions { Enabled = true, RewindSnapshotIntervalEvents = 1 });
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = FOREIGN_SAGA, TotalItems = 5 });
    var advanced = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 2 });
    await harness.Events.AppendAsync(streamId, started);
    await harness.Events.AppendAsync(streamId, advanced);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
    await Assert.That(snapshots.Created).IsEmpty()
      .Because("a snapshot of the scaffold would be restored by a later rewind as if an Apply had produced it, and written as the row");
  }

  [Test]
  public async Task Rewind_OfAStreamWithAnAcceptedEvent_WritesTheRowItFoldsAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = DeclineProgressPerspective.OWN_SAGA, TotalItems = 5 });
    var advanced = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = DeclineProgressPerspective.OWN_SAGA, Items = 2 });
    await harness.Events.AppendAsync(streamId, started);
    await harness.Events.AppendAsync(streamId, advanced);

    var completion = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.TotalItems).IsEqualTo(5);
    await Assert.That(row.Dispatched).IsEqualTo(2)
      .Because("the stream's own events are accepted, so the rewind writes the row they fold to");
    await Assert.That(completion.RowChanged).IsTrue();
  }

  [Test]
  public async Task Rewind_FromASnapshot_WithEveryLaterEventDeclined_KeepsWritingTheSnapshotsModelAsync() {
    var streamId = Guid.NewGuid();
    var snapshotEventId = Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _clock)));
    var snapshots = new RecordingSnapshotStore {
      Restorable = (snapshotEventId, _snapshotOf(new DeclineProgressModel {
        Id = streamId,
        SagaName = DeclineProgressPerspective.OWN_SAGA,
        TotalItems = 9,
      })),
    };
    var harness = new Harness(snapshots, new PerspectiveSnapshotOptions { Enabled = true });
    var foreign = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 2 });
    await harness.Events.AppendAsync(streamId, foreign);

    var completion = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, foreign.MessageId.Value, CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull()
      .Because("a snapshot holds a model an earlier Apply produced, so the rewind writes it even when nothing after it is accepted");
    await Assert.That(row!.TotalItems).IsEqualTo(9);
    await Assert.That(completion.RowChanged).IsTrue();
  }

  [Test]
  public async Task Rewind_WhereADeclinedEventPrecedesAnAcceptedOne_WritesOnlyWhatTheAcceptedOneFoldsAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var foreign = _envelope(new DeclineRunAdvanced { RunId = streamId, SagaName = FOREIGN_SAGA, Items = 7 });
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = DeclineProgressPerspective.OWN_SAGA, TotalItems = 3 });
    await harness.Events.AppendAsync(streamId, foreign);
    await harness.Events.AppendAsync(streamId, started);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, foreign.MessageId.Value, CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.TotalItems).IsEqualTo(3);
    await Assert.That(row.Dispatched).IsEqualTo(0);
  }

  [Test]
  public async Task LiveBatch_ASoftDeleteWithNoModel_OnAStreamWithNoRow_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var withdrawn = _envelope(new DeclineRunWithdrawn { RunId = streamId });

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [withdrawn], CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull()
      .Because("a soft delete that returns no model has nothing to mark on a stream with no row; storing the scaffold invents one");
  }

  [Test]
  public async Task Rewind_ASoftDeleteWithNoModel_OnAStreamWithNoRow_LeavesNoRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var withdrawn = _envelope(new DeclineRunWithdrawn { RunId = streamId });
    await harness.Events.AppendAsync(streamId, withdrawn);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, withdrawn.MessageId.Value, CancellationToken.None);

    await Assert.That(await harness.Rows.GetByStreamIdAsync(streamId)).IsNull();
  }

  [Test]
  public async Task LiveBatch_ASoftDeleteWithNoModel_OnALiveRow_KeepsTheRowAsync() {
    var streamId = Guid.NewGuid();
    var harness = new Harness();
    var started = _envelope(new DeclineRunStarted { RunId = streamId, SagaName = DeclineProgressPerspective.OWN_SAGA, TotalItems = 4 });
    var withdrawn = _envelope(new DeclineRunWithdrawn { RunId = streamId });

    await harness.Runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [started, withdrawn], CancellationToken.None);

    var row = await harness.Rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.TotalItems).IsEqualTo(4)
      .Because("on a stream an Apply already produced a model for, a soft delete keeps that model");
  }

  // -------------------------------------------------------------------------------------------

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public RowStore Rows { get; } = new();
    public IPerspectiveRunner Runner { get; }

    public Harness(IPerspectiveSnapshotStore? snapshots = null, PerspectiveSnapshotOptions? snapshotOptions = null) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<DeclineProgressPerspective>();
      var provider = services.BuildServiceProvider();
      // The real generated runner, resolved by name: naming a generated type in source does not
      // compile in a workspace that loads without running the generators (the formatting gate).
      var runnerType = typeof(DeclinedStreamLeavesNoRowTests).Assembly
        .GetType("Whizbang.Core.Tests.Generated.DeclineProgressPerspectiveRunner", throwOnError: true)!;
      var logger = Activator.CreateInstance(typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(runnerType))!;
      var ctor = runnerType.GetConstructors().Single();
      var args = new object?[ctor.GetParameters().Length];
      args[0] = provider;
      args[1] = logger;
      args[2] = Events;
      args[3] = Rows;
      args[4] = provider.GetRequiredService<IServiceScopeFactory>();
      args[6] = snapshots;
      args[7] = snapshotOptions is null ? null : Options.Create(snapshotOptions);
      Runner = (IPerspectiveRunner)ctor.Invoke(args);
    }
  }

  private static long _clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

  /// <summary>An envelope whose id sorts after every earlier one, so id order is the order the test wrote them in.</summary>
  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.From(Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Increment(ref _clock)))),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(_clock),
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private static JsonDocument _snapshotOf(DeclineProgressModel model) {
    var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<DeclineProgressModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(DeclineProgressModel));
    using var doc = JsonSerializer.SerializeToDocument(model, info);
    return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
  }

  /// <summary>Records every snapshot the runner writes, and restores one when the test gives it one.</summary>
  private sealed class RecordingSnapshotStore : IPerspectiveSnapshotStore {
    public List<(Guid StreamId, Guid SnapshotEventId)> Created { get; } = [];
    public (Guid SnapshotEventId, JsonDocument Data)? Restorable { get; init; }

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
      Created.Add((streamId, snapshotEventId));
      return Task.CompletedTask;
    }

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(Restorable);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(Restorable);

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(Restorable is { } r ? ((Guid, int?, JsonDocument)?)(r.SnapshotEventId, 1, r.Data) : null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Restorable is not null);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  private sealed class RowStore : IPerspectiveStore<DeclineProgressModel> {
    private readonly Dictionary<Guid, DeclineProgressModel> _rows = [];

    public Task<DeclineProgressModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, DeclineProgressModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, DeclineProgressModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<DeclineProgressModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<DeclineProgressModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, DeclineProgressModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
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
