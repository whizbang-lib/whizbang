// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives.RunnerBase;

/// <summary>An event the runner-base tests fold: it appends its mark to the row.</summary>
public sealed record RbMarked : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public string Mark { get; init; } = "m";
}

/// <summary>A second event type the tests fold, so a perspective folds more than one type.</summary>
public sealed record RbOther : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>An event the perspective does not fold.</summary>
public sealed record RbIgnored : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>An event that sets the row's scope.</summary>
public sealed record RbScoped : IScopeEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public PerspectiveScope Scope { get; init; } = new();
}

/// <summary>A collective event: another stream's set-based mutation the runner folds on replay.</summary>
public sealed record RbCollective : ICollectiveEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
}

/// <summary>The runner-base tests' model: the marks of the events folded into it.</summary>
public sealed record RbModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Marks { get; init; } = string.Empty;
}

/// <summary>
/// The perspective the runner-base tests derive their runner for (#2469). It is a real perspective so the generator
/// registers its model for snapshot serialization; the tests' runner decides what each Apply does.
/// </summary>
public sealed class RbPerspective :
    IPerspectiveFor<RbModel, RbMarked>,
    IPerspectiveFor<RbModel, RbOther>,
    IPerspectiveFor<RbModel, RbScoped>,
    IPerspectiveScopeFor<RbModel> {
  public RbModel Apply(RbModel currentData, RbMarked @event) => currentData with { Marks = currentData.Marks + @event.Mark };
  public RbModel Apply(RbModel currentData, RbOther @event) => currentData with { Marks = currentData.Marks + "o" };
  public RbModel Apply(RbModel currentData, RbScoped @event) => currentData with { Marks = currentData.Marks + "s" };

  /// <summary>Keeps the current tenant and takes the proposed user, so the test sees both scopes were consulted.</summary>
  public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) =>
    new() { TenantId = currentScope.TenantId ?? proposedScope.TenantId, UserId = proposedScope.UserId };
}

/// <summary>What a test fixes about its perspective: the traits the generator would emit.</summary>
public sealed record RbTraits {
  public bool IsEphemeral { get; init; }
  public bool KeepsRewindFloor { get; init; } = true;
  public bool IsOverwriteOnly { get; init; }
  public bool HasScopeInterface { get; init; }
  public bool SnapshotsBeforeWrite { get; init; }
  public string ArchivedStreamRowsName { get; init; } = "";
  public ScopeFields? InheritScopeOnCreate { get; init; }
  public ScopeFields? InheritScopeAlways { get; init; }
}

