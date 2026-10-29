// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;
using Whizbang.Sagas.Services;
using Whizbang.Sagas.Tests.Generators;
using Whizbang.Testing.MultiService;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Two hosts in one process, each with its own per-item stream namespace, each derive per-item stream ids from
/// their own (#1888).
/// </summary>
/// <remarks>
/// <para>
/// The namespace used to be one process-wide value that every <c>AddWhizbangSagas</c> call overwrote, and the
/// framework's per-item readers derived from it at call time, so the last host registered decided the namespace for
/// every host. A host registered without the setting reset it to the framework default, and another host's
/// stranded-item watchdog then derived ids for streams it never writes: it read items whose terminal events were
/// stored as not terminal, and failed them. Nothing logged, because the ids were well formed.
/// </para>
/// <para>
/// Both hosts here are booted by the multi-service harness, one after the other, the way a test server per service
/// boots them. Each host's saga service is resolved from that host's own container, as a hand-written saga registered
/// with <c>AddSagaService</c> is, and its watchdog tick runs at the stall limit, where every item it cannot find
/// terminal in the store is failed.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/SagaServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/Services/BaseSagaService.cs</code-under-test>
[Category("Saga")]
[NotInParallel("MultiServiceHarness")]
public class SagaItemStreamNamespacePerHostTests {
  private const string CONFIGURED_HOST = "configured-host";
  private const string DEFAULT_HOST = "default-host";
  private static readonly Guid _configuredNamespace = Guid.Parse("0b36f8d4-3884-4c3c-b92b-fc6ec74775ea");

  [Test]
  public async Task TwoHostsWithDifferentNamespaces_EachHostsWatchdogReadsItsOwnItemsAsTerminalAsync() {
    var store = new TerminalItemStore();
    var configuredSaga = new StoredSaga(Guid.CreateVersion7(), ["a", "b"]);
    var defaultSaga = new StoredSaga(Guid.CreateVersion7(), ["c", "d"]);
    // What each host's item streams hold: every item's terminal event, under the namespace that host writes with.
    store.RecordCompleted(_configuredNamespace, configuredSaga);
    store.RecordCompleted(SagaItemStreams.DefaultNamespace, defaultSaga);

    // The host with the setting boots first; the one without it boots last, as in a process whose last
    // registration used to decide the namespace for both.
    await using var harness = await MultiServiceHarness.Create()
      .AddService(CONFIGURED_HOST, svc => svc.Configure(services =>
        _registerSagaHost(services, store, configuredSaga, opts => opts.PerItemStreamNamespace = _configuredNamespace)))
      .AddService(DEFAULT_HOST, svc => svc.Configure(services =>
        _registerSagaHost(services, store, defaultSaga, configure: null)))
      .StartAsync();

    var configured = await _tickAtTheStallLimitAsync(harness.GetService(CONFIGURED_HOST), configuredSaga);
    var byDefault = await _tickAtTheStallLimitAsync(harness.GetService(DEFAULT_HOST), defaultSaga);

    await Assert.That(configured.FailedItems).IsEmpty()
      .Because("the configured host's items are terminal in its own streams; deriving under the other host's "
             + "namespace read them as not terminal and failed them");
    await Assert.That(configured.Outcome).IsEqualTo(WatchdogTickOutcome.Recovered);
    await Assert.That(configured.CompletedItems).IsEqualTo(2);
    await Assert.That(byDefault.FailedItems).IsEmpty();
    await Assert.That(byDefault.Outcome).IsEqualTo(WatchdogTickOutcome.Recovered);
    await Assert.That(byDefault.CompletedItems).IsEqualTo(2);
  }

  private static void _registerSagaHost(
      IServiceCollection services, TerminalItemStore store, StoredSaga saga, Action<SagaOptions>? configure) {
    services.AddSingleton(new RecordingEmitter());
    services.AddSingleton<ISagaEventEmitter>(sp => sp.GetRequiredService<RecordingEmitter>());
    services.AddSingleton<ISagaItemTerminalReader>(store);
    services.AddSingleton<ISagaItemRepository>(new RunningItems(saga));
    services.AddSingleton(saga);
    services.AddWhizbangSagas(configure);
    services.AddSagaService<NamespaceProbeSagaService>();
  }

  private static async Task<TickResult> _tickAtTheStallLimitAsync(MultiServiceHarness.ServiceRuntime host, StoredSaga saga) {
    await using var scope = host.Provider.CreateAsyncScope();
    var service = scope.ServiceProvider.GetRequiredService<NamespaceProbeSagaService>();
    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = saga.SagaId,
      SagaName = GeneratorTestTolerantOutcomeSaga.SagaName,
      EntityId = saga.SagaId,
      RescheduleCount = 4,
      LastObservedAt = DateTimeOffset.UnixEpoch,
      ConsecutiveStallCount = new SagaOptions().MaxConsecutiveStalls - 1,
    };

