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
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>A model that keeps only its creator's tenant, so the filter a scope event must bypass is visible.</summary>
[InheritScope(OnCreate = ScopeFields.Tenant)]
public sealed record ScopeEventRunnerModel {
  [StreamId]
  public Guid Id { get; init; }
  public int Notes { get; init; }
}

/// <summary>The same model shape, for the perspective that decides the final scope itself.</summary>
[InheritScope(OnCreate = ScopeFields.Tenant)]
public sealed record ScopeDecidingRunnerModel {
  [StreamId]
  public Guid Id { get; init; }
  public int Notes { get; init; }
}

public sealed record ScopeEventRunnerNoteAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

public sealed record ScopeEventRunnerShared : IScopeEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public PerspectiveScope Scope { get; init; } = new();
}

public sealed class ScopeEventRunnerPerspective :
    IPerspectiveFor<ScopeEventRunnerModel, ScopeEventRunnerNoteAdded>,
    IPerspectiveFor<ScopeEventRunnerModel, ScopeEventRunnerShared> {
  public ScopeEventRunnerModel Apply(ScopeEventRunnerModel currentData, ScopeEventRunnerNoteAdded @event) =>
    currentData with { Id = @event.StreamId, Notes = currentData.Notes + 1 };

  public ScopeEventRunnerModel Apply(ScopeEventRunnerModel currentData, ScopeEventRunnerShared @event) =>
    currentData with { Id = @event.StreamId };
}

public sealed class ScopeDecidingRunnerPerspective :
    IPerspectiveFor<ScopeDecidingRunnerModel, ScopeEventRunnerNoteAdded>,
    IPerspectiveFor<ScopeDecidingRunnerModel, ScopeEventRunnerShared>,
    IPerspectiveScopeFor<ScopeDecidingRunnerModel> {
  /// <summary>The current scope each <see cref="ApplyScope"/> call was given.</summary>
  public List<PerspectiveScope> CurrentScopesSeen { get; } = [];

  public ScopeDecidingRunnerModel Apply(ScopeDecidingRunnerModel currentData, ScopeEventRunnerNoteAdded @event) =>
    currentData with { Id = @event.StreamId, Notes = currentData.Notes + 1 };

  public ScopeDecidingRunnerModel Apply(ScopeDecidingRunnerModel currentData, ScopeEventRunnerShared @event) =>
    currentData with { Id = @event.StreamId };

  public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) {
    CurrentScopesSeen.Add(currentScope);
    // Share additively: keep what the row already allows and add what the event proposes.
    return new PerspectiveScope {
      TenantId = currentScope.TenantId ?? proposedScope.TenantId,
      AllowedPrincipals = [.. currentScope.AllowedPrincipals.Union(proposedScope.AllowedPrincipals)],
    };
  }
}

/// <summary>
/// The scope an <see cref="IScopeEvent"/> sets is the scope the generated runner writes (#1432): a later message in the
/// same batch does not overwrite it, the model's create-time filter does not narrow it, a perspective that decides the
/// scope sees the row's stored scope as the current one, and a batch whose envelopes carry no scope still writes it.
/// </summary>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
public class ScopeEventRunnerTests {
  private const string TENANT = "tenant-1";

  private static PerspectiveScope _shared(params string[] principals) =>
    new() { TenantId = TENANT, AllowedPrincipals = [.. principals] };

  [Test]
  public async Task ALaterMessageInTheBatch_DoesNotOverwriteTheScopeEventsScopeAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new ScopeRecordingStore<ScopeEventRunnerModel>();
    store.Seed(streamId, new ScopeEventRunnerModel { Id = streamId }, new PerspectiveScope { TenantId = TENANT });

    await _runner<ScopeEventRunnerPerspective, ScopeEventRunnerModel>(store, new ScopeEventRunnerPerspective())
      .RunWithEventsAsync(streamId, nameof(ScopeEventRunnerPerspective), null, [
        _envelope(new ScopeEventRunnerShared { StreamId = streamId, Scope = _shared("group:reviewers") },
          new PerspectiveScope { TenantId = TENANT, UserId = "user-sharer" }),
        _envelope(new ScopeEventRunnerNoteAdded { StreamId = streamId },
          new PerspectiveScope { TenantId = TENANT, UserId = "user-later", OrganizationId = "org-later" }),
      ], CancellationToken.None);