/// <summary>
/// A runner derived from the shared base by hand, as the generator derives one (#2469): each hook a generated runner
/// emits is a delegate a test can set, and an unset hook takes the base's default.
/// </summary>
internal sealed class RbRunner(
    RbTraits traits,
    IServiceProvider serviceProvider,
    ILogger logger,
    IEventStore eventStore,
    IPerspectiveStore<RbModel> perspectiveStore,
    IOptionsMonitor<TracingOptions>? tracingOptions,
    IPerspectiveSnapshotStore? snapshotStore,
    IOptions<PerspectiveSnapshotOptions>? snapshotOptions,
    IPerspectiveApplyCoordinator? applyCoordinator,
    ICollectiveReplayApplier? collectiveReplayApplier)
    : PerspectiveRunnerBase<RbPerspective, RbModel>(_traitsOf(traits), serviceProvider, logger, eventStore, perspectiveStore,
        serviceProvider.GetRequiredService<IServiceScopeFactory>(), tracingOptions, snapshotStore, snapshotOptions,
        applyCoordinator, collectiveReplayApplier) {
  private static RunnerTraits _traitsOf(RbTraits traits) {
    var built = new RunnerTraits {
      EventTypes = [typeof(RbMarked), typeof(RbOther), typeof(RbScoped)],
      ArchivedStreamRowsName = traits.ArchivedStreamRowsName,
      IsEphemeral = traits.IsEphemeral,
      KeepsRewindFloor = traits.KeepsRewindFloor,
      IsOverwriteOnly = traits.IsOverwriteOnly,
      HasScopeInterface = traits.HasScopeInterface,
      SnapshotsBeforeWrite = traits.SnapshotsBeforeWrite,
    };
    return traits.InheritScopeOnCreate is null && traits.InheritScopeAlways is null
      ? built
      : new RunnerTraits {
        EventTypes = built.EventTypes,
        ArchivedStreamRowsName = built.ArchivedStreamRowsName,
        IsEphemeral = built.IsEphemeral,
        KeepsRewindFloor = built.KeepsRewindFloor,
        IsOverwriteOnly = built.IsOverwriteOnly,
        HasScopeInterface = built.HasScopeInterface,
        SnapshotsBeforeWrite = built.SnapshotsBeforeWrite,
        InheritScopeOnCreate = traits.InheritScopeOnCreate ?? built.InheritScopeOnCreate,
        InheritScopeAlways = traits.InheritScopeAlways ?? built.InheritScopeAlways,
      };
  }

  /// <summary>Replaces the perspective's Apply: returns the model and action, or throws.</summary>
  public Func<RbModel?, IEvent, (RbModel?, ModelAction)>? Apply { get; set; }

  public Func<IEvent, bool>? Purges { get; set; }
  public Func<IEvent, bool>? DecidesAlone { get; set; }
  public Func<RbModel, Dictionary<string, object?>?>? Physical { get; set; }
  public Func<RbModel, RbModel>? Strip { get; set; }

  protected override RbModel CreateEmptyModel(Guid streamId) => new() { Id = streamId };

  protected override (RbModel?, ModelAction) ApplyEventCore(RbPerspective perspective, RbModel? currentModel, IEvent @event) {
    if (Apply is not null) {
      return Apply(currentModel, @event);
    }
    return @event switch {
      RbMarked marked => (perspective.Apply(currentModel!, marked), ModelAction.None),
      RbOther other => (perspective.Apply(currentModel!, other), ModelAction.None),
      RbScoped scoped => (perspective.Apply(currentModel!, scoped), ModelAction.None),
      _ => throw new InvalidOperationException($"no Apply for {@event.GetType().Name}"),
    };
  }

  protected override bool IsUnconditionalPurge(IEvent @event) => Purges?.Invoke(@event) ?? base.IsUnconditionalPurge(@event);
  protected override bool DecidesTheRowAlone(IEvent @event) => DecidesAlone?.Invoke(@event) ?? base.DecidesTheRowAlone(@event);
  protected override Dictionary<string, object?>? PhysicalFieldValues(RbModel model) =>
    Physical is null ? base.PhysicalFieldValues(model) : Physical(model);
  protected override RbModel StripPhysicalFields(RbModel model) => Strip?.Invoke(model) ?? base.StripPhysicalFields(model);
}

/// <summary>Builds envelopes for the runner-base tests.</summary>
internal static class Rb {
  public static readonly DateTimeOffset Minted = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

  private static ScopeDelta? _scopeOf(string? tenant) =>
    tenant is null ? null : ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = tenant });

  private static List<MessageHop> _hopsOf(bool hops, string? tenant) {
    if (!hops) {
      return [];
    }
    return [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = Minted,
        Scope = _scopeOf(tenant),
      },
    ];
  }

  /// <summary>An envelope; <paramref name="hops"/> false gives it no hops, and <paramref name="nullHops"/> a null list.</summary>
  public static MessageEnvelope<IEvent> Envelope(IEvent payload, Guid? id = null, string? tenant = null, bool hops = true,
      bool nullHops = false) => new() {
        MessageId = id is { } given ? MessageId.From(given) : MessageId.New(),
        Payload = payload,
        Hops = nullHops ? null! : _hopsOf(hops, tenant),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      };

  public static JsonDocument SnapshotOf(RbModel model) {
    var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RbModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RbModel));
    using var doc = JsonSerializer.SerializeToDocument(model, info);
    return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
  }

  /// <summary>A snapshot written by a serialization version this build cannot read.</summary>
  public static JsonDocument StaleSnapshot() {
    using var model = JsonDocument.Parse("{}");
    return SnapshotEnvelope.Wrap(model.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT + 1);
  }

  public static RbModel ModelOf(JsonDocument snapshot) {
    var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RbModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RbModel));
    var read = SnapshotEnvelope.Read(snapshot, Whizbang.Core.Serialization.SerializationVersion.CURRENT, SnapshotUpgradePolicy.RebuildFromEvents);
    return JsonSerializer.Deserialize(read.Model.GetRawText(), info)!;
  }

  public static async Task WithModeAsync(ProcessingMode mode, Func<Task> body) {
    var previous = ProcessingModeAccessor.Current;
    ProcessingModeAccessor.Current = mode;
    try {
      await body();
    } finally {
      ProcessingModeAccessor.Current = previous;
    }
  }
}

