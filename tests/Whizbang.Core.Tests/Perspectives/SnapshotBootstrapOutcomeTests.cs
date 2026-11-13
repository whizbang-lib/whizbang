// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>Folded by <see cref="BootstrapOutcomePerspective"/> only.</summary>
public sealed record BootstrapOutcomeAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

/// <summary>The row <see cref="BootstrapOutcomePerspective"/> folds.</summary>
public sealed class BootstrapOutcomeModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>A Sourced perspective: it keeps a rewind floor, so a bootstrap writes a snapshot of a live row.</summary>
public sealed class BootstrapOutcomePerspective : IPerspectiveFor<BootstrapOutcomeModel, BootstrapOutcomeAdded> {
  public BootstrapOutcomeModel Apply(BootstrapOutcomeModel currentData, BootstrapOutcomeAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>
/// #2294 over the real generated runner: a bootstrap snapshot says what it did. A bootstrap that found no row returned
/// exactly as one that wrote a snapshot, so the reap-driven pass logged success for a snapshot that was never taken,
/// and the bodies it was meant to cover were held for good.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots#bootstrap</docs>
public class SnapshotBootstrapOutcomeTests {
  [Test]
  public async Task Bootstrap_OfALiveRow_WritesTheSnapshot_AndReportsWrittenAsync() {
    var streamId = Guid.NewGuid();
    var at = Guid.NewGuid();
    var rows = new InMemoryRows<BootstrapOutcomeModel>();
    await rows.UpsertAsync(streamId, new BootstrapOutcomeModel { Id = streamId, Total = 3 });
    var snapshots = new RecordingSnapshotStore();
    var runner = _runner<BootstrapOutcomePerspective, BootstrapOutcomeModel>(rows, snapshots, enabled: true, logs: null);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<BootstrapOutcomePerspective>(), at);

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.Written);
    await Assert.That(snapshots.Written).IsEquivalentTo([(streamId, at)])
      .Because("the positive control: a live row is snapshotted at the requested event");
  }

