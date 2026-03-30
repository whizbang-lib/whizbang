using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for owned-domain routing rules:
/// - Commands in owned namespaces stay local (no transport)
/// - Events in owned namespaces STILL go to transport (other services need them)
/// - Owned events arriving via inbox are skipped (defense-in-depth)
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOwnedDomainTests.cs</tests>
public class DispatcherOwnedDomainTests {

  // ========================================
  // Test Messages
  // ========================================

  public record OwnedDomainEvent([property: StreamId] Guid EntityId) : IEvent;
  public record OwnedDomainCommand(string Data);

  // Command+Event pair for cascade testing (LocalInvokeAsync → receptor → cascade)
  public record CascadeTestCommand(Guid EntityId);
  public record CascadeTestEvent([property: StreamId] Guid EntityId) : IEvent;

  // ========================================
  // Stub Infrastructure
  // ========================================

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public List<InboxMessage> QueuedInboxMessages { get; } = [];
    public List<(Guid messageId, MessageProcessingStatus status)> QueuedCompletions { get; } = [];
    public List<(Guid messageId, MessageProcessingStatus status, string error)> QueuedFailures { get; } = [];
    public int FlushCount { get; private set; }

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) => QueuedInboxMessages.Add(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      QueuedCompletions.Add((messageId, completedStatus));
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) =>
      QueuedCompletions.Add((messageId, completedStatus));
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      QueuedFailures.Add((messageId, completedStatus, errorMessage));
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) =>
      QueuedFailures.Add((messageId, completedStatus, errorMessage));

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!
      );
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) {
      throw new NotImplementedException("Not needed for owned domain routing tests");
    }
  }

  // ========================================
  // Cascade receptor (discovered by source generator)
  // ========================================

  /// <summary>
  /// Receptor that returns an unwrapped event (default cascade = Outbox).
  /// Tests the cascade path: LocalInvokeAsync → receptor → _dispatchByModeAsync → outbox.
  /// </summary>
  public class CascadeTestCommandHandler : IReceptor<CascadeTestCommand, CascadeTestEvent> {
    public ValueTask<CascadeTestEvent> HandleAsync(CascadeTestCommand message, CancellationToken cancellationToken) {
      return ValueTask.FromResult(new CascadeTestEvent(message.EntityId));
    }
  }

  // ========================================
  // OUTBOX: Owned COMMANDS stay local
  // ========================================

  [Test]
  public async Task SendAsync_OwnedCommand_NoLocalReceptor_SkipsOutboxAsync() {
    // Arrange — command in owned namespace with no local receptor → skip outbox
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["Whizbang.Core.Tests.Dispatcher"]);

    // Act
    await dispatcher.SendAsync(new OwnedDomainCommand("test"));

    // Assert — owned commands never go to outbox
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(0);
  }

  [Test]
  public async Task SendAsync_NonOwnedCommand_GoesToOutboxAsync() {
    // Arrange — command NOT in owned namespace → goes to outbox
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["SomeOther.Namespace"]);

    // Act
    await dispatcher.SendAsync(new OwnedDomainCommand("test"));

    // Assert — non-owned commands go to outbox for cross-service delivery
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsNotNullOrEmpty();
  }

  // ========================================
  // OUTBOX: Owned EVENTS still go to transport
  // ========================================

  [Test]
  public async Task PublishAsync_OwnedEvent_StillGoesToTransportAsync() {
    // Arrange — PublishAsync is explicit cross-service; owned events still go to transport
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["Whizbang.Core.Tests.Dispatcher"]);

    // Act
    await dispatcher.PublishAsync(new OwnedDomainEvent(Guid.NewGuid()));

    // Assert — owned events go to transport (other services subscribe to them)
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsNotNullOrEmpty();
  }

  [Test]
  public async Task PublishAsync_NonOwnedEvent_GoesToTransportAsync() {
    // Arrange
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["SomeOther.Namespace"]);

    // Act
    await dispatcher.PublishAsync(new OwnedDomainEvent(Guid.NewGuid()));

    // Assert
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsNotNullOrEmpty();
  }

  // ========================================
  // CASCADE: Owned events MUST still reach transport
  // ========================================
  // This is the key test — when a receptor returns an unwrapped owned event,
  // it goes through _dispatchByModeAsync. Owned events must NOT be suppressed.

  [Test]
  public async Task Cascade_OwnedEvent_StillGoesToTransportAsync() {
    // Arrange — receptor returns owned event via cascade path
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["Whizbang.Core.Tests.Dispatcher"]);

    // Act — LocalInvokeAsync triggers CascadeTestCommandHandler → returns CascadeTestEvent
    var result = await dispatcher.LocalInvokeAsync<CascadeTestEvent>(new CascadeTestCommand(Guid.NewGuid()));

    // Assert — owned event cascaded from receptor STILL goes to transport with real destination
    await Assert.That(result).IsNotNull();
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsNotNullOrEmpty();
  }

  [Test]
  public async Task Cascade_NonOwnedEvent_GoesToTransportAsync() {
    // Arrange — non-owned event in cascade
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithOwnedDomains(strategy, ["SomeOther.Namespace"]);

    // Act
    var result = await dispatcher.LocalInvokeAsync<CascadeTestEvent>(new CascadeTestCommand(Guid.NewGuid()));

    // Assert — non-owned event goes to transport as before
    await Assert.That(result).IsNotNull();
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].Destination).IsNotNullOrEmpty();
  }

  // ========================================
  // FACTORY METHOD
  // ========================================

  private static IDispatcher _createDispatcherWithOwnedDomains(
    IWorkCoordinatorStrategy strategy,
    string[] ownedDomains
  ) {
    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains(ownedDomains);
    services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routingOptions));

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