/// <summary>
/// The event store of the runner-base tests: per-stream lists in append order, each event numbered by its version and
/// stamped with a commit sequence, read after an anchor by position as the stores read after an event's version.
/// </summary>
internal sealed class RbEvents() : ForwardingEventStoreDecorator(new InMemoryEventStore()) {
  private readonly Dictionary<Guid, List<MessageEnvelope<IEvent>>> _streams = [];
  private long _nextSequence = 1000;

  /// <summary>Archived events a read through the archive serves before the hot ones.</summary>
  public List<MessageEnvelope<IEvent>> Archived { get; } = [];

  /// <summary>Called before each polymorphic read, so a test can append while a rewind reads (its catch-up).</summary>
  public Action<Guid>? BeforeRead { get; set; }

  /// <summary>
  /// Reads after an anchor find nothing, as when the bodies after a snapshot were reaped between the rewind's look at the
  /// stream and its replay.
  /// </summary>
  public bool NothingAfterAnAnchor { get; set; }

  /// <summary>The answer to the history probe; null computes it from the stream.</summary>
  public bool? HistoryBefore { get; set; }

  public int ThroughArchiveReads { get; private set; }
  public Dictionary<Guid, long?> CommitSequenceOverrides { get; } = [];

  public MessageEnvelope<IEvent> Add(Guid streamId, IEvent payload, Guid? id = null, string? tenant = null,
      bool stamped = true, bool versioned = true, bool hops = true, bool nullHops = false) {
    var envelope = Rb.Envelope(payload, id, tenant, hops, nullHops);
    Store(streamId, envelope, stamped, versioned);
    return envelope;
  }

  public void Store(Guid streamId, MessageEnvelope<IEvent> envelope, bool stamped = true, bool versioned = true) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      _streams[streamId] = stream = [];
    }
    if (versioned) {
      envelope.LocalStreamVersion = stream.Count + 1;
    }
    envelope.IsFirstInLocalStream = stream.Count == 0;
    if (stamped && envelope.LocalCommitSequence is null) {
      envelope.LocalCommitSequence = ++_nextSequence;
    }
    stream.Add(envelope);
  }

  public IReadOnlyList<MessageEnvelope<IEvent>> Stream(Guid streamId) =>
    _streams.TryGetValue(streamId, out var stream) ? stream : [];

  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    BeforeRead?.Invoke(streamId);
    foreach (var envelope in _after(streamId, fromEventId, eventTypes)) {
      yield return envelope;
    }
    await Task.CompletedTask;
  }

  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicThroughArchiveAsync(
      Guid streamId, Guid? fromEventId, long afterVersion, IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    ThroughArchiveReads++;
    foreach (var envelope in Archived) {
      yield return envelope;
    }
    foreach (var envelope in _after(streamId, fromEventId, eventTypes)) {
      yield return envelope;
    }
    await Task.CompletedTask;
  }

  private List<MessageEnvelope<IEvent>> _after(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes) {
    var stream = Stream(streamId);
    var start = 0;
    if (fromEventId is not null && NothingAfterAnAnchor) {
      return [];
    }
    if (fromEventId is { } anchor) {
      var at = stream.ToList().FindIndex(e => e.MessageId.Value == anchor);
      start = at < 0 ? 0 : at + 1;
    }
    return [.. stream.Skip(start).Where(e => eventTypes.Contains(e.Payload.GetType()))];
  }

  public override Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {
    if (HistoryBefore is { } answer) {
      return Task.FromResult(answer);
    }
    var stream = Stream(streamId);
    var at = stream.ToList().FindIndex(e => e.MessageId.Value == beforeEventId);
    return Task.FromResult(stream.Take(at < 0 ? 0 : at).Any(e => eventTypes.Contains(e.Payload.GetType())));
  }

  public override Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) {
    if (CommitSequenceOverrides.TryGetValue(eventId, out var given)) {
      return Task.FromResult(given);
    }
    var found = _streams.Values.SelectMany(s => s).FirstOrDefault(e => e.MessageId.Value == eventId);
    return Task.FromResult(found?.LocalCommitSequence);
  }
}