  [Test]
  public async Task Bootstrap_OfAMissingRow_WritesNothing_ReportsRowMissing_AndLogsItAsync() {
    var streamId = Guid.NewGuid();
    var snapshots = new RecordingSnapshotStore();
    var logs = new List<(LogLevel Level, string Message)>();
    var runner = _runner<BootstrapOutcomePerspective, BootstrapOutcomeModel>(
      new InMemoryRows<BootstrapOutcomeModel>(), snapshots, enabled: true, logs);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<BootstrapOutcomePerspective>(), Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.RowMissing)
      .Because("a purged or never-written row has nothing to snapshot, and the caller must be told so");
    await Assert.That(snapshots.Written).IsEmpty();
    await Assert.That(logs.Any(l => l.Message.Contains("has no row to snapshot", StringComparison.Ordinal)
        && l.Message.Contains(streamId.ToString(), StringComparison.Ordinal))).IsTrue()
      .Because("the missing row is logged, naming the stream, instead of returning in silence");
  }

  [Test]
  public async Task Bootstrap_WithSnapshotsDisabled_WritesNothing_AndReportsSnapshotsOffAsync() {
    var streamId = Guid.NewGuid();
    var rows = new InMemoryRows<BootstrapOutcomeModel>();
    await rows.UpsertAsync(streamId, new BootstrapOutcomeModel { Id = streamId, Total = 1 });
    var snapshots = new RecordingSnapshotStore();
    var runner = _runner<BootstrapOutcomePerspective, BootstrapOutcomeModel>(rows, snapshots, enabled: false, logs: null);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<BootstrapOutcomePerspective>(), Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.SnapshotsOff);
    await Assert.That(snapshots.Written).IsEmpty();
  }

  [Test]
  public async Task Bootstrap_WithoutASnapshotStore_ReportsSnapshotsOffAsync() {
    var streamId = Guid.NewGuid();
    var rows = new InMemoryRows<BootstrapOutcomeModel>();
    await rows.UpsertAsync(streamId, new BootstrapOutcomeModel { Id = streamId, Total = 1 });
    var runner = _runner<BootstrapOutcomePerspective, BootstrapOutcomeModel>(rows, snapshots: null, enabled: true, logs: null);

    var outcome = await runner.TryBootstrapSnapshotAsync(streamId, _name<BootstrapOutcomePerspective>(), Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.SnapshotsOff);
  }

  [Test]
  public async Task BootstrapSnapshotAsync_IsTheSameBootstrap_WithItsOutcomeDiscardedAsync() {
    var streamId = Guid.NewGuid();
    var at = Guid.NewGuid();
    var rows = new InMemoryRows<BootstrapOutcomeModel>();
    await rows.UpsertAsync(streamId, new BootstrapOutcomeModel { Id = streamId, Total = 2 });
    var snapshots = new RecordingSnapshotStore();
    var runner = _runner<BootstrapOutcomePerspective, BootstrapOutcomeModel>(rows, snapshots, enabled: true, logs: null);

    await runner.BootstrapSnapshotAsync(streamId, _name<BootstrapOutcomePerspective>(), at);

    await Assert.That(snapshots.Written).IsEquivalentTo([(streamId, at)]);
  }

  [Test]
  public async Task TryBootstrap_OnARunnerThatImplementsOnlyBootstrap_RunsIt_AndReportsNotReportedAsync() {
    IPerspectiveRunner runner = new BootstrapOnlyRunner();

    var outcome = await runner.TryBootstrapSnapshotAsync(Guid.NewGuid(), "P", Guid.NewGuid());

    await Assert.That(outcome).IsEqualTo(SnapshotBootstrapOutcome.NotReported)
      .Because("a runner that says nothing about what it did cannot be counted as having written a snapshot");
    await Assert.That(((BootstrapOnlyRunner)runner).Calls).IsEqualTo(1);
  }

  internal static string _name<TPerspective>() => typeof(TPerspective).FullName!;

  internal static IPerspectiveRunner _runner<TPerspective, TModel>(
      IPerspectiveStore<TModel> rows,
      IPerspectiveSnapshotStore? snapshots,
      bool enabled,
      List<(LogLevel Level, string Message)>? logs,
      PerspectiveSnapshotOptions? options = null)
      where TPerspective : class
      where TModel : class {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TPerspective>();
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not compile in a workspace
    // that loads without running the generators (the formatting gate).
    var runnerType = typeof(SnapshotBootstrapOutcomeTests).Assembly
      .GetType($"Whizbang.Core.Tests.Generated.{typeof(TPerspective).Name}Runner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(runnerType), logs ?? [])!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = new EmptyEventStore();
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    var snapshotOptions = options ?? new PerspectiveSnapshotOptions();
    snapshotOptions.Enabled = enabled;
    args[7] = Options.Create(snapshotOptions);
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  private sealed class BootstrapOnlyRunner : IPerspectiveRunner {
    public int Calls { get; private set; }
    public Type PerspectiveType => typeof(object);
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) {
      Calls++;
      return Task.CompletedTask;
    }
    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>Records every log line the runner writes, by level.</summary>
  internal sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      sink.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>Records the (stream, event) of every snapshot written; holds none to read back.</summary>
  internal sealed class RecordingSnapshotStore : IPerspectiveSnapshotStore {
    public List<(Guid StreamId, Guid EventId)> Written { get; } = [];

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) {
      Written.Add((streamId, snapshotEventId));
      return Task.CompletedTask;
    }

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>(null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(Written.Exists(w => w.StreamId == streamId));

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  /// <summary>An event store with no events; a bootstrap reads only the commit sequence of its event.</summary>
  internal sealed class EmptyEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  internal sealed class InMemoryRows<TModel> : IPerspectiveStore<TModel> where TModel : class {
    private readonly Dictionary<Guid, TModel> _rows = [];

    public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<TModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
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
