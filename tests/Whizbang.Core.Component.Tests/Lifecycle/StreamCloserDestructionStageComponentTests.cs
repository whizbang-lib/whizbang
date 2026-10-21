// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.DestructionStages;
using Whizbang.Core.Component.Tests.Generated;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// #1504, against the REAL generated receptor registry and invoker and the closer the worker pipeline registers: a
/// period close runs the destruction lifecycle stages around its truncate, the way the ephemeral reap does. Before the
/// fix a close ran only the <see cref="IDestructionHook"/>, and a receptor declared at a destruction stage never saw the
/// events the close removed.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/StreamCloser.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Lifecycle/DestructionStageReceptors.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <docs>fundamentals/lifecycle/lifecycle-stages#destruction</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class StreamCloserDestructionStageComponentTests {
  private const long THROUGH_VERSION = 7;

  [Test]
  public async Task Close_RunsThePreStagesBeforeTheTruncateAndThePostStagesAfterItAsync() {
    var id = Guid.CreateVersion7();
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent));

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);
    await DestructionProbe.RanAsync(id, LifecycleStage.PreDestructionDetached).WaitAsync(TimeSpan.FromSeconds(30));
    await DestructionProbe.RanAsync(id, LifecycleStage.PostDestructionDetached).WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(fixture.Coordinator.TargetRequests).IsEquivalentTo([(id, THROUGH_VERSION)])
      .Because("the events read before the truncate are exactly the ones the close removes: the stream, through the close point");
    await Assert.That(fixture.Coordinator.StagesSeenAtTheTruncate).Contains(LifecycleStage.PreDestructionInline)
      .Because("Pre Inline runs, awaited, before the detail is truncated");
    await Assert.That(fixture.Coordinator.StagesSeenAtTheTruncate).DoesNotContain(LifecycleStage.PostDestructionInline)
      .Because("Post runs only after the truncate");
    var stages = DestructionProbe.Stages(id);
    await Assert.That(stages.Count(s => s == LifecycleStage.PreDestructionInline)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PostDestructionInline)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PreDestructionDetached)).IsEqualTo(1);
    await Assert.That(stages.Count(s => s == LifecycleStage.PostDestructionDetached)).IsEqualTo(1);
  }

  [Test]
  public async Task AThrowingPreInlineReceptor_VetoesTheCloseAsync() {
    var id = Guid.CreateVersion7();
    DestructionProbe.FailPreInlineFor(id);
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent));

    await Assert.That(async () => await fixture.Closer.CloseAsync(id, THROUGH_VERSION)).Throws<InvalidOperationException>()
      .Because("a throwing Pre Inline receptor aborts the close, as a throwing destruction hook does");

    await Assert.That(fixture.Coordinator.CloseCalls).IsEqualTo(0)
      .Because("durable detail is never truncated when the work that had to precede it failed");
    await Assert.That(DestructionProbe.Stages(id)).DoesNotContain(LifecycleStage.PostDestructionInline);
    await Assert.That(fixture.Log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains(id.ToString(), StringComparison.Ordinal)))
      .IsTrue().Because("the aborted close is logged with the stream it refused");
  }

  [Test]
  public async Task AHookThatCancels_RunsNoDestructionStageAsync() {
    var id = Guid.CreateVersion7();
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent), hook: new CancelingHook());

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);

    await Assert.That(result.Status).IsEqualTo("canceled");
    await Assert.That(fixture.Coordinator.CloseCalls).IsEqualTo(0);
    await Assert.That(DestructionProbe.Stages(id)).IsEmpty()
      .Because("a vetoed close destroys nothing, so no destruction stage runs");
  }

  [Test]
  public async Task ACloseThatTruncatesNothing_RunsNoPostStageAsync() {
    var id = Guid.CreateVersion7();
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent), closeStatus: "blocked");

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);
    await DestructionProbe.RanAsync(id, LifecycleStage.PreDestructionDetached).WaitAsync(TimeSpan.FromSeconds(30));

    await Assert.That(result.Status).IsEqualTo("blocked");
    await Assert.That(DestructionProbe.Stages(id)).Contains(LifecycleStage.PreDestructionInline);
    await Assert.That(DestructionProbe.Stages(id)).DoesNotContain(LifecycleStage.PostDestructionInline)
      .Because("the Post stages run only on a close that truncated, exactly as the post hook does");
  }

  [Test]
  public async Task AnEventWithNoDestructionReceptor_IsNotLoadedOrHandedToAnyStageAsync() {
    var id = Guid.CreateVersion7();
    var fixture = await _buildAsync(id, typeof(DsUnwatchedEvent));

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(DestructionProbe.Stages(id)).IsEmpty();
    await Assert.That(fixture.Store.Reads).IsEqualTo(0).Because("only events a destruction receptor handles are read before the truncate");
  }

  [Test]
  public async Task WithNoDestructionReceptorAnywhere_TheRangeIsNeverListedAsync() {
    var id = Guid.CreateVersion7();
    var fixture = await _buildAsync(id, typeof(DsUnwatchedEvent), catalogOnly: [typeof(DsUnwatchedEvent)]);

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);

    await Assert.That(result.Status).IsEqualTo("closed");
    await Assert.That(fixture.Coordinator.TargetRequests).IsEmpty()
      .Because("with no destruction-stage receptor registered, a close reads nothing it did not read before");
  }

  [Test]
  public async Task AThrowingPostInlineReceptor_IsLoggedAndTheCloseStandsAsync() {
    var id = Guid.CreateVersion7();
    DestructionProbe.FailPostInlineFor(id);
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent));

    var result = await fixture.Closer.CloseAsync(id, THROUGH_VERSION);

    await Assert.That(result.Status).IsEqualTo("closed")
      .Because("the truncate already committed: a failing Post stage cannot undo it, so it is reported, not thrown");
    var warning = fixture.Log.Entries.Single(e => e.Level == LogLevel.Warning);
    await Assert.That(warning.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(warning.Message).Contains(id.ToString());
  }

  [Test]
  public async Task APreInlineReceptorCanceled_PropagatesAndRecordsNoFailureAsync() {
    var id = Guid.CreateVersion7();
    DestructionProbe.CancelPreInlineFor(id);
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent));

    await Assert.That(async () => await fixture.Closer.CloseAsync(id, THROUGH_VERSION)).Throws<OperationCanceledException>();

    await Assert.That(fixture.Coordinator.CloseCalls).IsEqualTo(0);
    await Assert.That(fixture.Log.Entries).IsEmpty()
      .Because("a canceled close is shutdown, not a failing receptor, so nothing is logged as one");
  }

  [Test]
  public async Task APostInlineReceptorCanceled_SurfacesEvenThoughTheCloseCommittedAsync() {
    var id = Guid.CreateVersion7();
    DestructionProbe.CancelPostInlineFor(id);
    var fixture = await _buildAsync(id, typeof(DsEphemeralEvent));

    await Assert.That(async () => await fixture.Closer.CloseAsync(id, THROUGH_VERSION)).Throws<OperationCanceledException>();

    await Assert.That(fixture.Coordinator.CloseCalls).IsEqualTo(1);
    await Assert.That(fixture.Log.Entries).IsEmpty();
  }

  private static async Task<Fixture> _buildAsync(Guid id, Type eventType, IDestructionHook? hook = null,
      string closeStatus = "closed", Type[]? catalogOnly = null) {
    var store = new CountingEventStore();
    object payload = eventType == typeof(DsEphemeralEvent) ? new DsEphemeralEvent(id) : new DsUnwatchedEvent(id);
    await store.AppendAsync(id, new MessageEnvelope<IEvent> {
      MessageId = new MessageId(id),
      Payload = (IEvent)payload,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
    });
    var coordinator = new CloseCoordinator(id, closeStatus) {
      Targets = [new EphemeralDestructionTarget(id, id, $"{eventType.FullName}, {eventType.Assembly.GetName().Name}")],
    };
    var log = new ListLoggerProvider(typeof(StreamCloser).FullName!);
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<ILoggerFactory>(new LoggerFactory([log]));
    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEventStore>(store);
    if (hook is not null) {
      services.AddSingleton(hook);
    }
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    if (catalogOnly is null) {
      // The generated message-type catalog, which AddWhizbang() registers through this callback
      ServiceRegistrationCallbacks.MessageTypeCatalog?.Invoke(services);
    } else {
      services.AddSingleton<IMessageTypeCatalog>(new FixedCatalog(catalogOnly));
    }
    // The closer exactly as the worker pipeline registers it
    services.AddWhizbangWorkers();
    var provider = services.BuildServiceProvider();
    return new Fixture(provider.GetRequiredService<IStreamCloser>(), coordinator, store, log);
  }

  private sealed record Fixture(IStreamCloser Closer, CloseCoordinator Coordinator, CountingEventStore Store, ListLoggerProvider Log);

  private sealed class CancelingHook : IDestructionHook {
    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(DestructionResult.Canceled);
    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class FixedCatalog(Type[] types) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [.. types.Select(t => new MessageTypeCatalogEntry(t, t.FullName!, "Event", PinnedId: null))];
  }

  /// <summary>Records every entry logged under one category, with its level.</summary>
  private sealed class ListLoggerProvider(string category) : ILoggerProvider {
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public (LogLevel Level, string Message, Exception? Exception)[] Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) =>
      categoryName == category ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }

    private sealed class Logger(ListLoggerProvider owner) : ILogger {
      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(LogLevel logLevel) => true;
      public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        owner._entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
  }

  /// <summary>An in-memory event store that counts the reads the destruction stages make.</summary>
  private sealed class CountingEventStore : IEventStore {
    private readonly InMemoryEventStore _inner = new();
    public int Reads { get; private set; }

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId,
        IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      Reads++;
      return _inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      _inner.AppendAsync(streamId, envelope, cancellationToken);
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
      _inner.AppendAsync(streamId, message, cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      _inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      _inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _inner.GetLastSequenceAsync(streamId, cancellationToken);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      _inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }

  /// <summary>A coordinator whose close lists the given targets and records the stages that ran before its truncate.</summary>
  private sealed class CloseCoordinator(Guid watched, string closeStatus) : IWorkCoordinator {
    public List<EphemeralDestructionTarget> Targets { get; init; } = [];
    public List<(Guid StreamId, long ThroughVersion)> TargetRequests { get; } = [];
    public List<LifecycleStage> StagesSeenAtTheTruncate { get; } = [];
    public int CloseCalls { get; private set; }

    public Task<IReadOnlyList<EphemeralDestructionTarget>> GetStreamCloseTargetsAsync(Guid streamId, long throughVersion,
        CancellationToken cancellationToken = default) {
      TargetRequests.Add((streamId, throughVersion));
      return Task.FromResult<IReadOnlyList<EphemeralDestructionTarget>>(Targets);
    }

    public Task<StreamCloseResult> CloseStreamAsync(Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      CloseCalls++;
      StagesSeenAtTheTruncate.AddRange(DestructionProbe.Stages(watched));
      return Task.FromResult(new StreamCloseResult(closeStatus, closeStatus == "closed" ? 1 : 0));
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