/// <summary>
/// The rows of the runner-base tests: each with a version, its metadata and scope. A checked write lands only on the
/// version it read, and a write whose event id sorts below the row's stamp is refused, as the PostgreSQL store does.
/// </summary>
internal sealed class RbRows : IPerspectiveStore<RbModel> {
  private readonly Dictionary<Guid, (RbModel Model, long Version, PerspectiveMetadata Metadata, PerspectiveScope? Scope)> _rows = [];
  private long _nextVersion = 100;

  /// <summary>False for a store that tracks no versions (every read Unchecked).</summary>
  public bool Versioned { get; set; } = true;

  /// <summary>Refuses every write quietly, as the ordering guard does.</summary>
  public bool RefuseWrites { get; set; }

  /// <summary>Throws from every write.</summary>
  public Exception? WriteFails { get; set; }

  /// <summary>Throws from flush.</summary>
  public Exception? FlushFails { get; set; }

  /// <summary>Called before every write, so a test can move the row under the writer.</summary>
  public Action<Guid>? BeforeWrite { get; set; }

  /// <summary>Whether the store prepares writes and purges for a set-based batch.</summary>
  public bool Prepares { get; set; }

  /// <summary>Called when a write is flushed, so a test can advance its clock.</summary>
  public Action? OnFlush { get; set; }

  public int Writes { get; private set; }
  public int Flushes { get; private set; }
  public List<Guid> Purged { get; } = [];
  public List<(PerspectiveScope? Scope, bool Force)> WrittenScopes { get; } = [];
  public List<IDictionary<string, object?>> PhysicalWrites { get; } = [];
  public List<(Guid StreamId, RbModel Model, PerspectiveScope Scope, bool Force, IDictionary<string, object?>? Physical)> Prepared { get; } = [];
  public List<PerspectiveMetadata> WrittenMetadata { get; } = [];

  public void Seed(Guid id, RbModel model, Guid? stampEventId = null, long? commitSequence = null, PerspectiveScope? scope = null,
      string? eventType = "stored", string? stampText = null) =>
    _rows[id] = (model, ++_nextVersion,
      new PerspectiveMetadata { EventId = stampText ?? stampEventId?.ToString("D") ?? "", EventType = eventType!, CommitSequence = commitSequence },
      scope);

  public void Touch(Guid id) => _rows[id] = _rows[id] with { Version = ++_nextVersion };
  public bool Has(Guid id) => _rows.ContainsKey(id);
  public RbModel Row(Guid id) => _rows[id].Model;
  public PerspectiveMetadata MetadataOf(Guid id) => _rows[id].Metadata;

