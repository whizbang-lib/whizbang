using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Pipeline;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Tests for SystemEventServiceCollectionExtensions.
/// Extension methods for registering system event services with dependency injection.
/// </summary>
[Category("SystemEvents")]
[Category("DependencyInjection")]
public class SystemEventServiceCollectionExtensionsTests {
  #region AddSystemEvents Tests

  [Test]
  public async Task AddSystemEvents_RegistersOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEvents();
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetService<IOptions<SystemEventOptions>>();
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.Value).IsNotNull();
  }

  [Test]
  public async Task AddSystemEvents_RegistersTransportPublishFilterAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEvents();
    var provider = services.BuildServiceProvider();

    // Assert
    var filter = provider.GetService<ITransportPublishFilter>();
    await Assert.That(filter).IsNotNull();
    await Assert.That(filter).IsTypeOf<SystemEventTransportFilter>();
  }

  [Test]
  public async Task AddSystemEvents_WithConfiguration_AppliesConfigurationAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEvents(options => {
      options.EnableEventAudit();
      options.EnableCommandAudit();
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetRequiredService<IOptions<SystemEventOptions>>();
    await Assert.That(options.Value.EventAuditEnabled).IsTrue();
    await Assert.That(options.Value.CommandAuditEnabled).IsTrue();
  }

  [Test]
  public async Task AddSystemEvents_WithNullConfiguration_UsesDefaultsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEvents(null);
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetRequiredService<IOptions<SystemEventOptions>>();
    await Assert.That(options.Value.EventAuditEnabled).IsFalse();
    await Assert.That(options.Value.CommandAuditEnabled).IsFalse();
  }

  [Test]
  public async Task AddSystemEvents_ReturnsServiceCollectionForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddSystemEvents();

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }

  [Test]
  public async Task AddSystemEvents_CalledMultipleTimes_OnlyRegistersOnceAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEvents();
    services.AddSystemEvents();
    var provider = services.BuildServiceProvider();

    // Assert - Should not throw, TryAddSingleton prevents duplicates
    var filter = provider.GetService<ITransportPublishFilter>();
    await Assert.That(filter).IsNotNull();
  }

  [Test]
  public async Task AddSystemEvents_WithEventAuditEnabled_DecoratesEventStoreAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(new MockEventStore());
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());

    // Act
    services.AddSystemEvents(opts => opts.EnableEventAudit());
    var provider = services.BuildServiceProvider();

    // Assert
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  [Test]
  public async Task AddSystemEvents_WithAuditDisabled_DoesNotDecorateEventStoreAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(new MockEventStore());
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());

    // Act
    services.AddSystemEvents(); // No audit enabled
    var provider = services.BuildServiceProvider();

    // Assert
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<MockEventStore>();
  }

  #endregion

  #region AddSystemEventAuditing Tests

  [Test]
  public async Task AddSystemEventAuditing_RegistersBaseServicesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEventAuditing();
    var provider = services.BuildServiceProvider();

    // Assert - Base services registered
    var filter = provider.GetService<ITransportPublishFilter>();
    await Assert.That(filter).IsNotNull();
  }

  [Test]
  public async Task AddSystemEventAuditing_RegistersPipelineBehaviorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEventAuditing();

    // Assert - Check that the generic pipeline behavior type is registered
    var descriptor = services.FirstOrDefault(d =>
        d.ServiceType == typeof(IPipelineBehavior<,>) &&
        d.ImplementationType == typeof(CommandAuditPipelineBehavior<,>));

    await Assert.That(descriptor).IsNotNull();
  }

  [Test]
  public async Task AddSystemEventAuditing_WithConfiguration_AppliesConfigurationAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddSystemEventAuditing(options => options.EnableAudit());
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetRequiredService<IOptions<SystemEventOptions>>();
    await Assert.That(options.Value.EventAuditEnabled).IsTrue();
    await Assert.That(options.Value.CommandAuditEnabled).IsTrue();
  }

  [Test]
  public async Task AddSystemEventAuditing_ReturnsServiceCollectionForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddSystemEventAuditing();

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }

  #endregion

  #region DecorateEventStoreWithAuditing Tests

  [Test]
  public async Task DecorateEventStoreWithAuditing_WithNoEventStore_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSystemEvents();

    // Act & Assert
    await Assert.That(() => services.DecorateEventStoreWithAuditing())
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task DecorateEventStoreWithAuditing_ExceptionMessage_ContainsHelpfulTextAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSystemEvents();

    // Act
    InvalidOperationException? caughtException = null;
    try {
      services.DecorateEventStoreWithAuditing();
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    }

    // Assert
    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("No IEventStore registration found");
    await Assert.That(caughtException.Message).Contains("DecorateEventStoreWithAuditing");
  }

  [Test]
  public async Task DecorateEventStoreWithAuditing_WithEventStoreInstance_WrapsWithDecoratorAsync() {
    // Arrange
    var services = new ServiceCollection();
    var mockEventStore = new MockEventStore();
    services.AddSingleton<IEventStore>(mockEventStore);
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSystemEvents();

    // Act
    services.DecorateEventStoreWithAuditing();
    var provider = services.BuildServiceProvider();

    // Assert
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  [Test]
  public async Task DecorateEventStoreWithAuditing_WithEventStoreFactory_WrapsWithDecoratorAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore>(_ => new MockEventStore());
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSystemEvents();

    // Act
    services.DecorateEventStoreWithAuditing();
    var provider = services.BuildServiceProvider();

    // Assert
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  [Test]
  public async Task DecorateEventStoreWithAuditing_WithEventStoreType_WrapsWithDecoratorAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore, MockEventStore>();
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSystemEvents();

    // Act
    services.DecorateEventStoreWithAuditing();
    var provider = services.BuildServiceProvider();

    // Assert
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  [Test]
  public async Task DecorateEventStoreWithAuditing_ReturnsServiceCollectionForChainingAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IEventStore, MockEventStore>();
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSystemEvents();

    // Act
    var result = services.DecorateEventStoreWithAuditing();

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }

  #endregion

  #region Humanizer Coverage Tests (Lines 58, 61)

  [Test]
  public async Task AddSystemEvents_WithEventNameHumanizer_SetsCustomHumanizerAsync() {
    // Arrange
    var services = new ServiceCollection();
    string? humanizer(string eventType) => eventType == "TestEvent" ? "Test" : null;

    // Act - Exercise line 58: sets CustomHumanizer
    services.AddSystemEvents(opts => {
      opts.EventNameHumanizer = humanizer;
    });

    // Assert - CustomHumanizer was set
    await Assert.That(Whizbang.Core.SystemEvents.Audit.AuditEventProjection.CustomHumanizer).IsNotNull();

    // Cleanup
    Whizbang.Core.SystemEvents.Audit.AuditEventProjection.CustomHumanizer = null;
  }

  [Test]
  public async Task AddSystemEvents_WithEventDescriptionHumanizer_SetsCustomDescriptionHumanizerAsync() {
    // Arrange
    var services = new ServiceCollection();
    string? descHumanizer(string eventType) => "Description";

    // Act - Exercise line 61: sets CustomDescriptionHumanizer
    services.AddSystemEvents(opts => {
      opts.EventDescriptionHumanizer = descHumanizer;
    });

    // Assert - CustomDescriptionHumanizer was set
    await Assert.That(Whizbang.Core.SystemEvents.Audit.AuditEventProjection.CustomDescriptionHumanizer).IsNotNull();

    // Cleanup
    Whizbang.Core.SystemEvents.Audit.AuditEventProjection.CustomDescriptionHumanizer = null;
  }

  #endregion

  #region EventStore Resolution Path Coverage (Lines 82, 84)

  [Test]
  public async Task AddSystemEvents_WithEventAuditEnabled_FactoryRegistration_DecoratesEventStoreAsync() {
    // Arrange - Register IEventStore via factory (exercises line 82: ImplementationFactory path)
    var services = new ServiceCollection();
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSingleton<IEventStore>(sp => new MockEventStore());

    // Act
    services.AddSystemEvents(opts => opts.EnableEventAudit());
    var provider = services.BuildServiceProvider();

    // Assert - Factory was used to resolve inner, then wrapped
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  [Test]
  public async Task AddSystemEvents_WithEventAuditEnabled_TypeRegistration_DecoratesEventStoreAsync() {
    // Arrange - Register IEventStore via type (exercises line 84: ImplementationType path)
    var services = new ServiceCollection();
    services.AddSingleton<IDeferredOutboxChannel>(new MockDeferredOutboxChannel());
    services.AddSingleton<IEventStore, MockEventStore>();

    // Act
    services.AddSystemEvents(opts => opts.EnableEventAudit());
    var provider = services.BuildServiceProvider();

    // Assert - Type was used to resolve inner, then wrapped
    var eventStore = provider.GetService<IEventStore>();
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(eventStore).IsTypeOf<AuditingEventStoreDecorator>();
  }

  #endregion

  #region Mock Implementations

  private sealed class MockEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
        Task.CompletedTask;

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

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  private sealed class MockDeferredOutboxChannel : IDeferredOutboxChannel {
    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public IReadOnlyList<OutboxMessage> DrainAll() => [];

    public bool HasPending => false;
  }

  #endregion
}
