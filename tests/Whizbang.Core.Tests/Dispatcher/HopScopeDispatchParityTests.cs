// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Tests.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Every dispatch path stamps the same hop scope (#1854): under a request scope with an extension and one
/// listed claim, the message each path writes carries the extension and the listed claim, and never the
/// unlisted one. A path that built its hop scope its own way is how the extensions were lost.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Security/HopScope.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Security")]
[Category("Dispatcher")]
public class HopScopeDispatchParityTests {
  public enum DispatchPath {
    SendGeneric,
    SendObject,
    SendWithExplicitContext,
    SendMany,
    Publish,
    PublishMany,
    PublishScheduled,
    PublishFromAReceptor,
    LocalInvokeCascade,
    SendLocalCascade,
    EventStoreAppend,
  }

  [Test]
  [Arguments(DispatchPath.SendGeneric)]
  [Arguments(DispatchPath.SendObject)]
  [Arguments(DispatchPath.SendWithExplicitContext)]
  [Arguments(DispatchPath.SendMany)]
  [Arguments(DispatchPath.Publish)]
  [Arguments(DispatchPath.PublishMany)]
  [Arguments(DispatchPath.PublishScheduled)]
  [Arguments(DispatchPath.PublishFromAReceptor)]
  [Arguments(DispatchPath.LocalInvokeCascade)]
  [Arguments(DispatchPath.SendLocalCascade)]
  [Arguments(DispatchPath.EventStoreAppend)]
  public async Task EveryPath_CarriesExtensionsAndOnlyListedClaimsAsync(DispatchPath path) {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy);
    ScopeContextAccessor.CurrentContext = HopScopeTests.EdgeContext();

    var envelope = await _dispatchAsync(path, dispatcher, strategy);
    var scope = envelope.GetCurrentScope();