    await Assert.That(store.LastForceUpdate).IsTrue();
    await Assert.That(store.LastScope!.AllowedPrincipals).IsEquivalentTo(["group:reviewers"])
      .Because("the scope event's scope is the row's scope; the later message's envelope scope must not replace it");
    await Assert.That(store.LastScope.UserId).IsNull();
    await Assert.That(store.LastScope.OrganizationId).IsNull();
  }

  [Test]
  public async Task TheModelsCreateTimeFilter_DoesNotNarrowTheScopeEventsScopeAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new ScopeRecordingStore<ScopeEventRunnerModel>();
    store.Seed(streamId, new ScopeEventRunnerModel { Id = streamId }, new PerspectiveScope { TenantId = TENANT });

    await _runner<ScopeEventRunnerPerspective, ScopeEventRunnerModel>(store, new ScopeEventRunnerPerspective())
      .RunWithEventsAsync(streamId, nameof(ScopeEventRunnerPerspective), null, [
        _envelope(new ScopeEventRunnerShared { StreamId = streamId, Scope = _shared("group:reviewers") },
          new PerspectiveScope { TenantId = TENANT, UserId = "user-sharer" }),
      ], CancellationToken.None);

    await Assert.That(store.LastForceUpdate).IsTrue();
    await Assert.That(store.LastScope!.TenantId).IsEqualTo(TENANT);
    await Assert.That(store.LastScope.AllowedPrincipals).IsEquivalentTo(["group:reviewers"])
      .Because("OnCreate = Tenant decides what a new row inherits from its message, not what a scope event sets");
  }

  [Test]
  public async Task APerspectiveThatDecidesTheScope_IsGivenTheRowsStoredScopeAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new ScopeRecordingStore<ScopeDecidingRunnerModel>();
    store.Seed(streamId, new ScopeDecidingRunnerModel { Id = streamId }, _shared("group:owners"));
    var perspective = new ScopeDecidingRunnerPerspective();

    await _runner<ScopeDecidingRunnerPerspective, ScopeDecidingRunnerModel>(store, perspective)
      .RunWithEventsAsync(streamId, nameof(ScopeDecidingRunnerPerspective), null, [
        _envelope(new ScopeEventRunnerShared { StreamId = streamId, Scope = _shared("group:reviewers") },
          new PerspectiveScope { TenantId = TENANT, UserId = "user-sharer" }),
      ], CancellationToken.None);

    await Assert.That(perspective.CurrentScopesSeen).Count().IsEqualTo(1);
    await Assert.That(perspective.CurrentScopesSeen[0].AllowedPrincipals).IsEquivalentTo(["group:owners"])
      .Because("the current scope is the row's stored scope, not the scope of the message that carried the event");
    await Assert.That(store.LastScope!.AllowedPrincipals).IsEquivalentTo(["group:owners", "group:reviewers"]);
  }

  [Test]
  public async Task AScopeEventInABatchWithNoEnvelopeScope_IsStillWrittenAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new ScopeRecordingStore<ScopeEventRunnerModel>();
    store.Seed(streamId, new ScopeEventRunnerModel { Id = streamId }, new PerspectiveScope { TenantId = TENANT });

    await _runner<ScopeEventRunnerPerspective, ScopeEventRunnerModel>(store, new ScopeEventRunnerPerspective())
      .RunWithEventsAsync(streamId, nameof(ScopeEventRunnerPerspective), null, [
        _envelope(new ScopeEventRunnerShared { StreamId = streamId, Scope = _shared("group:reviewers") }, envelopeScope: null),
      ], CancellationToken.None);

    await Assert.That(store.LastForceUpdate).IsTrue()
      .Because("the scope event is what changes the scope; the envelope carrying no scope of its own does not cancel it");
    await Assert.That(store.LastScope!.AllowedPrincipals).IsEquivalentTo(["group:reviewers"]);
  }

  [Test]
  public async Task WithoutAScopeEvent_AnExistingRowsScopeIsNotForcedAsync() {
    // The control: the same runner and store, with only an ordinary message, leaves the stored scope alone, so the
    // forced writes above come from the scope event.
    var streamId = Guid.CreateVersion7();
    var store = new ScopeRecordingStore<ScopeEventRunnerModel>();
    store.Seed(streamId, new ScopeEventRunnerModel { Id = streamId }, new PerspectiveScope { TenantId = TENANT });

    await _runner<ScopeEventRunnerPerspective, ScopeEventRunnerModel>(store, new ScopeEventRunnerPerspective())
      .RunWithEventsAsync(streamId, nameof(ScopeEventRunnerPerspective), null, [
        _envelope(new ScopeEventRunnerNoteAdded { StreamId = streamId },
          new PerspectiveScope { TenantId = TENANT, UserId = "user-later" }),
      ], CancellationToken.None);

    await Assert.That(store.Writes).IsEqualTo(1);
    await Assert.That(store.LastForceUpdate).IsFalse();
  }

  private static IPerspectiveRunner _runner<TPerspective, TModel>(IPerspectiveStore<TModel> rows, TPerspective perspective)
      where TPerspective : class
      where TModel : class {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(perspective);
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not compile in a
    // workspace that loads without running the generators (the formatting gate).
    var runnerType = typeof(ScopeEventRunnerTests).Assembly
      .GetType($"Whizbang.Core.Tests.Generated.{typeof(TPerspective).Name}Runner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runnerType))!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = new PerspectiveRowConflictRetryTests.EmptyEventStore();
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  private static MessageEnvelope<IEvent> _envelope(IEvent payload, PerspectiveScope? envelopeScope) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
        Scope = envelopeScope is null ? null : ScopeDelta.FromPerspectiveScope(envelopeScope),
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>A store that returns the row's stored scope on the apply read and records the scope each write carries.</summary>
  private sealed class ScopeRecordingStore<TModel> : IPerspectiveStore<TModel> where TModel : class {
    private readonly Dictionary<Guid, (TModel Model, PerspectiveScope? Scope, long Version)> _rows = [];
    private long _nextVersion = 100;

    public int Writes { get; private set; }
    public PerspectiveScope? LastScope { get; private set; }
    public bool LastForceUpdate { get; private set; }

    public void Seed(Guid id, TModel model, PerspectiveScope? scope) => _rows[id] = (model, scope, ++_nextVersion);

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var row)
        ? new PerspectiveApplyRead(PerspectiveRowVersion.Of(row.Version), null, row.Scope)
        : new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null));

    public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Model : null);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveMetadata?>(null);

    public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) {
      Writes++;
      LastScope = scope;
      LastForceUpdate = forceUpdateScope;
      var stored = _rows.TryGetValue(streamId, out var current) ? current.Scope : null;
      _rows[streamId] = (model, forceUpdateScope || stored is null ? scope : stored, ++_nextVersion);
      return Task.CompletedTask;
    }

    public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the runner must write through the versioned overload");

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the model has no physical fields");

    public Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<TModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }
}
