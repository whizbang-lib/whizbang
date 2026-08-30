using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Verifies the audit decorator receives its optional dependencies when the container composes it.
/// </summary>
/// <remarks>
/// <para>
/// Every other test of this decorator constructs it directly, passing each argument by hand. That
/// proves the decorator uses a dependency it is given; it cannot prove the dependency ever arrives.
/// The registration builds the decorator with <c>new</c> inside a factory lambda, so an argument
/// omitted there is silently defaulted to null and the feature is simply absent at runtime.
/// </para>
/// <para>
/// That is not hypothetical: the instance provider was added to this decorator and wired at no
/// registration site, so audit records named their emitting instance in unit tests and never in a
/// composed application. The defect is invisible to a type-level test by construction, because the
/// test supplies the very argument the container omits.
/// </para>
/// <para>
/// These tests resolve <see cref="IEventStore"/> from a built provider, so an optional dependency
/// dropped at any registration site fails here rather than in production.
/// </para>
/// </remarks>
[Category("SystemEvents")]
[Category("DependencyInjection")]
public class AuditDependencyInjectionWiringTests {

  [Test]
  public async Task ComposedDecoratorReceivesTheRegisteredDecisionHookAsync() {
    var channel = new _recordingChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(new _noopStore());
    services.AddSingleton<IDeferredOutboxChannel>(channel);
    services.AddSingleton<IAuditDecisionHook>(new _refuseEverything());
    services.AddSystemEvents(opts => opts.EnableEventAudit());

    var store = services.BuildServiceProvider().GetRequiredService<IEventStore>();
    await store.AppendAsync(Guid.NewGuid(), _envelope(new _plainEvent { Name = "x" }));

    // OptOut mode audits by default, so an unwired hook queues a record here. Emptiness is only
    // reachable if the container actually handed the decorator the registered hook.
    await Assert.That(channel.QueuedMessages).IsEmpty()
      .Because("a registered decision hook must reach the decorator the container builds, or the "
             + "hook governs nothing outside its own unit tests");
  }

  [Test]
  public async Task ComposedDecoratorReceivesTheRegisteredInstanceProviderAsync() {
    var channel = new _recordingChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(new _noopStore());
    services.AddSingleton<IDeferredOutboxChannel>(channel);
    services.AddSingleton<IServiceInstanceProvider>(new _namedInstance());
    services.AddSystemEvents(opts => opts.EnableEventAudit());

    var store = services.BuildServiceProvider().GetRequiredService<IEventStore>();
    await store.AppendAsync(Guid.NewGuid(), _envelope(new _plainEvent { Name = "x" }));

    await Assert.That(channel.QueuedMessages).Count().IsEqualTo(1);
    var instance = channel.QueuedMessages[0].Envelope.Hops[0].ServiceInstance;
    await Assert.That(instance.ServiceName).IsEqualTo("composed-service")
      .Because("an audit record that cannot name its writer is untraceable, and a provider the "
             + "container never passes leaves every record equally anonymous");
  }

  private static MessageEnvelope<T> _envelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private sealed record _plainEvent : IEvent {
    public required string Name { get; init; }
  }

  private sealed class _refuseEverything : IAuditDecisionHook {
    public AuditDecision Decide(object payload, Type eventType) => AuditDecision.Skip;
  }

  private sealed class _namedInstance : IServiceInstanceProvider {
    public Guid InstanceId => Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public string ServiceName => "composed-service";
    public string HostName => "host-2";
    public int ProcessId => 7;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = Guid.Parse("00000000-0000-0000-0000-0000000000b2"),
      ServiceName = "composed-service",
      HostName = "host-2",
      ProcessId = 7,
    };
  }

  private sealed class _recordingChannel : IDeferredOutboxChannel {
    public List<OutboxMessage> QueuedMessages { get; } = [];

    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) {
      QueuedMessages.Add(message);
      return ValueTask.CompletedTask;
    }

    public IReadOnlyList<OutboxMessage> DrainAll() {
      var messages = QueuedMessages.ToList();
      QueuedMessages.Clear();
      return messages;
    }

    public bool HasPending => QueuedMessages.Count > 0;
  }

  private sealed class _noopStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default)
      => AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default)
      => AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default)
      => AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default)
      => Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