    await Assert.That(scope).IsNotNull().Because($"{path} wrote no scope at all");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-hop");
    await Assert.That(scope.Scope.Extensions.Select(e => (e.Key, e.Value)))
      .IsEquivalentTo([("acting-admin", (string?)"true")])
      .Because($"{path} must carry the request's scope extensions, as every other path does");
    await Assert.That(scope.Claims.Keys).IsEquivalentTo(["department"])
      .Because($"{path} carries the listed claim and only the listed claim");
  }

  /// <summary>
  /// The receptor that handles a locally dispatched command sees the request's extension and listed claim
  /// in its scope context, and the event it returns carries both.
  /// </summary>
  [Test]
  public async Task LocalReceptor_SeesTheExtensionAndTheEventItEmitsCarriesItAsync() {
    var strategy = new CapturingStrategy();
    var dispatcher = _createDispatcher(strategy);
    ScopeContextAccessor.CurrentContext = HopScopeTests.EdgeContext();
    var command = new HopScopeParityLocalCommand(TrackedGuid.New());

    await dispatcher.LocalInvokeAsync<HopScopeParityEmittedEvent>(command);

    var seen = HopScopeParityLocalCommandReceptor.Seen[command.Id];
    await Assert.That(seen.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo(["acting-admin"]);
    var emitted = strategy.Rows.Single(r => r.MessageType.Contains(nameof(HopScopeParityEmittedEvent), StringComparison.Ordinal));
    await Assert.That(emitted.Envelope.GetCurrentScope()!.Scope.Extensions.Select(e => e.Key)).IsEquivalentTo(["acting-admin"]);
    await Assert.That(emitted.Scope!.Extensions.Select(e => e.Key)).IsEquivalentTo(["acting-admin"])
      .Because("the outbox row's scope column is read from the same hop");
  }

  private static async Task<IMessageEnvelope> _dispatchAsync(DispatchPath path, IDispatcher dispatcher, CapturingStrategy strategy) {
    var id = (Guid)TrackedGuid.New();
    switch (path) {
      case DispatchPath.SendGeneric:
        await dispatcher.SendAsync(new HopScopeParityCommand(id));
        break;
      case DispatchPath.SendObject:
        await dispatcher.SendAsync((object)new HopScopeParityCommand(id));
        break;
      case DispatchPath.SendWithExplicitContext:
        // MessageContext.Create(cascade) shape: the ids, no scope context of its own.
        await dispatcher.SendAsync(new HopScopeParityCommand(id),
          new MessageContext { TenantId = "tenant-hop", UserId = "user-hop" });
        break;
      case DispatchPath.SendMany:
        await dispatcher.SendManyAsync<HopScopeParityCommand>([new HopScopeParityCommand(id)]);
        break;
      case DispatchPath.Publish:
        await dispatcher.PublishAsync(new HopScopeParityEvent(id));
        break;
      case DispatchPath.PublishMany:
        await dispatcher.PublishManyAsync<HopScopeParityEvent>([new HopScopeParityEvent(id)]);
        break;
      case DispatchPath.PublishScheduled:
        await dispatcher.PublishAsync(new HopScopeParityEvent(id),
          new DispatchOptions().WithScheduledFor(DateTimeOffset.UnixEpoch.AddYears(100)));
        break;
      case DispatchPath.PublishFromAReceptor:
        // A receptor publishing mid-handling: the dispatcher captures the establishing context as the source hop.
        ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
          CorrelationId = CorrelationId.New(),
          TenantId = "tenant-hop",
          UserId = "user-hop",
          ScopeContext = ScopeContextAccessor.CurrentContext,
        };
        await dispatcher.PublishAsync(new HopScopeParityEvent(id));
        break;
      case DispatchPath.LocalInvokeCascade:
        await dispatcher.LocalInvokeAsync<HopScopeParityEmittedEvent>(new HopScopeParityLocalCommand(id));
        return _row(strategy, nameof(HopScopeParityEmittedEvent));
      case DispatchPath.SendLocalCascade:
        await dispatcher.SendAsync(new HopScopeParityLocalCommand(id));
        return _row(strategy, nameof(HopScopeParityEmittedEvent));
      case DispatchPath.EventStoreAppend:
        return SecurityContextEventStoreDecorator.CreateAmbientEnvelope(new HopScopeParityEvent(id));
      default:
        throw new ArgumentOutOfRangeException(nameof(path), path, null);
    }
    return strategy.Rows.Single().Envelope;
  }

  private static IMessageEnvelope _row(CapturingStrategy strategy, string typeName) =>
    strategy.Rows.Single(r => r.MessageType.Contains(typeName, StringComparison.Ordinal)).Envelope;

  private static IDispatcher _createDispatcher(CapturingStrategy strategy) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }

  internal sealed class CapturingStrategy : IWorkCoordinatorStrategy {
    private readonly ConcurrentQueue<OutboxMessage> _rows = new();
    public IReadOnlyList<OutboxMessage> Rows => [.. _rows];
    public void QueueOutboxMessage(OutboxMessage message) => _rows.Enqueue(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }
}

// Message types for the hop scope parity tests (outside the class for source generator discovery).
public record HopScopeParityCommand([property: StreamId] Guid Id) : ICommand;
public record HopScopeParityEvent([property: StreamId] Guid Id) : IEvent;
public record HopScopeParityLocalCommand([property: StreamId] Guid Id) : ICommand;
public record HopScopeParityEmittedEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>Records the scope context it ran under and returns an event, which the dispatcher cascades.</summary>
public class HopScopeParityLocalCommandReceptor : IReceptor<HopScopeParityLocalCommand, HopScopeParityEmittedEvent> {
  public static ConcurrentDictionary<Guid, IScopeContext> Seen { get; } = new();

  public ValueTask<HopScopeParityEmittedEvent> HandleAsync(HopScopeParityLocalCommand message, CancellationToken cancellationToken = default) {
    Seen[message.Id] = ScopeContextAccessor.CurrentContext!;
    return ValueTask.FromResult(new HopScopeParityEmittedEvent(message.Id));
  }
}