  public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) {
    if (!Versioned) {
      return Task.FromResult(PerspectiveApplyRead.Unchecked);
    }
    return Task.FromResult(_rows.TryGetValue(streamId, out var row)
      ? new PerspectiveApplyRead(PerspectiveRowVersion.Of(row.Version), row.Metadata) { Scope = row.Scope }
      : new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null));
  }

  public Task<RbModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Model : null);

  public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Metadata : null);

  public Task<bool> TryUpsertAsync(Guid streamId, RbModel model, PerspectiveScope scope, bool forceUpdateScope,
      PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) =>
    Task.FromResult(_write(streamId, model, scope, forceUpdateScope, metadata, expectedVersion));

  public Task<bool> TryUpsertWithPhysicalFieldsAsync(Guid streamId, RbModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default) {
    PhysicalWrites.Add(physicalFieldValues);
    return Task.FromResult(_write(streamId, model, scope, forceUpdateScope, metadata, expectedVersion));
  }

  private bool _write(Guid streamId, RbModel model, PerspectiveScope? scope, bool force, PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion) {
    Writes++;
    BeforeWrite?.Invoke(streamId);
    if (WriteFails is { } failure) {
      throw failure;
    }
    var exists = _rows.TryGetValue(streamId, out var current);
    if (expectedVersion.IsChecked) {
      var actual = exists ? PerspectiveRowVersion.Of(current.Version) : PerspectiveRowVersion.Absent;
      if (actual != expectedVersion) {
        throw new PerspectiveRowConflictException(typeof(RbModel), streamId, expectedVersion, actual);
      }
    }
    if (RefuseWrites || (exists && string.CompareOrdinal(metadata.EventId, current.Metadata.EventId) < 0)) {
      return false;
    }
    WrittenScopes.Add((scope, force));
    WrittenMetadata.Add(metadata);
    _rows[streamId] = (model, ++_nextVersion, metadata, scope ?? (exists ? current.Scope : null));
    return true;
  }

  public PendingPerspectiveWrite? PrepareWrite(Guid streamId, RbModel model, PerspectiveScope scope, bool forceUpdateScope,
      PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, IDictionary<string, object?>? physicalFieldValues) {
    if (!Prepares) {
      return null;
    }
    Prepared.Add((streamId, model, scope, forceUpdateScope, physicalFieldValues));
    return PendingPerspectiveWrite.Write("\"rb\"", streamId, expectedVersion, new PerspectiveRowValues(
      "{}", "{}", "{}", forceUpdateScope, false, DateTime.UnixEpoch, DateTime.UnixEpoch, false, true, null, []));
  }

  public PendingPerspectiveWrite? PreparePurge(Guid streamId) =>
    Prepares ? PendingPerspectiveWrite.Purge("\"rb\"", streamId) : null;

  public Task UpsertAsync(Guid streamId, RbModel model, CancellationToken cancellationToken = default) =>
    throw new InvalidOperationException("the runner writes through the versioned overloads");

  public Task UpsertWithPhysicalFieldsAsync(Guid streamId, RbModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
    throw new InvalidOperationException("the runner writes through the versioned overloads");

  public Task<RbModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull => Task.FromResult<RbModel?>(null);

  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, RbModel model, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull => Task.CompletedTask;

  public Task FlushAsync(CancellationToken cancellationToken = default) {
    Flushes++;
    OnFlush?.Invoke();
    return FlushFails is { } failure ? Task.FromException(failure) : Task.CompletedTask;
  }

  public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
    Purged.Add(streamId);
    _rows.Remove(streamId);
    return Task.CompletedTask;
  }

  public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull => Task.CompletedTask;
}

/// <summary>The snapshots of the runner-base tests: a configurable floor, and every snapshot a run takes.</summary>
internal sealed class RbSnapshots : IPerspectiveSnapshotStore {
  public bool Configured { get; set; } = true;
  bool IPerspectiveSnapshotStore.IsConfigured => Configured;

  /// <summary>The floor every floor lookup returns, or null.</summary>
  public PerspectiveSnapshotFloor? Floor { get; set; }

  /// <summary>The floor the evicted-row lookup (stream order) returns, or null.</summary>
  public (Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)? StreamOrderFloor { get; set; }

