// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Owned;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Upstream;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The dispatch executor against a REAL PostgreSQL outbox (#1410 follow-up): on every public entry, a
/// message this service handles runs here exactly once, and what reaches <c>wh_outbox</c> is exactly what
/// the entry's policy asks for, marked, scheduled and routed as the policy decided. What this proves that
/// the component matrix cannot: the decision survives the real work coordinator, serialization into
/// <c>wh_outbox</c> and back, and the real drain worker's PreOutbox stage.
/// </summary>
/// <remarks>
/// The outbox is the real one: <see cref="ScopedWorkCoordinatorStrategy"/> over
/// <c>EFCoreWorkCoordinator</c>, on a fresh database per test. Only the transport is faked, in
/// <see cref="OutboxDrainHarness"/>. Every wait is on a signal, never on time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class DispatchExecutionIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Owned";
  private const string TENANT = "tenant-dispatch-execution";
  private static readonly DateTimeOffset _scheduledAt = new(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);
  private readonly JsonSerializerOptions _jsonOptions = JsonContextRegistry.CreateCombinedOptions();

  // ── an owned command never leaves its service ────────────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendAsync<T>")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  [Arguments("CascadeBoth")]
  [Arguments("CascadeOutbox")]
  [Arguments("RoutedOutbox")]
  [Arguments("LocalInvokeAsync")]
  [Arguments("LocalSendManyAsync")]
  public async Task OwnedCommand_OnEveryEntry_RunsOnceAndNeverReachesTheOutboxAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var command = new DxOwnedCommand(id);

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync((object)command); break;
      case "SendAsync<T>": await dispatcher.SendAsync(command); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { command }); break;
      case "PublishAsync": await dispatcher.PublishAsync((object)command); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { command }); break;
      case "CascadeBoth": await dispatcher.CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Both); break;
      case "CascadeOutbox": await dispatcher.CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Outbox); break;
      case "RoutedOutbox": await dispatcher.SendAsync(Route.Outbox(command)); break;
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)command); break;
      default: await dispatcher.LocalSendManyAsync(new object[] { command }); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxOwnedCommandReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the service owns and handles the command, so it runs here exactly once");
    await Assert.That(await _rowsOfAsync<DxOwnedCommand>()).IsEmpty()
      .Because($"{entry}: a command the service owns never reaches wh_outbox, whatever the route");
  }

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  public async Task OwnedCommandWithNoHandler_IsRefusedAndWritesNothingAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var command = new DxOwnedUnhandledCommand(Guid.CreateVersion7());

    Task dispatch() => entry switch {
      "SendAsync" => dispatcher.SendAsync((object)command),
      "SendManyAsync" => dispatcher.SendManyAsync(new object[] { command }),
      _ => dispatcher.PublishAsync((object)command),
    };

    await Assert.That(dispatch).Throws<ReceptorNotFoundException>()
      .Because($"{entry}: an owned command nobody here handles has nowhere to go");
    await Assert.That(await _rowsOfAsync<DxOwnedUnhandledCommand>()).IsEmpty();
  }

  // ── a message handled here that also goes on is written once, marked ─────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendAsync<T>")]
  [Arguments("SendManyAsync")]
  public async Task UnownedCommandWithALocalHandler_RunsHereAndWritesOneMarkedCopyAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var command = new DxUnownedCommand(id);

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync((object)command); break;
      case "SendAsync<T>": await dispatcher.SendAsync(command); break;
      default: await dispatcher.SendManyAsync(new object[] { command }); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxUnownedCommandReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the service has a handler, so it runs here exactly once");
    var row = await _singleRowOfAsync<DxUnownedCommand>(entry);
    await _assertMarkedCopyAsync(row, entry);
  }

  [Test]
  [Arguments("PublishAsync")]
  [Arguments("PublishAsync<T>")]
  [Arguments("PublishManyAsync")]
  [Arguments("SendAsync")]
  [Arguments("CascadeBoth")]
  public async Task EventWithALocalHandler_RunsHereAndWritesOneMarkedCopyAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var evt = new DxEvent(id);

    switch (entry) {
      case "PublishAsync": await dispatcher.PublishAsync((object)evt); break;
      case "PublishAsync<T>": await dispatcher.PublishAsync(evt); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { evt }); break;
      case "SendAsync": await dispatcher.SendAsync((object)evt); break;
      default: await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, DispatchModes.Both); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxEventReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the service has a handler, so it runs here exactly once");
    var row = await _singleRowOfAsync<DxEvent>(entry);
    await _assertMarkedCopyAsync(row, entry);
  }

  // ── a returned result cascades: the case #1410 broke ─────────────────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendAsync<T>")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("CascadeBoth")]
  public async Task OwnedCommandReturningAnEvent_RunsOnce_AndTheEventRunsOnceAndGoesOutMarkedAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var command = new DxOwnedResultCommand(id);

    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync((object)command); break;
      case "SendAsync<T>": await dispatcher.SendAsync(command); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new object[] { command }); break;
      case "PublishAsync": await dispatcher.PublishAsync((object)command); break;
      default: await dispatcher.CascadeMessageAsync(command, sourceEnvelope: null, DispatchModes.Both); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxOwnedResultCommandReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the owned command's handler runs here exactly once");
    await Assert.That(DispatchExecutionProbe.Count(nameof(DxFollowUpEventReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the event it returned cascades and its handler runs exactly once");
    await Assert.That(await _rowsOfAsync<DxOwnedResultCommand>()).IsEmpty()
      .Because($"{entry}: the owned command itself never reaches wh_outbox");
    var row = await _singleRowOfAsync<DxFollowUpEvent>(entry);
    await _assertMarkedCopyAsync(row, entry);
  }

  [Test]
  [Arguments("LocalInvokeAsync")]
  [Arguments("LocalInvokeAsync<TResult>")]
  [Arguments("LocalSendManyAsync")]
  public async Task OwnedCommandReturningAnEvent_OnALocalCall_RunsOnceAndTheEventRunsOnceAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var command = new DxOwnedResultCommand(id);

    switch (entry) {
      case "LocalInvokeAsync": await dispatcher.LocalInvokeAsync((object)command); break;
      case "LocalInvokeAsync<TResult>": await dispatcher.LocalInvokeAsync<DxFollowUpEvent>((object)command); break;
      default: await dispatcher.LocalSendManyAsync(new object[] { command }); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxOwnedResultCommandReceptor), id)).IsEqualTo(1);
    await Assert.That(DispatchExecutionProbe.Count(nameof(DxFollowUpEventReceptor), id)).IsEqualTo(1)
      .Because($"{entry}: the event the handler returned cascades and its handler runs exactly once");
    await Assert.That(await _rowsOfAsync<DxOwnedResultCommand>()).IsEmpty()
      .Because($"{entry}: a local call never sends the message itself on");
    var row = await _singleRowOfAsync<DxFollowUpEvent>(entry);
    await _assertMarkedCopyAsync(row, entry);
  }

  // ── a scheduled dispatch runs when the copy comes back, not now ──────────────────────────────

  [Test]
  [Arguments("SendAsync")]
  [Arguments("SendManyAsync")]
  [Arguments("PublishAsync")]
  [Arguments("PublishManyAsync")]
  public async Task ScheduledDispatch_RunsNothingNow_AndWritesOneCopyCarryingTheScheduleAndTheDeferredRunAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var options = new DispatchOptions().WithScheduledFor(_scheduledAt);

    var (receptor, rowsOf) = entry.StartsWith("Send", StringComparison.Ordinal)
      ? (nameof(DxUnownedCommandReceptor), (Func<Task<List<OutboxRecord>>>)_rowsOfAsync<DxUnownedCommand>)
      : (nameof(DxEventReceptor), _rowsOfAsync<DxEvent>);
    object message = entry.StartsWith("Send", StringComparison.Ordinal) ? new DxUnownedCommand(id) : new DxEvent(id);
    switch (entry) {
      case "SendAsync": await dispatcher.SendAsync(message, options); break;
      case "SendManyAsync": await dispatcher.SendManyAsync(new[] { message }, options); break;
      case "PublishAsync": await dispatcher.PublishAsync(message, options); break;
      default: await dispatcher.PublishManyAsync(new[] { message }, options); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(receptor, id)).IsEqualTo(0)
      .Because($"{entry}: a scheduled dispatch runs nothing now");
    var rows = await rowsOf();
    await Assert.That(rows).Count().IsEqualTo(1).Because($"{entry}: the scheduled copy is written exactly once");
    var row = rows[0];
    await Assert.That(row.ScheduledFor).IsEqualTo(_scheduledAt).Because($"{entry}: the row carries the schedule");
    var context = await _storedDispatchContextAsync(row.MessageId);
    await Assert.That(context.LocalDeferred).IsTrue()
      .Because($"{entry}: the row says this service handles it when it comes back, which is how it runs exactly once");
    await Assert.That(context.Mode.HasFlag(DispatchModes.LocalDispatch)).IsFalse()
      .Because($"{entry}: nothing ran here yet, so the copy must not claim it was handled");
  }

  // ── store-only ───────────────────────────────────────────────────────────────────────────────

  [Test]
  [Arguments("PublishAsync")]
  [Arguments("SendAsync")]
  [Arguments("PublishManyAsync")]
  [Arguments("CascadeEventStoreOnly")]
  public async Task EventRoutedStoreOnly_WritesOneUntransportedRowAndRunsNothingHereAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var evt = new DxEvent(id);

    switch (entry) {
      case "PublishAsync": await dispatcher.PublishAsync(Route.EventStoreOnly(evt)); break;
      case "SendAsync": await dispatcher.SendAsync(Route.EventStoreOnly(evt)); break;
      case "PublishManyAsync": await dispatcher.PublishManyAsync(new object[] { Route.EventStoreOnly(evt) }); break;
      default: await dispatcher.CascadeMessageAsync(evt, sourceEnvelope: null, DispatchModes.EventStoreOnly); break;
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxEventReceptor), id)).IsEqualTo(0)
      .Because($"{entry}: a store-only event is not handled in-process");
    var row = await _singleRowOfAsync<DxEvent>(entry);
    await Assert.That(row.Destination).IsNull()
      .Because($"{entry}: a store-only row has no destination, so it is stored and never transported");
  }

  [Test]
  [Arguments("SendAsync")]
  [Arguments("PublishAsync")]
  [Arguments("SendManyAsync")]
  public async Task CommandRoutedStoreOnly_IsRefusedAndWritesAndRunsNothingAsync(string entry) {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();
    var routed = Route.EventStoreOnly(new DxUnownedCommand(id));

    Task dispatch() => entry switch {
      "SendAsync" => dispatcher.SendAsync(routed),
      "PublishAsync" => dispatcher.PublishAsync(routed),
      _ => dispatcher.SendManyAsync(new object[] { routed }),
    };

    await Assert.That(dispatch).Throws<Exception>().Because($"{entry}: a command cannot be stored as an event");
    await Assert.That(DispatchExecutionProbe.Count(nameof(DxUnownedCommandReceptor), id)).IsEqualTo(0);
    await Assert.That(await _rowsOfAsync<DxUnownedCommand>()).IsEmpty();
  }

  // ── a unit of work that never commits ────────────────────────────────────────────────────────

  [Test]
  public async Task DispatchesInsideAUnitOfWorkThatNeverCommits_LeaveNoRowsAsync() {
    await using var provider = _createServices(withMessageSecurity: false).Provider;
    var dispatcher = provider.GetRequiredService<IDispatcher>();
    var id = Guid.CreateVersion7();

    using (var scope = DispatchOutboxCollector.BeginCollecting()) {
      await dispatcher.SendAsync(new DxUnownedCommand(id));
      await dispatcher.PublishAsync(new DxEvent(id));
      await dispatcher.SendManyAsync(new object[] { new DxUnownedCommand(Guid.CreateVersion7()) });
      await Assert.That(scope.Collector.Collected).Count().IsEqualTo(3)
        .Because("every copy went to the unit of work instead of the outbox");
    }

    await Assert.That(DispatchExecutionProbe.Count(nameof(DxUnownedCommandReceptor), id)).IsEqualTo(1);
    await Assert.That(DispatchExecutionProbe.Count(nameof(DxEventReceptor), id)).IsEqualTo(1);
    await Assert.That(await _rowsOfAsync<DxUnownedCommand>()).IsEmpty()
      .Because("the unit of work was dropped, so none of its copies may reach wh_outbox");
    await Assert.That(await _rowsOfAsync<DxEvent>()).IsEmpty();
  }

  // ── the drainer: a marked copy is not run again by its sender ────────────────────────────────

  [Test]
  [Arguments("Command", false)]
  [Arguments("Command", true)]
  [Arguments("Event", false)]
  [Arguments("Event", true)]
  public async Task MarkedCopy_ReachesTheDrainer_AndPreOutboxDoesNotRunTheSendersHandlerAgainAsync(string kind, bool bulkPublish) {
    var (provider, jsonOptions) = _createServices(withMessageSecurity: true);
    await using var _ = provider;
    var id = Guid.CreateVersion7();
    var probe = _registerRuntimeHandler(provider, kind, LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline);

    await _dispatchRuntimeHandledAsync(provider, kind, id);

    await Assert.That(probe.CountFor(id)).IsEqualTo(1).Because($"{kind}: the sender handles it in-process before the call returns");
    var published = await _drainTheOnlyRowAsync(provider, jsonOptions, kind, bulkPublish);
    await Assert.That(published).Count().IsEqualTo(1).Because($"{kind}: the copy still goes on to other services");
    await Assert.That(probe.CountFor(id)).IsEqualTo(1)
      .Because($"{kind}: the copy says it was handled here, so the sender's PreOutbox stage must not run it again");
  }

  [Test]
  [Arguments("Command", false)]
  [Arguments("Command", true)]
  [Arguments("Event", false)]
  [Arguments("Event", true)]
  public async Task UnmarkedCopy_ReachesTheDrainer_AndPreOutboxRunsTheSendersOnlyHandlerOnceAsync(string kind, bool bulkPublish) {
    var (provider, jsonOptions) = _createServices(withMessageSecurity: true);
    await using var _ = provider;
    var id = Guid.CreateVersion7();
    var probe = _registerRuntimeHandler(provider, kind, LifecycleStage.PreOutboxInline);

    await _dispatchRuntimeHandledAsync(provider, kind, id);

    await Assert.That(probe.CountFor(id)).IsEqualTo(0).Because($"{kind}: nothing handles it locally, so the call only queues it");
    var published = await _drainTheOnlyRowAsync(provider, jsonOptions, kind, bulkPublish);
    await Assert.That(published).Count().IsEqualTo(1);
    await Assert.That(probe.CountFor(id)).IsEqualTo(1)
      .Because($"{kind}: a copy nothing dispatched locally is where the sender's PreOutbox handler runs, once");
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────

  private async Task _assertMarkedCopyAsync(OutboxRecord row, string entry) {
    await Assert.That(row.Destination).IsNotNull().Because($"{entry}: the copy is transported to other services");
    var context = await _storedDispatchContextAsync(row.MessageId);
    await Assert.That(context.Mode.HasFlag(DispatchModes.LocalDispatch)).IsTrue()
      .Because($"{entry}: the stored copy says it was handled here, so neither this service nor a receiver that reads it runs it twice");
    await Assert.That(context.LocalDeferred).IsFalse();
  }

  /// <summary>The dispatch context of the envelope stored in the row, read back the way the drain worker reads it.</summary>
  private async Task<MessageDispatchContext> _storedDispatchContextAsync(Guid messageId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var select = connection.CreateCommand();
    select.CommandText = "SELECT event_data::text FROM wh_outbox WHERE message_id = @id";
    select.Parameters.AddWithValue("id", messageId);
    var json = (string)(await select.ExecuteScalarAsync())!;
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var envelope = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(json, typeInfo)!;
    return envelope.DispatchContext;
  }

  private async Task<List<OutboxRecord>> _rowsOfAsync<TMessage>() {
    await using var dbContext = CreateDbContext();
    var messageType = TypeNameFormatter.AssemblyQualifiedName(typeof(TMessage));
    return await dbContext.Outbox.AsNoTracking().Where(m => m.MessageType == messageType).ToListAsync();
  }

  private async Task<OutboxRecord> _singleRowOfAsync<TMessage>(string entry) {
    var rows = await _rowsOfAsync<TMessage>();
    await Assert.That(rows).Count().IsEqualTo(1).Because($"{entry}: exactly one {typeof(TMessage).Name} row reaches wh_outbox");
    return rows[0];
  }

  private static CountProbe _registerRuntimeHandler(ServiceProvider provider, string kind, params LifecycleStage[] stages) {
    var registry = provider.GetRequiredService<IReceptorRegistry>();
    if (kind == "Command") {
      var probe = new CountingReceptor<DxRuntimeHandledCommand>();
      foreach (var stage in stages) {
        registry.Register<DxRuntimeHandledCommand>(probe, stage);
      }
      return probe;
    }
    var eventProbe = new CountingReceptor<DxRuntimeHandledEvent>();
    foreach (var stage in stages) {
      registry.Register<DxRuntimeHandledEvent>(eventProbe, stage);
    }
    return eventProbe;
  }

  /// <summary>Dispatches with a security context on the envelope: the drain worker runs no stage for a row without one.</summary>
  private static async Task _dispatchRuntimeHandledAsync(ServiceProvider provider, string kind, Guid id) {
    var builder = provider.GetRequiredService<IDispatcher>().AsSystem().ForTenant(TENANT);
    if (kind == "Command") {
      await builder.SendAsync(new DxRuntimeHandledCommand(id));
    } else {
      await builder.PublishAsync(new DxRuntimeHandledEvent(id));
    }
  }

  private async Task<IReadOnlyCollection<Guid>> _drainTheOnlyRowAsync(
      ServiceProvider provider, JsonSerializerOptions jsonOptions, string kind, bool bulkPublish) {
    var rows = kind == "Command" ? await _rowsOfAsync<DxRuntimeHandledCommand>() : await _rowsOfAsync<DxRuntimeHandledEvent>();
    await Assert.That(rows).Count().IsEqualTo(1).Because($"{kind}: the dispatch writes exactly one copy");
    var row = rows[0];
    await OutboxDrainHarness.LeaseAsync(ConnectionString, provider.GetRequiredService<IServiceInstanceProvider>(), row.MessageId);
    return await OutboxDrainHarness.DrainAsync(provider, jsonOptions, bulkPublish, row.MessageId, row.StreamId ?? Guid.Empty);
  }

  /// <summary>
  /// Message security only where the drain worker needs it: it runs no stage for a row without a
  /// security context. The dispatch-only tests run without it, so each asserts the dispatch decision alone.
  /// </summary>
  private (ServiceProvider Provider, JsonSerializerOptions JsonOptions) _createServices(bool withMessageSecurity) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter()));

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    if (withMessageSecurity) {
      services.AddWhizbangMessageSecurity();
    }
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  private abstract class CountProbe {
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;

    protected ValueTask RecordAsync(Guid id) {
      _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Counts the runtime-registered handler's invocations. Generic so the source generator does not discover it.</summary>
  private sealed class CountingReceptor<TMessage> : CountProbe, IReceptor<TMessage> where TMessage : IDxIdentified {
    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) => RecordAsync(message.Id);
  }
}