    var outcome = await service.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    var published = host.Provider.GetRequiredService<RecordingEmitter>().Published;
    return new TickResult(
      outcome,
      [.. published.OfType<GeneratorTestTolerantOutcomeSaga.ItemFailedEvent>().Select(e => e.ItemIdentifier)],
      published.OfType<GeneratorTestTolerantOutcomeSaga.CompletedEvent>().SingleOrDefault()?.CompletedItems ?? 0);
  }

  private sealed record TickResult(WatchdogTickOutcome Outcome, List<string> FailedItems, int CompletedItems);

  /// <summary>One host's saga: its id and its items, every one still running in the item projection.</summary>
  private sealed record StoredSaga(Guid SagaId, string[] Items);

  /// <summary>The durable store both hosts share: which per-item streams carry a terminal event.</summary>
  private sealed class TerminalItemStore : ISagaItemTerminalReader {
    private readonly HashSet<Guid> _completed = [];

    public void RecordCompleted(Guid namespaceId, StoredSaga saga) {
      foreach (var item in saga.Items) {
        _completed.Add(SagaItemStreams.Of(namespaceId, saga.SagaId, item));
      }
    }

    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(_completed.Contains(perItemStreamId) ? SagaItemTerminalOutcome.Completed : SagaItemTerminalOutcome.NotTerminal);
  }

  /// <summary>The item projection, behind the store: every row still running.</summary>
  private sealed class RunningItems(StoredSaga saga) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(new SagaItemAggregate(Total: saga.Items.Length, Completed: 0, Failed: 0, InProgress: saga.Items.Length));

    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<IReadOnlyList<SagaItemModel>>([.. saga.Items.Select(item => new SagaItemModel {
        SagaId = saga.SagaId,
        SagaName = GeneratorTestTolerantOutcomeSaga.SagaName,
        ItemIdentifier = item,
        State = SagaItemState.Running,
        StartedAt = DateTimeOffset.UnixEpoch,
      })]);
  }

  /// <summary>Records what the saga publishes, so the test reads what its watchdog decided.</summary>
  private sealed class RecordingEmitter : ISagaEventEmitter {
    private readonly List<IEvent> _published = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<IEvent> Published {
      get {
        lock (_lock) {
          return [.. _published];
        }
      }
    }

    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => _record(eventData);
    public Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent => _record(eventData);

    public async Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      await _record(eventData);
      return true;
    }

    private Task _record(IEvent eventData) {
      lock (_lock) {
        _published.Add(eventData);
      }
      return Task.CompletedTask;
    }
  }

  /// <summary>
  /// A hand-written saga service, built by the host's container from the host's own registrations, the way
  /// <c>AddSagaService</c> builds one.
  /// </summary>
  private sealed class NamespaceProbeSagaService(
      ISagaEventEmitter emitter,
      ISagaItemRepository itemRepository,
      ISagaItemTerminalReader terminalReader,
      SagaOptions options,
      StoredSaga saga,
      ILogger<NamespaceProbeSagaService> logger)
    : BaseSagaService<GeneratorTestTolerantOutcomeSaga.InitiatedEvent, GeneratorTestTolerantOutcomeSaga.ItemsDispatchedEvent,
                      GeneratorTestTolerantOutcomeSaga.ItemStartedEvent, GeneratorTestTolerantOutcomeSaga.ItemCompletedEvent,
                      GeneratorTestTolerantOutcomeSaga.ItemFailedEvent, GeneratorTestTolerantOutcomeSaga.CompletedEvent,
                      GeneratorTestTolerantOutcomeSaga.ResetEvent, GeneratorTestTolerantOutcomeSaga.HookStartedEvent,
                      GeneratorTestTolerantOutcomeSaga.HookCompletedEvent>(
      GeneratorTestTolerantOutcomeSaga.SagaName, emitter, itemRepository, terminalReader, options, logger) {

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<BaseSagaModel?>(new BaseSagaModel {
        Id = saga.SagaId,
        SagaName = GeneratorTestTolerantOutcomeSaga.SagaName,
        EntityId = saga.SagaId,
        TotalItems = saga.Items.Length,
        Status = SagaStatus.Running,
      });

    protected override GeneratorTestTolerantOutcomeSaga.InitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override GeneratorTestTolerantOutcomeSaga.ItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override GeneratorTestTolerantOutcomeSaga.ItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override GeneratorTestTolerantOutcomeSaga.ItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override GeneratorTestTolerantOutcomeSaga.ItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override GeneratorTestTolerantOutcomeSaga.CompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override GeneratorTestTolerantOutcomeSaga.ResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override GeneratorTestTolerantOutcomeSaga.HookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, HookName = hookName, DisplayName = displayName };
    protected override GeneratorTestTolerantOutcomeSaga.HookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