  public bool HasAny { get; set; }
  public Exception? CreateFails { get; set; }
  public List<(Guid EventId, long? CommitSequence, JsonDocument Data, long? CollectivesThrough)> Created { get; } = [];
  public List<int> Prunes { get; } = [];

  public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
    CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, null, snapshotData, null, ct);

  public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, long? snapshotCommitSequence,
      JsonDocument snapshotData, CancellationToken ct = default) =>
    CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, snapshotCommitSequence, snapshotData, null, ct);

  public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, long? snapshotCommitSequence,
      JsonDocument snapshotData, long? collectivesThroughSequence, CancellationToken ct = default) {
    if (CreateFails is { } failure) {
      throw failure;
    }
    Created.Add((snapshotEventId, snapshotCommitSequence, snapshotData, collectivesThroughSequence));
    HasAny = true;
    return Task.CompletedTask;
  }

  public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
    Task.FromResult<(Guid, JsonDocument)?>(Floor is { } floor ? (floor.SnapshotEventId, floor.SnapshotData) : null);

  public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
    GetLatestSnapshotAsync(streamId, perspectiveName, ct);

  public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
      Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
    Task.FromResult(StreamOrderFloor);

  public Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
    Task.FromResult(Floor);

  public Task<PerspectiveSnapshotFloor?> GetLatestSnapshotFloorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
    Task.FromResult(Floor);

  public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult(HasAny);

  public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) {
    Prunes.Add(keepCount);
    return Task.CompletedTask;
  }

  public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Records every log line, by level.</summary>
internal sealed class RbLog : ILogger {
  public List<(LogLevel Level, string Message)> Lines { get; } = [];
  public LogLevel Minimum { get; set; } = LogLevel.Trace;
  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
  public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum;
  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
    if (IsEnabled(logLevel)) {
      Lines.Add((logLevel, formatter(state, exception)));
    }
  }

  public int Count(LogLevel level, string fragment) => Lines.Count(l => l.Level == level && l.Message.Contains(fragment));
}

/// <summary>Everything one runner-base test runs against, and the runner over it.</summary>
internal sealed class RbHarness {
  public const string NAME = "RbPerspective";

  public RbEvents Events { get; } = new();
  public RbRows Rows { get; } = new();
  public RbLog Log { get; } = new();
  public ServiceCollection Services { get; } = new();
  public RbTraits Traits { get; set; } = new();
  public RbSnapshots? Snapshots { get; set; }
  public PerspectiveSnapshotOptions? SnapshotOptions { get; set; }
  public IPerspectiveApplyCoordinator? Coordinator { get; set; }
  public ICollectiveReplayApplier? Collectives { get; set; }
  public TracingOptions? Tracing { get; set; }
  public Guid StreamId { get; } = TrackedGuid.New().Value;

  private RbRunner? _runner;
  private ServiceProvider? _provider;

  public RbHarness() {
    Services.AddSingleton<RbPerspective>();
  }

  /// <summary>The runner, built on first use from what the test configured.</summary>
  public RbRunner Runner => _runner ??= Build();

  public RbRunner Build() {
    _provider ??= Services.BuildServiceProvider();
    return new RbRunner(
      Traits, _provider, Log, Events, Rows,
      Tracing is null ? null : new StaticTracing(Tracing),
      Snapshots,
      SnapshotOptions is null ? null : Options.Create(SnapshotOptions),
      Coordinator,
      Collectives);
  }

  public MessageEnvelope<IEvent> Add(IEvent? payload = null, Guid? id = null, string? tenant = null, bool stamped = true,
      bool versioned = true, bool hops = true, bool nullHops = false) =>
    Events.Add(StreamId, payload ?? new RbMarked { StreamId = StreamId }, id, tenant, stamped, versioned, hops, nullHops);

  public RbMarked Marked(string mark = "m") => new() { StreamId = StreamId, Mark = mark };

  private sealed class StaticTracing(TracingOptions value) : IOptionsMonitor<TracingOptions> {
    public TracingOptions CurrentValue => value;
    public TracingOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<TracingOptions, string?> listener) => null;
  }
}
